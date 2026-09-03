using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Soneto.Core.Abstractions;

namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Hardware-tagged coverage for item 7b's modifier sanitiser -- the scenarios the
/// implementer's own report documented as skipped: a synthetically-held Shift being
/// suppressed cleanly around a real paste chord, the "released mid-injection -> do not
/// restore" stuck-modifier guard, and the configured-trigger-key collision skip. Mirrors
/// <see cref="WindowsTextInjectorNotepadSelfCheckTests"/>'s proven "launch real Notepad, poll
/// for a stable foreground window and a ready edit surface" pattern, and its precedent of
/// duplicating a tiny local <c>SendInput</c> P/Invoke helper (see that class's own comment,
/// and <c>Soneto.Daemon/Program.cs</c>'s <c>HoldLeftShiftAsync</c>, item 7b's manual
/// verification aid) rather than reaching into <c>Soneto.Platform.Windows</c>'s internal
/// <c>ModifierSanitizer</c> purely for a test.
///
/// <para>
/// Tagged <c>Category=Hardware</c> per this project's convention (see that class's doc
/// comment for why): this launches and controls a real, visible Notepad window and sends real
/// <c>SendInput</c> keystrokes, including a synthetically-held Left Shift, to whatever has OS
/// focus -- not for an unattended/default/CI run.
/// </para>
/// </summary>
[Trait("Category", "Hardware")]
public sealed class ModifierSanitizerHardwareTests
{
    private const string TestString = "The quick brown fox jumps over the lazy dog. 12345.";

