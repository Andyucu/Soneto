using System.Diagnostics;

namespace s4_inject_win;

internal enum InjectionOutcome { Injected, ClipboardFailed, SkippedRestoreNonText, SkippedRestoreSequenceChanged, RestoreFailed }

internal sealed record InjectionResult(InjectionOutcome Outcome, TimeSpan Elapsed, string[] Log, TimeSpan TimeToPasteSent);

internal sealed record InjectionOptions(
    string PasteChord = "ctrl+v",
    int PreDelayMs = 20,
    int ClipboardRestoreDelayMs = 150,
    ClipboardPolicy Policy = ClipboardPolicy.TextOnly);

/// <summary>
/// The §1.8 algorithm, steps 1-11, implemented as close to the plan's
/// pseudocode/numbering as possible so this spike is a direct, checkable
/// translation of the spec rather than a reinterpretation of it.
/// </summary>
internal static class Injector
{
    internal static InjectionResult Inject(string text, InjectionOptions opts, IntPtr? explicitTarget = null)
    {
        var log = new List<string>();
        void Log(string s)
        {
            log.Add(s);
            Console.WriteLine(s);
        }

        var sw = Stopwatch.StartNew();

        // 1/2. target capture -- CLI already captured this at "key-down" time (countdown start);
        // if the caller re-resolves at inject time (policy=current), that happens before this call.
        var target = explicitTarget ?? NativeMethods.GetForegroundWindow();
        Log($"inject: target hwnd=0x{target:X}");

        // 3. sequence number before we touch anything
        int seqBefore = ClipboardManager.GetSequenceNumber();
        Log($"inject: seqBefore={seqBefore}");

        // 4. inspect + save existing clipboard
        var backup = ClipboardManager.Save(Log);

        // 5. set our text, retry 3x/20ms
        if (!ClipboardManager.SetUnicodeTextWithRetry(text, attempts: 3, delayMs: 20, log: Log))
        {
            Log("inject: FAILED -- could not set clipboard after retries");
            sw.Stop();
            return new InjectionResult(InjectionOutcome.ClipboardFailed, sw.Elapsed, [.. log], sw.Elapsed);
        }
        int seqAfterOurSet = ClipboardManager.GetSequenceNumber();
        Log($"inject: seqAfterOurSet={seqAfterOurSet}");

        // 6. sanitize modifiers
        var held = ModifierSanitizer.Suppress(Log);

        // 7. pre-delay
        Thread.Sleep(opts.PreDelayMs);

        // 8. paste chord
        SendPasteChord(opts.PasteChord, Log);

        // Time-to-paste-sent: the actual "injection work" latency, excluding
        // the intentional opts.ClipboardRestoreDelayMs wait in step 10 below
        // (that wait is a deliberate race-avoidance delay, not injection lag
        // -- see README "Latency measurement" for why both numbers matter).
        var timeToPasteSent = sw.Elapsed;

        // 9. restore modifiers (re-check physical state first)
        ModifierSanitizer.Restore(held, Log);

        // 10. clipboard restore delay
        Thread.Sleep(opts.ClipboardRestoreDelayMs);

        // 11. sequence-number guard + clipboardPolicy
        sw.Stop();
        // This is an early, non-atomic read purely to short-circuit the
        // obviously-already-changed case and to log the observed sequence --
        // it is NOT the guard that gates the actual write below. The write
        // itself re-checks the sequence number atomically (inside the same
        // OpenClipboard/CloseClipboard critical section as EmptyClipboard/
        // SetClipboardData, see ClipboardManager.TryRestoreIfSequenceUnchanged)
        // because a standalone check-then-later-write here would leave a gap
        // for the user's Ctrl+C to land in between this read and the actual
        // write -- exactly the TOCTOU this design has to avoid.
        int seqNow = ClipboardManager.GetSequenceNumber();
        Log($"inject: seqNow={seqNow}, elapsed={sw.ElapsedMilliseconds}ms (timeToPasteSent={timeToPasteSent.TotalMilliseconds:F1}ms)");

        if (seqNow != seqAfterOurSet)
        {
            Log("inject: SKIP restore -- clipboard sequence number changed during restore window (user copied during window)");
            return new InjectionResult(InjectionOutcome.SkippedRestoreSequenceChanged, sw.Elapsed, [.. log], timeToPasteSent);
        }

        if (opts.Policy == ClipboardPolicy.Never)
        {
            Log("inject: policy=never -- leaving transcript on clipboard, not restoring");
            return new InjectionResult(InjectionOutcome.Injected, sw.Elapsed, [.. log], timeToPasteSent);
        }

        if (opts.Policy == ClipboardPolicy.TextOnly && backup.HadNonTextFormats)
        {
            Log("inject: SKIP restore -- textOnly policy and original clipboard had non-text formats (would destroy them); leaving transcript on clipboard");
            return new InjectionResult(InjectionOutcome.SkippedRestoreNonText, sw.Elapsed, [.. log], timeToPasteSent);
        }

        if (backup.HadUnicodeText)
        {
            bool restored = ClipboardManager.TryRestoreIfSequenceUnchanged(backup.UnicodeText!, seqAfterOurSet, out bool sequenceChangedAtRestore, Log);
            if (restored)
            {
                Log("inject: original clipboard text restored");
            }
            else if (sequenceChangedAtRestore)
            {
                // Caught by the atomic check inside the write's own
                // open/close section -- the early seqNow check above passed,
                // but the user's Ctrl+C landed in the remaining gap between
                // that check and the actual OpenClipboard here. This is
                // exactly the race fix 2 closes: we still must not overwrite.
                Log("inject: SKIP restore -- clipboard sequence number changed atomically at write time (user copied during the restore window)");
                return new InjectionResult(InjectionOutcome.SkippedRestoreSequenceChanged, sw.Elapsed, [.. log], timeToPasteSent);
            }
            else
            {
                // All retries failed for a reason other than a sequence
                // change (e.g. another process held the clipboard open the
                // whole time) -- do NOT claim success. Silently overwriting
                // or losing the user's original clipboard content without
                // saying so is the worst failure this app can have.
                Log("inject: FAILED to restore original clipboard after retries");
                return new InjectionResult(InjectionOutcome.RestoreFailed, sw.Elapsed, [.. log], timeToPasteSent);
            }
        }
        else
        {
            Log("inject: nothing to restore (clipboard was empty or non-text with no backup taken)");
        }

        return new InjectionResult(InjectionOutcome.Injected, sw.Elapsed, [.. log], timeToPasteSent);
    }

    private static void SendPasteChord(string chord, Action<string> log)
    {
        // Supported chords: "ctrl+v", "ctrl+shift+v"
        bool shift = chord.Contains("shift", StringComparison.OrdinalIgnoreCase);
        log($"inject: sending paste chord '{chord}'");

        ModifierSanitizer.SendKeyDown(NativeMethods.VK_LCONTROL);
        if (shift) ModifierSanitizer.SendKeyDown(NativeMethods.VK_LSHIFT);
        ModifierSanitizer.SendKeyDown(NativeMethods.VK_V);
        ModifierSanitizer.SendKeyUp(NativeMethods.VK_V);
        if (shift) ModifierSanitizer.SendKeyUp(NativeMethods.VK_LSHIFT);
        ModifierSanitizer.SendKeyUp(NativeMethods.VK_LCONTROL);
    }
}
