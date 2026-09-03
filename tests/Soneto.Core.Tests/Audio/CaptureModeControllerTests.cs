using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Abstractions;
using Soneto.Core.Audio;
using Soneto.Core.Configuration;

namespace Soneto.Core.Tests.Audio;

/// <summary>
/// Tests for <see cref="CaptureModeController"/> against a fake <see cref="IAudioCapture"/> —
/// no real PortAudio/hardware needed, per the class doc's own claim that this is "fully
/// unit-testable ... against a fake IAudioCapture." Covers the three capture-mode state
/// machines described in plan §1.5's table, including the WarmIdle idle-close timer's
/// start/cancel/fire behaviour. These tests were explicitly left undone by item 4c's
/// implementer and are written here independently.
/// </summary>
public class CaptureModeControllerTests
{
    /// <summary>
    /// Minimal, fully-scriptable <see cref="IAudioCapture"/> test double. Tracks open/close
    /// call counts and lets a test simulate <see cref="StartAsync"/> throwing (stream-open
    /// failure).
    /// </summary>
    private sealed class FakeAudioCapture : IAudioCapture
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool ThrowOnStart { get; set; }
        public bool IsRunning { get; private set; }

#pragma warning disable CS0067 // required by IAudioCapture; unused by this test double
        public event EventHandler<AudioLevelEventArgs>? LevelChanged;
#pragma warning restore CS0067