    private const int VK_LSHIFT = 0xA0;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [Fact]
    public async Task Suppress_and_restore_a_physically_held_Shift_leaves_a_clean_unmangled_paste()
    {
        using var proc = LaunchNotepad(out var hWnd);
        try
        {
            var logger = new CapturingLogger<WindowsTextInjector>();
            var injector = new WindowsTextInjector(logger);
            var opts = new InjectionOptions(
                InjectionMethod.ClipboardPaste, "ctrl+v",
                PreDelay: TimeSpan.FromMilliseconds(150),
                ClipboardRestoreDelay: TimeSpan.FromMilliseconds(150),
                RestoreClipboard: false,
                SanitizeModifiers: true,
                TriggerKey: "RightControl"); // default binding -- no collision with Shift

            // Hold Left Shift for the whole injection window (well past PreDelay + the chord
            // itself), so the sanitiser must suppress it before the chord and restore it
            // (it's still physically held) afterward.
            RefocusNotepad(hWnd);
            var holdTask = HoldLeftShiftAsync(ms: 600);
            var outcome = await injector.InjectAsync(TestString, hWnd, opts, CancellationToken.None);
            await holdTask;

            Assert.Equal(InjectionOutcome.Injected, outcome);
            Assert.DoesNotContain(logger.Messages, m => m.Contains("Injection target diverged", StringComparison.Ordinal));

            var readBack = NotepadVerifier.ReadTextStable(hWnd);
            // If the sanitiser failed to suppress Shift, the physically-held Shift would ride
            // along with the synthetic Ctrl+V chord. Plain Notepad has no "paste without
            // formatting" distinction, so the sharpest observable symptom of a genuinely
            // mangled/leaked chord is the paste not landing (or landing garbled) at all --
            // assert the exact clean round-trip.
            Assert.Equal(TestString, Normalize(readBack));

            Assert.Contains(logger.Messages, m => m.Contains("suppressed held modifier Shift(L)", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, m => m.Contains("restored still-held modifier Shift(L)", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, m => m.Contains("NOT restoring Shift(L)", StringComparison.Ordinal));
        }
        finally
        {
            KillNotepad(proc);
            // Best-effort safety net: make sure Shift isn't left physically "held" from this
            // process's perspective if an assertion above threw mid-test.
            SendShiftKeyEvent(keyUp: true);
        }
    }

    [Fact]
    public async Task Shift_released_before_the_post_chord_recheck_is_not_restored_stuck_modifier_guard()
    {
        using var proc = LaunchNotepad(out var hWnd);
        try
        {
            var logger = new CapturingLogger<WindowsTextInjector>();
            var injector = new WindowsTextInjector(logger);
            // A PreDelay comfortably longer than the synthetic hold below, so Shift is
            // suppressed before the chord, then released well before the chord (and hence
            // step 9's re-check) ever runs.
            var opts = new InjectionOptions(
                InjectionMethod.ClipboardPaste, "ctrl+v",
                PreDelay: TimeSpan.FromMilliseconds(300),
                ClipboardRestoreDelay: TimeSpan.FromMilliseconds(150),
                RestoreClipboard: false,
                SanitizeModifiers: true,
                TriggerKey: "RightControl");

            RefocusNotepad(hWnd);
            var holdTask = HoldLeftShiftAsync(ms: 60);
            var outcome = await injector.InjectAsync(TestString, hWnd, opts, CancellationToken.None);
            await holdTask;

            Assert.Equal(InjectionOutcome.Injected, outcome);
            Assert.DoesNotContain(logger.Messages, m => m.Contains("Injection target diverged", StringComparison.Ordinal));

            var readBack = NotepadVerifier.ReadTextStable(hWnd);
            Assert.Equal(TestString, Normalize(readBack));

            Assert.Contains(logger.Messages, m => m.Contains("suppressed held modifier Shift(L)", StringComparison.Ordinal));
            Assert.Contains(logger.Messages, m => m.Contains("NOT restoring Shift(L)", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, m => m.Contains("restored still-held modifier Shift(L)", StringComparison.Ordinal));

            // Observable side effect, not just the log line: Shift must not be left logically
            // stuck down. GetAsyncKeyState reads the actual physical state; by the time we get
            // here the synthetic hold has long since released it for real, so this should read
            // "up" regardless -- the real assertion above is the log-based one (the log is what
            // proves the sanitiser *chose* not to restore rather than merely never having
            // suppressed it), this is a belt-and-suspenders sanity check on top.
            Assert.False(IsShiftPhysicallyDown());
        }
        finally
        {
            KillNotepad(proc);
        }
    }

    [Fact]
    public async Task Trigger_key_collision_skips_suppression_of_the_configured_trigger_and_still_pastes_cleanly()
    {
        using var proc = LaunchNotepad(out var hWnd);
        try
        {
            var logger = new CapturingLogger<WindowsTextInjector>();
            var injector = new WindowsTextInjector(logger);
            var opts = new InjectionOptions(
                InjectionMethod.ClipboardPaste, "ctrl+v",
                PreDelay: TimeSpan.FromMilliseconds(150),
                ClipboardRestoreDelay: TimeSpan.FromMilliseconds(150),
                RestoreClipboard: false,
                SanitizeModifiers: true,
                TriggerKey: "LeftShift"); // trigger IS Shift -- must not be treated as a paste modifier

            RefocusNotepad(hWnd);
            var holdTask = HoldLeftShiftAsync(ms: 600);
            var outcome = await injector.InjectAsync(TestString, hWnd, opts, CancellationToken.None);
            await holdTask;

            Assert.Equal(InjectionOutcome.Injected, outcome);
            Assert.DoesNotContain(logger.Messages, m => m.Contains("Injection target diverged", StringComparison.Ordinal));

            var readBack = NotepadVerifier.ReadTextStable(hWnd);
            // Plain Notepad has no Ctrl+Shift+V-specific behaviour distinct from Ctrl+V, so the
            // paste should land identically whether or not the physically-held (trigger) Shift
            // rides along with the chord -- this asserts the "still pastes correctly" half of
            // the collision case.
            Assert.Equal(TestString, Normalize(readBack));

            // The collision-skip half: the sanitiser must explicitly skip Shift(L) as the
            // trigger, never suppress or restore it.
            Assert.Contains(logger.Messages, m =>
                m.Contains("skipping Shift(L)", StringComparison.Ordinal)
                && m.Contains("LeftShift", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, m => m.Contains("suppressed held modifier Shift(L)", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, m => m.Contains("restored still-held modifier Shift(L)", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, m => m.Contains("NOT restoring Shift(L)", StringComparison.Ordinal));
        }
        finally
        {
            KillNotepad(proc);
            SendShiftKeyEvent(keyUp: true);
        }
    }

    // ---- shared test-local helpers ----

    private static Process LaunchNotepad(out IntPtr hWnd)
    {
        // Post-review-style hardening, confirmed necessary directly during this test's own
        // authoring: modern (Windows 11) packaged Notepad is single-instance/tabbed -- if one
        // is already running, launching "notepad.exe" again just opens a new tab in the
        // *existing* process and the newly-started Process object exits almost immediately, so
        // that existing process's window (not necessarily the freshest tab) can end up
        // foreground instead. A leftover Notepad.exe from an earlier interrupted test run was
        // directly observed causing exactly this. Kill any pre-existing Notepad.exe first so
        // the instance this method launches is guaranteed to be the sole, freshly-created one
        // -- restoring the assumption WindowsTextInjectorNotepadSelfCheckTests's already-proven
        // title-based foreground detection relies on.
        foreach (var stale in Process.GetProcessesByName("notepad"))
        {
            try { stale.Kill(); stale.WaitForExit(2000); } catch { /* best-effort */ }
        }

        var proc = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;
        hWnd = NotepadVerifier.WaitForForegroundWindowTitled("Notepad");
        NotepadVerifier.WaitForEditSurfaceReady(hWnd);
        NotepadVerifier.ClearDocument(hWnd, SendKey);
        return proc;
    }

    /// <summary>
    /// Re-asserts foreground on <paramref name="hWnd"/> immediately before injecting.
    /// Confirmed necessary directly during this test's own authoring: this automation
    /// environment's foreground window is less stable than a normal interactive desktop
    /// session (observed via <c>WindowsTextInjector</c>'s own "Injection target diverged from
    /// the current foreground window" diagnostic log line firing between <see cref="LaunchNotepad"/>
    /// returning and <c>InjectAsync</c> running), so re-confirming focus right before the paste
    /// -- mirroring what a human operator does for <c>Soneto.Daemon/Program.cs</c>'s countdown-based
    /// manual verification aid -- is required for a deterministic result here.
    /// </summary>
    private static void RefocusNotepad(IntPtr hWnd)
    {
        SetForegroundWindow(hWnd);
        Thread.Sleep(100);
    }

    private static void KillNotepad(Process proc)
    {
        try { proc.Kill(); } catch { /* best-effort cleanup */ }
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n', '\r', ' ');

    /// <summary>
    /// Test-local capturing <see cref="ILogger{TCategoryName}"/>: records every formatted log
    /// message so tests can assert on the sanitiser's own debug-log evidence (the observable
    /// side effect the task asks for) rather than reaching into internal state.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }

    /// <summary>Synthesizes a physically-held Left Shift for <paramref name="ms"/>, same
    /// technique/duplication precedent as <c>Soneto.Daemon/Program.cs</c>'s
    /// <c>HoldLeftShiftAsync</c> (item 7b's own manual-verification aid).</summary>
    private static async Task HoldLeftShiftAsync(int ms)
    {
        SendShiftKeyEvent(keyUp: false);
        try
        {
            await Task.Delay(ms).ConfigureAwait(false);
        }
        finally
        {
            SendShiftKeyEvent(keyUp: true);
        }
    }

    private static bool IsShiftPhysicallyDown() => (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0;

    private static void SendShiftKeyEvent(bool keyUp)
    {
        ushort scan = (ushort)MapVirtualKey(VK_LSHIFT, 0);
        var input = new INPUT
        {
            type = 1,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_LSHIFT, wScan = scan, dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u, time = 0, dwExtraInfo = IntPtr.Zero } }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    // ---- minimal, test-local SendInput helper (duplicated rather than made internal-visible,
    // mirroring WindowsTextInjectorNotepadSelfCheckTests's own precedent) ----
    private static void SendKey(int vk, bool keyUp)
    {
        ushort scan = (ushort)MapVirtualKey((uint)vk, 0);
        var input = new INPUT
        {
            type = 1,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = scan, dwFlags = keyUp ? 0x0002u : 0u, time = 0, dwExtraInfo = IntPtr.Zero } }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
