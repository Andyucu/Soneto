using System.Diagnostics;
using System.IO;

namespace s4_inject_win;

/// <summary>
/// Fully automated, no human required: launches Notepad, injects the exact
/// S4 test string, reads the content back via UI Automation (see
/// NotepadVerifier -- GetWindowText/WM_GETTEXT do not work against modern
/// Notepad's UWP-hosted edit control), and asserts an exact string match
/// plus the diacritic byte-level check (comma-below ș/ț, not cedilla).
/// </summary>
internal static class NotepadSelfCheck
{
    internal static int Run()
    {
        Console.WriteLine("=== S4 notepad-selfcheck ===");

        const string originalClipboardContent = "S4-SELFCHECK-ORIGINAL-CLIPBOARD-CONTENT";
        Console.WriteLine("Seeding clipboard with known plain text so the restore path (step 11) is exercised cleanly...");
        ClipboardManager.SetUnicodeTextWithRetry(originalClipboardContent);

        using var proc = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;
        var hWnd = NotepadVerifier.WaitForForegroundWindowTitled("Notepad");
        Thread.Sleep(800); // title appearing doesn't guarantee the edit surface is interactive yet -- see NotepadVerifier.ClearDocument doc comment
        var titleSb = new System.Text.StringBuilder(256);
        NativeMethods.GetWindowText(hWnd, titleSb, 256);
        Console.WriteLine($"Foreground window after launch: 0x{hWnd:X} title='{titleSb}'");
        ScreenshotUtil.CaptureWindow(hWnd, Path.Combine(Path.GetTempPath(), "s4-debug-before.png"));

        NotepadVerifier.ClearDocument(hWnd);

        var result = Injector.Inject(TestData.TestString, new InjectionOptions(), explicitTarget: hWnd);
        Console.WriteLine($"Injection outcome={result.Outcome} elapsed={result.Elapsed.TotalMilliseconds:F1}ms");

        Thread.Sleep(300); // let Notepad's UI-thread-bound edit control actually update
        ScreenshotUtil.CaptureWindow(hWnd, Path.Combine(Path.GetTempPath(), "s4-debug-after.png"));
        var readBack = NotepadVerifier.ReadText(hWnd);

        bool exactMatch = readBack != null && NormalizeForCompare(readBack) == NormalizeForCompare(TestData.TestString);
        var (diacriticsPass, diacriticsDetail) = TestData.CheckDiacritics(readBack ?? "");

        Console.WriteLine($"Read back (raw): {Escape(readBack)}");
        Console.WriteLine($"Exact match (modulo trailing newline Notepad may add): {exactMatch}");
        Console.WriteLine($"Diacritics check: {(diacriticsPass ? "PASS" : "FAIL")} -- {diacriticsDetail}");
        Console.WriteLine($"Latency (full, incl. {200}ms bar's own restore-delay wait): {result.Elapsed.TotalMilliseconds:F1}ms");
        Console.WriteLine($"Latency (time-to-paste-sent, excl. clipboard-restore-delay wait): {result.TimeToPasteSent.TotalMilliseconds:F1}ms");

        var afterBackup = ClipboardManager.Save();
        bool clipboardRestored = afterBackup.HadUnicodeText && afterBackup.UnicodeText == originalClipboardContent;
        Console.WriteLine($"Original clipboard content restored: {clipboardRestored}");

        bool overallPass = exactMatch && diacriticsPass && result.Outcome == InjectionOutcome.Injected && clipboardRestored;
        Console.WriteLine(overallPass ? "OVERALL: PASS" : "OVERALL: FAIL");

        try { proc.Kill(); } catch { } // Kill, not CloseMainWindow: avoids an unsaved-changes dialog leaking a stray instance into the next run

        return overallPass ? 0 : 1;
    }

    private static string NormalizeForCompare(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n', '\r', ' ');

    private static string Escape(string? s) => s == null ? "(null)" : s.Replace("\n", "\\n").Replace("\r", "\\r");
}