        public Task StartAsync(AudioDeviceId? device, CancellationToken ct)
        {
            if (ThrowOnStart)
                throw new InvalidOperationException("simulated stream-open failure");
            StartCount++;
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCount++;
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void BeginCapture(TimeSpan preRoll) { }

        public ReadOnlyMemory<float> EndCapture() => ReadOnlyMemory<float>.Empty;

        public void AbortCapture() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static CaptureModeController CreateController(
        FakeAudioCapture capture, CaptureMode mode, int idleCloseMs = 50)
    {
        return new CaptureModeController(
            capture,
            NullLogger<CaptureModeController>.Instance,
            mode,
            idleCloseMs: idleCloseMs,
            preRollMs: 300);
    }

    // ── OnDemand ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnDemand_OpensOnEveryBeginAndClosesOnEveryEnd()
    {
        var capture = new FakeAudioCapture();
        await using var controller = CreateController(capture, CaptureMode.OnDemand);

        await controller.BeginUtteranceAsync();
        Assert.Equal(1, capture.StartCount);
        Assert.True(capture.IsRunning);

        await controller.EndUtteranceAsync();
        Assert.Equal(1, capture.StopCount);
        Assert.False(capture.IsRunning);

        await controller.BeginUtteranceAsync();
        Assert.Equal(2, capture.StartCount);

        await controller.EndUtteranceAsync();
        Assert.Equal(2, capture.StopCount);
    }

    [Fact]
    public async Task OnDemand_ClosesOnAbortToo()
    {
        var capture = new FakeAudioCapture();
        await using var controller = CreateController(capture, CaptureMode.OnDemand);

        await controller.BeginUtteranceAsync();
        await controller.AbortUtteranceAsync();

        Assert.Equal(1, capture.StopCount);
        Assert.False(capture.IsRunning);
    }

    // ── WarmIdle ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WarmIdle_OpensOnlyOnFirstUtteranceOfBurst()
    {
        var capture = new FakeAudioCapture();
        await using var controller = CreateController(capture, CaptureMode.WarmIdle, idleCloseMs: 10_000);

        await controller.BeginUtteranceAsync();
        Assert.Equal(1, capture.StartCount);
        await controller.EndUtteranceAsync();

        // Second utterance of the same burst starts before the (10s) idle timer fires, so the
        // stream must be reused, not reopened.
        await controller.BeginUtteranceAsync();
        Assert.Equal(1, capture.StartCount);
        await controller.EndUtteranceAsync();

        await controller.BeginUtteranceAsync();
        Assert.Equal(1, capture.StartCount);
        await controller.EndUtteranceAsync();
    }

    [Fact]
    public async Task WarmIdle_StaysOpenAcrossUtterancesInABurst()
    {
        var capture = new FakeAudioCapture();
        await using var controller = CreateController(capture, CaptureMode.WarmIdle, idleCloseMs: 10_000);

        await controller.BeginUtteranceAsync();
        await controller.EndUtteranceAsync();
        // Stream stays open between utterances (not closed immediately like OnDemand).
        Assert.True(capture.IsRunning);
        Assert.Equal(0, capture.StopCount);

        await controller.BeginUtteranceAsync();
        Assert.True(capture.IsRunning);
    }

    [Fact]
    public async Task WarmIdle_StartsIdleCloseTimerOnEndAndAbort()
    {
        var capture = new FakeAudioCapture();
        await using var controller = CreateController(capture, CaptureMode.WarmIdle, idleCloseMs: 10_000);

        await controller.BeginUtteranceAsync();
        Assert.False(controller.IsIdleCloseTimerPending);

        await controller.EndUtteranceAsync();
        Assert.True(controller.IsIdleCloseTimerPending);

        await controller.BeginUtteranceAsync(); // cancels it
        await controller.AbortUtteranceAsync();
        Assert.True(controller.IsIdleCloseTimerPending);
    }

    [Fact]
    public async Task WarmIdle_CancelsTimerIfNewUtteranceStartsBeforeItFires()
    {
        var capture = new FakeAudioCapture();
        // Idle-close set long enough that, absent cancellation, it would not have fired by the
        // time this test asserts — proves the second BeginUtterance genuinely cancelled it
        // rather than the assertion racing a short timer.
        await using var controller = CreateController(capture, CaptureMode.WarmIdle, idleCloseMs: 5_000);

        await controller.BeginUtteranceAsync();
        await controller.EndUtteranceAsync();
        Assert.True(controller.IsIdleCloseTimerPending);

        await controller.BeginUtteranceAsync();
        Assert.False(controller.IsIdleCloseTimerPending);
        Assert.Equal(0, capture.StopCount);
        Assert.True(capture.IsRunning);
    }

    [Fact]
    public async Task WarmIdle_ClosesStreamIfTimerFiresWithNoNewUtterance()
    {
        var capture = new FakeAudioCapture();
        // Short/fake timer duration so the test runs fast, not a real 90s wait.
        await using var controller = CreateController(capture, CaptureMode.WarmIdle, idleCloseMs: 30);

        await controller.BeginUtteranceAsync();
        await controller.EndUtteranceAsync();
        Assert.True(controller.IsIdleCloseTimerPending);

        // Poll rather than a single fixed sleep, to avoid CI flakiness while staying fast.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (capture.IsRunning && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.False(capture.IsRunning);
        Assert.Equal(1, capture.StopCount);
        Assert.False(controller.IsIdleCloseTimerPending);

        // The next utterance starts a fresh burst: stream must be reopened.
        await controller.BeginUtteranceAsync();
        Assert.Equal(2, capture.StartCount);
    }

    // ── AlwaysOn ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AlwaysOn_OpensOnceViaStartAsyncAndNeverAutoCloses()
    {
        var capture = new FakeAudioCapture();
        await using var controller = CreateController(capture, CaptureMode.AlwaysOn);

        await controller.StartAsync();
        Assert.Equal(1, capture.StartCount);

        await controller.BeginUtteranceAsync();
        Assert.Equal(1, capture.StartCount); // no second open

        await controller.EndUtteranceAsync();
        Assert.True(capture.IsRunning);
        Assert.Equal(0, capture.StopCount);

        await controller.AbortUtteranceAsync();
        Assert.True(capture.IsRunning);
        Assert.Equal(0, capture.StopCount);
    }

    [Fact]
    public async Task AlwaysOn_OpensLazilyOnFirstBeginUtteranceIfStartAsyncWasNeverCalled()
    {
        var capture = new FakeAudioCapture();
        await using var controller = CreateController(capture, CaptureMode.AlwaysOn);

        await controller.BeginUtteranceAsync();
        Assert.Equal(1, capture.StartCount);

        await controller.EndUtteranceAsync();
        await controller.BeginUtteranceAsync();
        Assert.Equal(1, capture.StartCount);
    }

    // ── Stream-open failure path ─────────────────────────────────────────────────────

    [Fact]
    public async Task BeginUtterance_PropagatesExceptionIfStreamFailsToOpen()
    {
        var capture = new FakeAudioCapture { ThrowOnStart = true };
        await using var controller = CreateController(capture, CaptureMode.OnDemand);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.BeginUtteranceAsync());
    }

    // ── Idle-close timer generation/TOCTOU race (item 4c review fix 1) ────────────────

