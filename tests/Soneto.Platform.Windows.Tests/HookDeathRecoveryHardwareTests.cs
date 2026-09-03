using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpHook;
using SharpHook.Data;
using SharpHook.Simulation;
using Soneto.Core;
using Soneto.Core.Abstractions;
using Soneto.Core.Asr;
using Soneto.Core.Audio;
using Soneto.Core.Configuration;
using Soneto.Core.PostProcessing;

namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Phase 4 item 5 (§4.6) -- real-conditions verification of hook-death recovery, NOT a
/// redesign. Phase 1 item 9 already built <see cref="SessionController"/>'s watchdog
/// (<c>HandleHookFaultedAsync</c>: up to 5 <see cref="IHotkeySource.RestartAsync"/> attempts,
/// exponential backoff 1s/2s/4s/8s/16s, ~31s worst case, permanent <c>Faulted</c> only after
/// exhausting them -- see that class's own "Watchdog backoff shape" doc comment) and it has
/// been thoroughly unit-tested against a <c>FakeHotkeySource</c> with millisecond-scale backoff
/// (<c>Soneto.Core.Tests.SessionControllerTests</c>'s <c>HookFaulted_*</c> cases). What has
/// never been exercised before this test: a GENUINELY dead native Windows hook (not a fake
/// throwing an exception), detected by the REAL <see cref="System.Threading.Timer"/>-driven
/// heartbeat running for real wall-clock time (not reflection-invoking
/// <c>OnHeartbeatTick</c> the way <see cref="WindowsHotkeySourceHeartbeatTests"/> does for
/// speed), recovered via the REAL <see cref="SessionController.HandleHookFaultedAsync"/> code
/// path (not a reimplementation) calling the REAL <see cref="WindowsHotkeySource.RestartAsync"/>.
///
/// <para>
/// <b>This test genuinely takes over a minute (idle threshold 60s + up to one more 15s timer
/// period + the ~770ms probe-wait window) -- that is expected and correct, not a bug to "fix"
/// by speeding up the mechanism itself</b> (see this work item's own explicit instruction not to
/// shortcut the real timer/threshold path via reflection). Tagged <c>[Trait("Category","Hardware")]</c>
/// per this project's established convention (see <see cref="WindowsHotkeySourceHardwareScenariosTests"/>),
/// excluded from the default `dotnet test` filter, so the fast suite is unaffected.
/// </para>
///
/// <para>
/// <b>How the real hook is genuinely killed, without touching any external app or the real
/// desktop.</b> Same technique <see cref="WindowsHotkeySourceHeartbeatTests"/> already
/// established and this project's safety convention already accepts: reflection into
/// <see cref="WindowsHotkeySource"/>'s own private <c>_hook</c> field to call the REAL
/// <see cref="SimpleGlobalHook.Stop()"/> on Soneto's own real, installed hook instance --
/// never any other application's window, never a synthetic keystroke sent anywhere but this
/// process's own real hook/probe-key channel below.
/// </para>
///
/// <para>
/// <b>Why the "confirm the hotkey is genuinely working again" proof uses the heartbeat's own
/// probe-key channel (F24), not the configured trigger key, for its post-recovery liveness
/// check -- a real, already-documented limitation, not a new discovery.</b>
/// <see cref="WindowsHotkeySourceTests"/>'s own class doc comment (see its skipped
/// <c>Pressed_and_Released_fire_in_order_for_a_real_synthesized_press_release</c> test) already
/// established that <see cref="EventSimulator"/>-driven synthetic input is tagged
/// <c>IsEventSimulated</c> identically to <see cref="WindowsTextInjector"/>'s own synthetic
/// paste-chord modifiers, and <see cref="WindowsHotkeySource.OnKeyPressed"/>/
/// <see cref="WindowsHotkeySource.OnKeyReleased"/> deliberately ignore any TRIGGER-key-coded
/// event with <c>IsEventSimulated=true</c> (see that class's own doc comment) -- so no
/// automated test can synthesize a "real" trigger press post-recovery via <c>EventSimulator</c>
/// (this is why that pre-existing test is skipped, not deleted). The heartbeat's own probe-key
/// branch (<see cref="WindowsHotkeySource.ProbeKeyCode"/>, F24) deliberately does NOT apply that
/// filter (see the class doc comment's "self-injected keyboard events" note), so sending a real
/// synthetic F24 press/release directly after recovery and confirming the NEW hook's real
/// <c>OnKeyPressed</c>/<c>OnKeyReleased</c> callbacks genuinely observe it is the strongest real,
/// automatable, end-to-end liveness proof available for the reinstalled hook -- it exercises the
/// identical native callback pipeline the trigger path uses, differing only in which branch of
/// the same method runs.
/// </para>
/// </summary>
[Collection(RealHotkeyHookCollection.Name)]
[Trait("Category", "Hardware")]
public sealed class HookDeathRecoveryHardwareTests
{
    private const string TriggerKeyConfig = "F17"; // distinct from F15/F16/F24 already used elsewhere in this suite.

