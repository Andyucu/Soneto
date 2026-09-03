using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpHook;
using Soneto.Core.Abstractions;

namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Tests for <see cref="WindowsHotkeySource"/>'s heartbeat/fault-detection path (plan §1.12's
/// "heartbeat: no events for 60s + a test-event probe" row).
///
/// <para>
/// <b>Why reflection is used here, and why it's flagged as a should-fix rather than avoided.</b>
/// <see cref="WindowsHotkeySource"/>'s heartbeat has no testability seam: the 60s idle
/// threshold and 15s timer interval are hardcoded <c>static readonly</c> fields, there is no
/// injectable clock, and <c>OnHeartbeatTick</c> is private with no public trigger. Waiting a
/// real 60+ seconds per test is not acceptable for a suite meant to run in the default fast
/// `dotnet test` pass. The only way to exercise this deterministically today is reflection:
/// rewinding the private <c>_lastEventTicksUtc</c> field so the class believes it has been
/// idle past the threshold, then invoking the private <c>OnHeartbeatTick</c> method directly
/// instead of waiting for the real <see cref="Timer"/> to fire. This is inherently more
/// fragile than a real testability seam (renaming either member breaks these tests with no
/// compiler error until run) -- <b>recommended should-fix for review:</b> introduce a small
/// injectable clock/time-provider (or an internal test-only trigger method) so this class's
/// idle-detection logic can be driven deterministically without reflecting into private
/// members. Not applied here per this session's role split (test authorship only, no
/// production-code changes).
/// </para>
/// <para>
/// Despite using reflection to trigger the check, everything the check itself DOES is real:
/// a real <see cref="SimpleGlobalHook"/>, a real <see cref="SharpHook.Simulation.EventSimulator"/>-driven
/// probe key event, and (for the "genuinely dead" case) a real, actually-stopped hook that
/// genuinely cannot observe the probe -- nothing about the pass/fail outcome itself is faked.
/// </para>
/// <para>
/// Pinned to the same sequential-only collection as <see cref="WindowsHotkeySourceTests"/>
/// (see that class's doc comment) since this class also spins up a real hook instance.
/// </para>
/// </summary>
[Collection(RealHotkeyHookCollection.Name)]
public sealed class WindowsHotkeySourceHeartbeatTests
{
    private const string TriggerKeyConfig = "F16";

    [Fact]
    public async Task Heartbeat_does_not_fault_when_the_hook_is_genuinely_alive_and_observes_the_probe()
    {
        await using var source = new WindowsHotkeySource(NullLogger<WindowsHotkeySource>.Instance);

        HotkeyFaultEventArgs? fault = null;
        source.Faulted += (_, e) => fault = e;

        await source.StartAsync(new HotkeyBinding(TriggerKeyConfig, Suppress: false), CancellationToken.None);

        RewindLastEventTimestampPastIdleThreshold(source);
        await InvokeHeartbeatTickAndWait(source);

        Assert.Null(fault);
    }

