using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soneto.Composition;
using Soneto.Core;
using Soneto.Core.Abstractions;
using Soneto.Core.Configuration;
using Soneto.Core.Dictionary;

namespace Soneto.App;

/// <summary>
/// Phase 3 item 5's sub-task A: the FIRST place in this whole phase that wires the real
/// dictation pipeline (<see cref="SessionController"/>, real audio capture, the real global
/// hotkey) into <c>Soneto.App</c> — an explicit ordering decision made HERE, in this item,
/// not left implicit. Nothing in the build order (items 0-11) previously did this wiring;
/// items 5, 6, 8, 9 all implicitly assumed a running session existed, but item 5 is the
/// first one that actually NEEDS one (a HUD showing recording state/level has nothing to
/// react to without a real session). Item 6 (History) will later reuse the SAME already
/// running <see cref="SessionController"/> instance this class exposes to subscribe to
/// <c>DictationCompleted</c> — it does not re-wire anything.
///
/// <para>
/// Mirrors <c>Soneto.Daemon/Program.cs</c>'s own real daemon-startup sequence exactly (see
/// that file's calls to <see cref="DaemonComposition.CreateConfigService"/>/
/// <see cref="DaemonComposition.CreateDictionaryService"/>/
/// <see cref="DaemonComposition.LoadAndStartWatchingConfigAsync"/>/
/// <see cref="DaemonComposition.LoadAndStartWatchingDictionaryAsync"/>/
/// <see cref="DaemonComposition.CreatePlatformHotkeySourceAndTextInjector"/>/
/// <see cref="DaemonComposition.BuildAndStartSessionControllerAsync"/>) rather than
/// re-deriving the sequence — this class does not duplicate any of that composition logic,
/// it only calls it in the same order <c>Soneto.Daemon</c> already does.
/// </para>
///
/// <para>
/// <b>Fire-and-forget, non-blocking, non-fatal:</b> <see cref="StartInBackground"/> is
/// called from <c>App.axaml.cs</c> AFTER the main window/tray are already shown — ASR cold
/// model load alone is ~1.7-2s, and the app's tray/window must appear immediately regardless
/// of whether the pipeline ever comes up. Every failure point is caught here as an extra,
/// outermost safety net on top of <see cref="DaemonComposition.BuildAndStartSessionControllerAsync"/>'s
/// own already-established "never throws, logs critical, returns null" contract — if
/// startup fails for any reason (no model, no mic, hotkey source fails to start, etc.), this
/// class logs it and simply never raises <see cref="Started"/>; the app keeps running with
/// the HUD feature inactive, not crashed or half-wired.
/// </para>
/// </summary>
public static class PipelineHost
{
    /// <summary>
    /// Raised exactly once, on whatever thread the background startup task happens to run
    /// on (NOT guaranteed to be the UI thread) — subscribers (e.g. <c>RecordingHud</c>) must
    /// marshal any UI work themselves, the same discipline every other event this class's
    /// doc comment mentions already requires. Only raised when startup produces a real,
    /// non-null <see cref="SessionController"/> — never raised on failure.
    /// </summary>
    public static event EventHandler<PipelineStartedEventArgs>? Started;

    /// <summary>
    /// Item 9 (§3.13, the Permissions Doctor): raised exactly once, on whatever thread the
    /// background startup task runs on, ONLY when real pipeline startup did NOT produce a
    /// usable <see cref="SessionController"/> — either <see cref="DaemonComposition.BuildAndStartSessionControllerAsync"/>
    /// returned a null controller, or an unexpected exception was thrown. This is the
    /// counterpart to <see cref="Started"/> the Permissions Doctor's "global hook active"
    /// check needs to report an honest "not tested — pipeline is not running, and here is
    /// why" instead of silently having no idea a failure ever happened.
    /// </summary>
    public static event EventHandler<PipelineFailedEventArgs>? Failed;

