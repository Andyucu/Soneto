using System.Diagnostics;

namespace s4_inject_win;

/// <summary>
/// The three adversarial cases required by the plan's S4 section. Each is
/// fully automated (no human at the keyboard) using synthetic SendInput
/// events to fake the "physically holding a key" / "user hits Ctrl+C"
/// conditions -- the same technique spikes/s3-hotkey-win used with
/// SharpHook's EventSimulator to fake a physical Shift hold and prove
/// GetAsyncKeyState reflects it.
///
/// Caveat carried over from that same S3 precedent (see its README, "Jitter"
/// section): a SendInput-synthesized "hold" and a SendInput-synthesized
/// "release" both write to the exact same GetAsyncKeyState-visible key-state
/// table, so this harness cannot distinguish "the sanitiser's own suppression
/// key-up cleared the state" from "a genuinely separate physical key was
/// released" -- see RunShiftHold's inline comment for what this does and
/// does not prove automatically, and README for the resulting manual step.
/// </summary>
internal static class AdversarialTests
{
    /// <summary>Case 1: hold Shift during injection; confirm the sanitiser suppresses it and nothing is left stuck afterward.</summary>
    internal static int RunShiftHold()
    {
        Console.WriteLine("=== Adversarial 1: hold Shift during injection ===");

        using var proc = Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;
        var hWnd = NotepadVerifier.WaitForForegroundWindowTitled("Notepad");
        NotepadVerifier.ClearDocument(hWnd); // session-restore may carry over content from a prior run -- see NotepadVerifier.ClearDocument

        Console.WriteLine("Synthesizing a physical Shift-down (SendInput) to simulate the user holding Shift...");
        ModifierSanitizer.SendKeyDown(NativeMethods.VK_LSHIFT);
        Thread.Sleep(50);
        Console.WriteLine($"GetAsyncKeyState(VK_LSHIFT) reports held: {NativeMethods.IsDown(NativeMethods.VK_LSHIFT)}");

        var result = Injector.Inject(TestData.TestString, new InjectionOptions(), explicitTarget: hWnd);

        bool suppressedShift = result.Log.Any(l => l.Contains("suppressed held modifier Shift"));
        bool restoredShift = result.Log.Any(l => l.Contains("restored still-held modifier Shift"));
        bool correctlySkippedRestore = result.Log.Any(l => l.Contains("NOT restoring Shift"));
        Console.WriteLine($"Sanitiser suppressed Shift before paste chord: {suppressedShift}");
        Console.WriteLine($"Sanitiser restored Shift after paste chord (still held at that point): {restoredShift}");
        if (correctlySkippedRestore)
        {
            // Expected and correct in THIS harness: the "physical hold" here is
            // itself synthesized via SendInput (see class doc), and our own
            // suppression key-up (step 6) writes to the exact same
            // GetAsyncKeyState-visible key-state table as that synthesized
            // hold -- so by the time step 9's re-check runs, Shift already
            // reads as "not held," and the sanitiser correctly does NOT
            // restore it (its own documented, correct behaviour per §1.8:
            // "re-check ... restore only what's still physically held").
            // This means this harness cannot exercise the plan's specific
            // claim that GetAsyncKeyState keeps reporting a REAL hardware key
            // as down after a synthetic key-up (a genuinely different
            // hardware-vs-software distinction SendInput-based simulation
            // can't reproduce) -- that half of §1.8's claim needs a human
            // physically holding Shift to confirm. What IS fully validated
            // here, automatically, is the functional pass bar the plan
            // actually states for this case: no stuck modifier afterward.
            Console.WriteLine("Sanitiser correctly skipped restoring Shift (read as not-held at re-check) -- see code comment for why this harness can't force the 'still held' branch.");
        }

        // Now the "user" releases Shift, then types a few plain characters.
        // If the chord leaked as Ctrl+Shift+V or a modifier got stuck, these
        // would come out wrong (e.g. uppercase, or not appear at all).
        Console.WriteLine("Releasing synthetic Shift, then typing 'abc'...");
        ModifierSanitizer.SendKeyUp(NativeMethods.VK_LSHIFT);
        Thread.Sleep(100);
        TypePlainLetters(hWnd, "abc");
        Thread.Sleep(300);

        // Poll/retry the read-back rather than trusting a single UI Automation
        // read taken right after typing -- see NotepadVerifier.ReadTextStable
        // doc comment: a single read here was the actual source of this
        // test's ~30% flake, not a real stuck-modifier bug.
        var readBack = NotepadVerifier.ReadTextStable(hWnd);
        bool endsWithLowercase = readBack.TrimEnd('\r', '\n').EndsWith("abc", StringComparison.Ordinal);
        bool endsWithUppercase = readBack.TrimEnd('\r', '\n').EndsWith("ABC", StringComparison.Ordinal);
        Console.WriteLine($"Read back tail: ...{Tail(readBack, 20)}");
        Console.WriteLine($"Typed characters landed lowercase (no stuck Shift): {endsWithLowercase}");
        if (endsWithUppercase) Console.WriteLine("FAIL DETAIL: characters landed UPPERCASE -- a modifier was left stuck.");

        bool pass = suppressedShift && (restoredShift || correctlySkippedRestore) && endsWithLowercase && !endsWithUppercase
                    && result.Outcome == InjectionOutcome.Injected;
        Console.WriteLine(pass ? "OVERALL: PASS" : "OVERALL: FAIL");

        try { proc.Kill(); } catch { } // Kill, not CloseMainWindow: an unsaved-changes save dialog would otherwise block the close and leak a stray Notepad instance into the next run
        return pass ? 0 : 1;
    }

