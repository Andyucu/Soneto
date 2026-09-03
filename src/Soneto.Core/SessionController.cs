using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Soneto.Core.Abstractions;
using Soneto.Core.Asr;
using Soneto.Core.Audio;
using Soneto.Core.Configuration;
using Soneto.Core.PostProcessing;

namespace Soneto.Core;

/// <summary>States of plan §1.4's push-to-talk state machine.</summary>
public enum SessionState
{
    Initializing,
    Idle,
    Recording,
    Finalizing,
    Transcribing,
    Injecting,
    Cooldown,
    Faulted,
}

/// <summary>Raised by <see cref="SessionController"/> whenever <see cref="SessionController.State"/> changes.</summary>
public sealed record SessionStateChangedEventArgs(SessionState From, SessionState To);

/// <summary>
/// Raised by <see cref="SessionController"/> exactly once per completed dictation that produced
/// text (plan §3.6) -- see <see cref="SessionController.DictationCompleted"/>'s own doc comment
/// for exactly which code paths raise this and which "nothing to inject" exit paths deliberately
/// do not.
/// </summary>
/// <param name="AudioSamples">
/// Phase 3 item 10 (§3.14, data &amp; privacy controls): the recorded audio actually fed to the
/// transcriber -- i.e. <c>SileroVadDetector.Trim</c>'s <c>VadTrimResult.TrimmedSamples</c>, at
/// <see cref="Soneto.Core.Audio.CaptureFormatSelector.TargetSampleRate"/> (16kHz mono), NOT the
/// raw pre-VAD-trim buffer. <b>Design decision, documented here per this item's own instruction
/// (mirroring item 2's own reasoning-in-the-open standard for touching this file):</b> the
/// TRIMMED samples were chosen over the raw untrimmed recording because they are exactly what
/// produced <see cref="RawText"/> -- for a debugging aid (this field's only consumer,
/// <c>Soneto.App</c>'s opt-in "keep last N clips for debugging" toggle, off by default per plan
/// §8), "what did the model actually hear" is strictly more useful than "everything the
/// microphone captured, including any leading/trailing silence VAD discarded." Threaded through
/// mechanically, the same way item 2 threaded <c>RecordingDuration</c>/<c>RulesFired</c>: an
/// existing local (<c>RunFinalizingAsync</c>'s <c>trim.TrimmedSamples</c>, itself a zero-copy
/// <see cref="ReadOnlyMemory{T}"/> view over the buffer <c>CaptureModeController.EndUtteranceAsync</c>
/// already returned) is passed down through <c>RunTranscribingAsync</c>/<c>RunInjectingAsync</c>
/// as one more parameter -- no new allocation, no restructuring of the call chain's shape. Always
/// non-empty when this event fires (every "nothing to inject" exit path that could produce empty
/// audio -- below-<see cref="SessionControllerOptions.MinDurationMs"/> discard, VAD-discard,
/// audio-device-lost -- returns before <c>RunInjectingAsync</c>/this event is ever reached; see
/// <see cref="SessionController.DictationCompleted"/>'s own doc comment for the full list).
/// </param>
public sealed record DictationCompletedEventArgs(
    string RawText, string FinalText, IReadOnlyList<AppliedRule> RulesFired,
    TimeSpan RecordingDuration, TimeSpan ProcessingLatency, bool WasInjected,
    ReadOnlyMemory<float> AudioSamples);

/// <summary>
/// Narrow, platform-agnostic options <see cref="SessionController"/> needs out of
/// <see cref="SonetoConfig"/>, mirroring <see cref="HotkeyConfig.ToBinding"/>/
/// <see cref="InjectionConfig.ToOptions"/>'s established config-vs-runtime-abstraction split
/// (see those types' doc comments) -- <see cref="SessionController"/>'s own constructor takes
/// this record, not a whole <see cref="SonetoConfig"/>, so it never gains a hard dependency on
/// config sections it doesn't use (PostProcessConfig/LoggingConfig/etc., which the composition
/// root reads separately to build <see cref="PostProcessorChain"/>/Serilog). Defaults mirror
/// <see cref="AudioConfig"/>/<see cref="AsrConfig"/>'s own field defaults.
/// </summary>
public sealed record SessionControllerOptions(
    HotkeyBinding HotkeyBinding,
    InjectionOptions InjectionOptions,
    int MinDurationMs = 250,
    int MaxDurationMs = 120_000,
    int LongUtteranceCueMs = 15_000,
    int AsrTimeoutMs = 10_000,
    // Watchdog backoff shape (the "any | hook faulted" row) -- not part of plan §1.10's JSON
    // schema (the plan doesn't specify these as config knobs), kept as options-record fields
    // rather than hardcoded constants purely so SessionControllerTests can drive the recovery
    // path (including the "all attempts fail" branch) in milliseconds instead of the real
    // ~31s worst case, without resorting to reflection into private statics. Defaults are the
    // documented production shape: 5 attempts, exponential backoff starting at 1s (1s/2s/4s/
    // 8s/16s) -- see the class doc comment's "Watchdog backoff shape" paragraph.
    int MaxHookRestartAttempts = 5,
    TimeSpan? HookRestartInitialBackoff = null)
{
    /// <summary>Effective initial backoff: <see cref="HookRestartInitialBackoff"/> if set, else the documented 1s default.</summary>
    public TimeSpan EffectiveHookRestartInitialBackoff => HookRestartInitialBackoff ?? TimeSpan.FromSeconds(1);

    /// <summary>Convenience factory for the composition root, matching <c>HotkeyConfig.ToBinding()</c>'s pattern.</summary>
    public static SessionControllerOptions FromConfig(SonetoConfig config) => new(
        config.Hotkey.ToBinding(),
        config.Injection.ToOptions(config.Hotkey.Key),
        config.Audio.MinDurationMs,
        config.Audio.MaxDurationMs,
        config.Audio.LongUtteranceCueMs,
        config.Asr.TimeoutMs);
}