    /// <summary>
    /// Guards <see cref="_lastStarted"/>/<see cref="_lastFailureReason"/> — the background
    /// startup task writes one of them on a thread pool thread, and a future late subscriber
    /// could read them from a different thread (e.g. the UI thread). Post-review fix for item
    /// 9: the fields were previously plain auto-properties with no ordering/visibility
    /// guarantee across threads.
    /// </summary>
    private static readonly object _lastResultLock = new();
    private static PipelineStartedEventArgs? _lastStarted;
    private static string? _lastFailureReason;

    /// <summary>
    /// Item 9: the most recent <see cref="Started"/> payload, or null if the pipeline has
    /// never started successfully this session. A late subscriber (constructed after
    /// <see cref="Started"/> already fired) can read this directly instead of missing the
    /// event — not currently exercised by any subscriber in this codebase (every existing
    /// subscriber, including the Permissions Doctor, is constructed before
    /// <see cref="StartInBackground"/> is ever called — see <c>App.axaml.cs</c>'s
    /// composition-root ordering), but cheap, correct insurance against a future ordering
    /// change reintroducing a race.
    /// </summary>
    public static PipelineStartedEventArgs? LastStarted
    {
        get { lock (_lastResultLock) { return _lastStarted; } }
        private set { lock (_lastResultLock) { _lastStarted = value; } }
    }

    /// <summary>Item 9: the most recent <see cref="Failed"/> payload's reason, or null if
    /// the pipeline has never failed to start this session. Same late-subscriber safety net
    /// as <see cref="LastStarted"/>.</summary>
    public static string? LastFailureReason
    {
        get { lock (_lastResultLock) { return _lastFailureReason; } }
        private set { lock (_lastResultLock) { _lastFailureReason = value; } }
    }

    /// <summary>
    /// Kicks off real pipeline startup as a fire-and-forget background task. Safe to call
    /// exactly once, from the composition root, after the main window is already visible.
    /// </summary>
    /// <param name="loggerFactory">Shared app logger factory.</param>
    /// <param name="configService">
    /// Phase 3 item 8's architecture decision (mirroring item 7's <c>IDictionaryService</c>
    /// decoupling, which itself mirrored item 6's <c>IHistoryStore</c> decoupling): the
    /// ALREADY-CONSTRUCTED, already-loaded-and-watching <see cref="IConfigService"/> from the
    /// composition root (<c>App.axaml.cs</c>) — this class no longer constructs its own via
    /// <see cref="DaemonComposition.CreateConfigService"/>/
    /// <see cref="DaemonComposition.LoadAndStartWatchingConfigAsync"/>. The Settings page (§3.12)
    /// needs a synchronously-loaded config available before the UI is even shown (a hand-edited
    /// <c>config.json</c> must show its real values immediately), independent of whether the ASR
    /// model/pipeline below ever comes up this session — so the composition root now owns that
    /// service's whole lifecycle, and simply hands it in here so the real pipeline composition
    /// can still be built from its already-loaded <see cref="IConfigService.Current"/> exactly
    /// as before.
    /// </param>
    /// <param name="dictionaryService">
    /// Phase 3 item 7's architecture decision (mirroring item 6's <c>IHistoryStore</c>
    /// decoupling): the ALREADY-CONSTRUCTED, already-loaded-and-watching
    /// <see cref="IDictionaryService"/> from the composition root (<c>App.axaml.cs</c>) — this
    /// class no longer constructs its own via <see cref="DaemonComposition.CreateDictionaryService"/>/
    /// <see cref="DaemonComposition.LoadAndStartWatchingDictionaryAsync"/>. The Dictionary
    /// editor (§3.11) needs a synchronously-loaded dictionary available before the UI is even
    /// shown, independent of whether the ASR model/pipeline below ever comes up this session —
    /// so the composition root now owns that service's whole lifecycle, and simply hands it in
    /// here so the real post-processor chain can still be built from its already-loaded
    /// <see cref="IDictionaryService.Current"/> entries exactly as before.
    /// </param>
    public static void StartInBackground(
        ILoggerFactory loggerFactory, IConfigService configService, IDictionaryService dictionaryService)
    {
        _ = StartAsync(loggerFactory, configService, dictionaryService);
    }