    [Fact]
    public async Task Heartbeat_raises_Faulted_when_the_hook_is_genuinely_dead_and_the_probe_is_not_observed()
    {
        await using var source = new WindowsHotkeySource(NullLogger<WindowsHotkeySource>.Instance);

        HotkeyFaultEventArgs? fault = null;
        source.Faulted += (_, e) => fault = e;

        await source.StartAsync(new HotkeyBinding(TriggerKeyConfig, Suppress: false), CancellationToken.None);

        // Genuinely kill the hook's ability to observe anything (not a mock/fake outcome --
        // the underlying SimpleGlobalHook is really stopped), so the heartbeat's synthetic
        // probe genuinely cannot be observed.
        var hookField = typeof(WindowsHotkeySource).GetField("_hook", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var hook = (SimpleGlobalHook?)hookField.GetValue(source);
        Assert.NotNull(hook);
        hook!.Stop();
        // Stop() unregisters the native hook asynchronously (mirrors StartAsync's own 200ms
        // settle delay after RunAsync) -- without this, the probe key-press synthesized below
        // can still land on the not-yet-fully-unhooked native hook and be observed, producing
        // a false "still alive" result.
        await Task.Delay(300);

        RewindLastEventTimestampPastIdleThreshold(source);
        await InvokeHeartbeatTickAndWait(source);

        Assert.NotNull(fault);
        Assert.Contains("not observed by the hook", fault!.Reason);
    }

    /// <summary>
    /// Regression proof for BLOCKING fix 1: reproduces the exact race the review described --
    /// a heartbeat tick already mid-flight (idle-check already passed, probe already injected
    /// but genuinely unobservable, now sleeping through its ~770ms probe-wait window) when a
    /// full, legitimate <see cref="WindowsHotkeySource.RestartAsync"/> races in and completes
    /// underneath it, freshly resetting <c>_faultRaised</c> to 0 on the new, healthy instance.
    ///
    /// <para>
    /// This can NOT be reproduced by sequentially capturing a generation, running a restart,
    /// and only afterward manually invoking <c>OnHeartbeatTick</c> with the stale generation
    /// (the pattern item 4c's <c>OnIdleCloseTimerFired_IgnoresAStaleGenerationEvenAfterCancelAlreadyRan</c>
    /// uses) -- unlike that timer callback, <c>OnHeartbeatTick</c>'s idle check reads the LIVE
    /// <c>_lastEventTicksUtc</c> field at the moment it's invoked, which a real restart resets
    /// to "now." A sequential invocation would therefore fail the idle check on its own
    /// (idle-for-0-seconds &lt; 60s threshold) and return early regardless of the generation
    /// check, on BOTH the pre-fix and post-fix code -- proving nothing. The actual bug requires
    /// the OLD tick to have already captured "idle for 60+s" as a local variable
    /// (<c>idleFor</c>) and already injected its probe BEFORE the restart happens, and only
    /// THEN have the restart race in while it sleeps -- so this test genuinely races a real
    /// background invocation of the real tick against a real, concurrent <c>RestartAsync</c>.
    /// </para>
    /// <para>
    /// The OLD hook is stopped (and given the same ~300ms settle wait
    /// <see cref="Heartbeat_raises_Faulted_when_the_hook_is_genuinely_dead_and_the_probe_is_not_observed"/>
    /// uses) BEFORE the stale tick is launched, so its synthetic probe genuinely cannot be
    /// observed by anything -- otherwise the still-alive old hook could observe its own probe
    /// and take the unrelated "hook is alive" early-return path, which would pass regardless
    /// of the generation fix and prove nothing. The restart is then raced in ~50ms into the
    /// stale tick's ~770ms sleep, leaving a generous ~700ms of slack for it to complete first.
    /// </para>
    /// <para>
    /// Confirmed (by temporarily removing the post-sleep generation re-check in
    /// <c>OnHeartbeatTick</c> and re-running) that this test reliably FAILS -- a spurious
    /// <c>Faulted</c> is raised against the freshly-restarted, healthy instance -- against the
    /// pre-fix code, and reliably PASSES against the fix.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OnHeartbeatTick_IgnoresAStaleGenerationWhenARestartRacesInDuringItsProbeWait()
    {
        await using var source = new WindowsHotkeySource(NullLogger<WindowsHotkeySource>.Instance);

        HotkeyFaultEventArgs? fault = null;
        source.Faulted += (_, e) => fault = e;

        await source.StartAsync(new HotkeyBinding(TriggerKeyConfig, Suppress: false), CancellationToken.None);

        var generationField = typeof(WindowsHotkeySource).GetField(
            "_heartbeatGeneration", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_heartbeatGeneration field not found (test relies on the fix-1 field name).");
        var onTickMethod = typeof(WindowsHotkeySource).GetMethod(
            "OnHeartbeatTick", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("OnHeartbeatTick method not found (test relies on the fix-1 method name).");

        // Capture the generation the real heartbeat timer was created with -- standing in for
        // "the ThreadPool has already dequeued the callback with this generation captured as
        // `state`."
        object staleGeneration = generationField.GetValue(source)!;

        RewindLastEventTimestampPastIdleThreshold(source);

        // Genuinely kill the OLD hook's ability to observe anything BEFORE the stale tick's
        // probe is injected (mirrors the "genuinely dead" test above, including its ~300ms
        // settle wait) -- otherwise the still-alive old hook could observe its own probe and
        // take the unrelated "hook is alive" path, proving nothing about the generation fix.
        var hookField = typeof(WindowsHotkeySource).GetField("_hook", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var oldHook = (SimpleGlobalHook?)hookField.GetValue(source);
        Assert.NotNull(oldHook);
        oldHook!.Stop();
        await Task.Delay(300);

        // Invoke the real tick directly with its real (soon-to-be-stale) generation, on a
        // background thread. It passes the up-front generation/idle checks (both still
        // match/true at this instant), resets _probeObserved, injects the real synthetic
        // probe key event (genuinely unobservable now that the old hook is stopped), then
        // blocks for ~770ms (20ms key-up delay + ProbeWaitWindow) -- exactly the "mid-flight,
        // sleeping through its probe-wait window" state the review described.
        var staleTickTask = Task.Run(() => onTickMethod.Invoke(source, new object?[] { staleGeneration }));

        // Race a real restart in while the tick above is still sleeping through its
        // probe-wait window -- exactly the seam SessionController's watchdog will drive in
        // item 9. This stops the old (already-stopped) hook/timer (bumping the generation
        // once) and starts a brand-new, healthy one (bumping it again, resetting
        // _lastEventTicksUtc, and resetting _faultRaised to 0).
        await Task.Delay(50);
        await source.RestartAsync(CancellationToken.None);

        // Let the stale tick finish waking up and re-checking its (now stale) generation.
        await staleTickTask;

        // BLOCKING fix 1's whole point: this must be a no-op. Before the fix, the stale tick
        // above -- having already captured "idle for 60+s" before the restart ever happened,
        // and having genuinely never observed its own probe -- wakes up, finds
        // _probeObserved still 0 and _faultRaised freshly reset to 0 by the new StartAsync,
        // and unconditionally raises Faulted against the brand-new, healthy instance
        // milliseconds after a successful restart.
        Assert.Null(fault);
    }

    private static void RewindLastEventTimestampPastIdleThreshold(WindowsHotkeySource source)
    {
        var idleThresholdField = typeof(WindowsHotkeySource).GetField("HeartbeatIdleThreshold", BindingFlags.NonPublic | BindingFlags.Static)!;
        var idleThreshold = (TimeSpan)idleThresholdField.GetValue(null)!;

        var lastEventField = typeof(WindowsHotkeySource).GetField("_lastEventTicksUtc", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var rewound = DateTime.UtcNow - idleThreshold - TimeSpan.FromSeconds(5);
        lastEventField.SetValue(source, rewound.Ticks);
    }

    private static async Task InvokeHeartbeatTickAndWait(WindowsHotkeySource source)
    {
        var method = typeof(WindowsHotkeySource).GetMethod("OnHeartbeatTick", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var probeWaitField = typeof(WindowsHotkeySource).GetField("ProbeWaitWindow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var probeWait = (TimeSpan)probeWaitField.GetValue(null)!;

        // Fix 1 (blocking) requires OnHeartbeatTick's `state` to be the real, current
        // generation token (a boxed long), not null -- the timer that would really invoke
        // this passes its own captured generation, and the method now unconditionally casts
        // `state` to `long`. Read the real, current generation via reflection rather than
        // hardcoding 1L, so this helper keeps working regardless of how many times a given
        // test has already started/restarted the source.
        var generationField = typeof(WindowsHotkeySource).GetField("_heartbeatGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var currentGeneration = generationField.GetValue(source)!;

        // OnHeartbeatTick blocks synchronously (Thread.Sleep) for roughly ProbeWaitWindow, so
        // run it on a background thread and wait generously past that.
        await Task.Run(() => method.Invoke(source, new object?[] { currentGeneration }));
        await Task.Delay(probeWait + TimeSpan.FromMilliseconds(200));
    }
}