/// <summary>
/// The heart of the daemon (plan §1.4): drives the push-to-talk state machine
/// <c>Initializing → Idle → Recording → Finalizing → Transcribing → Injecting → Cooldown →
/// Idle</c>, plus <c>Faulted</c>, wiring together every component built by items 1-8
/// (<see cref="IHotkeySource"/>, <see cref="CaptureModeController"/>, <see cref="SileroVadDetector"/>,
/// <see cref="ITranscriber"/>, <see cref="PostProcessorChain"/>, <see cref="ITextInjector"/>).
///
/// <para>
/// <b>Threading model (plan §1.4, non-negotiable): one dedicated session worker consuming a
/// <see cref="Channel{T}"/>.</b> <see cref="IHotkeySource.Pressed"/>/<see cref="IHotkeySource.Released"/>/
/// <see cref="IHotkeySource.Faulted"/> handlers here do nothing but post a small
/// <c>SessionCommand</c> struct into an unbounded channel and return -- the same "never blocks,
/// just posts and returns" discipline <see cref="IHotkeySource"/>'s own doc comment requires of
/// its subscribers, and the same pattern <c>WindowsHotkeySource</c> itself uses one layer down.
/// A single worker <see cref="Task"/> (<see cref="RunWorkerLoopAsync"/>) drains the channel and
/// processes exactly one command at a time, fully <c>await</c>ing each command's entire
/// state-machine handler (including the async calls into capture/VAD/ASR/injection) before
/// pulling the next command off the channel. This single fact is what makes every other
/// concurrency property below hold without extra locks:
/// </para>
/// <list type="bullet">
/// <item><description><b>Edge case 1 (key-down while not Idle) is enforced simply by checking
/// <see cref="State"/> at the top of the key-down handler</b> -- since the worker is
/// single-threaded and processes commands strictly in order, by the time a second key-down is
/// dequeued the first one's transition (if any) has already fully applied.</description></item>
/// <item><description><b>A stray <c>Released</c> firing while the worker is still mid-way through
/// a <c>Pressed</c> handler</b> cannot race the state field: it just waits in the channel
/// until the worker finishes the current command and calls <c>ReadAsync</c> again. By the time
/// key-up is processed, <see cref="State"/> already correctly reflects whatever key-down decided
/// (Recording, or still Idle if the guard rejected it).</description></item>
/// <item><description><b><c>Faulted</c> firing mid-transcription/mid-injection</b> is handled the
/// same way: the <c>HookFaulted</c> command simply queues behind whatever command is currently
/// being processed and is only handled once that finishes naturally. The one state where a hook
/// fault must actively interrupt in-flight work is <c>Recording</c> (the only state in which the
/// worker has returned to await the *next* channel item while real-world audio capture continues
/// in the background) -- <see cref="HandleHookFaultedAsync"/> special-cases exactly that: it
/// aborts the orphaned capture (a fault means the key-up that would have ended it can now never
/// arrive) before attempting recovery.</description></item>
/// <item><description><b><see cref="DisposeAsync"/> racing an in-flight transition</b>: disposal
/// cancels the worker's own <see cref="CancellationToken"/> and completes the channel, then
/// awaits the worker task to actually finish (rather than tearing down owned resources
/// concurrently with whatever the worker is still doing) -- so a command that's mid-flight when
/// <c>DisposeAsync</c> is called either finishes and its (fully-owned, single-writer) state
/// update lands before disposal proceeds, or observes cancellation via the token threaded into
/// its own <c>Task.Delay</c>/<c>RestartAsync</c> calls and unwinds via
/// <see cref="OperationCanceledException"/>, which the worker loop swallows as the normal
/// shutdown path.</description></item>
/// </list>
///
/// <para>
/// <b>Edge case 2 (focus changed during transcription) -- already fully handled one layer down,
/// verified by reading the source, not assumed.</b> <c>WindowsTextInjector.InjectAsync</c>'s own
/// doc comment/body (steps 1-2) states: "This item supports only <c>targetLostPolicy="current"</c>
/// ... SendInput always delivers to whatever window currently has OS focus, not to a specific
/// HWND -- there is no way to target a specific, possibly-not-foreground window with it. So the
/// handle that actually matters here is the CURRENT foreground window, unconditionally fetched
/// below, not necessarily the one captured at [key-down]." <see cref="SessionController"/>
/// therefore does exactly what the plan's edge case 2 describes as sufficient: it passes the
/// target captured at key-down (via <see cref="ITextInjector.CaptureTarget"/>) through to
/// <see cref="ITextInjector.InjectAsync"/> completely unchanged and trusts the injector's own
/// policy -- no extra "did focus change" logic lives here, because the injector already logs
/// both handles and re-resolves the live foreground window itself. Nothing to duplicate.
/// </para>
///
/// <para>
/// <b>Edge case 4 (trigger key held during injection) -- already fully handled two layers down,
/// verified by reading the source, not assumed.</b> The plan's concern is specifically "the
/// injector's own synthetic paste-chord modifiers colliding with the trigger key." Two separate,
/// already-shipped fixes cover this completely:
/// </para>
/// <list type="bullet">
/// <item><description><c>WindowsHotkeySource</c>'s doc comment ("Self-injected keyboard events
/// are never treated as a trigger press"): it checks SharpHook's <c>HookEventArgs.IsEventSimulated</c>
/// (backed by the Windows <c>LLKHF_INJECTED</c> flag) and ignores any trigger-key-coded event
/// that is self-injected -- so <see cref="WindowsTextInjector"/>'s own synthetic
/// <c>VK_LCONTROL</c>/<c>VK_LSHIFT</c> paste-chord keystrokes can never be misread as a second,
/// phantom trigger press/release, regardless of which physical key the trigger is bound
/// to.</description></item>
/// <item><description><c>ModifierSanitizer</c>'s doc comment (item 7b): before sending the paste
/// chord, it suppresses physically-held Shift/Alt/Win/Left-Control, excluding whichever single
/// VK code the configured trigger itself resolves to (via <c>InjectionOptions.TriggerKey</c>, the
/// exact config-schema trigger string this class forwards unchanged through
/// <c>SessionControllerOptions.InjectionOptions</c>) -- so a trigger that happens to be a
/// modifier key never has its own physical-hold state misread as "the user wants this suppressed
/// for paste purposes."</description></item>
/// </list>
/// <para>
/// Given both, <see cref="SessionController"/> needs to do nothing extra for edge case 4 beyond
/// calling <see cref="ITextInjector.InjectAsync"/> normally with the trigger-aware
/// <see cref="InjectionOptions"/> it was constructed with -- which is exactly what
/// <see cref="RunInjectingAsync"/> does.
/// </para>
///
/// <para>
/// <b>Edge case 3 (key stuck down)</b> is not a separate mechanism from the "max duration timer"
/// row -- <see cref="HandleMaxDurationElapsedAsync"/> IS this edge case's handling; there is
/// deliberately no second code path.
/// </para>
///
/// <para>
/// <b>"Audio device lost" (item 10: reconciled the STATE MACHINE behaviour with plan §1.12
/// over item 9's literal §1.4 reading; the underlying reachability gap this paragraph used to
/// overclaim as solved is now documented honestly below, per code review).</b> Neither
/// <see cref="IAudioCapture"/> nor <see cref="CaptureModeController"/> exposes a "device lost"
/// event (confirmed by reading both types) -- the only way a mid-recording hardware failure
/// could ever surface to this class is as an <see cref="Exception"/> thrown out of
/// <see cref="CaptureModeController.EndUtteranceAsync"/>/<see cref="CaptureModeController.AbortUtteranceAsync"/>
/// while <see cref="SessionState.Recording"/>. Item 9 originally read plan §1.4's literal table
/// row ("Recording | audio device lost → Faulted") and transitioned straight to the permanent
/// <see cref="SessionState.Faulted"/> state on this exception; plan §1.12's error-handling
/// matrix is more specific about what "device lost" should actually do: "abort session, fall
/// back to default device", recovery = <c>auto</c>, not a permanent fault.
/// <see cref="FinalizeRecordingAsync"/>'s catch block now follows §1.12 instead: it
/// best-effort aborts the capture (<see cref="SafeAbortCaptureAsync"/>, unchanged) and takes
/// the SAME normal end-of-utterance path every other non-fatal abort takes --
/// <see cref="EnterCooldownAsync"/> (→ <see cref="SessionState.Cooldown"/> →
/// <see cref="SessionState.Idle"/>) -- instead of <see cref="SessionState.Faulted"/>. If this
/// catch block DOES fire, the recovery is genuine: <see cref="Audio.CaptureDeviceResolver"/>
/// (item 4b) already re-resolves the configured device fresh on every subsequent
/// <c>StartAsync</c> call and automatically falls back to the system default if the
/// previously-configured device is gone, so the very next key-down naturally gets the
/// device-not-found-fallback-to-default behaviour §1.12 asks for, with no additional plumbing
/// needed here -- proven by <c>SessionControllerTests.Recording_AudioDeviceLost_SubsequentKeyDown_StartsAFreshRecordingNormally</c>.
/// </para>
/// <para>
/// <b>Honest, code-review-flagged gap: this catch block is very likely UNREACHABLE via the
/// real <c>PortAudioCapture</c>/<see cref="CaptureModeController"/> chain today.</b> Tracing
/// the actual production call chain: <c>EndUtteranceAsync()</c> →
/// <c>IAudioCapture.EndCapture()</c> (pure in-memory ring-buffer read -- does not throw for
/// hardware reasons) → <c>CaptureModeController.AfterUtteranceEndedAsync()</c> →
/// <c>CloseStreamAsync()</c> → <c>PortAudioCapture.StopAsync()</c>, whose only failure-prone
/// call (<c>stream.Stop()</c>) is wrapped in a <c>try/catch</c> that logs a warning and
/// deliberately NEVER rethrows (see that method's own "closes in <c>finally</c>, always" §1.12
/// comment). So a physically-removed device discovered at key-up time does not, in production
/// today, actually propagate an exception through this chain -- it is swallowed and logged,
/// and the recording finalizes "normally" with whatever partial/garbage audio was buffered.
/// This is not a regression introduced by item 10 -- item 9's original <c>Faulted</c> path had
/// the exact same reachability gap; item 10's job was correcting what happens IF this catch
/// block fires, which is done and verified above, not building new hardware-level device-loss
/// detection (a meaningfully bigger change than this item's scope, in the same category as
/// item 4b's own standing "no agent session can produce real audio/verify real hardware"
/// gaps). Today this catch block is best understood as a defensive backstop against whatever
/// unanticipated exception COULD reach it (a future PortAudio/native layer change, a different
/// <see cref="IAudioCapture"/> implementation, etc.), not a confirmed, exercised path for a
/// real unplugged-mic scenario. Wiring an actual device-loss signal up through
/// <c>PortAudioCapture</c>/<see cref="CaptureModeController"/> (e.g. surfacing
/// <c>stream.Stop()</c>'s swallowed exception, or a native callback-level status flag) remains
/// a real, open candidate for a future item if genuine hardware-level device-loss detection
/// during an active recording is wanted -- not silently claimed as already solved here.
/// </para>
/// <para>
/// A capture-open failure at key-down time (<see cref="HandleKeyDownAsync"/>) is unaffected by
/// any of the above -- since no recording was ever actually started, that stays in
/// <see cref="SessionState.Idle"/> rather than even transiently visiting Cooldown, matching the
/// spirit of the adjacent <c>!IsReady</c> row.
/// </para>
///
/// <para>
/// <b>"Model loaded" contract (feeds the <c>Initializing → Idle</c> row).</b> The CALLER is
/// responsible for having already run <see cref="ITranscriber.InitializeAsync"/> to completion
/// before constructing/starting this class -- <see cref="StartAsync"/> simply checks
/// <see cref="ITranscriber.IsReady"/> once at the top and treats <c>false</c> as
/// "model load failed" (→ <see cref="SessionState.Faulted"/>), never blocking waiting for it.
/// This keeps model warm-up entirely off this class's own lifecycle and matches how
/// <c>Soneto.Daemon</c>'s other commands (<c>--transcribe</c>) already sequence
/// <c>InitializeAsync</c> before use.
/// </para>
///
/// <para>
/// <b>Watchdog backoff shape (the "any | hook faulted" row), a documented choice since the plan
/// doesn't specify one.</b> Up to 5 <see cref="IHotkeySource.RestartAsync"/> attempts, exponential
/// backoff starting at 1s and doubling each failed attempt (1s/2s/4s/8s/16s, ~31s worst case) --
/// long enough to ride out a transient hook-install hiccup without hammering the OS hook API, short
/// enough that a human notices the daemon is still trying within well under a minute. On success,
/// returns to <see cref="SessionState.Idle"/>. If every attempt fails, per plan §1.12's "the daemon
/// never exits on a recoverable error" principle, this class transitions to and stays in
/// <see cref="SessionState.Faulted"/> -- meaning "hotkey capture is broken and this session can't
/// recover automatically," never "the process should exit." The daemon process itself keeps
/// running; a human/future item 10 concern is what happens next, not this class's job.
/// </para>
///
/// <para>
/// <b>Very long transcript (edge case 6).</b> <see cref="RunInjectingAsync"/> caps the
/// post-processed transcript at <see cref="TranscriptCharCap"/> (20,000) chars, applied AFTER
/// <see cref="PostProcessorChain.Process(string)"/> runs (post-processing can change length) and
/// BEFORE <see cref="ITextInjector.InjectAsync"/> is called, logging the truncation.
/// </para>
///
/// <para>
/// <b>Long-utterance processing cue (<see cref="SessionControllerOptions.LongUtteranceCueMs"/>).</b>
/// Per the work item's own explicit allowance ("a log line is sufficient for Phase 1, no audio cue
/// infrastructure beyond what item 4c already built"), this is a log-only line emitted once a
/// finished recording's duration meets/exceeds the configured threshold -- deliberately not wired
/// to <see cref="Audio.AudioCuePlayer"/>, which this class doesn't own an instance of (it lives
/// inside <see cref="CaptureModeController"/>, keyed to stream-open/ready events, not to this
/// unrelated post-recording signal). Same reasoning applies to the plan table's "log warn, beep"
/// wording on the <c>!IsReady</c> key-down guard -- log-only here for the same reason.
/// </para>
///
/// <para>
/// <b><see cref="DisposeAsync"/> ownership: this class owns disposal of everything it was
/// constructed with</b> (<see cref="IHotkeySource"/>, <see cref="CaptureModeController"/>,
/// <see cref="SileroVadDetector"/>, <see cref="ITranscriber"/>) -- deliberate choice, since in the
/// real daemon composition root (<c>Soneto.Daemon/Program.cs</c>) nothing else holds a reference
/// to any of these once <see cref="SessionController"/> is constructed; leaving them undisposed
/// after this class stops would just leak them. <see cref="ITextInjector"/> and
/// <see cref="PostProcessorChain"/> carry no disposal surface at all (neither implements
/// <see cref="IDisposable"/>/<see cref="IAsyncDisposable"/>), so there's nothing to do for
/// them.
/// </para>
/// </summary>
public sealed class SessionController : IAsyncDisposable
{
    /// <summary>Edge case 6: transcript char cap, applied after post-processing, before injection.</summary>
    public const int TranscriptCharCap = 20_000;

