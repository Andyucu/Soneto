using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Abstractions;
using Soneto.Core.Asr;
using Soneto.Core.Audio;
using Soneto.Core.Configuration;
using Soneto.Core.Dictionary;
using Soneto.Core.PostProcessing;
using Soneto.Core.Wav;

namespace Soneto.Core.Tests;

/// <summary>
/// Tests for <see cref="SessionController"/> (item 9), per plan §1.13: "drive with a fake
/// hotkey source, fake capture, fake transcriber. Every row of the §1.4 table gets a test,
/// including all six edge cases." Runs with no audio device and no ASR model file present --
/// <see cref="IHotkeySource"/>/<see cref="IAudioCapture"/>/<see cref="ITranscriber"/> are all
/// hand-written fakes below; the only REAL production components exercised are
/// <see cref="CaptureModeController"/> (wrapping the fake <see cref="IAudioCapture"/>, per
/// item 4c's own test precedent), <see cref="SileroVadDetector"/> (its model is a tiny
/// embedded resource, not the ~640MB ASR model -- safe in the default run, per
/// <c>SileroVadDetectorTests</c>' own doc comment), and <see cref="PostProcessorChain"/>
/// (pure string logic, no I/O).
/// </summary>
public sealed class SessionControllerTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────────────

    private sealed class FakeHotkeySource : IHotkeySource
    {
        public int StartCount { get; private set; }
        public int RestartCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool ThrowOnStart { get; set; }

        /// <summary>Queued outcomes for successive RestartAsync calls; true = succeed, false = throw. Empty queue = always succeed.</summary>
        public Queue<bool> RestartOutcomes { get; } = new();

        public event EventHandler<HotkeyEventArgs>? Pressed;
        public event EventHandler<HotkeyEventArgs>? Released;
        public event EventHandler<HotkeyFaultEventArgs>? Faulted;

        public Task StartAsync(HotkeyBinding binding, CancellationToken ct)
        {
            if (ThrowOnStart)
                throw new InvalidOperationException("simulated hook start failure");
            StartCount++;
            return Task.CompletedTask;
        }

        public Task RestartAsync(CancellationToken ct)
        {
            RestartCount++;
            bool succeed = RestartOutcomes.Count == 0 || RestartOutcomes.Dequeue();
            if (!succeed)
                throw new InvalidOperationException("simulated hook restart failure");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public void FirePressed(DateTimeOffset ts) => Pressed?.Invoke(this, new HotkeyEventArgs(ts));
        public void FireReleased(DateTimeOffset ts) => Released?.Invoke(this, new HotkeyEventArgs(ts));
        public void FireFaulted(string reason, Exception? ex = null) => Faulted?.Invoke(this, new HotkeyFaultEventArgs(reason, ex));
    }

    private sealed class FakeAudioCapture : IAudioCapture
    {
        public bool IsRunning { get; private set; }
        public bool ThrowOnStart { get; set; }
        public bool ThrowOnEndCapture { get; set; }
        public float[] SamplesToReturn { get; set; } = new float[16_000]; // 1s of silence by default
        public int BeginCount { get; private set; }
        public int EndCount { get; private set; }
        public int AbortCount { get; private set; }

#pragma warning disable CS0067 // required by IAudioCapture; unused by this test double
        public event EventHandler<AudioLevelEventArgs>? LevelChanged;
#pragma warning restore CS0067

        public Task StartAsync(AudioDeviceId? device, CancellationToken ct)
        {
            if (ThrowOnStart)
                throw new InvalidOperationException("simulated stream-open failure");
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void BeginCapture(TimeSpan preRoll) => BeginCount++;

        public ReadOnlyMemory<float> EndCapture()
        {
            EndCount++;
            if (ThrowOnEndCapture)
                throw new InvalidOperationException("simulated audio device lost");
            return SamplesToReturn;
        }

        public void AbortCapture() => AbortCount++;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public bool IsReady { get; set; } = true;
        public TranscriptionResult NextResult { get; set; } =
            new("hello", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), IsEmpty: false);
        public Exception? ThrowOnTranscribe { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public int TranscribeCallCount { get; private set; }
        public int DisposeCount { get; private set; }
        public ReadOnlyMemory<float> LastSamples { get; private set; }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> samples16k, CancellationToken ct)
        {
            TranscribeCallCount++;
            LastSamples = samples16k;
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, ct).ConfigureAwait(false); // honors caller's timeout ct
            if (ThrowOnTranscribe is not null)
                throw ThrowOnTranscribe;
            return NextResult;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTextInjector : ITextInjector
    {
        public object? TargetToReturn { get; set; } = "fake-foreground-window";
        public InjectionOutcome NextOutcome { get; set; } = InjectionOutcome.Injected;
        public Exception? ThrowOnInject { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public int CaptureTargetCallCount { get; private set; }
        public int InjectCallCount { get; private set; }
        public string? LastText { get; private set; }
        public object? LastTarget { get; private set; }
        public InjectionOptions? LastOptions { get; private set; }

        // Review fix (Phase 4 item 3 code review): lets a test prove SessionController's own
        // new TryResolveProcessExecutableName -> PostProcessorChain.Process(text, name) wiring,
        // not just PostProcessorChain in isolation. Defaults to null -- every pre-existing test
        // that never sets this gets the exact same "no resolved process name" behavior as
        // before this property existed (ITextInjector's own default interface method also
        // returns null, so this fake's default just matches that).
        public string? ProcessExecutableNameToReturn { get; set; }
        public int TryResolveProcessExecutableNameCallCount { get; private set; }
        public object? LastResolveTarget { get; private set; }

        public object? CaptureTarget()
        {
            CaptureTargetCallCount++;
            return TargetToReturn;
        }

        public string? TryResolveProcessExecutableName(object? target)
        {
            TryResolveProcessExecutableNameCallCount++;
            LastResolveTarget = target;
            return ProcessExecutableNameToReturn;
        }

        public async Task<InjectionOutcome> InjectAsync(string text, object? target, InjectionOptions opts, CancellationToken ct)
        {
            InjectCallCount++;
            LastText = text;
            LastTarget = target;
            LastOptions = opts;
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, ct).ConfigureAwait(false);
            if (ThrowOnInject is not null)
                throw ThrowOnInject;
            return NextOutcome;
        }
    }

    // ── Test wiring helpers ─────────────────────────────────────────────────────────

    private static SessionControllerOptions DefaultOptions(
        int minDurationMs = 50,
        int maxDurationMs = 100_000,
        int asrTimeoutMs = 5_000,
        int maxHookRestartAttempts = 5,
        TimeSpan? hookRestartInitialBackoff = null) => new(
        new HotkeyBinding("RightControl", true),
        new InjectionOptions(
            Soneto.Core.Abstractions.InjectionMethod.ClipboardPaste, "ctrl+v", TimeSpan.Zero, TimeSpan.Zero,
            RestoreClipboard: true, TriggerKey: "RightControl"),
        MinDurationMs: minDurationMs,
        MaxDurationMs: maxDurationMs,
        LongUtteranceCueMs: 15_000,
        AsrTimeoutMs: asrTimeoutMs,
        MaxHookRestartAttempts: maxHookRestartAttempts,
        HookRestartInitialBackoff: hookRestartInitialBackoff);

    /// <summary>VAD disabled -- passes audio through untrimmed, never discards. Used by every
    /// test that isn't specifically exercising the VAD-discard row (see class doc comment).</summary>
    private static SileroVadDetector PassthroughVad() =>
        new(NullLogger<SileroVadDetector>.Instance, new VadConfig { Enabled = false });

    private static SessionController CreateController(
        FakeHotkeySource hotkey,
        FakeAudioCapture capture,
        FakeTranscriber transcriber,
        FakeTextInjector injector,
        SileroVadDetector? vad = null,
        PostProcessorChain? chain = null,
        SessionControllerOptions? options = null)
    {
        var captureController = new CaptureModeController(
            capture, NullLogger<CaptureModeController>.Instance, CaptureMode.OnDemand,
            idleCloseMs: 1000, preRollMs: 0);

        return new SessionController(
            hotkey,
            captureController,
            vad ?? PassthroughVad(),
            transcriber,
            chain ?? new PostProcessorChain(Array.Empty<IPostProcessor>()),
            injector,
            options ?? DefaultOptions(),
            NullLogger<SessionController>.Instance);
    }

    /// <summary>
    /// Race-free wait for <paramref name="target"/>: subscribes to <see cref="SessionController.StateChanged"/>
    /// BEFORE the caller triggers whatever should cause the transition, closing the
    /// check-then-fire gap a naive poll-after-firing approach would have.
    /// </summary>
    private static async Task<bool> WaitForStateAsync(SessionController controller, SessionState target, int timeoutMs = 5000)
    {
        if (controller.State == target) return true;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<SessionStateChangedEventArgs> handler = (_, e) =>
        {
            if (e.To == target) tcs.TrySetResult(true);
        };
        controller.StateChanged += handler;
        try
        {
            if (controller.State == target) return true; // closes the subscribe race
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            return completed == tcs.Task && tcs.Task.Result;
        }
        finally
        {
            controller.StateChanged -= handler;
        }
    }

    // ── Initializing → Idle / Faulted ───────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ModelLoadedAndHookStarted_TransitionsToIdle()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        await using var controller = CreateController(hotkey, capture, transcriber, injector);

        await controller.StartAsync();

        Assert.Equal(SessionState.Idle, controller.State);
        Assert.Equal(1, hotkey.StartCount);
    }

    [Fact]
    public async Task StartAsync_TranscriberNotInitialized_TransitionsToFaulted()
    {
        // "Model load failed" row: the caller is responsible for InitializeAsync completing
        // before StartAsync (see class doc comment) -- IsReady=false at StartAsync time is
        // what satisfies that row.
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = false };
        var injector = new FakeTextInjector();
        await using var controller = CreateController(hotkey, capture, transcriber, injector);

        await controller.StartAsync();

        Assert.Equal(SessionState.Faulted, controller.State);
        Assert.Equal(0, hotkey.StartCount); // never even attempted to start the hook
    }

    [Fact]
    public async Task StartAsync_HotkeySourceFailsToStart_TransitionsToFaulted()
    {
        var hotkey = new FakeHotkeySource { ThrowOnStart = true };
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        await using var controller = CreateController(hotkey, capture, transcriber, injector);

        await controller.StartAsync();

        Assert.Equal(SessionState.Faulted, controller.State);
    }

    // ── Idle → Recording (and the !IsReady / edge case 5 guard) ────────────────────

    [Fact]
    public async Task Idle_KeyDown_Ready_CapturesTargetAndBeginsCapture_TransitionsToRecording()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        await using var controller = CreateController(hotkey, capture, transcriber, injector);
        await controller.StartAsync();

        var wait = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await wait);

        Assert.Equal(1, injector.CaptureTargetCallCount);
        Assert.Equal(1, capture.BeginCount);
    }

    [Fact]
    public async Task Idle_KeyDown_NotReady_StaysIdle_EdgeCase5()
    {
        // Edge case 5 (model still warming): guarded on ITranscriber.IsReady at every key-down,
        // not just at StartAsync -- flip IsReady false again after a successful start to reach
        // this branch (StartAsync itself requires IsReady=true, see class doc comment).
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        await using var controller = CreateController(hotkey, capture, transcriber, injector);
        await controller.StartAsync();
        transcriber.IsReady = false;

        hotkey.FirePressed(DateTimeOffset.UtcNow);
        await Task.Delay(200); // give the worker a chance to (not) act

        Assert.Equal(SessionState.Idle, controller.State);
        Assert.Equal(0, capture.BeginCount);
    }

    // ── Edge case 1: key-down while not Idle is ignored, not queued ────────────────

    [Fact]
    public async Task KeyDown_WhileRecording_IsIgnored_EdgeCase1()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        await using var controller = CreateController(hotkey, capture, transcriber, injector);
        await controller.StartAsync();

        var wait = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await wait);

        hotkey.FirePressed(DateTimeOffset.UtcNow); // second key-down while already Recording
        await Task.Delay(200);

        Assert.Equal(SessionState.Recording, controller.State);
        Assert.Equal(1, capture.BeginCount); // no second capture started
    }

    // ── Recording → Finalizing / Cooldown / Faulted ─────────────────────────────────

    [Fact]
    public async Task Recording_KeyUp_BelowMinDuration_AbortsAndEntersCooldown()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 5000); // large, so a same-instant press/release is "too short"
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var ts = DateTimeOffset.UtcNow;
        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(ts);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(ts); // same timestamp -> elapsed == 0 < 5000ms
        Assert.True(await idle);

        Assert.Equal(1, capture.AbortCount);
        Assert.Equal(0, capture.EndCount);
        Assert.Equal(0, transcriber.TranscribeCallCount);
    }

    [Fact]
    public async Task Recording_KeyUp_AtOrAboveMinDuration_EndsCaptureAndProceedsToFinalizing()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var seen = new List<SessionState>();
        controller.StateChanged += (_, e) => seen.Add(e.To);

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Equal(1, capture.EndCount);
        Assert.Equal(0, capture.AbortCount);
        // Full happy-path sequence proves Finalizing -> Transcribing -> Injecting -> Cooldown all fired in order.
        Assert.Equal(
            [SessionState.Recording, SessionState.Finalizing, SessionState.Transcribing,
             SessionState.Injecting, SessionState.Cooldown, SessionState.Idle],
            seen);
    }

    [Fact]
    public async Task Recording_MaxDurationTimerFires_ForceFinalizes_EdgeCase3()
    {
        // Edge case 3 (key stuck down) IS this row -- deliberately no separate test exists,
        // per the work item's own instruction not to build it twice.
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0, maxDurationMs: 50);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        // Subscribe for Idle only now that we're actually Recording -- otherwise the wait
        // would resolve instantly against the pre-press Idle state instead of the real cycle.
        var idle = WaitForStateAsync(controller, SessionState.Idle, timeoutMs: 5000);
        // Deliberately never fire Released -- the timer alone must force-finalize.
        Assert.True(await idle);

        Assert.Equal(1, capture.EndCount);
        Assert.Equal(0, capture.AbortCount);
        Assert.Equal(1, transcriber.TranscribeCallCount);
    }

    [Fact]
    public async Task Recording_AudioDeviceLost_AbortsAndAutoRecoversToIdle_NotFaulted()
    {
        // Item 10: reconciles plan §1.12's "audio device lost -> auto" row over item 9's
        // literal §1.4 "-> Faulted" reading -- see SessionController's class doc comment and
        // FinalizeRecordingAsync's catch block. This replaces the old
        // Recording_AudioDeviceLost_AbortsAndTransitionsToFaulted test's assertion.
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture { ThrowOnEndCapture = true };
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle, timeoutMs: 3000);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Equal(1, capture.AbortCount);
        Assert.Equal(SessionState.Idle, controller.State); // NOT Faulted -- genuine auto-recovery, not a crash-avoidance stub
    }

    [Fact]
    public async Task Recording_AudioDeviceLost_SubsequentKeyDown_StartsAFreshRecordingNormally()
    {
        // Proves genuine recovery, not just "didn't crash": after the device-lost auto-recovery
        // above lands back on Idle, the daemon must still be able to start a brand-new recording
        // on the next key-down (the real device-lost scenario relies on
        // CaptureDeviceResolver re-resolving/falling back to default on this next StartAsync
        // call -- see class doc comment; this test uses the fake capture, which doesn't itself
        // model device resolution, but proves SessionController's own state machine genuinely
        // re-arms rather than latching into some other non-recording state).
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture { ThrowOnEndCapture = true };
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle, timeoutMs: 3000);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        // Device "comes back" -- the next recording should succeed normally end to end.
        capture.ThrowOnEndCapture = false;
        var recordingAgain = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recordingAgain);

        var idleAgain = WaitForStateAsync(controller, SessionState.Idle, timeoutMs: 3000);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idleAgain);

        Assert.Equal(2, capture.BeginCount);
        Assert.Equal(1, injector.InjectCallCount); // the second, successful recording made it all the way to injection
    }

    // ── Finalizing → Transcribing / Cooldown ────────────────────────────────────────

    [Fact]
    public async Task Finalizing_VadDiscardsSilence_TransitionsToCooldownThenIdle_WithoutTranscribing()
    {
        // Real, enabled Silero VAD (see class doc comment: this is the one test that uses it)
        // fed pure silence -- guaranteed to detect no speech and discard.
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture { SamplesToReturn = new float[16_000] }; // 1s silence
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0);
        var vad = new SileroVadDetector(NullLogger<SileroVadDetector>.Instance, new VadConfig());
        await using var controller = CreateController(hotkey, capture, transcriber, injector, vad: vad, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle, timeoutMs: 10_000);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Equal(0, transcriber.TranscribeCallCount);
        Assert.Equal(0, injector.InjectCallCount);
    }

    // ── Transcribing → Injecting / Cooldown ─────────────────────────────────────────

    [Fact]
    public async Task Transcribing_NonEmptyResult_RunsPostProcessorChain_ThenInjects()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber
        {
            IsReady = true,
            NextResult = new("hello world", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), IsEmpty: false),
        };
        var injector = new FakeTextInjector();
        var chain = new PostProcessorChain([new TrailingSpaceProcessor()]); // appends a trailing space
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, chain: chain, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Equal(1, injector.InjectCallCount);
        Assert.Equal("hello world ", injector.LastText); // proves the chain actually ran
    }

    /// <summary>
    /// Review fix (Phase 4 item 3 code review): the test above proves the base chain runs, but
    /// with a fake that always resolves a null process name -- indistinguishable from
    /// <see cref="ITextInjector"/>'s own pre-Phase-4-item-3 default. This test proves the actual
    /// NEW wiring at the <see cref="SessionController"/> call site itself: a fake
    /// <see cref="ITextInjector"/> resolving a real, non-null process name (<c>"wt.exe"</c>),
    /// fed into a <see cref="PostProcessorChain"/> built with a per-app table that enables
    /// <see cref="Soneto.Core.Dictionary.PerAppOverride.AutoCapitalize"/> for that exact process
    /// -- and asserts the FINAL INJECTED TEXT reflects the widened (capitalizing) processor set,
    /// not just that <see cref="PostProcessorChain"/> selects it correctly in isolation (already
    /// covered by <c>PostProcessorChainTests</c>).
    /// </summary>
    [Fact]
    public async Task Transcribing_ResolvedProcessName_SelectsPerAppWidenedChain_ReflectedInInjectedText()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber
        {
            IsReady = true,
            NextResult = new("hello world", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), IsEmpty: false),
        };
        var injector = new FakeTextInjector { ProcessExecutableNameToReturn = "wt.exe" };
        var perApp = new Dictionary<string, Soneto.Core.Dictionary.PerAppOverride>
        {
            ["wt.exe"] = new()
            {
                Id = "test.wt",
                ProcessName = "wt.exe",
                AutoCapitalize = true,
                TrailingPunctuation = false,
            },
        };
        var chain = new PostProcessorChain(Array.Empty<IPostProcessor>(), perApp);
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, chain: chain, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Equal(1, injector.InjectCallCount);
        // Proves the wiring end-to-end: the captured target was handed to
        // TryResolveProcessExecutableName, the resolved "wt.exe" name selected the
        // AutoCapitalize-widened chain, and the actual injected text is capitalized -- not just
        // that PostProcessorChain would select the right list given a name by itself.
        Assert.True(injector.TryResolveProcessExecutableNameCallCount >= 1);
        Assert.Equal("Hello world", injector.LastText);
    }

    [Fact]
    public async Task Transcribing_EmptyResult_TransitionsToCooldownThenIdle_WithoutInjecting()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber
        {
            IsReady = true,
            NextResult = new("", TimeSpan.Zero, TimeSpan.Zero, IsEmpty: true),
        };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Equal(0, injector.InjectCallCount);
    }

    [Fact]
    public async Task Transcribing_ThrowsException_TransitionsToCooldownThenIdle()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true, ThrowOnTranscribe = new InvalidOperationException("boom") };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Equal(0, injector.InjectCallCount);
    }

    [Fact]
    public async Task Transcribing_TimesOut_TransitionsToCooldownThenIdle()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true, Delay = TimeSpan.FromSeconds(5) };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0, asrTimeoutMs: 50);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle, timeoutMs: 3000);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Equal(0, injector.InjectCallCount);
    }

    // ── Injecting → Cooldown ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Injecting_Injected_TransitionsToCooldownThenIdle()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector { NextOutcome = InjectionOutcome.Injected };
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Equal(1, injector.InjectCallCount);
    }

    [Fact]
    public async Task Injecting_NotInjected_StillTransitionsToCooldownThenIdle()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector { NextOutcome = InjectionOutcome.TargetLost };
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Equal(1, injector.InjectCallCount); // still attempted; failure alone doesn't fault the session
    }

    // ── Cooldown → Idle ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cooldown_WaitsAtLeast150MsBeforeReturningToIdle()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        // Force the fast "too short" abort path straight into Cooldown, avoiding the full
        // capture/VAD/ASR/inject pipeline's own variable timing.
        var options = DefaultOptions(minDurationMs: 5000);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        DateTime? cooldownAt = null, idleAt = null;
        controller.StateChanged += (_, e) =>
        {
            if (e.To == SessionState.Cooldown) cooldownAt = DateTime.UtcNow;
            if (e.To == SessionState.Idle && cooldownAt is not null) idleAt = DateTime.UtcNow;
        };

        var ts = DateTimeOffset.UtcNow;
        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(ts);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(ts); // elapsed == 0 -> too short -> Cooldown
        Assert.True(await idle);

        Assert.NotNull(cooldownAt);
        Assert.NotNull(idleAt);
        Assert.True((idleAt!.Value - cooldownAt!.Value).TotalMilliseconds >= 130, // small slack for scheduler jitter
            $"Expected >= ~150ms in Cooldown, observed {(idleAt.Value - cooldownAt.Value).TotalMilliseconds:F0}ms.");
    }

    // ── any → hook faulted (watchdog) ───────────────────────────────────────────────

    [Fact]
    public async Task HookFaulted_DuringRecording_AbortsOrphanedCaptureThenRecoversToIdle()
    {
        var hotkey = new FakeHotkeySource();
        hotkey.RestartOutcomes.Enqueue(true);
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(maxHookRestartAttempts: 3, hookRestartInitialBackoff: TimeSpan.FromMilliseconds(5));
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle, timeoutMs: 3000);
        hotkey.FireFaulted("simulated hook death");
        Assert.True(await idle);

        Assert.Equal(1, capture.AbortCount); // orphaned recording was aborted, not left hanging
        Assert.Equal(1, hotkey.RestartCount);
    }

    [Fact]
    public async Task HookFaulted_WhileIdle_RecoversToIdleWithoutTouchingCapture()
    {
        var hotkey = new FakeHotkeySource();
        hotkey.RestartOutcomes.Enqueue(true);
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(maxHookRestartAttempts: 3, hookRestartInitialBackoff: TimeSpan.FromMilliseconds(5));
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();
        Assert.Equal(SessionState.Idle, controller.State);

        // Recovery lands back on Idle whether or not the session was already Idle -- observe via
        // RestartCount instead of a state-change wait (State never actually leaves Idle here).
        hotkey.FireFaulted("simulated hook death");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (hotkey.RestartCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(1, hotkey.RestartCount);
        Assert.Equal(0, capture.AbortCount);
        Assert.Equal(SessionState.Idle, controller.State);
    }

    [Fact]
    public async Task HookFaulted_AllRestartAttemptsFail_TransitionsToFaultedAndStaysRunning()
    {
        var hotkey = new FakeHotkeySource();
        hotkey.RestartOutcomes.Enqueue(false);
        hotkey.RestartOutcomes.Enqueue(false);
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(maxHookRestartAttempts: 2, hookRestartInitialBackoff: TimeSpan.FromMilliseconds(5));
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var faulted = WaitForStateAsync(controller, SessionState.Faulted, timeoutMs: 3000);
        hotkey.FireFaulted("simulated permanent hook death");
        Assert.True(await faulted);

        Assert.Equal(2, hotkey.RestartCount);
        // "Faulted" means this session can't recover automatically, not that the process exits
        // (plan §1.12) -- nothing in this class's own state throws/crashes past this point;
        // the daemon process itself staying alive is Program.cs's concern, verified separately.
        Assert.Equal(SessionState.Faulted, controller.State);
    }

    // ── Edge case 2 (focus changed during transcription): already handled by ITextInjector ──

    [Fact]
    public async Task CapturedTarget_IsPassedThroughToInjectorUnchanged_EdgeCase2()
    {
        // Per class doc comment: WindowsTextInjector re-resolves the CURRENT foreground window
        // itself and only uses the captured target as a fallback/log value -- SessionController's
        // only contract is to pass the key-down-captured target through unchanged, which this
        // proves at the SessionController level, independent of the Windows injector's own policy.
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector { TargetToReturn = "captured-at-keydown" };
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Equal("captured-at-keydown", injector.LastTarget);
    }

    // ── Edge case 4 (trigger key held during injection): already handled two layers down ──

    [Fact]
    public async Task InjectionOptions_TriggerKey_IsPassedThroughToInjectorUnchanged_EdgeCase4()
    {
        // Per class doc comment: WindowsHotkeySource's IsEventSimulated check and
        // ModifierSanitizer's trigger-exclusion already fully resolve this at lower layers.
        // SessionController's only contract is to forward the configured InjectionOptions
        // (carrying TriggerKey) through unchanged -- proven here.
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Equal("RightControl", injector.LastOptions!.TriggerKey);
    }

    // ── Edge case 6: very long transcript is capped before injection ───────────────

    [Fact]
    public async Task VeryLongTranscript_TruncatedTo20000CharsBeforeInjection_EdgeCase6()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber
        {
            IsReady = true,
            NextResult = new(new string('a', 25_000), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), IsEmpty: false),
        };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Equal(SessionController.TranscriptCharCap, injector.LastText!.Length);
    }

    // ── Lifecycle / disposal ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_DisposesOwnedComponents_AndIsIdempotent()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var controller = CreateController(hotkey, capture, transcriber, injector);
        await controller.StartAsync();

        await controller.DisposeAsync();
        Assert.Equal(1, hotkey.DisposeCount);
        Assert.Equal(1, transcriber.DisposeCount);

        await controller.DisposeAsync(); // idempotent -- must not throw or double-dispose
        Assert.Equal(1, hotkey.DisposeCount);
        Assert.Equal(1, transcriber.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_AfterDisposal_KeyEventsAreNoLongerProcessed()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var controller = CreateController(hotkey, capture, transcriber, injector);
        await controller.StartAsync();
        await controller.DisposeAsync();

        // Handlers were unsubscribed in DisposeAsync -- firing Pressed after disposal must be a
        // silent no-op, not a crash or a resurrected session.
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        await Task.Delay(200);

        Assert.Equal(0, capture.BeginCount);
    }

    [Fact]
    public async Task DisposeAsync_WhileMidTranscription_CompletesWithoutHanging()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true, Delay = TimeSpan.FromMilliseconds(300) };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0, asrTimeoutMs: 5000);
        var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var transcribing = WaitForStateAsync(controller, SessionState.Transcribing);
        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await transcribing);

        // Bounded by the in-flight transcription naturally finishing (up to its own 300ms delay
        // or the 5s ASR timeout, whichever first) -- not instant cancellation of in-flight decode
        // work, which this class deliberately does not attempt (see DisposeAsync's doc comment).
        var disposeTask = controller.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(3000));
        Assert.Same(disposeTask, completed);
    }

    // ── Coordinator-flagged coverage gaps (post-review, closed before code review) ─────

    [Fact]
    public async Task HookFaulted_WhileTranscribing_QueuesBehindInFlightWorkAndRecoversAfterward()
    {
        // The fault command must queue behind whatever the worker is currently processing
        // (it can only be dequeued once RunTranscribingAsync/RunInjectingAsync/EnterCooldownAsync
        // have all fully run for THIS command) -- not interrupt or corrupt the in-flight
        // transcription/injection, and not cause a double-transition. See class doc comment's
        // threading model paragraph for the reasoning this test is verifying empirically.
        var hotkey = new FakeHotkeySource();
        hotkey.RestartOutcomes.Enqueue(true);
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true, Delay = TimeSpan.FromMilliseconds(300) };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(
            minDurationMs: 0, asrTimeoutMs: 5000, maxHookRestartAttempts: 3,
            hookRestartInitialBackoff: TimeSpan.FromMilliseconds(5));
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var seen = new List<SessionState>();
        controller.StateChanged += (_, e) => seen.Add(e.To);

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var transcribing = WaitForStateAsync(controller, SessionState.Transcribing);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await transcribing);

        // Fire the fault while genuinely mid-Transcribing (the transcriber's 300ms delay is
        // still in flight) -- this must queue behind the in-flight command, not preempt it.
        hotkey.FireFaulted("simulated hook death mid-transcription");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((controller.State != SessionState.Idle || hotkey.RestartCount == 0) && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(SessionState.Idle, controller.State);
        Assert.Equal(1, transcriber.TranscribeCallCount);
        Assert.Equal(1, injector.InjectCallCount); // in-flight result was NOT lost/corrupted by the fault
        Assert.Equal(1, hotkey.RestartCount); // the queued fault WAS eventually processed, not dropped
        Assert.Equal(0, capture.AbortCount); // no longer Recording by the time the fault was actually handled
        Assert.Equal(
            [SessionState.Recording, SessionState.Finalizing, SessionState.Transcribing,
             SessionState.Injecting, SessionState.Cooldown, SessionState.Idle],
            seen); // exactly one clean pass through the pipeline -- no extra/duplicate transitions
    }

    [Fact]
    public async Task HookFaulted_WhileInjecting_QueuesBehindInFlightWorkAndRecoversAfterward()
    {
        var hotkey = new FakeHotkeySource();
        hotkey.RestartOutcomes.Enqueue(true);
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector { Delay = TimeSpan.FromMilliseconds(300) };
        var options = DefaultOptions(
            minDurationMs: 0, maxHookRestartAttempts: 3, hookRestartInitialBackoff: TimeSpan.FromMilliseconds(5));
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var seen = new List<SessionState>();
        controller.StateChanged += (_, e) => seen.Add(e.To);

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var injecting = WaitForStateAsync(controller, SessionState.Injecting);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await injecting);

        // Fire the fault while genuinely mid-Injecting (the injector's 300ms delay is still in
        // flight) -- must queue behind it, not preempt/corrupt the in-flight injection.
        hotkey.FireFaulted("simulated hook death mid-injection");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((controller.State != SessionState.Idle || hotkey.RestartCount == 0) && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(SessionState.Idle, controller.State);
        Assert.Equal(1, injector.InjectCallCount); // the in-flight injection completed, not aborted mid-flight
        Assert.Equal(1, hotkey.RestartCount);
        Assert.Equal(0, capture.AbortCount);
        Assert.Equal(
            [SessionState.Recording, SessionState.Finalizing, SessionState.Transcribing,
             SessionState.Injecting, SessionState.Cooldown, SessionState.Idle],
            seen);
    }

    [Fact]
    public async Task DisposeAsync_WhileGenuinelyRecording_CompletesPromptlyAndClosesTheStream()
    {
        // Distinct from DisposeAsync_WhileMidTranscription_CompletesWithoutHanging above: this
        // disposes while sitting in Recording waiting for a key-up/max-duration timer that never
        // comes (Released is deliberately never fired), not mid-async-pipeline-work.
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0, maxDurationMs: 100_000); // won't force-finalize mid-test
        var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);
        Assert.True(capture.IsRunning); // genuinely mid-flight capture, not a fabricated setup

        var disposeTask = controller.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(3000));
        Assert.Same(disposeTask, completed); // doesn't hang waiting on a key-up/timer that never arrives

        // CaptureModeController.DisposeAsync() -> CloseStreamAsync() closes the stream
        // unconditionally whenever it's running (checked via IsRunning, not via whether an
        // utterance was ever formally ended) -- confirm that actually happened, not just that
        // DisposeAsync itself returned.
        Assert.False(capture.IsRunning);
    }

    [Fact]
    public async Task Finalizing_RealEnabledVad_KeepsGenuineSpeech_RoutesToTranscribing()
    {
        // Every other VAD-adjacent test in this file uses VadConfig.Enabled=false (a hardcoded
        // pass-through where ShouldDiscard is always false) -- this is the one test that
        // exercises the REAL Trim-then-route-to-Transcribing decision in RunFinalizingAsync
        // through a genuinely ENABLED Silero VAD. Real speech, not a synthetic tone: an earlier
        // item found Silero correctly REJECTS a synthetic sine tone as non-speech (see
        // SileroVadDetectorTests' own doc comment), so a tone is not a safe stand-in here.
        // Reuses this project's existing TestAssets/en-16k.wav real-speech clip (already
        // copied to the test output directory for SileroVadDetectorTests) rather than needing
        // a live mic or a new asset.
        string clipPath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "en-16k.wav");
        Assert.True(File.Exists(clipPath), $"Test clip not found: {clipPath}");
        var wav = WavReader.Read(clipPath);
        Assert.Equal(16_000, wav.SampleRate);
        float[] speechSamples = wav.Samples.ToArray();

        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture { SamplesToReturn = speechSamples };
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0);
        var vad = new SileroVadDetector(NullLogger<SileroVadDetector>.Instance, new VadConfig()); // real, enabled
        await using var controller = CreateController(hotkey, capture, transcriber, injector, vad: vad, options: options);
        await controller.StartAsync();

        var seen = new List<SessionState>();
        controller.StateChanged += (_, e) => seen.Add(e.To);

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle, timeoutMs: 10_000);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        // Genuine speech well over the 300ms discard floor must have been kept and routed to
        // Transcribing/Injecting by the REAL VAD trim decision -- not discarded.
        Assert.Contains(SessionState.Transcribing, seen);
        Assert.Equal(1, transcriber.TranscribeCallCount);
        Assert.Equal(1, injector.InjectCallCount);

        // The samples actually handed to the transcriber must be the VAD-TRIMMED subset, not
        // the raw untrimmed capture buffer -- proves real trimming happened, not a disguised
        // pass-through.
        Assert.True(
            transcriber.LastSamples.Length < speechSamples.Length,
            $"Expected VAD-trimmed samples shorter than the raw {speechSamples.Length}-sample "
            + $"capture, got {transcriber.LastSamples.Length}.");
        Assert.True(transcriber.LastSamples.Length > 0);
    }

    // ── DictationCompleted (Phase 3 item 2, §3.6) ───────────────────────────────────

    [Fact]
    public async Task DictationCompleted_SuccessfulInjection_FiresExactlyOnceWithCorrectData()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber
        {
            IsReady = true,
            // FillerWordStripper below turns this into "hello world" (strips "um"), giving a
            // genuinely non-trivial RawText != FinalText + a non-empty RulesFired list.
            NextResult = new("hello um world", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), IsEmpty: false),
        };
        var injector = new FakeTextInjector { NextOutcome = InjectionOutcome.Injected };
        var chain = new PostProcessorChain([new FillerWordStripper()]);
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, chain: chain, options: options);
        await controller.StartAsync();

        var events = new List<DictationCompletedEventArgs>();
        controller.DictationCompleted += (_, e) => events.Add(e);

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        // A real (not just simulated-timestamp) delay between key-down and key-up so
        // RecordingDuration (measured off real wall-clock time -- see FinalizeRecordingAsync)
        // reflects an actual, controllable recording window.
        await Task.Delay(200);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow);
        Assert.True(await idle);

        var completed = Assert.Single(events);
        Assert.Equal("hello um world", completed.RawText);
        Assert.Equal("hello world", completed.FinalText);
        Assert.Single(completed.RulesFired);
        Assert.True(completed.WasInjected);
        Assert.True(completed.RecordingDuration >= TimeSpan.FromMilliseconds(150),
            $"Expected RecordingDuration to reflect the ~200ms real recording window, got {completed.RecordingDuration}.");
        Assert.True(completed.ProcessingLatency >= TimeSpan.Zero);
        // Item 10 (§3.14): AudioSamples carries the exact VAD-trimmed buffer actually
        // transcribed -- non-empty here since this dictation reached RunInjectingAsync at all
        // (a genuinely VAD-discarded buffer never reaches this event, see the
        // DictationCompleted_DoesNotFire_VadDiscardedSilence test below).
        Assert.True(completed.AudioSamples.Length > 0);
    }

    [Fact]
    public async Task DictationCompleted_ProcessingLatency_MeasuresFromEndOfRecording_IndependentlyOfRecordingDuration()
    {
        // The success test above only asserts ProcessingLatency >= TimeSpan.Zero, which would
        // pass even if the field were accidentally swapped with RecordingDuration, measured from
        // the wrong starting point, or hardcoded to zero. This test uses a deliberately LARGE,
        // ASYMMETRIC recording duration (~1000ms) against a much smaller injection delay
        // (~150ms), with both a lower AND an upper bound on ProcessingLatency, specifically so a
        // hypothetically-buggy "ProcessingLatency = RecordingDuration + postRecordingElapsed"
        // implementation (which would land around ~1150ms here) is clearly distinguishable from
        // -- and would violate the upper bound of -- a genuinely independent measurement that
        // starts fresh at end-of-recording (which should land around ~150-300ms). A smaller/more
        // symmetric pair of numbers (e.g. 50ms recording / 300ms delay, as an earlier draft of
        // this test used) can't tell the two hypotheses apart, since both land in the same
        // ballpark when the recording duration is small relative to the delay.
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber
        {
            IsReady = true,
            NextResult = new("hello", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), IsEmpty: false),
        };
        var injector = new FakeTextInjector
        {
            NextOutcome = InjectionOutcome.Injected,
            Delay = TimeSpan.FromMilliseconds(150),
        };
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var events = new List<DictationCompletedEventArgs>();
        controller.DictationCompleted += (_, e) => events.Add(e);

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        await Task.Delay(1000); // long, asymmetric recording window

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow);
        Assert.True(await idle);

        var completed = Assert.Single(events);
        Assert.True(completed.RecordingDuration >= TimeSpan.FromMilliseconds(900),
            $"Expected RecordingDuration to reflect the ~1000ms real recording window, got {completed.RecordingDuration}.");
        Assert.True(completed.ProcessingLatency >= TimeSpan.FromMilliseconds(100),
            $"Expected ProcessingLatency to reflect the ~150ms post-recording injection delay, got {completed.ProcessingLatency}.");
        Assert.True(completed.ProcessingLatency < TimeSpan.FromMilliseconds(600),
            "ProcessingLatency must be measured independently from end-of-recording, not additively include " +
            $"RecordingDuration (an additive bug would land around ~1150ms here) -- got {completed.ProcessingLatency}.");
    }

    [Fact]
    public async Task DictationCompleted_ThrowingSubscriber_StillReachesIdle_NotStrandedInInjecting()
    {
        // Blocking bug found by code review: RaiseDictationCompleted is called from
        // RunInjectingAsync immediately BEFORE its mandatory EnterCooldownAsync() call -- the
        // only path back to Idle. Before the fix, a throwing subscriber would unwind straight
        // past EnterCooldownAsync with no other catch until RunWorkerLoopAsync's own outer
        // per-command catch, which logs and moves on without ever restoring State -- stranding
        // the session in Injecting forever (every subsequent key-down silently ignored). This
        // test proves a throwing DictationCompleted handler can no longer do that.
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector { NextOutcome = InjectionOutcome.Injected };
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        controller.DictationCompleted += (_, _) => throw new InvalidOperationException("boom");

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow);
        Assert.True(await idle, "Session must still reach Idle even when a DictationCompleted subscriber throws.");

        // Confirm the session is genuinely usable afterward, not just transiently visiting Idle.
        var secondRecording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await secondRecording, "A subsequent key-down must still start a new recording.");
    }

    [Fact]
    public async Task DictationCompleted_InjectionOutcomeNotInjected_FiresWithWasInjectedFalse()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber
        {
            IsReady = true,
            NextResult = new("hello world", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), IsEmpty: false),
        };
        var injector = new FakeTextInjector { NextOutcome = InjectionOutcome.TargetLost };
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var events = new List<DictationCompletedEventArgs>();
        controller.DictationCompleted += (_, e) => events.Add(e);

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        var completed = Assert.Single(events);
        Assert.Equal("hello world", completed.RawText);
        Assert.Equal("hello world", completed.FinalText);
        Assert.False(completed.WasInjected);
    }

    [Fact]
    public async Task DictationCompleted_InjectionThrows_FiresWithWasInjectedFalse()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber
        {
            IsReady = true,
            NextResult = new("hello world", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), IsEmpty: false),
        };
        var injector = new FakeTextInjector { ThrowOnInject = new InvalidOperationException("simulated injection failure") };
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var events = new List<DictationCompletedEventArgs>();
        controller.DictationCompleted += (_, e) => events.Add(e);

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        var completed = Assert.Single(events);
        Assert.Equal("hello world", completed.RawText);
        Assert.Equal("hello world", completed.FinalText);
        Assert.False(completed.WasInjected);
    }

    [Fact]
    public async Task DictationCompleted_DoesNotFire_KeyUpBelowMinDuration()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 5000);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var events = new List<DictationCompletedEventArgs>();
        controller.DictationCompleted += (_, e) => events.Add(e);

        var ts = DateTimeOffset.UtcNow;
        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(ts);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(ts); // elapsed == 0 < 5000ms -> discard
        Assert.True(await idle);

        Assert.Empty(events);
    }

    [Fact]
    public async Task DictationCompleted_DoesNotFire_VadDiscardedSilence()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture { SamplesToReturn = new float[16_000] }; // 1s silence
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0);
        var vad = new SileroVadDetector(NullLogger<SileroVadDetector>.Instance, new VadConfig());
        await using var controller = CreateController(hotkey, capture, transcriber, injector, vad: vad, options: options);
        await controller.StartAsync();

        var events = new List<DictationCompletedEventArgs>();
        controller.DictationCompleted += (_, e) => events.Add(e);

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle, timeoutMs: 10_000);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Empty(events);
    }

    [Fact]
    public async Task DictationCompleted_DoesNotFire_EmptyTranscriptionResult()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber
        {
            IsReady = true,
            NextResult = new("", TimeSpan.Zero, TimeSpan.Zero, IsEmpty: true),
        };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var events = new List<DictationCompletedEventArgs>();
        controller.DictationCompleted += (_, e) => events.Add(e);

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Empty(events);
    }

    [Fact]
    public async Task DictationCompleted_DoesNotFire_TranscriptionThrows()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true, ThrowOnTranscribe = new InvalidOperationException("boom") };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var events = new List<DictationCompletedEventArgs>();
        controller.DictationCompleted += (_, e) => events.Add(e);

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Empty(events);
    }

    [Fact]
    public async Task DictationCompleted_DoesNotFire_TranscriptionTimesOut()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture();
        var transcriber = new FakeTranscriber { IsReady = true, Delay = TimeSpan.FromSeconds(5) };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0, asrTimeoutMs: 50);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var events = new List<DictationCompletedEventArgs>();
        controller.DictationCompleted += (_, e) => events.Add(e);

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle, timeoutMs: 3000);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Empty(events);
    }

    [Fact]
    public async Task DictationCompleted_DoesNotFire_AudioDeviceLost()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new FakeAudioCapture { ThrowOnEndCapture = true };
        var transcriber = new FakeTranscriber { IsReady = true };
        var injector = new FakeTextInjector();
        var options = DefaultOptions(minDurationMs: 0);
        await using var controller = CreateController(hotkey, capture, transcriber, injector, options: options);
        await controller.StartAsync();

        var events = new List<DictationCompletedEventArgs>();
        controller.DictationCompleted += (_, e) => events.Add(e);

        var recording = WaitForStateAsync(controller, SessionState.Recording);
        hotkey.FirePressed(DateTimeOffset.UtcNow);
        Assert.True(await recording);

        var idle = WaitForStateAsync(controller, SessionState.Idle, timeoutMs: 3000);
        hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
        Assert.True(await idle);

        Assert.Empty(events);
    }
}
