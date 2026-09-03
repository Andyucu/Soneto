using System.Windows.Automation;

namespace s4_inject_win;

/// <summary>
/// Reads Notepad's text content back via UI Automation. GetWindowText/
/// WM_GETTEXT do not work against modern (Windows 11) Notepad's edit
/// control, which is a UWP-hosted RichEditBox, not a classic Win32 EDIT
/// control -- UI Automation is the reliable cross-version approach and is
/// what this spike uses for round-trip verification.
/// </summary>
internal static class NotepadVerifier
{
    /// <summary>
    /// Polls GetForegroundWindow until its title contains the expected
    /// substring, instead of a single fixed Thread.Sleep. A blind sleep was
    /// observed (this spike) to occasionally win a race against a slow
    /// Notepad launch under load, silently sending the paste chord to
    /// whatever window happened to be foreground instead (harness flakiness,
    /// not an injector bug -- but worth eliminating rather than tolerating).
    /// </summary>
    internal static IntPtr WaitForForegroundWindowTitled(string titleContains, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr lastCandidate = IntPtr.Zero;
        int stableCount = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var hWnd = NativeMethods.GetForegroundWindow();
            var sb = new System.Text.StringBuilder(256);
            NativeMethods.GetWindowText(hWnd, sb, 256);
            if (sb.ToString().Contains(titleContains, StringComparison.OrdinalIgnoreCase))
            {
                // Require the match to be stable for a couple of consecutive
                // polls -- some other app (e.g. a browser notification/error
                // page) was observed (this spike) to transiently steal
                // foreground for a single poll interval right after launch.
                if (hWnd == lastCandidate) stableCount++;
                else { lastCandidate = hWnd; stableCount = 1; }

                if (stableCount >= 2)
                {
                    NativeMethods.SetForegroundWindow(hWnd);
                    return hWnd;
                }
            }
            else
            {
                lastCandidate = IntPtr.Zero;
                stableCount = 0;
            }
            Thread.Sleep(150);
        }
        // Timed out -- return whatever's foreground and let the caller's own
        // checks catch the mismatch rather than throwing here.
        return NativeMethods.GetForegroundWindow();
    }

    /// <summary>
    /// Windows 11 Notepad has a "reopen previous tabs" session-restore
    /// feature: a freshly-launched window can silently carry over unsaved
    /// content from an earlier run in the same test session, and paste
    /// inserts at the caret rather than replacing the document. Select-all +
    /// Delete first so every test starts from a genuinely empty document.
    /// </summary>
    internal static void ClearDocument(IntPtr hWnd)
    {
        // Retry with a verifying read-back: a title match doesn't guarantee
        // the edit surface is actually accepting input yet, so a single
        // blind Ctrl+A/Delete was observed (this spike) to occasionally land
        // before the control was ready and get silently dropped.
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            ModifierSanitizer.SendKeyDown(NativeMethods.VK_LCONTROL);
            ModifierSanitizer.SendKeyDown(0x41); // VK_A
            ModifierSanitizer.SendKeyUp(0x41);
            ModifierSanitizer.SendKeyUp(NativeMethods.VK_LCONTROL);
            Thread.Sleep(120);
            ModifierSanitizer.SendKeyDown(0x2E); // VK_DELETE
            ModifierSanitizer.SendKeyUp(0x2E);
            Thread.Sleep(150);

            var content = ReadText(hWnd);
            // Require an actual empty string, not null -- null means the UI
            // Automation read itself failed transiently, which is NOT the
            // same as "document confirmed empty" and must not be treated as
            // success (a false-positive here was observed letting a later
            // paste append onto still-present content).
            if (content == string.Empty) return;
        }
    }

    /// <summary>
    /// Polls ReadText until two consecutive reads agree, instead of trusting
    /// a single read immediately after typing/pasting. A single UI Automation
    /// read taken right after a synthetic keystroke sequence was observed
    /// (this spike, adversarial-shift test) to occasionally come back empty
    /// or containing a stray U+FFFC ("object replacement character") -- a
    /// transient UI Automation read race with the RichEditBox still catching
    /// up to the just-typed characters, not a real stuck-modifier bug (the
    /// sanitiser's own log lines were correct on every one of those runs).
    /// Same "poll for stability" pattern as WaitForForegroundWindowTitled
    /// above, applied to content reads instead of window-title reads.
    /// </summary>
    internal static string ReadTextStable(IntPtr hWnd, int timeoutMs = 2000, int pollIntervalMs = 100)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? lastGood = null;
        int stableCount = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var current = ReadText(hWnd);
            bool suspect = current == null || current.Contains('￼'); // object replacement character
            if (!suspect && current == lastGood)
            {
                stableCount++;
                if (stableCount >= 2) return current!;
            }
            else
            {
                lastGood = suspect ? null : current;
                stableCount = suspect ? 0 : 1;
            }
            Thread.Sleep(pollIntervalMs);
        }
        // Timed out -- return the last non-suspect read we saw (possibly
        // never stabilized), or empty string, and let the caller's own
        // pass/fail checks catch a genuine mismatch rather than throwing here.
        return lastGood ?? "";
    }

    internal static string? ReadText(IntPtr hWnd)
    {
        var window = AutomationElement.FromHandle(hWnd);
        if (window == null) return null;

        // Modern Notepad: ControlType.Document (RichEditBox). Classic Notepad: ControlType.Edit.
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

        var editElement = window.FindFirst(TreeScope.Descendants, condition);
        if (editElement == null) return null;

        if (editElement.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) && patternObj is TextPattern textPattern)
        {
            return textPattern.DocumentRange.GetText(-1);
        }

        if (editElement.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj) && valueObj is ValuePattern valuePattern)
        {
            return valuePattern.Current.Value;
        }

        return null;
    }
}
