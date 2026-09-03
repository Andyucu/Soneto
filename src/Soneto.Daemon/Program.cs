using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Context;
using Serilog.Events;
using Soneto.Core;
using Soneto.Core.Abstractions;
using Soneto.Core.Asr;
using Soneto.Core.Audio;
using Soneto.Core.Configuration;
using Soneto.Core.Dictionary;
using Soneto.Core.PostProcessing;
using Soneto.Core.Wav;

// ---- CLI flags -------------------------------------------------------------
// --verbose            raise the log level to Verbose (per-buffer audio levels / raw
//                      transcript detail land at this level once items 4c/9 exist).
// --config <path>      override the resolved config file path (mainly for manual
//                      testing / demos without touching %LOCALAPPDATA%).
// --transcribe <wav>   item 3's "done when": resolve the model, transcribe the given
//                      16kHz mono WAV file, print text + timings, then exit (no host).
// --record <seconds>   item 4b's "done when": open the default audio device on-demand,
//                      capture <seconds> of real audio, close the stream, print the
//                      negotiated rate / path / time-to-first-sample, and save a WAV.
//                      Item 5 addition: also runs VAD trim on the captured audio and
//                      reports the detected speech/silence boundaries and discard verdict.
// --capture-demo <OnDemand|WarmIdle|AlwaysOn> <utterances> [idleCloseMsOverride]
//                      item 4c's "done when": drive CaptureModeController through a
//                      sequence of simulated key-down/key-up cycles, logging stream
//                      open/close events, ready/failure cue firing, and idle-timer
//                      start/cancel/fire, so OnDemand-vs-WarmIdle behaviour can be
//                      observed directly. idleCloseMsOverride lets a demo run use a much
//                      shorter idle-close than the real 90s default (WarmIdle only).
// --vad-demo <wav>     item 5's "done when": run VAD trim on an existing 16kHz mono WAV
//                      file (no live hardware needed) and report the detected
//                      speech/silence boundaries and discard verdict.
// --vad-demo-transient item 5's "done when" (open-transient case): synthesize a short WAV
//                      containing a brief loud transient click followed by silence (the
//                      kind of driver click / DC-settling artifact plan §1.5 warns about
//                      right after a cold on-demand stream open), run VAD on it, and show
//                      it is correctly discarded rather than reaching the transcriber.
// --watch-hotkey [--seconds N]
//                      item 6's "done when": runs the real WindowsHotkeySource against the
//                      real global keyboard hook and logs every DOWN/UP with a timestamp
//                      and the held-modifier state (Shift/Alt/LeftCtrl/Win) at that moment.
//                      Runs for --seconds (default 60) or until Ctrl+C. Windows-only
//                      (compiled only into the net10.0-windows build of this project).
// --inject ["text"]    item 7's "done when": a 3-second countdown (Alt-Tab to a target app,
//                      same pattern as spikes/s4-inject-win's `countdown` mode), then
//                      injects the given text -- or, if omitted, the exact S4 test string
//                      (Romanian diacritics + punctuation) -- into whatever has focus via
//                      WindowsTextInjector, reporting the outcome and elapsed time.
//                      Windows-only (net10.0-windows build only; item 11 is the Linux
//                      injector).
// --hold-shift-ms N   item 7b manual-verification aid: synthesizes a physically-held
//                      Left Shift (real SendInput, indistinguishable at the OS level from
//                      a genuine key-hold) starting right before the countdown ends and
//                      releasing it N ms later (spanning the paste chord), to exercise the
//                      modifier sanitiser's suppress/restore path without a human at the
//                      keyboard -- same technique spikes/s4-inject-win's AdversarialTests
//                      and WindowsHotkeySourceTests already use (EventSimulator/SendInput
//                      to stand in for a physical hold). Only valid together with --inject.
bool verbose = args.Contains("--verbose");

string? configPathOverride = null;
string? transcribeWavPath = null;
double? recordSeconds = null;
string? captureDemoMode = null;
int captureDemoUtterances = 0;
int? captureDemoIdleCloseMsOverride = null;
string? vadDemoWavPath = null;
bool vadDemoTransient = false;
bool watchHotkey = false;
int watchHotkeySeconds = 60;
bool injectRequested = false;
string? injectText = null;
int? injectHoldShiftMs = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--config" && i + 1 < args.Length)
    {
        configPathOverride = args[++i];
    }
    else if (args[i] == "--transcribe" && i + 1 < args.Length)
    {
        transcribeWavPath = args[++i];
    }
    else if (args[i] == "--record" && i + 1 < args.Length)
    {
        recordSeconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    }
    else if (args[i] == "--capture-demo" && i + 2 < args.Length)
    {
        captureDemoMode = args[++i];
        captureDemoUtterances = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
        if (i + 1 < args.Length && int.TryParse(
                args[i + 1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int idleOverride))
        {
            captureDemoIdleCloseMsOverride = idleOverride;
            i++;
        }
    }
    else if (args[i] == "--vad-demo" && i + 1 < args.Length)
    {
        vadDemoWavPath = args[++i];
    }
    else if (args[i] == "--vad-demo-transient")
    {
        vadDemoTransient = true;
    }
    else if (args[i] == "--watch-hotkey")
    {
        watchHotkey = true;
    }
    else if (args[i] == "--seconds" && i + 1 < args.Length)
    {
        watchHotkeySeconds = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    }
    else if (args[i] == "--inject")
    {
        injectRequested = true;
        // Text is optional: if the next arg looks like another flag (or there isn't one),
        // leave injectText null so RunInjectCommandAsync falls back to the S4 test string.
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            injectText = args[++i];
    }
    else if (args[i] == "--hold-shift-ms" && i + 1 < args.Length)
    {
        injectHoldShiftMs = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    }
}

var configPath = ConfigPaths.Resolve(configPathOverride);

// ---- --transcribe: a standalone CLI mode, no host/hotkey/daemon lifecycle -----------
if (transcribeWavPath != null)
    return await RunTranscribeCommandAsync(transcribeWavPath, configPath);