    /// <summary>
    /// Wall-clock stress attempt at the TOCTOU race the reviews flagged: a very short
    /// (near-zero) <c>WarmIdle</c> idle-close timer racing tight back-to-back
    /// <c>EndUtteranceAsync</c>/<c>BeginUtteranceAsync</c> calls, so the timer callback is
    /// sometimes genuinely dequeued by the ThreadPool around the same instant the next
    /// utterance begins and cancels it. This alone can't cleanly distinguish "timer legitimately
    /// won the race" (acceptable) from the TOCTOU bug (a callback that should have observed the
    /// cancellation still closes anyway) by timing alone, so <see cref="OnIdleCloseTimerFired_IgnoresAStaleGenerationEvenAfterCancelAlreadyRan"/>
    /// below is the deterministic proof for fix 1; this test is a looser sanity net that many
    /// iterations of tight racing don't produce any observable inconsistency (stream flapping
    /// open/closed mid-burst) even under real timer/ThreadPool scheduling.
    /// </summary>
    [Fact]
    public async Task WarmIdle_TightRacingDoesNotFlapTheStreamMidBurst()
    {
        var capture = new FakeAudioCapture();
        await using var controller = CreateController(capture, CaptureMode.WarmIdle, idleCloseMs: 5);

        await controller.BeginUtteranceAsync();

        const int iterations = 100;
        for (int i = 0; i < iterations; i++)
        {
            await controller.EndUtteranceAsync(); // schedules a 5ms idle-close timer
            await controller.BeginUtteranceAsync(); // races to cancel it / reuse the stream

            // If BeginUtteranceAsync's own decision to skip reopening (because it observed
            // IsRunning == true) is ever invalidated by a stale close landing afterward, the
            // stream would end up closed here despite an utterance believing it's in progress --
            // that inconsistent state, not "does it eventually reopen," is the actual hazard.
            Assert.True(capture.IsRunning);
        }
    }

    /// <summary>
    /// Deterministic proof for BLOCKING fix 1: directly simulates the exact TOCTOU window the
    /// reviews described, without depending on real-world timer/ThreadPool scheduling luck.
    /// <c>EndUtteranceAsync</c> schedules the idle-close timer (capturing its generation token via
    /// reflection, standing in for "the timer has fired and its callback has been dequeued onto a
    /// ThreadPool thread, about to run with this captured generation"). Then
    /// <c>BeginUtteranceAsync</c> runs and cancels it (bumping the generation) -- exactly as if the
    /// new utterance's key-down legitimately arrived and cancelled first. Only THEN is the stale
    /// callback allowed to actually execute (via reflection, standing in for the ThreadPool
    /// finally scheduling that already-dequeued callback body). Before the fix, this callback
    /// unconditionally closed the stream regardless of the cancellation that already ran; with the
    /// fix, the generation mismatch must make it a no-op.
    /// </summary>
    [Fact]
    public async Task OnIdleCloseTimerFired_IgnoresAStaleGenerationEvenAfterCancelAlreadyRan()
    {
        var capture = new FakeAudioCapture();
        await using var controller = CreateController(capture, CaptureMode.WarmIdle, idleCloseMs: 10_000);

        var generationField = typeof(CaptureModeController).GetField(
            "_timerGeneration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("_timerGeneration field not found (test relies on the fix-1 field name).");
        var onFiredMethod = typeof(CaptureModeController).GetMethod(
            "OnIdleCloseTimerFired", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("OnIdleCloseTimerFired method not found (test relies on the fix-1 method name).");

        await controller.BeginUtteranceAsync();
        await controller.EndUtteranceAsync(); // schedules the (long, never-fires-in-test) idle-close timer

        // Capture the generation the real timer was scheduled with -- standing in for "the
        // ThreadPool has already dequeued the callback with this generation captured as `state`."
        object staleGeneration = generationField.GetValue(controller)!;

        // The next utterance begins and legitimately cancels the pending timer (bumping the
        // generation) before the stale callback below gets a chance to run.
        await controller.BeginUtteranceAsync();
        Assert.False(controller.IsIdleCloseTimerPending);

        // Now let the stale, already-dequeued callback finally execute with its stale generation.
        onFiredMethod.Invoke(controller, [staleGeneration]);

        // BLOCKING fix 1's whole point: this must be a no-op. Before the fix, this unconditionally
        // closed the stream out from under the utterance that's supposedly now running on it.
        Assert.True(capture.IsRunning);
        Assert.Equal(0, capture.StopCount);
    }
}
