using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Test-only helper: reads Notepad's text content back via UI Automation for
/// <see cref="WindowsTextInjectorNotepadSelfCheckTests"/>. Ported (test-project-local, not
/// shared product code) from <c>spikes/s4-inject-win/NotepadVerifier.cs</c> --
/// GetWindowText/WM_GETTEXT do not work against modern (Windows 11) Notepad's edit control,
/// which is a UWP-hosted RichEditBox, not a classic Win32 EDIT control; UI Automation is the
/// reliable cross-version approach the spike validated.
/// </summary>
internal static class NotepadVerifier
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    /// <summary>Polls until the foreground window's title contains <paramref name="titleContains"/>
    /// for two consecutive polls, instead of trusting a single blind sleep+check (a title
    /// match was observed by the spike to occasionally race a slow launch or a transient
    /// foreground steal by an unrelated app).</summary>
    internal static IntPtr WaitForForegroundWindowTitled(string titleContains, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr lastCandidate = IntPtr.Zero;
        int stableCount = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var hWnd = GetForegroundWindow();
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            if (sb.ToString().Contains(titleContains, StringComparison.OrdinalIgnoreCase))
            {
                if (hWnd == lastCandidate) stableCount++;
                else { lastCandidate = hWnd; stableCount = 1; }

                if (stableCount >= 2)
                {
                    SetForegroundWindow(hWnd);
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
        return GetForegroundWindow();
    }

    /// <summary>
    /// Post-review fix (flaky-test finding): polls until the edit surface is actually
    /// interactable -- UI Automation can find its <see cref="ControlType.Document"/>/
    /// <see cref="ControlType.Edit"/> element AND retrieve a text pattern from it, for two
    /// consecutive polls -- instead of the fixed <c>Thread.Sleep(800)</c> guess this replaces
    /// ("title appearing doesn't guarantee the edit surface is interactive yet"). Mirrors this
    /// file's other <c>WaitFor*</c>/<c>*Stable</c> helpers' "poll for two consecutive
    /// agreeing/successful reads" pattern rather than trusting a single check.
    /// </summary>
    internal static void WaitForEditSurfaceReady(IntPtr hWnd, int timeoutMs = 8000, int pollIntervalMs = 100)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int readyCount = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (ReadText(hWnd) != null)
            {
                readyCount++;
                if (readyCount >= 2) return;
            }
            else
            {
                readyCount = 0;
            }
            Thread.Sleep(pollIntervalMs);
        }
        // Timed out without ever confirming readiness -- fall through and let the caller's
        // own subsequent steps (ClearDocument's retry loop, ReadTextStable's polling) surface
        // any remaining problem rather than throwing here.
    }

    /// <summary>Selects-all + deletes so the test starts from a genuinely empty document,
    /// working around Windows 11 Notepad's "reopen previous tabs" session restore.</summary>
    internal static void ClearDocument(IntPtr hWnd, Action<int, bool> sendKey)
    {
        const int VK_CONTROL = 0x11, VK_A = 0x41, VK_DELETE = 0x2E;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            sendKey(VK_CONTROL, false);
            sendKey(VK_A, false);
            sendKey(VK_A, true);
            sendKey(VK_CONTROL, true);
            Thread.Sleep(120);
            sendKey(VK_DELETE, false);
            sendKey(VK_DELETE, true);
            Thread.Sleep(150);

            if (ReadText(hWnd) == string.Empty) return;
        }
    }

    /// <summary>Polls <see cref="ReadText"/> until two consecutive reads agree, instead of
    /// trusting a single read immediately after typing/pasting (the spike found a single
    /// read taken right after a synthetic keystroke sequence could transiently race
    /// Notepad's RichEditBox and come back empty or containing a stray object-replacement
    /// character).</summary>
    internal static string ReadTextStable(IntPtr hWnd, int timeoutMs = 3000, int pollIntervalMs = 100)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? lastGood = null;
        int stableCount = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var current = ReadText(hWnd);
            bool suspect = current == null || current.Contains('￼');
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
        return lastGood ?? "";
    }

    internal static string? ReadText(IntPtr hWnd)
    {
        var window = AutomationElement.FromHandle(hWnd);
        if (window == null) return null;

        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

        // Testing-specialist hardening, confirmed necessary by direct observation: Windows 11
        // Notepad's "reopen previous tabs" session restore (already the reason ClearDocument
        // exists at all -- see its own doc comment) can leave MULTIPLE Document/Edit-typed tabs
        // inside the SAME hWnd once a machine has accumulated enough leftover unsaved tabs
        // across earlier test runs -- a plain `FindFirst(TreeScope.Descendants, condition)` can
        // then return a stale, unfocused tab's stale content instead of the tab this test
        // process is actually typing/pasting into, causing a real (not flaky-timing) content
        // mismatch that has nothing to do with the paste itself. Prefer whichever matching
        // element currently has keyboard focus -- that is always the tab actually receiving
        // this process's synthetic input -- falling back to the original broad match only if
        // no element reports focus (keeps existing single-tab behaviour unchanged).
        AutomationElement? editElement = null;
        try
        {
            var globallyFocused = AutomationElement.FocusedElement;
            if (globallyFocused != null
                && (globallyFocused.Current.ControlType == ControlType.Document || globallyFocused.Current.ControlType == ControlType.Edit)
                && IsDescendantOf(globallyFocused, window))
            {
                editElement = globallyFocused;
            }
        }
        catch (ElementNotAvailableException)
        {
            // Focus moved/element torn down between the check above and reading its properties
            // -- fall through to the broader, less precise search below.
        }

        editElement ??= window.FindFirst(TreeScope.Descendants, new AndCondition(condition, new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true)))
            ?? window.FindFirst(TreeScope.Descendants, condition);
        if (editElement == null) return null;

        if (editElement.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) && patternObj is TextPattern textPattern)
            return textPattern.DocumentRange.GetText(-1);

        if (editElement.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj) && valueObj is ValuePattern valuePattern)
            return valuePattern.Current.Value;

        return null;
    }

    /// <summary>Walks up the UI Automation tree from <paramref name="element"/> checking for
    /// <paramref name="ancestor"/> -- used to confirm the globally-focused element in
    /// <see cref="ReadText"/> actually belongs to the target Notepad window, not some other
    /// application that happened to have focus at the instant of the check.</summary>
    private static bool IsDescendantOf(AutomationElement element, AutomationElement ancestor)
    {
        try
        {
            var walker = TreeWalker.RawViewWalker;
            var current = element;
            for (int i = 0; i < 64 && current != null; i++)
            {
                if (current.Equals(ancestor)) return true;
                current = walker.GetParent(current);
            }
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        return false;
    }
}
