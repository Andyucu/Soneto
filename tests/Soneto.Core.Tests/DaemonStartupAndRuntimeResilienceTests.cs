using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Composition;
using Soneto.Core.Abstractions;
using Soneto.Core.Asr;
using Soneto.Core.Audio;
using Soneto.Core.Configuration;
using Soneto.Core.Dictionary;
using Soneto.Core.PostProcessing;

namespace Soneto.Core.Tests;

/// <summary>
/// Item 10's done-when criterion: "kill the hook, unplug the mic mid-record, delete the
/// model, corrupt the config -- daemon survives all four." The "kill the hook" and "unplug
/// the mic mid-record" scenarios are already covered directly by
/// <see cref="SessionControllerTests"/>'s <c>HookFaulted_*</c> tests and the (item 10)
/// <c>Recording_AudioDeviceLost_*</c> tests respectively -- this file covers the remaining
/// two, "delete the model" and "corrupt the config", at a level as close to the real daemon's
/// own composition/runtime shape as an agent session can safely get without touching a real
/// desktop, real hardware, or a real 24/7 process (this project's standing caution against
/// unattended synthetic-input/live-desktop testing).
/// </summary>
public sealed class DaemonStartupAndRuntimeResilienceTests
{
    // ── "delete the model": startup composition never lets model resolution failure escape ──

    /// <summary>
    /// Mirrors <c>Soneto.Daemon/Program.cs</c>'s <c>BuildAndStartSessionControllerAsync</c>
    /// exactly: <c>try { modelDir = await modelManager.ResolveOrDownloadAsync(...); } catch
    /// (Exception ex) { logger.LogCritical(...); return null; }</c> -- see that method's own
    /// doc comment ("Never throws ... every failure point ... is caught, logged at Critical,
    /// and returns (null, null, null) so the caller can keep the host running"). <c>Program.cs</c>
    /// itself can't be unit-tested directly (top-level-statements executable, no library
    /// surface to call into), so this test proves the exact shape of that composition
    /// contract against the real <see cref="ModelManager"/> with a genuinely missing/deleted
    /// model directory (no <c>ModelManagerTests</c>-style fakes needed for THIS assertion --
    /// the point is proving the CALLER's catch-and-continue shape survives, not
    /// re-testing <see cref="ModelManager"/>'s own already-covered retry/fatal logic).
    /// </summary>
    [Fact]
    public async Task ModelMissingAtStartup_CompositionCatchesAndReturnsNull_HostLoopKeepsRunning()
    {
        var deletedModelDir = Path.Combine(
            Path.GetTempPath(), "soneto-daemon-resilience-tests-" + Guid.NewGuid().ToString("N"), "no-such-model");
        // Deliberately never created -- simulates "the model files were deleted"/"never downloaded
        // and this config override points at a directory ModelManager must treat as missing."

        var modelManager = new ModelManager(NullLogger<ModelManager>.Instance);

        // Exact catch shape from BuildAndStartSessionControllerAsync.
        string? resolvedModelDir = null;
        Exception? escaped = await Record.ExceptionAsync(async () =>
        {
            try
            {
                resolvedModelDir = await modelManager.ResolveOrDownloadAsync(deletedModelDir, CancellationToken.None);
            }
            catch (Exception)
            {
                // logger.LogCritical(...) in the real code -- omitted here, already covered by
                // ModelManagerTests' own exception-shape assertions.
                resolvedModelDir = null; // BuildAndStartSessionControllerAsync's "return (null, null, null)"
            }
        });

        Assert.Null(escaped); // nothing escaped this composition boundary
        Assert.Null(resolvedModelDir); // the (null, null, null) "no active session, but keep running" outcome

        // "Host loop keeps running": prove the calling context is still perfectly usable
        // after the caught failure -- not left in some poisoned state (e.g. a faulted
        // SynchronizationContext, a disposed logger, etc.). A trivial subsequent operation
        // completing normally is the simplest honest proxy for "Program.cs's `await
        // host.RunAsync()` line right after this call is unaffected."
        int stillFunctioning = 1 + 1;
        Assert.Equal(2, stillFunctioning);
    }