    private static readonly TimeSpan CooldownDelay = TimeSpan.FromMilliseconds(150);

    private readonly IHotkeySource _hotkeySource;
    private readonly CaptureModeController _captureController;
    private readonly SileroVadDetector _vad;
    private readonly ITranscriber _transcriber;
    private readonly PostProcessorChain _postProcessorChain;
    private readonly ITextInjector _textInjector;
    private readonly SessionControllerOptions _options;
    private readonly ILogger<SessionController> _logger;

    private readonly Channel<SessionCommand> _commandChannel =
        Channel.CreateUnbounded<SessionCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private volatile SessionState _state = SessionState.Initializing;
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private CancellationToken _workerToken;
    private bool _disposed;

    // Recording-session-scoped state -- only ever read/written on the single session worker
    // thread (see class doc comment's threading model), so no synchronization is needed here.
    private object? _capturedTarget;
    private DateTimeOffset _recordingStartedAt;
    private long _recordingGeneration;

    // _maxDurationTimer is touched from two places: StartMaxDurationTimer/CancelMaxDurationTimer
    // (both always called from the worker thread, like the fields above) AND DisposeAsync's own
    // final CancelMaxDurationTimer() call, which runs on whatever thread calls DisposeAsync
    // (potentially the real daemon's Ctrl+C/SIGTERM shutdown path, NOT the worker thread) --
    // see DisposeAsync's own doc comment for why that call is placed strictly after `await
    // _workerTask` so the two are never concurrent (post-review fix, BLOCKING 2): once the
    // worker task has completed, it is provably done touching this field for good, making
    // DisposeAsync's later call effectively single-threaded too, without needing a lock.
    private Timer? _maxDurationTimer;