    [Fact]
    public async Task RealHeartbeatDetectsAGenuinelyDeadHookAndSessionControllerRecoversViaTheRealWatchdog()
    {
        var hotkey = new WindowsHotkeySource(NullLogger<WindowsHotkeySource>.Instance);
        var capture = new NoOpAudioCapture();
        var captureController = new CaptureModeController(
            capture, NullLogger<CaptureModeController>.Instance, CaptureMode.OnDemand, idleCloseMs: 1000, preRollMs: 0);
        var vad = new SileroVadDetector(NullLogger<SileroVadDetector>.Instance, new VadConfig { Enabled = false });
        var transcriber = new NoOpTranscriber();
        var chain = new PostProcessorChain(Array.Empty<IPostProcessor>());
        var injector = new NoOpTextInjector();

        // Production defaults (5 attempts, 1s initial backoff) -- deliberately not sped up, per
        // this work item's own instruction that the real mechanism must be exercised at real speed.
        var options = new SessionControllerOptions(
            new HotkeyBinding(TriggerKeyConfig, Suppress: false),
            new InjectionOptions(Soneto.Core.Abstractions.InjectionMethod.ClipboardPaste, "ctrl+v", TimeSpan.Zero, TimeSpan.Zero,
                RestoreClipboard: true, TriggerKey: TriggerKeyConfig));

        await using var controller = new SessionController(
            hotkey, captureController, vad, transcriber, chain, injector, options,
            NullLogger<SessionController>.Instance);

        await controller.StartAsync();
        Assert.Equal(SessionState.Idle, controller.State);

        var hookField = typeof(WindowsHotkeySource).GetField("_hook", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var deadHook = (SimpleGlobalHook?)hookField.GetValue(hotkey);
        Assert.NotNull(deadHook);

        // Genuinely kill Soneto's own real, installed hook -- never any other app's window.
        deadHook!.Stop();
        // Same ~300ms native-unhook settle wait WindowsHotkeySourceHeartbeatTests already
        // established is necessary: Stop() unregisters asynchronously.
        await Task.Delay(300);

        // Wait for the REAL heartbeat timer (60s idle threshold + up to one more 15s period +
        // the ~770ms probe-wait window) to detect the dead hook for real, and for the REAL
        // SessionController.HandleHookFaultedAsync watchdog to call the REAL RestartAsync and
        // install a genuinely new, running hook -- observed structurally (a new hook instance,
        // actually running) rather than via any StateChanged event, since a successful recovery
        // never leaves Idle in the first place (SetState is a no-op when the state doesn't
        // change -- see SessionController.SetState).
        var recovered = await WaitUntilAsync(
            () =>
            {
                var current = (SimpleGlobalHook?)hookField.GetValue(hotkey);
                return current is not null && !ReferenceEquals(current, deadHook) && current.IsRunning;
            },
            timeout: TimeSpan.FromSeconds(120),
            pollInterval: TimeSpan.FromSeconds(2));

        Assert.True(recovered, "WindowsHotkeySource never installed a new, running hook within 120s of the real hook being killed -- the real heartbeat/watchdog recovery path did not complete.");
        Assert.Equal(SessionState.Idle, controller.State); // recovered, not permanently Faulted.

        // "Genuinely working again," not just "RestartAsync returned without throwing": send a
        // real synthetic probe-key press/release (F24 -- the same channel the heartbeat itself
        // uses, and the only one not subject to WindowsHotkeySourceTests' documented
        // IsEventSimulated/trigger-key limitation above) and confirm the NEW hook's real
        // OnKeyPressed/OnKeyReleased callbacks genuinely observe it.
        var probeObservedField = typeof(WindowsHotkeySource).GetField("_probeObserved", BindingFlags.NonPublic | BindingFlags.Instance)!;
        probeObservedField.SetValue(hotkey, 0);

        using (var simulator = EventSimulator.Create("Soneto.Platform.Windows.Tests"))
        {
            simulator.SimulateKeyPress(WindowsHotkeySource.ProbeKeyCode);
            await Task.Delay(20);
            simulator.SimulateKeyRelease(WindowsHotkeySource.ProbeKeyCode);
        }

        var probeObserved = await WaitUntilAsync(
            () => (int)probeObservedField.GetValue(hotkey)! == 1,
            timeout: TimeSpan.FromSeconds(3),
            pollInterval: TimeSpan.FromMilliseconds(100));

        Assert.True(probeObserved, "The reinstalled hook did not observe a real synthetic probe key event -- it is not genuinely alive.");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, TimeSpan pollInterval)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(pollInterval);
        }
        return predicate();
    }

    // ── Minimal, deliberately inert fakes: this test's fault happens while Idle, so none of
    //    these are ever actually called -- they exist only to satisfy SessionController's
    //    constructor, mirroring Soneto.Core.Tests.SessionControllerTests' own fake pattern. ──

    private sealed class NoOpAudioCapture : IAudioCapture
    {
        public bool IsRunning { get; private set; }
#pragma warning disable CS0067
        public event EventHandler<AudioLevelEventArgs>? LevelChanged;
#pragma warning restore CS0067
        public Task StartAsync(AudioDeviceId? device, CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
        public void BeginCapture(TimeSpan preRoll) { }
        public ReadOnlyMemory<float> EndCapture() => new float[16_000];
        public void AbortCapture() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpTranscriber : ITranscriber
    {
        public bool IsReady => true;
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> samples16k, CancellationToken ct) =>
            Task.FromResult(new TranscriptionResult("", TimeSpan.Zero, TimeSpan.Zero, IsEmpty: true));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpTextInjector : ITextInjector
    {
        public object? CaptureTarget() => null;
        public Task<InjectionOutcome> InjectAsync(string text, object? target, InjectionOptions opts, CancellationToken ct) =>
            Task.FromResult(InjectionOutcome.Injected);
    }
}