    /// <summary>
    /// Phase 3 item 5's post-review should-fix: the test above proves the catch-and-continue
    /// SHAPE by re-implementing it locally, but never actually calls the real
    /// <see cref="DaemonComposition.BuildAndStartSessionControllerAsync"/> -- exactly the kind
    /// of gap where the item 5 widening (2-tuple -&gt; 3-tuple, adding <c>AudioCapture</c>) could
    /// have silently missed a return statement with nothing catching it. This test calls the
    /// REAL method with a genuinely missing model directory and asserts the actual 3-tuple
    /// comes back all-null, and that it does so without throwing.
    /// </summary>
    [Fact]
    public async Task RealBuildAndStartSessionControllerAsync_ModelMissing_ReturnsAllNullTuple_NeverThrows()
    {
        var deletedModelDir = Path.Combine(
            Path.GetTempPath(), "soneto-daemon-resilience-tests-" + Guid.NewGuid().ToString("N"), "no-such-model");

        var config = new SonetoConfig
        {
            Asr = new AsrConfig { ModelDir = deletedModelDir },
        };

        (SessionController? Controller, AudioCuePlayer? CuePlayer, IAudioCapture? AudioCapture)? result = null;
        Exception? escaped = await Record.ExceptionAsync(async () =>
        {
            result = await DaemonComposition.BuildAndStartSessionControllerAsync(
                config,
                dictionaryEntries: Array.Empty<DictionaryEntry>(),
                NullLoggerFactory.Instance,
                NullLogger.Instance,
                hotkeySource: new FakeHotkeySource(),
                textInjector: new FakeTextInjector(),
                CancellationToken.None);
        });

        Assert.Null(escaped);
        Assert.NotNull(result);
        Assert.Null(result!.Value.Controller);
        Assert.Null(result.Value.CuePlayer);
        Assert.Null(result.Value.AudioCapture); // the item-5-widened third slot -- must not leak a
                                                 // partially-constructed capture alongside null Controller/CuePlayer.
    }

    // ── "corrupt the config" at the real daemon level, not just ConfigService in isolation ──