    private static async Task StartAsync(
        ILoggerFactory loggerFactory, IConfigService configService, IDictionaryService dictionaryService)
    {
        var logger = loggerFactory.CreateLogger("PipelineHost");
        try
        {
            logger.LogInformation("Soneto.App: starting real pipeline composition in the background");

            var (hotkeySource, textInjector) =
                DaemonComposition.CreatePlatformHotkeySourceAndTextInjector(loggerFactory, configService.Current);

            var (controller, _, audioCapture) = await DaemonComposition.BuildAndStartSessionControllerAsync(
                configService.Current, dictionaryService.Current.Entries, loggerFactory, logger,
                hotkeySource, textInjector, CancellationToken.None);

            if (controller is null)
            {
                // BuildAndStartSessionControllerAsync already logged the critical reason
                // (no model, mic open failure, hook failed to start, etc.) — nothing more
                // to do here except leave the HUD permanently inactive for this run.
                const string reason =
                    "Real pipeline did not start (see the preceding application log line for the exact "
                    + "cause — e.g. a missing/invalid ASR model, a microphone open failure, or a hotkey "
                    + "source startup failure).";
                logger.LogWarning(
                    "Soneto.App: {Reason} The app keeps running; the Recording HUD stays inactive.", reason);
                LastFailureReason = reason;
                Failed?.Invoke(null, new PipelineFailedEventArgs(reason));
                return;
            }

            logger.LogInformation("Soneto.App: real pipeline started successfully");
            // Item 9 (§3.13): the real, already-started IHotkeySource is exposed here too —
            // NOT a second instance, the exact same one CreatePlatformHotkeySourceAndTextInjector
            // just constructed above and handed into BuildAndStartSessionControllerAsync. The
            // Permissions Doctor's "global hook active" check subscribes to THIS instance's own
            // Faulted event rather than ever constructing its own IHotkeySource (SharpHook cannot
            // run two concurrent hook instances in one process on this machine — see
            // Docs/PROJECT-MEMORY.md item 6's writeup).
            var args = new PipelineStartedEventArgs(controller, audioCapture, hotkeySource);
            LastStarted = args;
            Started?.Invoke(null, args);
        }
        catch (Exception ex)
        {
            // Outermost safety net: everything called above already has its own
            // never-throws contract, but this class's own instruction is explicit —
            // pipeline startup must be completely non-fatal to Soneto.App on ANY failure.
            logger.LogCritical(ex,
                "Soneto.App: unexpected error during real pipeline startup. The app keeps "
                + "running; the Recording HUD stays inactive.");
            string reason = $"Unexpected error during real pipeline startup: {ex.Message}";
            LastFailureReason = reason;
            Failed?.Invoke(null, new PipelineFailedEventArgs(reason));
        }
    }
}

/// <summary>
/// Carries the real, already-started <see cref="SessionController"/>/<see cref="IAudioCapture"/>/
/// <see cref="IHotkeySource"/> from <see cref="PipelineHost.StartAsync"/> to whatever subscribes
/// to <see cref="PipelineHost.Started"/> (<c>RecordingHud</c>, the History append hook, and item
/// 9's Permissions Doctor — the last of which is the first subscriber to actually need
/// <see cref="HotkeySource"/>, not just <see cref="Controller"/>/<see cref="AudioCapture"/>).
/// </summary>
public sealed record PipelineStartedEventArgs(SessionController Controller, IAudioCapture? AudioCapture, IHotkeySource HotkeySource);

/// <summary>Item 9: carries the reason real pipeline startup did not produce a usable
/// <see cref="SessionController"/> — see <see cref="PipelineHost.Failed"/>'s doc comment.</summary>
public sealed record PipelineFailedEventArgs(string Reason);
