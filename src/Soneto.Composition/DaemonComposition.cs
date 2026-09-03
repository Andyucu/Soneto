using Microsoft.Extensions.Logging;
using Soneto.Core;
using Soneto.Core.Abstractions;
using Soneto.Core.Asr;
using Soneto.Core.Audio;
using Soneto.Core.Configuration;
using Soneto.Core.Dictionary;
using Soneto.Core.PostProcessing;

namespace Soneto.Composition;

/// <summary>
/// Phase 3 item 0: the real end-to-end dictation composition logic extracted verbatim out of
/// <c>Soneto.Daemon/Program.cs</c> (model resolution, config/dictionary service creation and
/// hot-reload wiring, audio capture/VAD/post-processor-chain/injection wiring, and
/// <see cref="SessionController"/> construction/startup), so a future <c>Soneto.App</c> (the
/// Avalonia shell, per <c>Docs/soneto-implementation-plan-phase3.md</c> §3.3) can call the
/// exact same code path <c>Soneto.Daemon</c> already runs instead of forking it — this
/// project's own established discipline (see <see cref="BuildAndStartSessionControllerAsync"/>'s
/// own doc comment: one function already serves both the Windows and Linux branches of
/// <c>Main</c>; this class extends that "write it once" discipline to serve two EXECUTABLES,
/// not just two platform branches within one).
///
/// <para>
/// <b>Design decision — plain constructors/factory methods, not <c>IServiceCollection</c>
/// registration:</b> <c>Soneto.Daemon</c> today wires <see cref="IConfigService"/>/
/// <see cref="IDictionaryService"/> into a <c>Microsoft.Extensions.Hosting</c>
/// <c>HostApplicationBuilder</c>'s DI container. <c>Soneto.App</c> (Avalonia) is not
/// guaranteed to use the same hosting/DI shape — Avalonia apps commonly wire services by hand
/// or via a different container. Rather than force a second caller to adopt
/// <c>Microsoft.Extensions.DependencyInjection</c> just to satisfy this helper's API, every
/// method here takes an <see cref="ILoggerFactory"/> (a shape both a
/// <c>Microsoft.Extensions.Hosting</c> host AND a hand-rolled Avalonia composition root can
/// trivially produce) and returns constructed instances directly. <c>Soneto.Daemon</c> itself
/// still resolves its <see cref="ILoggerFactory"/> from its own DI container exactly as
/// before — only the two or three lines that used to be <c>builder.Services.AddSingleton</c>
/// registrations become direct calls to <see cref="CreateConfigService"/>/
/// <see cref="CreateDictionaryService"/> after <c>host.Build()</c>, which is behaviorally
/// identical (same <see cref="ILogger{TCategoryName}"/> category names, same construction
/// order, same lifetime — a single instance held for the process's lifetime either way).
/// </para>
/// </summary>
public static class DaemonComposition
{
    /// <summary>
    /// Constructs a real <see cref="ConfigService"/> the same way <c>Soneto.Daemon</c>'s
    /// former <c>builder.Services.AddSingleton&lt;IConfigService&gt;</c> registration did —
    /// see this class's own doc comment for why this is a plain factory method rather than a
    /// DI-container registration.
    /// </summary>
    public static IConfigService CreateConfigService(ILoggerFactory loggerFactory, string configPath)
        => new ConfigService(loggerFactory.CreateLogger<ConfigService>(), configPath);

    /// <summary>
    /// Constructs a real <see cref="DictionaryService"/> — sibling to
    /// <see cref="CreateConfigService"/>, same reasoning (Phase 2 item 10's own doc comment:
    /// a sibling service, not a generalized dual-file <c>ConfigService</c>).
    /// </summary>
    public static IDictionaryService CreateDictionaryService(ILoggerFactory loggerFactory, string dictionaryPath)
        => new DictionaryService(loggerFactory.CreateLogger<DictionaryService>(), dictionaryPath);