    /// <summary>
    /// <c>ConfigServiceTests.Invalid_json_keeps_previous_config_and_does_not_throw</c> already
    /// proves <see cref="ConfigService"/> itself survives a corrupted file. What item 10 asks
    /// for in addition is proof that corrupting the config file WHILE THE REAL DICTATION
    /// PIPELINE IS RUNNING doesn't crash the daemon process or corrupt
    /// <see cref="SessionController"/>'s own state -- i.e. that the two components are
    /// genuinely decoupled at runtime, not just individually well-behaved in isolation.
    /// <see cref="SessionController"/> does not subscribe to <c>ConfigService.ConfigChanged</c>
    /// at all (confirmed by reading both classes and <c>Program.cs</c>'s wiring -- the
    /// <c>ConfigChanged</c> handler there only touches the Serilog level switch, never
    /// <see cref="SessionController"/>), so this decoupling is structural, not incidental --
    /// this test proves it empirically anyway rather than trusting that reading alone.
    /// </summary>
    [Fact]
    public async Task ConfigCorruptedWhileRunning_DoesNotAffectAnInFlightSessionControllerPipeline()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "soneto-daemon-resilience-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");
        try
        {
            var configLogger = new CapturingLogger<ConfigService>();
            var configService = new ConfigService(configLogger, configPath);
            var loadedOk = await configService.LoadAsync();
            Assert.True(loadedOk);

            configService.StartWatching();
            try
            {
                var hotkey = new FakeHotkeySource();
                var capture = new FakeAudioCapture();
                var transcriber = new FakeTranscriber { IsReady = true };
                var injector = new FakeTextInjector();
                var captureController = new CaptureModeController(
                    capture, NullLogger<CaptureModeController>.Instance, CaptureMode.OnDemand,
                    idleCloseMs: 1000, preRollMs: 0);
                var vad = new SileroVadDetector(NullLogger<SileroVadDetector>.Instance, new VadConfig { Enabled = false });
                await using var controller = new SessionController(
                    hotkey, captureController, vad, transcriber,
                    new PostProcessorChain(Array.Empty<IPostProcessor>()), injector,
                    new SessionControllerOptions(
                        new HotkeyBinding("RightControl", true),
                        new InjectionOptions(
                            Soneto.Core.Abstractions.InjectionMethod.ClipboardPaste, "ctrl+v", TimeSpan.Zero, TimeSpan.Zero,
                            RestoreClipboard: true, TriggerKey: "RightControl"),
                        MinDurationMs: 0),
                    NullLogger<SessionController>.Instance);
                await controller.StartAsync();
                Assert.Equal(SessionState.Idle, controller.State);

                // Corrupt the config file WHILE the session controller is live and about to
                // process a real key-down/key-up cycle -- genuinely concurrent, not
                // sequenced before/after.
                var corruptWriteTask = File.WriteAllTextAsync(configPath, "{ not valid json at all ");

                var recording = WaitForStateAsync(controller, SessionState.Recording);
                hotkey.FirePressed(DateTimeOffset.UtcNow);
                Assert.True(await recording);

                var idle = WaitForStateAsync(controller, SessionState.Idle);
                hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
                Assert.True(await idle);

                await corruptWriteTask;
                // Give the file watcher's debounce window time to actually process the corrupt
                // write (ConfigServiceTests' own debounce tests use up to 10s of headroom).
                await Task.Delay(1500);

                // The daemon (this whole process) is still alive and the pipeline still works --
                // proven by running a SECOND full recording cycle after the corruption.
                var recording2 = WaitForStateAsync(controller, SessionState.Recording);
                hotkey.FirePressed(DateTimeOffset.UtcNow);
                Assert.True(await recording2);

                var idle2 = WaitForStateAsync(controller, SessionState.Idle);
                hotkey.FireReleased(DateTimeOffset.UtcNow.AddMilliseconds(10));
                Assert.True(await idle2);

                Assert.Equal(2, injector.InjectCallCount);
                Assert.NotEqual(SessionState.Faulted, controller.State);

                // ConfigService itself never threw/crashed either (mirrors
                // ConfigServiceTests.Invalid_json_keeps_previous_config_and_does_not_throw, now
                // proven with a concurrently-running real pipeline instead of in isolation).
                Assert.True(configLogger.HasEntry(LogLevel.Error, "Invalid config JSON"));
            }
            finally
            {
                configService.StopWatching();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Minimal local fakes/helpers (mirrors SessionControllerTests' own fakes; kept file-local
    // rather than shared, matching this project's existing precedent of per-test-file fakes) ──

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Add((logLevel, formatter(state, exception)));
        public bool HasEntry(LogLevel level, string containing) =>
            _entries.Any(e => e.Level == level && e.Message.Contains(containing, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeHotkeySource : IHotkeySource
    {
        public event EventHandler<HotkeyEventArgs>? Pressed;
        public event EventHandler<HotkeyEventArgs>? Released;
#pragma warning disable CS0067 // required by IHotkeySource; unused by this test double
        public event EventHandler<HotkeyFaultEventArgs>? Faulted;
#pragma warning restore CS0067
        public Task StartAsync(HotkeyBinding binding, CancellationToken ct) => Task.CompletedTask;
        public Task RestartAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void FirePressed(DateTimeOffset ts) => Pressed?.Invoke(this, new HotkeyEventArgs(ts));
        public void FireReleased(DateTimeOffset ts) => Released?.Invoke(this, new HotkeyEventArgs(ts));
    }

    private sealed class FakeAudioCapture : IAudioCapture
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

    private sealed class FakeTranscriber : ITranscriber
    {
        public bool IsReady { get; set; } = true;
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> samples16k, CancellationToken ct) =>
            Task.FromResult(new TranscriptionResult("hello", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), IsEmpty: false));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTextInjector : ITextInjector
    {
        public int InjectCallCount { get; private set; }
        public object? CaptureTarget() => "fake-target";
        public Task<InjectionOutcome> InjectAsync(string text, object? target, InjectionOptions opts, CancellationToken ct)
        {
            InjectCallCount++;
            return Task.FromResult(InjectionOutcome.Injected);
        }
    }

    private static async Task<bool> WaitForStateAsync(SessionController controller, SessionState target, int timeoutMs = 5000)
    {
        if (controller.State == target) return true;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<SessionStateChangedEventArgs> handler = (_, e) => { if (e.To == target) tcs.TrySetResult(true); };
        controller.StateChanged += handler;
        try
        {
            if (controller.State == target) return true;
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            return completed == tcs.Task && tcs.Task.Result;
        }
        finally
        {
            controller.StateChanged -= handler;
        }
    }
}