// ---- --record: item 4b's standalone CLI mode, no host/hotkey/daemon lifecycle ------
if (recordSeconds != null)
    return await RunRecordCommandAsync(recordSeconds.Value, configPath);

// ---- --capture-demo: item 4c's standalone CLI mode, no host/hotkey/daemon lifecycle -
if (captureDemoMode != null)
    return await RunCaptureDemoCommandAsync(captureDemoMode, captureDemoUtterances, captureDemoIdleCloseMsOverride, configPath);

// ---- --vad-demo / --vad-demo-transient: item 5's standalone CLI modes -------------
if (vadDemoWavPath != null)
    return await RunVadDemoCommandAsync(vadDemoWavPath, configPath);

if (vadDemoTransient)
    return await RunVadTransientDemoCommandAsync(configPath);

// ---- --watch-hotkey: item 6's standalone CLI mode, Windows-only ----------------------
if (watchHotkey)
{
#if WINDOWS
    return await RunWatchHotkeyCommandAsync(watchHotkeySeconds, configPath);
#else
    Console.Error.WriteLine(
        "--watch-hotkey requires the Windows build of Soneto.Daemon (net10.0-windows); "
        + "run with `dotnet run -f net10.0-windows -- --watch-hotkey`.");
    return 1;
#endif
}

// ---- --inject: item 7's standalone CLI mode, Windows-only ----------------------------
if (injectRequested)
{
#if WINDOWS
    return await RunInjectCommandAsync(injectText, configPath, injectHoldShiftMs);
#else
    Console.Error.WriteLine(
        "--inject requires the Windows build of Soneto.Daemon (net10.0-windows); "
        + "run with `dotnet run -f net10.0-windows -- --inject`.");
    return 1;
#endif
}

// Bootstrap: peek at logging.level / logging.retainDays *before* Serilog exists, using a
// throwaway console-only logger, so the daemon's real logger can be built from the
// config file's own settings on first launch instead of always hardcoding them. This
// mirrors ConfigService's own "never throws" contract — any failure here just falls
// back to the hardcoded defaults below.
var bootstrapLevel = LogEventLevel.Information;
var retainDays = 7;
try
{
    using var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var bootstrapConfigService = new ConfigService(
        bootstrapLoggerFactory.CreateLogger<ConfigService>(), configPath);
    await bootstrapConfigService.LoadAsync();

    if (Enum.TryParse(bootstrapConfigService.Current.Logging.Level, ignoreCase: true,
            out LogEventLevel parsedLevel))
        bootstrapLevel = parsedLevel;
    retainDays = bootstrapConfigService.Current.Logging.RetainDays;
}
catch (Exception ex)
{
    Console.Error.WriteLine(
        $"Warning: failed to read logging settings from config before startup; "
        + $"using hardcoded defaults (Information / 7-day retention). {ex}");
}

// ---- Serilog: console + daily rolling file (plan §1.11) -------------------
// --verbose always forces Verbose regardless of the config file, both now and on any
// later hot-reload (see the ConfigChanged handler below); otherwise the level tracks
// logging.level from the config file and is live-updatable via levelSwitch.
var levelSwitch = new Serilog.Core.LoggingLevelSwitch(
    verbose ? LogEventLevel.Verbose : bootstrapLevel);

const string outputTemplate =
    "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{SessionId}] {Message:lj}{NewLine}{Exception}";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(levelSwitch)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("SessionId", "-") // default when no session scope is active
    .WriteTo.Console(outputTemplate: outputTemplate)
    .WriteTo.File(
        "logs/soneto-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: retainDays,
        outputTemplate: outputTemplate)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(Log.Logger, dispose: true);

    // Phase 2 item 10: dictionary.json lives alongside config.json (DictionaryPaths.Resolve()
    // mirrors ConfigPaths.Resolve()'s directory) -- no CLI override flag exists for it yet,
    // unlike --config for config.json, since nothing in Phase 2's scope needed one.
    var dictionaryPath = DictionaryPaths.Resolve();

    using var host = builder.Build();

    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    var loggerFactoryForServices = host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();

    // Phase 3 item 0: IConfigService/IDictionaryService construction moved into
    // Soneto.Composition.DaemonComposition (see its own doc comment for why this is now a
    // plain factory call rather than a builder.Services.AddSingleton registration -- same
    // ILogger<T> category names, same single-instance-for-process-lifetime shape, zero
    // behavior change).
    var configService = Soneto.Composition.DaemonComposition.CreateConfigService(loggerFactoryForServices, configPath);
    var dictionaryService = Soneto.Composition.DaemonComposition.CreateDictionaryService(loggerFactoryForServices, dictionaryPath);

    logger.LogInformation("Soneto daemon starting (verbose={Verbose})", verbose);

    // Phase 3 item 0: the load-and-watch mechanics for both services now live in
    // Soneto.Composition.DaemonComposition (identical log lines, identical "subscribe only
    // after the initial load completes" ordering, identical last-resort try/catch
    // boundaries). The daemon-specific reaction to a config hot-reload -- live-updating
    // Serilog's levelSwitch, which only makes sense next to the Serilog bootstrap that stays
    // in this file -- is passed in as the documented onConfigChanged extension seam rather
    // than hardcoded into the shared helper.
    await Soneto.Composition.DaemonComposition.LoadAndStartWatchingConfigAsync(
        configService,
        logger,
        onConfigChanged: e =>
        {
            if (verbose)
                return; // --verbose always wins, config-file level is ignored entirely.

            if (Enum.TryParse(e.Config.Logging.Level, ignoreCase: true, out LogEventLevel newLevel)
                && levelSwitch.MinimumLevel != newLevel)
            {
                levelSwitch.MinimumLevel = newLevel;
                logger.LogInformation("Log level live-updated to {LogLevel} from hot-reloaded config", newLevel);
            }
        });

    await Soneto.Composition.DaemonComposition.LoadAndStartWatchingDictionaryAsync(dictionaryService, logger);

    // Item 9: real end-to-end SessionController wiring. Item 11 extends this to Linux:
    // BuildAndStartSessionControllerAsync is now platform-agnostic (takes IHotkeySource/
    // ITextInjector), and each platform branch below only constructs its own concrete
    // implementations before calling into that one shared function -- no parallel/duplicated
    // composition logic per platform. Model-load/hotkey-start failure here must NOT crash the
    // daemon process (plan §1.12's "never exits on a recoverable error") --
    // BuildAndStartSessionControllerAsync never throws; it logs a fatal-looking error and
    // returns null, and the host loop below keeps running regardless (even though dictation
    // itself can't function until a human fixes whatever's wrong and restarts the process).
    SessionController? sessionController = null;
    AudioCuePlayer? sessionCuePlayer = null;
    using (LogContext.PushProperty("SessionId", "startup"))
    {
        var loggerFactory = host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
        var appLifetime = host.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();

        // Phase 3 item 0: per-platform IHotkeySource/ITextInjector selection and the full
        // BuildAndStartSessionControllerAsync composition now live in Soneto.Composition so
        // a future Soneto.App calls the exact same code, not a forked copy.
        var (hotkeySource, textInjector) =
            Soneto.Composition.DaemonComposition.CreatePlatformHotkeySourceAndTextInjector(loggerFactory, configService.Current);

        // Phase 3 item 5 widened BuildAndStartSessionControllerAsync to also return the
        // constructed IAudioCapture (Soneto.App's Recording HUD needs to subscribe to its
        // LevelChanged event) -- Soneto.Daemon has no use for it and simply discards it here;
        // zero functional change to the daemon itself.
        (sessionController, sessionCuePlayer, _) = await Soneto.Composition.DaemonComposition.BuildAndStartSessionControllerAsync(
            configService.Current, dictionaryService.Current.Entries, loggerFactory, logger,
            hotkeySource, textInjector, appLifetime.ApplicationStopping);
    }

    // Runs until Ctrl+C / SIGTERM. Item 1's scaffold merely proved Start/Stop compiled;
    // from here on the daemon is meant to actually run for the rest of its life
    // (plan §1.1), which is also what makes hot-reload observable in practice.
    await host.RunAsync();

    if (sessionController is not null)
        await sessionController.DisposeAsync();
    sessionCuePlayer?.Dispose();

    logger.LogInformation("Soneto daemon stopped");
}
finally
{
    Log.CloseAndFlush();
}