    /// <summary>
    /// Loads <paramref name="configService"/> and starts its file watcher, logging the exact
    /// same "Config loaded from..." / "Config hot-reloaded..." lines
    /// <c>Soneto.Daemon/Program.cs</c> always has, and swallowing any unexpected failure the
    /// same last-resort way (plan §1.12: recoverable errors must never kill the caller).
    /// <paramref name="onConfigChanged"/> is the documented extension seam for app-specific
    /// reactions to a hot-reload (e.g. <c>Soneto.Daemon</c>'s Serilog <c>levelSwitch</c>
    /// live-update, which is inherently per-executable and stays in each executable's own
    /// <c>Program.cs</c>/composition root rather than being hardcoded here) — it is invoked
    /// AFTER this method's own informational log line, and is never invoked for the initial
    /// load (only for genuine subsequent <see cref="IConfigService.ConfigChanged"/> events,
    /// exactly as before: the subscription happens only after <c>LoadAsync</c> completes).
    /// </summary>
    public static async Task LoadAndStartWatchingConfigAsync(
        IConfigService configService,
        ILogger logger,
        Action<ConfigChangedEventArgs>? onConfigChanged = null)
    {
        // Never let a config/watcher failure here take the whole caller down (plan §1.12:
        // recoverable errors must never kill the daemon). LoadAsync itself never throws by
        // contract, and StartWatching now swallows its own IO failures, but this is a
        // last-resort boundary against anything unanticipated — Current already defaults to
        // `new SonetoConfig()` if the load never actually succeeds.
        try
        {
            await configService.LoadAsync();
            logger.LogInformation(
                "Config loaded from {ConfigPath}: captureMode={CaptureMode} numThreads={NumThreads} "
                + "hotkey={HotkeyKey}",
                configService.ConfigPath, configService.Current.Audio.CaptureMode,
                configService.Current.Asr.NumThreads, configService.Current.Hotkey.Key);

            // Subscribed only AFTER the initial load completes, so this handler — and its
            // "hot-reloaded" log line — only ever fires for genuine subsequent reloads, not
            // for the initial LoadAsync's own ConfigChanged event.
            configService.ConfigChanged += (_, e) =>
            {
                logger.LogInformation(
                    "Config hot-reloaded: captureMode={CaptureMode} numThreads={NumThreads} "
                    + "hotkey={HotkeyKey} logging.level={LoggingLevel}",
                    e.Config.Audio.CaptureMode, e.Config.Asr.NumThreads,
                    e.Config.Hotkey.Key, e.Config.Logging.Level);

                onConfigChanged?.Invoke(e);
            };

            configService.StartWatching();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Unexpected error loading/watching config at {ConfigPath}; continuing with "
                + "in-memory defaults and hot-reload disabled", configService.ConfigPath);
        }
    }

    /// <summary>
    /// Loads <paramref name="dictionaryService"/> and starts its file watcher, logging the
    /// exact same "Dictionary loaded from..." / "Dictionary hot-reloaded..." lines
    /// <c>Soneto.Daemon/Program.cs</c> always has, including the honest, deliberate
    /// "restart required for a hot-reloaded dictionary to take effect" warning (plan §2.7)
    /// — there is no caller-supplied extension seam here (unlike
    /// <see cref="LoadAndStartWatchingConfigAsync"/>'s <c>onConfigChanged</c>) because this
    /// reaction is not daemon-specific: the "PostProcessorChain is built once at startup"
    /// limitation applies identically to any caller of
    /// <see cref="BuildAndStartSessionControllerAsync"/>, including a future
    /// <c>Soneto.App</c>.
    /// </summary>
    public static async Task LoadAndStartWatchingDictionaryAsync(
        IDictionaryService dictionaryService,
        ILogger logger)
    {
        // Phase 2 item 10: real dictionary.json load/watch wiring, mirroring the config
        // method above. LoadAsync itself never throws by contract (see DictionaryService's
        // own doc comment) and now writes+loads the embedded seed dictionary on first run
        // (item 10, ConfigService parity) rather than starting empty (item 9's behavior);
        // this try/catch is the same last-resort boundary the config method above uses
        // against anything unanticipated.
        try
        {
            await dictionaryService.LoadAsync();
            logger.LogInformation(
                "Dictionary loaded from {DictionaryPath}: {EntryCount} entries ({RejectedCount} rejected)",
                dictionaryService.DictionaryPath,
                dictionaryService.Current.Entries.Count,
                dictionaryService.Current.RejectedEntries.Count);

            // Subscribed only AFTER the initial load completes, same reasoning as
            // configService.ConfigChanged's handler above -- this only ever fires for genuine
            // subsequent reloads (DictionaryService.LoadAsync never raises DictionaryChanged for a
            // first-run load, per its own doc comment).
            //
            // Honest, deliberate limitation (plan §2.7, decided here rather than left implicit):
            // PostProcessorChain is built ONCE at daemon startup (see BuildAndStartSessionControllerAsync
            // below) from a snapshot of dictionaryService.Current.Entries, exactly like
            // config.PostProcess's own toggles already are -- there is no live in-place swap of a
            // running SessionController's PostProcessorChain today (doing so would require changing
            // SessionController itself, which plan §1.16 explicitly flags as a signal something is
            // wrong, not a normal cost of adding a feature; see PROJECT-MEMORY.md for the full
            // reasoning). A hot-reloaded dictionary.json is therefore validated and logged, but a
            // daemon RESTART is required for the change to actually take effect in the active
            // session -- log that loudly here so this isn't a silently-broken promise.
            dictionaryService.DictionaryChanged += (_, e) =>
            {
                logger.LogWarning(
                    "Dictionary hot-reloaded from {DictionaryPath} ({EntryCount} entries, {RejectedCount} "
                    + "rejected), but the active dictation session's PostProcessorChain was built once at "
                    + "daemon startup from a dictionary snapshot -- restart the daemon for this change to "
                    + "take effect in the running session.",
                    dictionaryService.DictionaryPath, e.Config.Entries.Count, e.Config.RejectedEntries.Count);
            };

            dictionaryService.StartWatching();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Unexpected error loading/watching dictionary at {DictionaryPath}; continuing with "
                + "an empty in-memory dictionary and hot-reload disabled", dictionaryService.DictionaryPath);
        }
    }

    /// <summary>
    /// Constructs the real, per-platform <see cref="IHotkeySource"/>/<see cref="ITextInjector"/>
    /// pair (Windows vs. Linux, exactly the same <c>#if WINDOWS</c>/<c>#else</c> selection
    /// <c>Soneto.Daemon/Program.cs</c>'s <c>Main</c> used to do inline) — extracted here so
    /// both <c>Soneto.Daemon</c> and a future <c>Soneto.App</c> select the same concrete
    /// platform implementations the same way, rather than each executable duplicating this
    /// selection logic. Callers still own passing the result into
    /// <see cref="BuildAndStartSessionControllerAsync"/> — that contract is unchanged.
    /// </summary>
    /// <param name="loggerFactory">Same as every other method here — see class doc comment.</param>
    /// <param name="config">
    /// Phase 4 item 2 (§4.4): the loaded <see cref="SonetoConfig"/>, so
    /// <see cref="InjectionConfig.PerApp"/> can be resolved into the real
    /// <see cref="Soneto.Platform.Windows.WindowsTextInjector"/> instance exactly once, here at
    /// composition time — see that constructor's own doc comment for why the per-app table is
    /// threaded in this way rather than read from a config singleton at injection time (mirrors
    /// how <c>DictionaryEngineProcessor</c> et al. already take their rule sets via constructor
    /// parameter, per Phase 2's own established pattern). Wrapped in a fresh
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>-keyed dictionary here (NOT reusing
    /// <c>config.Injection.PerApp</c>'s own default-comparer dictionary as-is) so process-name
    /// matching is case-insensitive per Windows filename conventions — <see cref="PerAppOverrideResolver"/>
    /// itself applies no comparer of its own; this is the one place that decision is made. Only
    /// used on the Windows branch below; the Linux branch has no equivalent mechanism yet (see
    /// <c>LinuxTextInjector</c>'s own doc comment for the honestly-documented gap: no portable
    /// way to resolve a focused window's owning process under Wayland from an unprivileged
    /// process, so <see cref="InjectionConfig.PerApp"/> resolution does not extend to Linux this
    /// item).
    /// </param>
    public static (IHotkeySource HotkeySource, ITextInjector TextInjector) CreatePlatformHotkeySourceAndTextInjector(
        ILoggerFactory loggerFactory, SonetoConfig config)
    {
#if WINDOWS
        IHotkeySource hotkeySource = new Soneto.Platform.Windows.WindowsHotkeySource(
            loggerFactory.CreateLogger<Soneto.Platform.Windows.WindowsHotkeySource>());
        // Fully-qualified: `PerAppOverride` is ambiguous in this file -- the injection-config
        // one (Soneto.Core.Configuration) and the unrelated dictionary-entry type of the same
        // name (Soneto.Core.Dictionary, Phase 4 item 3's subject, not this one's).
        var perApp = new Dictionary<string, Soneto.Core.Configuration.PerAppOverride>(
            config.Injection.PerApp, StringComparer.OrdinalIgnoreCase);
        ITextInjector textInjector = new Soneto.Platform.Windows.WindowsTextInjector(
            loggerFactory.CreateLogger<Soneto.Platform.Windows.WindowsTextInjector>(), perApp);
#else
        // Item 11: real (best-effort, S5-gated -- see LinuxHotkeySource/LinuxTextInjector's
        // own doc comments for exactly what is and isn't verified) Linux implementations.
        // Phase 4 item 2: config.Injection.PerApp is NOT threaded into LinuxTextInjector -- see
        // this method's own <param name="config"> doc comment for the real, structural gap
        // (no portable per-process target resolution under Wayland) this leaves open.
        IHotkeySource hotkeySource = new Soneto.Platform.Linux.LinuxHotkeySource(
            loggerFactory.CreateLogger<Soneto.Platform.Linux.LinuxHotkeySource>());
        ITextInjector textInjector = new Soneto.Platform.Linux.LinuxTextInjector(
            loggerFactory.CreateLogger<Soneto.Platform.Linux.LinuxTextInjector>());
#endif
        return (hotkeySource, textInjector);
    }

    /// <summary>
    /// Item 9's real end-to-end composition (item 11: now platform-agnostic, taking the
    /// caller-constructed <paramref name="hotkeySource"/>/<paramref name="textInjector"/>
    /// rather than building a Windows-specific pair itself, so the exact same composition
    /// logic serves both the Windows and Linux branches in <c>Main</c> -- see that call site's
    /// comment for why; Phase 3 item 0 extends this to serve both <c>Soneto.Daemon</c> and a
    /// future <c>Soneto.App</c>, not just two platform branches within one executable).
    /// Resolves the model (exact same path <c>RunTranscribeCommandCoreAsync</c>
    /// uses -- config override, then repo-local dev models/ dir, then %LOCALAPPDATA%/download),
    /// builds a real <see cref="SherpaOnnxTranscriber"/> (initialized before use, per
    /// <see cref="SessionController"/>'s documented "caller owns model loading" contract), a
    /// real <see cref="PortAudioCapture"/> wrapped in <see cref="CaptureModeController"/> (same
    /// construction shape as <c>--capture-demo</c>), a real <see cref="SileroVadDetector"/>, a
    /// real <see cref="PostProcessorChain"/> (built from <c>configService.Current.PostProcess</c>'s
    /// four toggles), and a real <see cref="SessionController"/> tying them together -- then
    /// starts it.
    ///
    /// <para>
    /// Never throws (plan §1.12: the daemon must never exit on a recoverable error) -- every
    /// failure point (model resolution, model init, hotkey start) is caught, logged at
    /// <see cref="LogLevel.Critical"/>, and returns
    /// <c>(null, null, null)</c> so the caller can keep running with no active session rather
    /// than letting an unhandled exception escape the caller's own entry point.
    /// </para>
    ///
    /// <para>
    /// <b>Phase 3 item 5 — return-shape widening, a small necessary extension, not a
    /// restructuring:</b> this used to return a 2-tuple (<c>Controller</c>, <c>CuePlayer</c>).
    /// The Recording HUD (<c>Soneto.App</c>) needs to subscribe directly to the
    /// <see cref="IAudioCapture.LevelChanged"/> event fired by the <see cref="PortAudioCapture"/>
    /// this method already constructs internally -- that instance was never exposed to callers
    /// before. Widened to a 3-tuple (<c>Controller</c>, <c>CuePlayer</c>, <c>AudioCapture</c>)
    /// rather than a new named record, since a positional named-tuple already documents each
    /// field via its own name and nothing here needs the extra ceremony of a record type.
    /// <c>Soneto.Daemon</c>'s own call site was updated to match this signature (it simply
    /// ignores the new third value) -- zero functional/behavioral change to
    /// <c>Soneto.Daemon</c> itself, confirmed by inspection: every other line of this method's
    /// body, every failure path, every log line, and every one of the two-values-returned early
    /// exits (now three-values, with the third slot as <c>null</c> in every early-exit branch
    /// exactly where <c>Controller</c>/<c>CuePlayer</c> were already <c>null</c>) is unchanged.
    /// </para>
    /// </summary>
    public static async Task<(SessionController? Controller, AudioCuePlayer? CuePlayer, IAudioCapture? AudioCapture)> BuildAndStartSessionControllerAsync(
        SonetoConfig config,
        IReadOnlyList<DictionaryEntry> dictionaryEntries,
        ILoggerFactory loggerFactory,
        ILogger logger,
        IHotkeySource hotkeySource,
        ITextInjector textInjector,
        CancellationToken ct)
    {
        string? effectiveModelDirOverride = config.Asr.ModelDir;
        if (string.IsNullOrWhiteSpace(effectiveModelDirOverride))
        {
            var devModelDir = FindDevModelDirWalkingUp();
            if (devModelDir != null && ModelManager.AreRequiredFilesPresent(devModelDir))
            {
                logger.LogInformation("SessionController startup: using repo-local dev model dir: {Dir}", devModelDir);
                effectiveModelDirOverride = devModelDir;
            }
        }

        var modelManager = new ModelManager(loggerFactory.CreateLogger<ModelManager>());
        string modelDir;
        try
        {
            modelDir = await modelManager.ResolveOrDownloadAsync(effectiveModelDirOverride, ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "SessionController startup FAILED: could not resolve the ASR model. Dictation will not be "
                + "available until this is fixed and the daemon is restarted; the daemon itself keeps running.");
            return (null, null, null);
        }

        var transcriber = new SherpaOnnxTranscriber(
            loggerFactory.CreateLogger<SherpaOnnxTranscriber>(), modelDir, config.Asr.NumThreads, config.Asr.DecodingMethod);
        try
        {
            await transcriber.InitializeAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "SessionController startup FAILED: ASR model failed to initialize. Dictation will not be "
                + "available until this is fixed and the daemon is restarted; the daemon itself keeps running.");
            await transcriber.DisposeAsync();
            return (null, null, null);
        }

        var capture = new PortAudioCapture(
            loggerFactory.CreateLogger<PortAudioCapture>(), preRollCapacityMs: config.Audio.PreRollMs);
        var cuePlayer = new AudioCuePlayer(loggerFactory.CreateLogger<AudioCuePlayer>());
        var captureController = new CaptureModeController(
            capture,
            loggerFactory.CreateLogger<CaptureModeController>(),
            config.Audio.CaptureMode,
            config.Audio.IdleCloseMs,
            config.Audio.PreRollMs,
            device: null,
            // Always passed, even when readyCue=None -- CaptureModeController gates the routine
            // ready cue on readyCueMode itself but plays the failure cue unconditionally of it
            // (see its own doc comment); passing null here would silently defeat that.
            cuePlayer: cuePlayer,
            readyCueMode: config.Audio.ReadyCue);

        var vad = new SileroVadDetector(loggerFactory.CreateLogger<SileroVadDetector>(), config.Audio.Vad);

        // Phase 4 item 3 (§4.4): resolved once, here at composition time, exactly mirroring
        // item 2's InjectionConfig.PerApp precedent (see CreatePlatformHotkeySourceAndTextInjector's
        // own comment) -- see BuildDictionaryPerAppTable's own doc comment for what's filtered
        // and why case-insensitive matching is decided here, not inside PostProcessorChain.
        var dictionaryPerApp = BuildDictionaryPerAppTable(dictionaryEntries);
        var postProcessorChain = new PostProcessorChain(
            BuildPostProcessors(config.PostProcess, dictionaryEntries, loggerFactory), dictionaryPerApp);

        var options = SessionControllerOptions.FromConfig(config);

        var sessionController = new SessionController(
            hotkeySource, captureController, vad, transcriber, postProcessorChain, textInjector, options,
            loggerFactory.CreateLogger<SessionController>());

        try
        {
            await sessionController.StartAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "SessionController startup FAILED unexpectedly. Dictation will not be available until this "
                + "is fixed and the daemon is restarted; the daemon itself keeps running.");
            await sessionController.DisposeAsync();
            cuePlayer.Dispose();
            return (null, null, null);
        }

        if (sessionController.State == SessionState.Faulted)
        {
            // StartAsync itself never throws for the "model not ready" / "hook failed to start"
            // rows -- it transitions to Faulted and returns normally (see its own doc comment).
            // Surface that just as loudly here rather than silently returning a dead controller.
            logger.LogCritical(
                "SessionController started but is already Faulted (see the preceding log line for the exact "
                + "cause). Dictation will not be available until this is fixed and the daemon is restarted.");
        }
        else
        {
            logger.LogInformation(
                "SessionController started: real end-to-end dictation is live (trigger={Trigger}).",
                config.Hotkey.Key);
        }

        return (sessionController, cuePlayer, capture);
    }

    /// <summary>
    /// Builds the full seven-stage post-processor chain (Phase 1's four plus Phase 2's three) from
    /// <c>configService.Current.PostProcess</c>'s toggles and the real loaded
    /// <c>dictionaryService.Current.Entries</c>, per plan §1.7/item 8 (Phase 1), updated by Phase 2
    /// item 6 (spoken commands migration) and item 10 (this item -- wiring the remaining three
    /// dictionary-backed processors with real entries instead of an empty list / not constructing
    /// them at all). <see cref="UnicodeNormalizerProcessor"/> has no toggle at all (deliberately
    /// "always on," see its own doc comment) -- <c>PostProcessConfig.NormalizeUnicode</c> exists in
    /// the schema but is not wired to a disable switch, matching that class's documented decision.
    ///
    /// <para>
    /// <b>Phase 2 item 6 (unchanged by this item):</b> Phase 1's <c>SpokenCommandsProcessor</c>
    /// (order 20, a small fixed EN/RO table) is retired; <see cref="SpokenCommandsExtensionProcessor"/>
    /// (order 60) takes its place, reusing the same <c>config.SpokenCommands</c> enable/disable
    /// toggle.
    /// </para>
    ///
    /// <para>
    /// <b>Phase 2 item 10 (this item):</b> <paramref name="dictionaryEntries"/> -- the real, validated
    /// <c>dictionaryService.Current.Entries</c> snapshot taken at daemon startup -- is now threaded
    /// into all three entry-backed processors that were previously either not constructed at all
    /// (<see cref="DictionaryEngineProcessor"/>, <see cref="RegexRuleProcessor"/>) or constructed with
    /// an empty entry list (<see cref="SpokenCommandsExtensionProcessor"/>, which used to get
    /// <c>[]</c> here since no <c>DictionaryService</c> existed yet to source entries from).
    /// <see cref="FillerWordStripper"/> is NOT entry-backed (per item 9's own design note -- there is
    /// no <c>dictionary.json</c> schema type for filler words) and gets its own dedicated
    /// <c>config.FillerWordStripping</c> toggle instead. <see cref="DictionaryEngineProcessor"/>/
    /// <see cref="RegexRuleProcessor"/> each get their OWN separate toggle
    /// (<c>config.DictionaryEngine</c>/<c>config.RegexRules</c>) rather than one shared "dictionary"
    /// switch -- see <see cref="PostProcessConfig.DictionaryEngine"/>'s doc comment for why.
    /// </para>
    ///
    /// <para>
    /// <b>Honest, deliberate limitation -- no live rebuild into a running chain:</b> this function is
    /// called exactly once, at daemon startup, from a snapshot of both <c>config.PostProcess</c> and
    /// <paramref name="dictionaryEntries"/>. Neither a hot-reloaded <c>config.json</c> nor a
    /// hot-reloaded <c>dictionary.json</c> rebuilds the already-constructed
    /// <see cref="PostProcessing.PostProcessorChain"/> live -- see
    /// <see cref="LoadAndStartWatchingDictionaryAsync"/>'s <c>DictionaryChanged</c> handler for the
    /// loud, explicit warning logged instead of silently doing nothing. This is a pre-existing gap
    /// for <c>config.PostProcess</c> (Phase 1 never rebuilt the chain either) that Phase 2 does not
    /// attempt to close, per plan §1.16's own instruction to treat "this needs a
    /// <see cref="SessionController"/> change" as a signal to stop and reconsider rather than a
    /// challenge to solve.
    /// </para>
    /// </summary>
    public static IEnumerable<IPostProcessor> BuildPostProcessors(
        PostProcessConfig config,
        IReadOnlyList<DictionaryEntry> dictionaryEntries,
        ILoggerFactory loggerFactory)
    {
        yield return new UnicodeNormalizerProcessor();
        yield return new WhitespaceCleanerProcessor(config.CleanWhitespace);
        yield return new DictionaryEngineProcessor(dictionaryEntries, config.DictionaryEngine);
        yield return new RegexRuleProcessor(
            dictionaryEntries, config.RegexRules, loggerFactory.CreateLogger<RegexRuleProcessor>());
        yield return new SpokenCommandsExtensionProcessor(dictionaryEntries, config.SpokenCommands);
        yield return new FillerWordStripper(config.FillerWordStripping);
        yield return new TrailingSpaceProcessor(config.TrailingSpace);
    }

    /// <summary>
    /// Phase 4 item 3 (§4.4): builds the dictionary-side per-app profile table
    /// <see cref="PostProcessorChain"/>'s two-argument constructor consumes, from the same
    /// <paramref name="dictionaryEntries"/> snapshot <see cref="BuildPostProcessors"/> already
    /// reads. Filters <paramref name="dictionaryEntries"/> down to enabled
    /// <see cref="Soneto.Core.Dictionary.PerAppOverride"/> entries only (a disabled entry -- per
    /// <see cref="Soneto.Core.Dictionary.DictionaryEntry.Enabled"/> -- must never be selectable,
    /// filtered here once rather than re-checked on every utterance, matching
    /// <see cref="DictionaryEngineProcessor"/>/<see cref="RegexRuleProcessor"/>/
    /// <see cref="SpokenCommandsExtensionProcessor"/>'s own established "filter disabled entries
    /// at construction time" convention), keyed by
    /// <see cref="Soneto.Core.Dictionary.PerAppOverride.ProcessName"/> with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> -- the exact same case-insensitivity
    /// decision <see cref="CreatePlatformHotkeySourceAndTextInjector"/> already makes for the
    /// injection-side table, made here, once, for the same reason (Windows filename
    /// conventions); <see cref="Soneto.Core.Dictionary.PerAppOverrideResolver"/> itself applies
    /// no comparer of its own, mirroring <c>Configuration.PerAppOverrideResolver</c>'s identical
    /// contract. A later duplicate <see cref="Soneto.Core.Dictionary.PerAppOverride.ProcessName"/>
    /// (case-insensitively) overwrites an earlier one -- <c>dictionary.json</c> authoring is not
    /// expected to produce duplicates, and silently keeping "last one wins" here matches
    /// <c>Dictionary<![CDATA[<]]>TKey,TValue<![CDATA[>]]></c>'s own natural construction-from-pairs
    /// semantics rather than adding bespoke collision handling for a case nothing else in this
    /// codebase's dictionary loading defends against either.
    /// </summary>
    public static IReadOnlyDictionary<string, Soneto.Core.Dictionary.PerAppOverride> BuildDictionaryPerAppTable(
        IReadOnlyList<Soneto.Core.Dictionary.DictionaryEntry> dictionaryEntries)
    {
        var table = new Dictionary<string, Soneto.Core.Dictionary.PerAppOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in dictionaryEntries)
        {
            if (entry is Soneto.Core.Dictionary.PerAppOverride perAppOverride && perAppOverride.Enabled)
            {
                table[perAppOverride.ProcessName] = perAppOverride;
            }
        }
        return table;
    }

    // DEV-CONVENIENCE ONLY. The returned directory is fed straight in as a config override,
    // which SKIPS hash verification entirely (config overrides are trusted as
    // already-complete per ModelManager.ResolveOrDownloadAsync). This is only safe because
    // the requirement below (a soneto.slnx marker alongside the discovered models/ dir, same
    // check as SherpaOnnxTranscriberCorpusTests.FindRepoRoot) ties it to a real Soneto repo
    // checkout with a model that was presumably verified when it was first downloaded. Do NOT
    // reuse this helper — or its "walk up and check any ancestor dir" pattern — for real
    // daemon model resolution without re-adding hash verification.
    //
    // Kept public (not private) so Soneto.Daemon's own RunTranscribeCommandCoreAsync/
    // TryBonusTranscribeAsync CLI-only helpers -- which stay in Program.cs per this item's
    // scope, since those CLI commands are harness-specific and Soneto.App will never need
    // them -- can still call this exact same dev-convenience lookup instead of Program.cs
    // keeping its own duplicate copy.
    public static string? FindDevModelDirWalkingUp()
    {
        const string modelFolderName = ModelManager.ModelFolderName;
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "models", modelFolderName);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "soneto.slnx")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