    /// <summary>Case 2: something else writes to the clipboard within the restore delay; the sequence-number guard must abort the restore and the interloper's content must survive.</summary>
    internal static int RunRestoreRace()
    {
        Console.WriteLine("=== Adversarial 2: copy-during-restore-window ===");

        const string originalClipboardContent = "ORIGINAL_BEFORE_S4_TEST";
        const string userCopyDuringRestore = "USER_COPIED_DURING_RESTORE_WINDOW";

        Console.WriteLine("Seeding clipboard with a known 'original' value...");
        ClipboardManager.SetUnicodeTextWithRetry(originalClipboardContent);

        using var proc = Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;
        var hWnd = NotepadVerifier.WaitForForegroundWindowTitled("Notepad");
        NotepadVerifier.ClearDocument(hWnd);

        // Use a generous restore delay (300ms) purely so the race window in this
        // automated test is comfortably winnable without flaking -- the plan's
        // production default (150ms) is exercised in the other tests/matrix runs.
        var opts = new InjectionOptions(ClipboardRestoreDelayMs: 300);

        InjectionResult? result = null;
        var injectTask = Task.Run(() => result = Injector.Inject(TestData.TestString, opts, explicitTarget: hWnd));

        Thread.Sleep(100); // land inside the 300ms restore window, after our own SetClipboardData
        Console.WriteLine("Racing: simulating the user hitting Ctrl+C (direct SetClipboardData) inside the restore window...");
        ClipboardManager.SetUnicodeTextWithRetry(userCopyDuringRestore);

        injectTask.Wait();

        int finalSeq = ClipboardManager.GetSequenceNumber();
        var (_, finalClipboardText) = ReadCurrentClipboardText();
        Console.WriteLine($"Injection outcome: {result!.Outcome}");
        Console.WriteLine($"Final clipboard content: {finalClipboardText}");

        bool guardFired = result.Outcome == InjectionOutcome.SkippedRestoreSequenceChanged;
        bool userCopySurvived = finalClipboardText == userCopyDuringRestore;
        Console.WriteLine($"Sequence-number guard aborted the restore: {guardFired}");
        Console.WriteLine($"User's copy survived (not overwritten by original-clipboard restore): {userCopySurvived}");

        bool pass = guardFired && userCopySurvived;
        Console.WriteLine(pass ? "OVERALL: PASS" : "OVERALL: FAIL");

        try { proc.Kill(); } catch { } // Kill, not CloseMainWindow: an unsaved-changes save dialog would otherwise block the close and leak a stray Notepad instance into the next run
        return pass ? 0 : 1;
    }

    /// <summary>Case 3: an image is on the clipboard; textOnly policy must skip restoration (not silently destroy the image with a text restore) and log why.</summary>
    internal static int RunImageOnClipboard()
    {
        Console.WriteLine("=== Adversarial 3: image on clipboard ===");

        Console.WriteLine("Placing a synthetic bitmap on the clipboard...");
        if (!ClipboardManager.PutSyntheticBitmap(msg => Console.WriteLine(msg)))
        {
            Console.WriteLine("Could not place test bitmap -- aborting test.");
            return 1;
        }

        using var proc = Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;
        var hWnd = NotepadVerifier.WaitForForegroundWindowTitled("Notepad");
        NotepadVerifier.ClearDocument(hWnd);

        var result = Injector.Inject(TestData.TestString, new InjectionOptions(Policy: ClipboardPolicy.TextOnly), explicitTarget: hWnd);

        bool skippedForNonText = result.Outcome == InjectionOutcome.SkippedRestoreNonText;
        bool loggedWhy = result.Log.Any(l => l.Contains("original clipboard had non-text formats"));
        Console.WriteLine($"Outcome: {result.Outcome}");
        Console.WriteLine($"Skipped restore because of non-text formats: {skippedForNonText}");
        Console.WriteLine($"Logged the reason clearly: {loggedWhy}");

        bool pass = skippedForNonText && loggedWhy;
        Console.WriteLine(pass ? "OVERALL: PASS" : "OVERALL: FAIL");

        try { proc.Kill(); } catch { } // Kill, not CloseMainWindow: an unsaved-changes save dialog would otherwise block the close and leak a stray Notepad instance into the next run
        return pass ? 0 : 1;
    }

    private static void TypePlainLetters(IntPtr hWnd, string letters)
    {
        foreach (char c in letters)
        {
            int vk = char.ToUpperInvariant(c); // VK codes for A-Z equal the uppercase ASCII code
            ModifierSanitizer.SendKeyDown(vk);
            ModifierSanitizer.SendKeyUp(vk);
            Thread.Sleep(30);
        }
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];

    private static (bool Success, string Text) ReadCurrentClipboardText()
    {
        var backup = ClipboardManager.Save();
        return (backup.HadUnicodeText, backup.UnicodeText ?? "(no text)");
    }
}