return 0;

/// <summary>
/// Item 3's "done when" criterion: resolve the model (config override, then
/// %LOCALAPPDATA%\Soneto\models\ / ~/.local/share/soneto/models/, downloading if absent
/// from both per plan §1.6), transcribe <paramref name="wavPath"/>, print text and
/// per-stage timings.
///
/// Local dev convenience (documented, not silent): if the config has no explicit
/// asr.modelDir and this process is running from inside the Soneto repo checkout (i.e. a
/// "models/" folder exists somewhere above the executable, same auto-discovery pattern as
/// spikes/s1-asr/Program.cs), that repo-root models/ dir is used as the effective config
/// override — so this command finds the model already downloaded at
/// e:/Projects/Soneto/models/... without touching %LOCALAPPDATA% or re-downloading
/// anything. This lookup happens here in the CLI, not inside ModelManager itself, so
/// ModelManager's own resolution order (plan §1.6) stays exactly as specified.
/// </summary>
static async Task<int> RunTranscribeCommandAsync(string wavPath, string configPath)
{
    using var consoleLoggerFactory = LoggerFactory.Create(b => b
        .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
        .SetMinimumLevel(LogLevel.Information));
    var logger = consoleLoggerFactory.CreateLogger("Transcribe");

    if (!File.Exists(wavPath))
    {
        Console.Error.WriteLine($"WAV file not found: {wavPath}");
        return 1;
    }

    // A real, cancellable token for this command's lifetime (Ctrl+C), rather than
    // hardcoding CancellationToken.None throughout — needed so a stalled first-run model
    // download (see HttpModelArchiveDownloader's stall-timeout) or a long-running decode
    // can actually be interrupted by the user instead of only by the internal stall timer.
    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    Console.CancelKeyPress += onCancel;
    try
    {
        return await RunTranscribeCommandCoreAsync(wavPath, configPath, consoleLoggerFactory, logger, cts.Token);
    }
    finally
    {
        Console.CancelKeyPress -= onCancel;
    }
}

