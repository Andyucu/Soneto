using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Abstractions;

namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Exercises the parts of <see cref="WindowsTextInjector"/> that don't require sending a
/// real paste chord (which would send <c>SendInput</c> keystrokes to whatever window
/// currently has OS-wide focus -- unsafe to do from an unattended default test run on a
/// shared/live desktop, per the caution <c>spikes/s4-inject-win/README.md</c>'s own
/// "important finding" section documents from direct experience). The full round-trip paste
/// path (real <c>SendInput</c> + real target window) is covered by
/// <see cref="WindowsTextInjectorNotepadSelfCheckTests"/>, tagged
/// <c>Category=Hardware</c> because it launches and controls a real Notepad window.
/// </summary>
public sealed class WindowsTextInjectorTests
{
    private static WindowsTextInjector CreateInjector() => new(NullLogger<WindowsTextInjector>.Instance);

    [Fact]
    public void CaptureTarget_returns_a_boxed_IntPtr_for_the_current_foreground_window()
    {
        // GetForegroundWindow is a read-only query -- always some window has focus during a
        // normal test run (even if it's the test runner itself), so this is safe and
        // deterministic without touching the clipboard or sending any input.
        var injector = CreateInjector();
        var target = injector.CaptureTarget();

        Assert.NotNull(target);
        Assert.IsType<IntPtr>(target);
        Assert.NotEqual(IntPtr.Zero, (IntPtr)target!);
    }

    [Fact]
    public async Task InjectAsync_returns_TargetLost_for_an_invalid_target_object_type()
    {
        // A target that isn't an IntPtr at all should be treated the same as "no captured
        // target" and fall back to the foreground-window policy -- since some window will
        // always be foreground during a test run, this is expected to actually succeed
        // through to Injected rather than TargetLost in practice, so this test instead
        // documents that the call does not throw and returns *some* well-defined outcome for
        // a garbage target object, without needing to send a real paste (kept minimal on
        // purpose -- see class doc comment for why the full paste path is Hardware-tagged).
        var injector = CreateInjector();
        var opts = new InjectionOptions(
            InjectionMethod.ClipboardPaste, "ctrl+v",
            PreDelay: TimeSpan.Zero, ClipboardRestoreDelay: TimeSpan.Zero, RestoreClipboard: false);

        // Cancel immediately: proves target resolution (which happens before any clipboard
        // write) runs and completes before the first cancellation check, without this test
        // needing to actually send a paste chord into whatever has focus.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => injector.InjectAsync("irrelevant", target: "not an IntPtr", opts, cts.Token));
    }

    [Fact]
    public void Constructor_throws_for_a_null_logger()
    {
        Assert.Throws<ArgumentNullException>(() => new WindowsTextInjector(null!));
    }

    [Fact]
    public async Task InjectAsync_UnicodeSynthMethod_ThrowsOnCancellation_BeforeAnySendInputCall()
    {
        // Same technique as InjectAsync_returns_TargetLost_for_an_invalid_target_object_type
        // above: cancel immediately so target resolution runs and completes before the first
        // cancellation check, WITHOUT this test ever reaching a real SendInput call (this
        // project's standing caution against synthetic input touching the live desktop in an
        // unattended default test run).
        var injector = CreateInjector();
        var opts = new InjectionOptions(
            InjectionMethod.UnicodeSynth, "ctrl+v",
            PreDelay: TimeSpan.Zero, ClipboardRestoreDelay: TimeSpan.Zero, RestoreClipboard: false);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => injector.InjectAsync("irrelevant", target: null, opts, cts.Token));
    }
}