    public SessionController(
        IHotkeySource hotkeySource,
        CaptureModeController captureController,
        SileroVadDetector vad,
        ITranscriber transcriber,
        PostProcessorChain postProcessorChain,
        ITextInjector textInjector,
        SessionControllerOptions options,
        ILogger<SessionController> logger)
    {
        _hotkeySource = hotkeySource ?? throw new ArgumentNullException(nameof(hotkeySource));
        _captureController = captureController ?? throw new ArgumentNullException(nameof(captureController));
        _vad = vad ?? throw new ArgumentNullException(nameof(vad));
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _postProcessorChain = postProcessorChain ?? throw new ArgumentNullException(nameof(postProcessorChain));
        _textInjector = textInjector ?? throw new ArgumentNullException(nameof(textInjector));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Current state -- safe to read from any thread for logging/diagnostics/tests (see class doc comment).</summary>
    public SessionState State => _state;

    /// <summary>Raised on the session worker thread whenever <see cref="State"/> changes. Handlers should not block.</summary>
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised on the session worker thread exactly once per completed dictation that produced
    /// text -- from inside <see cref="RunInjectingAsync"/>, in both its normal-outcome branches
    /// (<see cref="InjectionOutcome.Injected"/> and any other outcome) AND its exception-catch
    /// branch, since in all three cases post-processed final text genuinely exists and was
    /// attempted to be injected; only <see cref="DictationCompletedEventArgs.WasInjected"/>
    /// differs (true only for <see cref="InjectionOutcome.Injected"/>). Deliberately NOT raised
    /// from any earlier "nothing to inject" exit path (below-<see cref="SessionControllerOptions.MinDurationMs"/>
    /// discard, audio-device-lost, VAD-discard, transcription failure/timeout, empty transcription
    /// result, or <see cref="HandleHookFaultedAsync"/> aborting an orphaned recording) -- none of
    /// those reached a post-processed final text. Handlers should not block.
    /// </summary>
    public event EventHandler<DictationCompletedEventArgs>? DictationCompleted;

    /// <summary>
    /// Starts the hotkey source and the session worker. See class doc comment for the "model
    /// loaded" contract -- the caller must have already run <see cref="ITranscriber.InitializeAsync"/>
    /// to completion; <see cref="ITranscriber.IsReady"/> is checked once, synchronously, never
    /// awaited/blocked on here.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (State != SessionState.Initializing)
            throw new InvalidOperationException(
                $"StartAsync must only be called once, from Initializing (current state: {State}).");

        if (!_transcriber.IsReady)
        {
            _logger.LogCritical(
                "SessionController.StartAsync called before the transcriber finished InitializeAsync "
                + "(IsReady=false) -- the caller owns model loading (see class doc comment). Transitioning to Faulted.");
            SetState(SessionState.Faulted);
            return;
        }

        _hotkeySource.Pressed += OnHotkeyPressed;
        _hotkeySource.Released += OnHotkeyReleased;
        _hotkeySource.Faulted += OnHotkeyFaulted;

        _workerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _workerTask = Task.Run(() => RunWorkerLoopAsync(_workerCts.Token));

        try
        {
            await _hotkeySource.StartAsync(_options.HotkeyBinding, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Hotkey source failed to start; transitioning to Faulted.");
            SetState(SessionState.Faulted);
            return;
        }

        SetState(SessionState.Idle);
        _logger.LogInformation(
            "SessionController ready: trigger={Trigger} suppress={Suppress}.",
            _options.HotkeyBinding.Key, _options.HotkeyBinding.Suppress);
    }

    /// <summary>Alias for <see cref="DisposeAsync"/>, per the work item's spec naming both. Same shutdown path.</summary>
    public Task StopAsync() => DisposeAsync().AsTask();

    /// <summary>
    /// Stops the hotkey source, drains/cancels the session worker, and disposes every component
    /// this class owns (see class doc comment's ownership decision). Idempotent.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _hotkeySource.Pressed -= OnHotkeyPressed;
        _hotkeySource.Released -= OnHotkeyReleased;
        _hotkeySource.Faulted -= OnHotkeyFaulted;

        _commandChannel.Writer.TryComplete();
        _workerCts?.Cancel();

        if (_workerTask is not null)
        {
            try
            {
                await _workerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected shutdown path.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session worker task ended with an unexpected exception during shutdown.");
            }
        }

        // Post-review fix (BLOCKING 2): this call is deliberately placed AFTER awaiting
        // _workerTask above, not before it. _maxDurationTimer is genuinely only ever touched
        // from the session worker thread (StartMaxDurationTimer/CancelMaxDurationTimer both run
        // inside command handlers) as long as THIS call doesn't itself run concurrently with the
        // worker -- and calling it before awaiting _workerTask would do exactly that: DisposeAsync
        // could run on an arbitrary caller thread (e.g. the daemon's Ctrl+C/SIGTERM shutdown path)
        // at the same instant the worker thread is mid-flight inside HandleKeyDownAsync, right
        // after StartMaxDurationTimer's own `_maxDurationTimer = new Timer(...)` assignment but
        // before that assignment is guaranteed visible cross-thread -- no lock/memory barrier
        // protected that field the way CaptureModeController's own analogous idle-close timer
        // field is guarded by `_gate` (see that class's ScheduleIdleClose/CancelIdleCloseTimer).
        // Once _workerTask has completed, the worker is guaranteed to have finished processing
        // whatever command it was on (per RunWorkerLoopAsync's own "fully await each command
        // before dequeuing the next" contract) and will never touch _maxDurationTimer again, so
        // this line is now provably single-threaded and needs no lock.
        CancelMaxDurationTimer();

        _workerCts?.Dispose();

        try { await _hotkeySource.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Error disposing IHotkeySource."); }

        try { await _captureController.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Error disposing CaptureModeController."); }

        try { _vad.Dispose(); }
        catch (Exception ex) { _logger.LogError(ex, "Error disposing SileroVadDetector."); }

        try { await _transcriber.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Error disposing ITranscriber."); }
    }

    // ── Hook event handlers (never block; post-and-return, per IHotkeySource's contract) ──

    private void OnHotkeyPressed(object? sender, HotkeyEventArgs e) =>
        _commandChannel.Writer.TryWrite(new SessionCommand(SessionCommandKind.KeyDown, e.Timestamp));

    private void OnHotkeyReleased(object? sender, HotkeyEventArgs e) =>
        _commandChannel.Writer.TryWrite(new SessionCommand(SessionCommandKind.KeyUp, e.Timestamp));

    private void OnHotkeyFaulted(object? sender, HotkeyFaultEventArgs e) =>
        _commandChannel.Writer.TryWrite(new SessionCommand(SessionCommandKind.HookFaulted, DateTimeOffset.UtcNow, Fault: e));

    // ── The single session worker (see class doc comment's threading model) ──

    private async Task RunWorkerLoopAsync(CancellationToken ct)
    {
        _workerToken = ct;
        try
        {
            await foreach (var cmd in _commandChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    switch (cmd.Kind)
                    {
                        case SessionCommandKind.KeyDown:
                            await HandleKeyDownAsync(cmd.Timestamp).ConfigureAwait(false);
                            break;
                        case SessionCommandKind.KeyUp:
                            await HandleKeyUpAsync(cmd.Timestamp).ConfigureAwait(false);
                            break;
                        case SessionCommandKind.MaxDurationElapsed:
                            await HandleMaxDurationElapsedAsync(cmd.Generation).ConfigureAwait(false);
                            break;
                        case SessionCommandKind.HookFaulted:
                            await HandleHookFaultedAsync(cmd.Fault!).ConfigureAwait(false);
                            break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Unhandled exception processing a session command ({Kind}); worker continues.", cmd.Kind);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path: DisposeAsync/StopAsync cancelled the worker token.
        }
    }

    // ── Idle → Recording ──

    private async Task HandleKeyDownAsync(DateTimeOffset ts)
    {
        if (State != SessionState.Idle)
        {
            // Edge case 1: ignore, don't queue, don't start a second recording.
            _logger.LogDebug("Key-down ignored: not Idle (current state {State}).", State);
            return;
        }

        if (!_transcriber.IsReady)
        {
            // Edge case 5: guard on IsReady, never block this thread waiting for warm-up.
            _logger.LogWarning("Key-down ignored: ASR model is still warming up (IsReady=false).");
            return;
        }

        _capturedTarget = _textInjector.CaptureTarget();
        _recordingStartedAt = ts;
        long generation = ++_recordingGeneration;

        try
        {
            await _captureController.BeginUtteranceAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // No recording ever actually started here, so this is NOT the "audio device lost
            // while Recording" row (see class doc comment) -- stay Idle rather than fault the
            // whole session over a single failed stream-open.
            _logger.LogError(ex, "Failed to begin capture on key-down; staying Idle.");
            return;
        }

        StartMaxDurationTimer(generation);
        SetState(SessionState.Recording);
        _logger.LogInformation("Recording started.");
    }

    // ── Recording → Finalizing / Cooldown / Faulted ──

    private async Task HandleKeyUpAsync(DateTimeOffset ts)
    {
        if (State != SessionState.Recording)
        {
            _logger.LogDebug("Key-up ignored: not Recording (current state {State}).", State);
            return;
        }

        CancelMaxDurationTimer();
        var elapsed = ts - _recordingStartedAt;

        if (elapsed.TotalMilliseconds < _options.MinDurationMs)
        {
            _logger.LogInformation(
                "Recording too short ({ElapsedMs:F0}ms < {MinMs}ms); discarding.",
                elapsed.TotalMilliseconds, _options.MinDurationMs);
            await SafeAbortCaptureAsync().ConfigureAwait(false);
            await EnterCooldownAsync().ConfigureAwait(false);
            return;
        }

        await FinalizeRecordingAsync(truncated: false).ConfigureAwait(false);
    }

    private async Task HandleMaxDurationElapsedAsync(long generation)
    {
        // Stale-timer guard: if we've already left Recording (or re-entered it with a newer
        // generation) by the time this fires, this is a no-op -- see class doc comment.
        if (State != SessionState.Recording || generation != Volatile.Read(ref _recordingGeneration))
            return;

        // Edge case 3 (key stuck down) IS this row -- no separate handling exists or is needed.
        _logger.LogWarning(
            "Max recording duration ({MaxMs}ms) reached; force-finalizing "
            + "(possible stuck key / missed key-up).", _options.MaxDurationMs);

        await FinalizeRecordingAsync(truncated: true).ConfigureAwait(false);
    }

    private async Task FinalizeRecordingAsync(bool truncated)
    {
        ReadOnlyMemory<float> samples;
        try
        {
            samples = await _captureController.EndUtteranceAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // "Audio device lost" -- see class doc comment's §1.12-over-§1.4 reconciliation.
            // Auto-recovery, not a permanent Faulted: best-effort abort the capture, then take
            // the same Cooldown -> Idle path every other non-fatal abort takes. The NEXT
            // key-down's CaptureDeviceResolver call will fall back to the default device if the
            // one that just failed is genuinely gone.
            _logger.LogError(
                ex, "Audio device lost while ending capture; recovering to Idle (auto-recovery, plan §1.12).");
            await SafeAbortCaptureAsync().ConfigureAwait(false);
            await EnterCooldownAsync().ConfigureAwait(false);
            return;
        }

        if (truncated)
            _logger.LogWarning("Recording truncated at max duration ({MaxMs}ms).", _options.MaxDurationMs);

        var recordingDuration = DateTimeOffset.UtcNow - _recordingStartedAt;
        if (recordingDuration.TotalMilliseconds >= _options.LongUtteranceCueMs)
        {
            // Long-utterance processing cue: log-only in Phase 1, see class doc comment.
            _logger.LogInformation(
                "Long recording ({DurationMs:F0}ms >= {CueMs}ms) -- processing may take a moment.",
                recordingDuration.TotalMilliseconds, _options.LongUtteranceCueMs);
        }

        // ProcessingLatency (DictationCompletedEventArgs, §3.6) is measured "key-up/forced-
        // finalization -> injected" -- started here, right as the recording phase ends and
        // processing begins, read at the actual DictationCompleted raise-site in RunInjectingAsync.
        var processingStopwatch = Stopwatch.StartNew();

        SetState(SessionState.Finalizing);
        await RunFinalizingAsync(samples, recordingDuration, processingStopwatch).ConfigureAwait(false);
    }

    // ── Finalizing → Transcribing / Cooldown ──

    private async Task RunFinalizingAsync(
        ReadOnlyMemory<float> samples, TimeSpan recordingDuration, Stopwatch processingStopwatch)
    {
        var trim = _vad.Trim(samples);
        if (trim.ShouldDiscard)
        {
            _logger.LogInformation(
                "VAD discarded utterance ({SpeechMs:F0}ms of speech, below the minimum); nothing to transcribe.",
                trim.TotalSpeechDuration.TotalMilliseconds);
            await EnterCooldownAsync().ConfigureAwait(false);
            return;
        }

        SetState(SessionState.Transcribing);
        await RunTranscribingAsync(trim.TrimmedSamples, recordingDuration, processingStopwatch).ConfigureAwait(false);
    }

    // ── Transcribing → Injecting / Cooldown ──

    private async Task RunTranscribingAsync(
        ReadOnlyMemory<float> samples, TimeSpan recordingDuration, Stopwatch processingStopwatch)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_options.AsrTimeoutMs));
        TranscriptionResult result;
        var sw = Stopwatch.StartNew();
        try
        {
            result = await _transcriber.TranscribeAsync(samples, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transcription failed or timed out after {TimeoutMs}ms.", _options.AsrTimeoutMs);
            await EnterCooldownAsync().ConfigureAwait(false);
            return;
        }
        sw.Stop();

        if (result.IsEmpty)
        {
            _logger.LogInformation("Transcription result was empty (no speech); nothing to inject.");
            await EnterCooldownAsync().ConfigureAwait(false);
            return;
        }

        // Phase 4 item 3 (§4.4): resolve the focused app's process executable name from the
        // target captured at key-down, mirroring the exact shape of the pre-existing
        // `_textInjector.InjectAsync(text, _capturedTarget, ...)` call below -- this is the
        // one, deliberately small and mechanical, addition this item makes to
        // SessionController itself (see Docs/PROJECT-MEMORY.md's "SessionController.cs is the
        // designated highest-risk file... touched only twice on purpose" note; this is a
        // third, narrow, deliberate exception, not a restructuring). All real selection/
        // filtering logic lives in
        // PostProcessorChain/PerAppOverrideResolver (Soneto.Core.Dictionary), not here.
        string? capturedTargetProcessExecutableName = _textInjector.TryResolveProcessExecutableName(_capturedTarget);
        var postProcessed = _postProcessorChain.Process(result.Text, capturedTargetProcessExecutableName);
        SetState(SessionState.Injecting);
        // `samples` here is exactly trim.TrimmedSamples from RunFinalizingAsync, threaded down
        // unchanged (item 10, §3.14) -- see DictationCompletedEventArgs.AudioSamples's own doc
        // comment for why the trimmed (not raw) samples were chosen.
        await RunInjectingAsync(
            postProcessed.Text, sw.Elapsed, result, postProcessed.Applied, recordingDuration, processingStopwatch,
            samples)
            .ConfigureAwait(false);
    }

    // ── Injecting → Cooldown ──

    private async Task RunInjectingAsync(
        string text, TimeSpan decodeElapsed, TranscriptionResult transcriptionResult,
        IReadOnlyList<AppliedRule> rulesFired, TimeSpan recordingDuration, Stopwatch processingStopwatch,
        ReadOnlyMemory<float> audioSamples)
    {
        string toInject = text;
        if (toInject.Length > TranscriptCharCap)
        {
            // Edge case 6: cap AFTER post-processing, BEFORE injection.
            _logger.LogWarning(
                "Transcript truncated from {OrigLen} to {CapLen} chars before injection.",
                toInject.Length, TranscriptCharCap);
            toInject = toInject[..TranscriptCharCap];
        }

        var sw = Stopwatch.StartNew();
        InjectionOutcome outcome;
        try
        {
            outcome = await _textInjector.InjectAsync(
                toInject, _capturedTarget, _options.InjectionOptions, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Injection threw unexpectedly.");
            RaiseDictationCompleted(
                transcriptionResult.Text, toInject, rulesFired, recordingDuration, processingStopwatch.Elapsed,
                wasInjected: false, audioSamples);
            await EnterCooldownAsync().ConfigureAwait(false);
            return;
        }
        sw.Stop();

        if (outcome == InjectionOutcome.Injected)
        {
            _logger.LogInformation(
                "Injected {Chars} chars. Timings: audio={AudioMs:F0}ms decode={DecodeMs:F0}ms inject={InjectMs:F0}ms.",
                toInject.Length, transcriptionResult.AudioDuration.TotalMilliseconds,
                decodeElapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds);
        }
        else
        {
            // The text is already on the clipboard by this point -- WindowsTextInjector's own
            // paste algorithm sets it before attempting the paste chord, regardless of outcome --
            // so "leave text on clipboard as fallback" requires no extra action here, only the log.
            _logger.LogWarning(
                "Injection failed with outcome {Outcome}; text left on the clipboard as a fallback.", outcome);
        }

        RaiseDictationCompleted(
            transcriptionResult.Text, toInject, rulesFired, recordingDuration, processingStopwatch.Elapsed,
            wasInjected: outcome == InjectionOutcome.Injected, audioSamples);

        await EnterCooldownAsync().ConfigureAwait(false);
    }

    private void RaiseDictationCompleted(
        string rawText, string finalText, IReadOnlyList<AppliedRule> rulesFired, TimeSpan recordingDuration,
        TimeSpan processingLatency, bool wasInjected, ReadOnlyMemory<float> audioSamples)
    {
        // Code review, item 2: both call sites in RunInjectingAsync raise this event immediately
        // BEFORE their mandatory EnterCooldownAsync() call (the only path back to Idle). Unlike
        // StateChanged (raised after its state write already landed), a throwing subscriber here
        // would unwind straight past EnterCooldownAsync entirely -- there is no other catch
        // between here and RunWorkerLoopAsync's own outer per-command catch, which logs and moves
        // on to the next channel item without ever restoring State. That would strand the session
        // in Injecting forever (every subsequent key-down is silently ignored by HandleKeyDownAsync's
        // State != Idle guard) until the whole process is restarted. Catch and log here, the same
        // defensive-backstop discipline this class already uses elsewhere (SafeAbortCaptureAsync,
        // HandleHookFaultedAsync's per-attempt catch), so a misbehaving subscriber can never
        // prevent the session from reaching Cooldown/Idle.
        try
        {
            DictationCompleted?.Invoke(
                this,
                new DictationCompletedEventArgs(
                    rawText, finalText, rulesFired, recordingDuration, processingLatency, wasInjected,
                    audioSamples));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DictationCompleted subscriber threw; continuing to cooldown regardless.");
        }
    }

    // ── Cooldown → Idle ──

    private async Task EnterCooldownAsync()
    {
        SetState(SessionState.Cooldown);
        try
        {
            await Task.Delay(CooldownDelay, _workerToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // shutting down -- leave state as Cooldown, DisposeAsync is tearing down anyway.
        }

        if (State == SessionState.Cooldown)
            SetState(SessionState.Idle);
    }

    // ── any → hook faulted ──

    private async Task HandleHookFaultedAsync(HotkeyFaultEventArgs fault)
    {
        _logger.LogError(fault.Exception, "Hotkey source faulted: {Reason}. Attempting recovery.", fault.Reason);

        if (State == SessionState.Recording)
        {
            // The hook is dead, so the key-up that would have ended this recording can never
            // arrive -- the capture is orphaned. Abort it before attempting recovery.
            _logger.LogWarning("Hook faulted mid-recording; aborting the orphaned capture.");
            CancelMaxDurationTimer();
            await SafeAbortCaptureAsync().ConfigureAwait(false);
        }

        bool recovered = false;
        int maxAttempts = _options.MaxHookRestartAttempts;
        var delay = _options.EffectiveHookRestartInitialBackoff;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _hotkeySource.RestartAsync(_workerToken).ConfigureAwait(false);
                recovered = true;
                _logger.LogInformation("Hotkey source recovered on attempt {Attempt}/{Max}.", attempt, maxAttempts);
                break;
            }
            catch (OperationCanceledException)
            {
                return; // shutting down
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hotkey restart attempt {Attempt}/{Max} failed.", attempt, maxAttempts);
                if (attempt < maxAttempts)
                {
                    try
                    {
                        await Task.Delay(delay, _workerToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return; // shutting down
                    }
                    delay += delay; // exponential
                }
            }
        }

        if (recovered)
        {
            SetState(SessionState.Idle);
        }
        else
        {
            _logger.LogCritical(
                "Hotkey source failed to recover after {Max} attempts; hotkey capture is permanently broken "
                + "for this session. The daemon process itself keeps running (plan §1.12).", maxAttempts);
            SetState(SessionState.Faulted);
        }
    }

    // ── Helpers ──

    private async Task SafeAbortCaptureAsync()
    {
        try
        {
            await _captureController.AbortUtteranceAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AbortUtteranceAsync itself failed while cleaning up.");
        }
    }

    private void StartMaxDurationTimer(long generation)
    {
        _maxDurationTimer?.Dispose();
        _maxDurationTimer = new Timer(
            _ => _commandChannel.Writer.TryWrite(
                new SessionCommand(SessionCommandKind.MaxDurationElapsed, DateTimeOffset.UtcNow, generation)),
            null, TimeSpan.FromMilliseconds(_options.MaxDurationMs), Timeout.InfiniteTimeSpan);
    }

    private void CancelMaxDurationTimer()
    {
        _maxDurationTimer?.Dispose();
        _maxDurationTimer = null;
    }

    private void SetState(SessionState newState)
    {
        var old = _state;
        if (old == newState) return;
        _state = newState;
        _logger.LogDebug("State transition: {Old} -> {New}", old, newState);
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(old, newState));
    }

    private enum SessionCommandKind { KeyDown, KeyUp, MaxDurationElapsed, HookFaulted }

    private readonly record struct SessionCommand(
        SessionCommandKind Kind,
        DateTimeOffset Timestamp,
        long Generation = 0,
        HotkeyFaultEventArgs? Fault = null);
}