static async Task<int> RunTranscribeCommandCoreAsync(
    string wavPath,
    string configPath,
    Microsoft.Extensions.Logging.ILoggerFactory consoleLoggerFactory,
    Microsoft.Extensions.Logging.ILogger logger,
    CancellationToken ct)
{
    var configService = new ConfigService(consoleLoggerFactory.CreateLogger<ConfigService>(), configPath);
    await configService.LoadAsync();
    var asrConfig = configService.Current.Asr;

    string? effectiveModelDirOverride = asrConfig.ModelDir;
    if (string.IsNullOrWhiteSpace(effectiveModelDirOverride))
    {
        var devModelDir = Soneto.Composition.DaemonComposition.FindDevModelDirWalkingUp();
        if (devModelDir != null && ModelManager.AreRequiredFilesPresent(devModelDir))
        {
            logger.LogInformation("Using repo-local dev model dir: {Dir}", devModelDir);
            effectiveModelDirOverride = devModelDir;
        }
    }

    var modelManager = new ModelManager(consoleLoggerFactory.CreateLogger<ModelManager>());
    string modelDir;
    try
    {
        modelDir = await modelManager.ResolveOrDownloadAsync(effectiveModelDirOverride, ct);
    }
    catch (Exception ex) when (ex is ModelFilesMissingException or ModelHashMismatchException)
    {
        Console.Error.WriteLine($"Model resolution failed: {ex.Message}");
        return 1;
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        Console.Error.WriteLine("Model resolution was cancelled.");
        return 1;
    }
    catch (TimeoutException ex)
    {
        Console.Error.WriteLine($"Model download stalled: {ex.Message}");
        return 1;
    }

    await using var transcriber = new SherpaOnnxTranscriber(
        consoleLoggerFactory.CreateLogger<SherpaOnnxTranscriber>(),
        modelDir,
        asrConfig.NumThreads,
        asrConfig.DecodingMethod);

    var initSw = Stopwatch.StartNew();
    try
    {
        await transcriber.InitializeAsync(ct);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Model failed to initialize: {ex.Message}");
        return 1;
    }
    initSw.Stop();

    WavReader.WavData wav;
    try
    {
        wav = WavReader.Read(wavPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to read WAV file '{wavPath}': {ex.Message}");
        return 1;
    }

    if (wav.SampleRate != 16000)
    {
        Console.Error.WriteLine(
            $"'{wavPath}' is {wav.SampleRate}Hz, not 16000Hz mono. ITranscriber requires "
            + "already-resampled 16kHz input (the polyphase resampler is item 4, not built "
            + "yet) — resample the file first, e.g. with ffmpeg: "
            + "ffmpeg -i in.wav -ar 16000 -ac 1 out.wav");
        return 1;
    }

    var result = await transcriber.TranscribeAsync(wav.Samples, ct);

    Console.WriteLine();
    Console.WriteLine($"Text: {result.Text}");
    Console.WriteLine();
    Console.WriteLine($"Model init (load + warm-up): {initSw.Elapsed.TotalMilliseconds:F0}ms");
    Console.WriteLine($"Audio duration:              {result.AudioDuration.TotalMilliseconds:F0}ms");
    Console.WriteLine($"Decode time:                 {result.DecodeTime.TotalMilliseconds:F0}ms");
    Console.WriteLine(
        $"RTF:                          {result.DecodeTime.TotalSeconds / Math.Max(result.AudioDuration.TotalSeconds, 1e-6):F4}");

    return 0;
}

/// <summary>
/// Item 4b's "done when" criterion: open the default audio device on-demand (plan §1.5's
/// <c>OnDemand</c> capture mode — open, capture, close), wait for the stream to actually
/// start delivering audio, capture <paramref name="seconds"/> of real audio, close the
/// stream, and report the negotiated sample rate, which path was taken (direct 16 kHz vs.
/// resample-from-native), time-to-first-sample, and total samples captured. Saves the result
/// to a WAV file next to the working directory for manual playback/inspection. As a bonus
/// sanity check (only attempted if the ASR model is already present locally — this command
/// must not trigger a ~500MB first-run download just to demo the mic), also transcribes the
/// captured audio and prints the text, proving capture -> resample -> ASR works end to end.
/// </summary>
static async Task<int> RunRecordCommandAsync(double seconds, string configPath)
{
    using var consoleLoggerFactory = LoggerFactory.Create(b => b
        .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
        .SetMinimumLevel(LogLevel.Information));
    var logger = consoleLoggerFactory.CreateLogger("Record");

    if (seconds <= 0)
    {
        Console.Error.WriteLine("--record <seconds> must be a positive number.");
        return 1;
    }

    var capture = new PortAudioCapture(consoleLoggerFactory.CreateLogger<PortAudioCapture>());
    await using (capture.ConfigureAwait(false))
    {
        try
        {
            await capture.StartAsync(device: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to open the audio input stream: {ex.Message}");
            return 1;
        }

        double firstSampleMs;
        try
        {
            firstSampleMs = await capture.WaitForFirstSampleAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException ex)
        {
            Console.Error.WriteLine(ex.Message);
            // Distinguish "device never started" from "device started but is silent" per
            // item 4b's restored time-to-first-sample metric -- both stay null if no buffer
            // arrived at all, but TimeToFirstBufferMs is set (while TimeToFirstSampleMs is
            // not) if buffers are arriving all-zero.
            if (capture.TimeToFirstBufferMs is { } bufMs)
                Console.Error.WriteLine(
                    $"(A buffer DID arrive after {bufMs:F1}ms, but every buffer so far has been silent "
                    + "-- device opened/started but is producing no signal.)");
            else
                Console.Error.WriteLine("(No buffer arrived at all -- device never delivered any callback.)");
            return 1;
        }

        Console.WriteLine($"Time to first sample: {firstSampleMs:F1}ms");
        Console.WriteLine($"Negotiated sample rate: {capture.NegotiatedSampleRate}Hz");
        Console.WriteLine($"Path: {capture.CapturePath}");
        Console.WriteLine($"Recording {seconds:F1}s of audio -- speak now...");

        capture.BeginCapture(TimeSpan.Zero); // preRoll ignored in OnDemand, per §1.5
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        ReadOnlyMemory<float> samples16k = capture.EndCapture();

        await capture.StopAsync();

        Console.WriteLine($"Captured {samples16k.Length} samples ({samples16k.Length / 16000.0:F2}s at 16kHz)");

        string wavPath = Path.Combine(
            Directory.GetCurrentDirectory(), $"soneto-record-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        WavWriter.Write(wavPath, samples16k.Span, 16000);
        Console.WriteLine($"Saved: {wavPath}");

        // Item 5: VAD trim, per plan §1.5/§1.4's Finalizing state ("VAD trim; if speech <
        // 300ms -> discard"). Reads audio.vad.* from config, same as the real pipeline
        // would.
        var configService = new ConfigService(consoleLoggerFactory.CreateLogger<ConfigService>(), configPath);
        await configService.LoadAsync();
        var vadConfig = configService.Current.Audio.Vad;

        using var vad = new SileroVadDetector(consoleLoggerFactory.CreateLogger<SileroVadDetector>(), vadConfig);
        var vadResult = vad.Trim(samples16k);
        PrintVadReport(vadResult);

        if (!vadResult.ShouldDiscard)
            await TryBonusTranscribeAsync(vadResult.TrimmedSamples, configPath, consoleLoggerFactory, logger);
        else
            Console.WriteLine("(Skipping bonus transcription: VAD discarded this recording as effectively empty.)");
    }

    return 0;
}

/// <summary>
/// Item 5's "done when" criterion: run VAD trim on an existing 16kHz mono WAV file (no live
/// hardware needed) and report the detected speech/silence boundaries and discard verdict.
/// </summary>
static async Task<int> RunVadDemoCommandAsync(string wavPath, string configPath)
{
    using var consoleLoggerFactory = LoggerFactory.Create(b => b
        .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
        .SetMinimumLevel(LogLevel.Information));

    if (!File.Exists(wavPath))
    {
        Console.Error.WriteLine($"WAV file not found: {wavPath}");
        return 1;
    }

    WavReader.WavData wav;
    try
    {
        wav = WavReader.Read(wavPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to read WAV file '{wavPath}': {ex.Message}");
        return 1;
    }

    if (wav.SampleRate != 16000)
    {
        Console.Error.WriteLine(
            $"'{wavPath}' is {wav.SampleRate}Hz, not 16000Hz mono. SileroVadDetector, like "
            + "ITranscriber, requires already-resampled 16kHz input.");
        return 1;
    }

    var configService = new ConfigService(consoleLoggerFactory.CreateLogger<ConfigService>(), configPath);
    await configService.LoadAsync();
    var vadConfig = configService.Current.Audio.Vad;

    Console.WriteLine(
        $"Input: {wavPath} ({wav.Samples.Length / 16000.0:F2}s at 16kHz), "
        + $"VAD enabled={vadConfig.Enabled} threshold={vadConfig.Threshold} "
        + $"minSilenceMs={vadConfig.MinSilenceMs} minSpeechMs={vadConfig.MinSpeechMs}");

    using var vad = new SileroVadDetector(consoleLoggerFactory.CreateLogger<SileroVadDetector>(), vadConfig);
    var result = vad.Trim(wav.Samples);
    PrintVadReport(result);

    return 0;
}

/// <summary>
/// Item 5's "open-transient discarded" done-when criterion: synthesizes a short WAV
/// containing a brief loud transient click (a stand-in for the driver click / DC-settling
/// artifact plan §1.5 warns is common in the first 50-150ms after a cold on-demand stream
/// open) followed by silence, runs VAD on it, and demonstrates it is correctly discarded
/// (not passed to the transcriber) rather than being mistaken for real speech.
/// </summary>
static async Task<int> RunVadTransientDemoCommandAsync(string configPath)
{
    using var consoleLoggerFactory = LoggerFactory.Create(b => b
        .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
        .SetMinimumLevel(LogLevel.Information));

    const int sampleRate = 16000;
    const double transientDurationS = 0.03; // 30ms -- shorter than a real syllable
    const double totalDurationS = 1.0;

    var samples = new float[(int)(sampleRate * totalDurationS)];
    int transientSamples = (int)(sampleRate * transientDurationS);
    var rnd = new Random(1234);
    for (int i = 0; i < transientSamples; i++)
    {
        // A sharp, loud, decaying click -- not speech-shaped (no harmonic structure), the
        // kind of thing a driver/DC-settling transient looks like.
        double envelope = Math.Exp(-i / (transientSamples * 0.15));
        samples[i] = (float)(envelope * (rnd.NextDouble() * 2 - 1));
    }
    // The rest of the buffer stays silence (zeros).

    string wavPath = Path.Combine(
        Directory.GetCurrentDirectory(), $"soneto-vad-transient-demo-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
    WavWriter.Write(wavPath, samples, sampleRate);
    Console.WriteLine(
        $"Synthesized {totalDurationS:F2}s clip: a {transientDurationS * 1000:F0}ms transient "
        + $"click at t=0, followed by silence. Saved: {wavPath}");

    var configService = new ConfigService(consoleLoggerFactory.CreateLogger<ConfigService>(), configPath);
    await configService.LoadAsync();
    var vadConfig = configService.Current.Audio.Vad;

    using var vad = new SileroVadDetector(consoleLoggerFactory.CreateLogger<SileroVadDetector>(), vadConfig);
    var result = vad.Trim(samples);
    PrintVadReport(result);

    if (result.ShouldDiscard)
        Console.WriteLine(
            "PASS: the open-transient click was correctly discarded -- it would NOT reach the transcriber.");
    else
        Console.WriteLine(
            "UNEXPECTED: the transient was treated as real speech and would reach the transcriber.");

    return 0;
}

/// <summary>
/// Shared VAD result reporting for <c>--record</c>/<c>--vad-demo</c>/<c>--vad-demo-transient</c>,
/// per item 5's "done when" criterion: "reports speech/silence boundaries; silent input and
/// open-transient discarded."
/// </summary>
static void PrintVadReport(VadTrimResult result)
{
    Console.WriteLine();
    if (result.TotalSpeechDuration == TimeSpan.Zero && result.SpeechStartSample == result.SpeechEndSample
        && result.LeadingSilenceTrimmed > TimeSpan.Zero && result.TrailingSilenceTrimmed == TimeSpan.Zero
        && result.ShouldDiscard)
    {
        // No speech segments at all (Trim's "firstStart is null" branch reports the whole
        // buffer as leading silence with zero trailing).
        Console.WriteLine(
            $"VAD: no speech detected in {result.LeadingSilenceTrimmed.TotalMilliseconds:F0}ms of input -- DISCARDING.");
    }
    else
    {
        double speechStartMs = result.LeadingSilenceTrimmed.TotalMilliseconds;
        double speechEndMs = speechStartMs + result.TotalSpeechDuration.TotalMilliseconds;
        Console.WriteLine(
            $"VAD: speech detected from {speechStartMs:F0}ms to {speechEndMs:F0}ms "
            + $"(discarding {result.LeadingSilenceTrimmed.TotalMilliseconds:F0}ms leading silence "
            + $"and {result.TrailingSilenceTrimmed.TotalMilliseconds:F0}ms trailing silence).");
        Console.WriteLine($"Total speech after trim: {result.TotalSpeechDuration.TotalMilliseconds:F0}ms");
        Console.WriteLine(
            result.ShouldDiscard
                ? "Verdict: DISCARD (total speech below the minimum -- treated as effectively empty audio)."
                : "Verdict: KEEP (would proceed to transcription).");
    }
    Console.WriteLine();
}

/// <summary>
/// Bonus sanity check (item 4b's spec: "only do this if it's straightforward"). Reuses the
/// exact same model-resolution path as <c>--transcribe</c>, but only if the model is already
/// downloaded locally — this command must never kick off a ~500MB download on its own.
/// </summary>
static async Task TryBonusTranscribeAsync(
    ReadOnlyMemory<float> samples16k,
    string configPath,
    Microsoft.Extensions.Logging.ILoggerFactory consoleLoggerFactory,
    Microsoft.Extensions.Logging.ILogger logger)
{
    try
    {
        var configService = new ConfigService(consoleLoggerFactory.CreateLogger<ConfigService>(), configPath);
        await configService.LoadAsync();
        var asrConfig = configService.Current.Asr;

        string? effectiveModelDirOverride = asrConfig.ModelDir;
        if (string.IsNullOrWhiteSpace(effectiveModelDirOverride))
        {
            var devModelDir = Soneto.Composition.DaemonComposition.FindDevModelDirWalkingUp();
            if (devModelDir != null && ModelManager.AreRequiredFilesPresent(devModelDir))
                effectiveModelDirOverride = devModelDir;
        }

        if (string.IsNullOrWhiteSpace(effectiveModelDirOverride)
            || !ModelManager.AreRequiredFilesPresent(effectiveModelDirOverride))
        {
            Console.WriteLine(
                "(Skipping bonus transcription check: no ASR model found locally. "
                + "Run --transcribe once to fetch it, then re-run --record.)");
            return;
        }

        await using var transcriber = new SherpaOnnxTranscriber(
            consoleLoggerFactory.CreateLogger<SherpaOnnxTranscriber>(),
            effectiveModelDirOverride, asrConfig.NumThreads, asrConfig.DecodingMethod);
        await transcriber.InitializeAsync(CancellationToken.None);
        var result = await transcriber.TranscribeAsync(samples16k, CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"Bonus transcription: \"{result.Text}\"");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Bonus transcription check failed (non-fatal, --record's own result above is unaffected)");
    }
}

/// <summary>
/// Item 4c's "done when" criterion: exercise <see cref="CaptureModeController"/> through a
/// sequence of simulated key-down/key-up cycles (begin utterance -> wait 2s "speaking" ->
/// end utterance -> wait 1s before the next one) under a configurable capture mode, logging
/// every stream open/close event, idle-timer start/cancel/fire, and ready/failure cue firing
/// so <c>OnDemand</c> (opens/closes every utterance) and <c>WarmIdle</c> (stays open across
/// a burst, eventually idle-closes) can be directly observed and compared. Uses a real
/// <see cref="PortAudioCapture"/> and a real <see cref="AudioCuePlayer"/> — this is a
/// hardware-touching manual demo, not a unit test (those live against a fake
/// <see cref="IAudioCapture"/>, see <c>Soneto.Core.Tests</c>).
/// </summary>
static async Task<int> RunCaptureDemoCommandAsync(
    string modeArg, int utteranceCount, int? idleCloseMsOverride, string configPath)
{
    using var consoleLoggerFactory = LoggerFactory.Create(b => b
        .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
        .SetMinimumLevel(LogLevel.Information));
    var logger = consoleLoggerFactory.CreateLogger("CaptureDemo");

    if (!Enum.TryParse<CaptureMode>(modeArg, ignoreCase: true, out var mode))
    {
        Console.Error.WriteLine($"Unknown capture mode '{modeArg}'. Valid: OnDemand, WarmIdle, AlwaysOn.");
        return 1;
    }
    if (utteranceCount <= 0)
    {
        Console.Error.WriteLine("--capture-demo <mode> <utterances> requires a positive utterance count.");
        return 1;
    }

    var configService = new ConfigService(consoleLoggerFactory.CreateLogger<ConfigService>(), configPath);
    await configService.LoadAsync();
    var audioConfig = configService.Current.Audio;
    int idleCloseMs = idleCloseMsOverride ?? audioConfig.IdleCloseMs;

    logger.LogInformation(
        "capture-demo starting: mode={Mode} utterances={Count} idleCloseMs={IdleCloseMs} "
        + "preRollMs={PreRollMs} readyCue={ReadyCue}",
        mode, utteranceCount, idleCloseMs, audioConfig.PreRollMs, audioConfig.ReadyCue);

    await using var capture = new PortAudioCapture(
        consoleLoggerFactory.CreateLogger<PortAudioCapture>(), preRollCapacityMs: audioConfig.PreRollMs);
    using var cuePlayer = new AudioCuePlayer(consoleLoggerFactory.CreateLogger<AudioCuePlayer>());

    await using var controller = new CaptureModeController(
        capture,
        consoleLoggerFactory.CreateLogger<CaptureModeController>(),
        mode,
        idleCloseMs,
        audioConfig.PreRollMs,
        device: null,
        // Always pass the real cuePlayer, even when readyCue=None: CaptureModeController gates
        // the routine ready cue on readyCueMode itself, but plays the failure cue unconditionally
        // of it (item 4c review fix 2) -- passing null here would silently defeat that and bring
        // back the "silence is the worst possible feedback" scenario on a dead mic.
        cuePlayer: cuePlayer,
        readyCueMode: audioConfig.ReadyCue);

    try
    {
        await controller.StartAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"AlwaysOn startup stream failed to open: {ex.Message}");
        return 1;
    }

    for (int i = 1; i <= utteranceCount; i++)
    {
        Console.WriteLine();
        logger.LogInformation("=== Utterance {Index}/{Count}: key-down ===", i, utteranceCount);
        try
        {
            await controller.BeginUtteranceAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to begin utterance {i}: {ex.Message}");
            continue;
        }
        Console.WriteLine(
            $"[{i}] stream open={capture.IsRunning}, idleCloseTimerPending={controller.IsIdleCloseTimerPending}. "
            + "Speak now (2s)...");
        await Task.Delay(TimeSpan.FromSeconds(2));

        logger.LogInformation("=== Utterance {Index}/{Count}: key-up ===", i, utteranceCount);
        var samples = await controller.EndUtteranceAsync();
        Console.WriteLine(
            $"[{i}] captured {samples.Length} samples ({samples.Length / 16000.0:F2}s at 16kHz). "
            + $"stream open now={capture.IsRunning}, idleCloseTimerPending={controller.IsIdleCloseTimerPending}");

        if (i < utteranceCount)
            await Task.Delay(TimeSpan.FromSeconds(1));
    }

    if (mode == CaptureMode.WarmIdle)
    {
        Console.WriteLine();
        int waitMs = idleCloseMs + 1500;
        Console.WriteLine($"All utterances done. Waiting {waitMs}ms to observe the idle-close timer fire...");
        await Task.Delay(TimeSpan.FromMilliseconds(waitMs));
        Console.WriteLine(
            $"After waiting past idleCloseMs: stream open={capture.IsRunning}, "
            + $"idleCloseTimerPending={controller.IsIdleCloseTimerPending}");
    }

    Console.WriteLine();
    Console.WriteLine("capture-demo complete.");
    return 0;
}

#if WINDOWS

/// <summary>
/// Item 6's "done when" criterion: run the real <c>WindowsHotkeySource</c> against the
/// real global keyboard hook, logging every DOWN/UP with a timestamp and the held-modifier
/// state (Shift/Alt/LeftCtrl/Win) at that moment, for <paramref name="seconds"/> or until
/// Ctrl+C.
///
/// <para>
/// <b>What this demo can and cannot verify, stated honestly (per the work item's explicit
/// instruction not to fabricate verification of things an agent session can't actually
/// test):</b> this proves the hook fires DOWN/UP correctly, that suppression is wired
/// (verifiable synthetically via <c>EventSimulator</c> in the test suite — see
/// <c>WindowsHotkeySourceSuppressionTests</c>), and that held-modifier reads via
/// <c>GetAsyncKeyState</c> work. It does NOT and CANNOT verify, from a non-interactive
/// agent session: (1) that suppression actually stops a physical key press from leaking a
/// character/modifier into a real focused app like Notepad or VS Code, (2) a physical
/// Shift-held-during-trigger press on real hardware, (3) 30-minute idle survival, or
/// (4) lock/unlock cycle survival. Those four categories are exactly the ones
/// <c>spikes/s3-hotkey-win/README.md</c> already left as a manual test script for a human,
/// and remain so here — this demo is a real, live tool a human can run to close those gaps,
/// not a substitute for having done so.
/// </para>
/// </summary>
static async Task<int> RunWatchHotkeyCommandAsync(int seconds, string configPath)
{
    using var consoleLoggerFactory = LoggerFactory.Create(b => b
        .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
        .SetMinimumLevel(LogLevel.Information));
    var logger = consoleLoggerFactory.CreateLogger("WatchHotkey");

    var configService = new ConfigService(consoleLoggerFactory.CreateLogger<ConfigService>(), configPath);
    await configService.LoadAsync();
    var binding = configService.Current.Hotkey.ToBinding();

    Console.WriteLine($"=== --watch-hotkey === trigger={binding.Key} suppress={binding.Suppress} duration={seconds}s");
    Console.WriteLine("Hold/release the trigger key. Press Ctrl+C to stop early.");
    Console.WriteLine();
    Console.WriteLine("STILL NEEDS HUMAN/MANUAL VERIFICATION (cannot be certified by this agent session):");
    Console.WriteLine("  1. No character/modifier leak into a real focused app (Notepad/VS Code).");
    Console.WriteLine("  2. Physical Shift held during a real trigger press.");
    Console.WriteLine("  3. 30-minute idle survival.");
    Console.WriteLine("  4. Lock/unlock cycle survival.");
    Console.WriteLine();

    await using var hotkeySource = new Soneto.Platform.Windows.WindowsHotkeySource(
        consoleLoggerFactory.CreateLogger<Soneto.Platform.Windows.WindowsHotkeySource>());

    int downCount = 0, upCount = 0;
    hotkeySource.Pressed += (_, e) =>
    {
        downCount++;
        var mods = Soneto.Platform.Windows.Interop.ModifierState.Read();
        Console.WriteLine($"[{e.Timestamp:HH:mm:ss.fff}] DOWN  #{downCount}  heldModifiers={mods}");
    };
    hotkeySource.Released += (_, e) =>
    {
        upCount++;
        Console.WriteLine($"[{e.Timestamp:HH:mm:ss.fff}] UP    #{upCount}");
    };
    hotkeySource.Faulted += (_, e) =>
    {
        Console.WriteLine($"[FAULTED] {e.Reason}");
        logger.LogError(e.Exception, "Hotkey source faulted: {Reason}", e.Reason);
    };

    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    Console.CancelKeyPress += onCancel;
    try
    {
        await hotkeySource.StartAsync(binding, CancellationToken.None);
        Console.WriteLine("Hook registered and running.\n");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C: normal early exit.
        }
    }
    finally
    {
        Console.CancelKeyPress -= onCancel;
    }

    Console.WriteLine($"\nStopped. Total: {downCount} DOWN, {upCount} UP.");
    return 0;
}

/// <summary>
/// Item 7's "done when" criterion: <c>--inject "text"</c> puts the given text (or, if
/// omitted, the exact S4 test string) into the focused app. Uses the same 3-second countdown
/// pattern as <c>spikes/s4-inject-win</c>'s <c>countdown</c> mode -- gives the operator time
/// to Alt-Tab to a real target app before injection fires -- and re-captures the foreground
/// window right before injecting, so whatever the operator switched to during the countdown
/// is what actually gets the paste (this item's only supported <c>targetLostPolicy</c>,
/// "current"; see <c>WindowsTextInjector</c>'s doc comment).
/// </summary>
static async Task<int> RunInjectCommandAsync(string? text, string configPath, int? holdShiftMs = null)
{
    // Exact S4 test string (plan §1.8 / Docs/soneto-implementation-plan-phase0-1.md), built
    // with an explicit "\n" so its byte content is independent of git line-ending
    // normalization -- same convention as spikes/s4-inject-win/TestData.cs.
    const string defaultTestString =
        "Ăsta e un test: șoseaua Ștefan cel Mare, țară, îngheț — 100% \"quoted\" & <tagged>.\n"
        + "Line two after a newline.";
    string injectText = text ?? defaultTestString;

    using var consoleLoggerFactory = LoggerFactory.Create(b => b
        .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
        .SetMinimumLevel(LogLevel.Information));

    var configService = new ConfigService(consoleLoggerFactory.CreateLogger<ConfigService>(), configPath);
    await configService.LoadAsync();
    var opts = configService.Current.Injection.ToOptions(configService.Current.Hotkey.Key);

    Console.WriteLine(
        "=== --inject === (Phase 1 items 7+7b: base injection + modifier sanitiser done; "
        + "clipboard sequence guard/policy is item 7c -- see WindowsTextInjector's doc "
        + "comment for exactly what that means)");
    Console.WriteLine($"Text to inject ({injectText.Length} chars):");
    Console.WriteLine(injectText);
    Console.WriteLine();
    Console.WriteLine(
        $"method={opts.Method} chord={opts.PasteChord} preDelay={opts.PreDelay.TotalMilliseconds:F0}ms "
        + $"restoreDelay={opts.ClipboardRestoreDelay.TotalMilliseconds:F0}ms");
    Console.WriteLine();

    var injector = new Soneto.Platform.Windows.WindowsTextInjector(
        consoleLoggerFactory.CreateLogger<Soneto.Platform.Windows.WindowsTextInjector>());

    for (int s = 3; s >= 1; s--)
    {
        Console.WriteLine($"Injecting in {s}... (Alt-Tab to your target app now)");
        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    // Re-capture the target right before injecting, not at the start of the countdown --
    // the whole point of the countdown is to let the operator Alt-Tab away from this console.
    var target = injector.CaptureTarget();

    // --hold-shift-ms manual-verification aid (item 7b): synthesize a physically-held Left
    // Shift starting now, overlapping the injection that's about to run. Run concurrently
    // (not awaited before InjectAsync starts), since the whole point is for the hold to be
    // in effect *during* the paste chord, not to finish beforehand.
    Task? shiftHoldTask = null;
    if (holdShiftMs is int ms && ms > 0)
    {
        Console.WriteLine(
            $"Synthesizing a physically-held Left Shift for {ms}ms starting now (item 7b "
            + "manual-verification aid -- watch for 'suppressed held modifier' / 'restored "
            + "still-held modifier' / 'NOT restoring' log lines above the outcome below).");
        shiftHoldTask = HoldLeftShiftAsync(ms);
    }

    var sw = Stopwatch.StartNew();
    var outcome = await injector.InjectAsync(injectText, target, opts, CancellationToken.None);
    sw.Stop();

    if (shiftHoldTask is not null)
        await shiftHoldTask;

    Console.WriteLine();
    Console.WriteLine($"Outcome: {outcome}");
    Console.WriteLine($"Elapsed: {sw.ElapsedMilliseconds}ms");

    return outcome == InjectionOutcome.Injected ? 0 : 1;
}

/// <summary>
/// Item 7b manual-verification aid only: synthesizes a physically-held Left Shift via a
/// direct, self-contained <c>SendInput</c> call (real key events, indistinguishable at the
/// OS level from a genuine physical hold -- same technique
/// <c>spikes/s4-inject-win/AdversarialTests.cs</c>'s <c>RunShiftHold</c> and this project's
/// own <c>WindowsHotkeySourceTests</c> already use to exercise held-modifier paths without
/// a human at the keyboard). Deliberately a small, self-contained P/Invoke block here
/// rather than reaching into <c>Soneto.Platform.Windows</c>'s internal
/// <c>ModifierSanitizer</c>/<c>WindowsTextInjector.SendSingleKey</c> -- this mirrors
/// <c>WindowsTextInjectorNotepadSelfCheckTests.cs</c>'s own documented precedent of
/// duplicating a tiny native helper rather than exposing product internals purely for a
/// test/demo caller.
/// </summary>
static async Task HoldLeftShiftAsync(int ms)
{
    const int VK_LSHIFT = 0xA0;
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;

    SendShiftKeyEvent(keyUp: false);
    try
    {
        await Task.Delay(ms).ConfigureAwait(false);
    }
    finally
    {
        SendShiftKeyEvent(keyUp: true);
    }

    static void SendShiftKeyEvent(bool keyUp)
    {
        ushort scan = (ushort)NativeInteropForShiftHoldDemo.MapVirtualKey(VK_LSHIFT, 0);
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = VK_LSHIFT,
                    wScan = scan,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                }
            }
        };
        NativeInteropForShiftHoldDemo.SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }
}

#endif

#if WINDOWS
// ---- item 7b manual-verification aid: minimal self-contained SendInput P/Invoke surface
// for HoldLeftShiftAsync above (--hold-shift-ms). Type declarations must come after all
// top-level statements/local functions in a top-level-statements Program.cs, hence living
// at the very end of the file rather than next to HoldLeftShiftAsync itself. -------------
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct INPUT { public uint type; public InputUnion U; }

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
struct InputUnion
{
    [System.Runtime.InteropServices.FieldOffset(0)] public MOUSEINPUT mi;
    [System.Runtime.InteropServices.FieldOffset(0)] public KEYBDINPUT ki;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

static class NativeInteropForShiftHoldDemo
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
#endif
