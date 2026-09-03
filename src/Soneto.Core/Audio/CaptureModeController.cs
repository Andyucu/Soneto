using Microsoft.Extensions.Logging;
using Soneto.Core.Abstractions;
using Soneto.Core.Configuration;

namespace Soneto.Core.Audio;

/// <summary>
/// Owns WHEN an <see cref="IAudioCapture"/>'s underlying stream opens/closes relative to a
/// sequence of push-to-talk utterances (key-down/key-up cycles), per plan §1.5's three
/// capture modes. Formalizes what item 4b's <c>--record</c> CLI demo did manually for
/// <c>OnDemand</c> into a reusable, fully unit-testable class (against a fake
/// <see cref="IAudioCapture"/> — no real PortAudio needed).
///
/// <para><b>State machine</b> (per mode, driven by <see cref="BeginUtteranceAsync"/> /
/// <see cref="EndUtteranceAsync"/> / <see cref="AbortUtteranceAsync"/>):</para>
/// <list type="bullet">
/// <item><description><c>OnDemand</c>: <c>BeginUtterance</c> opens the stream if not already
/// open (it never is — see below); <c>EndUtterance</c>/<c>AbortUtterance</c> closes it
/// immediately. Every utterance pays the stream-open cost.</description></item>
/// <item><description><c>WarmIdle</c>: <c>BeginUtterance</c> opens the stream only on the
/// first utterance of a burst (cancelling any pending idle-close timer if one was running);
/// <c>EndUtterance</c>/<c>AbortUtterance</c> starts an <c>idleCloseMs</c> timer instead of
/// closing immediately. A subsequent <c>BeginUtterance</c> before the timer fires cancels it
/// and reuses the still-open stream (this is where <see cref="PreRollRingBuffer"/> pays off —
/// non-empty pre-roll from the 2nd utterance of the burst onward, per plan §1.5). If the timer
/// fires with no new utterance, the stream closes and the next <c>BeginUtterance</c> starts a
/// fresh burst (no pre-roll on its first utterance, same as <c>OnDemand</c>).</description></item>
/// <item><description><c>AlwaysOn</c>: <see cref="StartAsync"/> (or, failing that, the first
/// <c>BeginUtterance</c>) opens the stream once; it is never idle-closed —
/// <c>EndUtterance</c>/<c>AbortUtterance</c> is a no-op for stream lifecycle in this
/// mode.</description></item>
/// </list>
///
/// <para><b>Ready/failure cue wiring (plan §1.5, "mandatory in OnDemand"):</b> if the
/// underlying <paramref name="capture"/> also implements <see cref="IReadySignal"/> (real
/// <c>PortAudioCapture</c> does; a bare test fake need not), a successful stream open starts a
/// best-effort background wait for that signal and plays the ready cue once it resolves — using
/// item 4b's "first non-zero buffer" metric, not just "stream opened," per §1.5's own
/// reasoning. The ready cue is gated on <paramref name="readyCueMode"/> ==
/// <see cref="ReadyCue.Sound"/> (this is the routine "you're all set" beep the config knob is
/// meant to let users suppress). A failed stream open plays the distinct, lower failure cue
/// immediately and <b>unconditionally of <paramref name="readyCueMode"/></b> — per §1.5's
/// separate, independent reasoning that silence is the worst possible feedback when the mic is
/// dead; <c>readyCue: none</c> means "don't beep on success," not "give me zero feedback on
/// failure too." Passing a null <paramref name="cuePlayer"/> disables both (used by tests that
/// don't want any audio side effect).</para>
///
/// <para><b>Ownership:</b> this class does NOT take ownership of the <see cref="IAudioCuePlayer"/>
/// it is given — the caller (e.g. <c>Program.cs</c>'s <c>--capture-demo</c>) owns its
/// disposal.</para>
/// </summary>
public sealed class CaptureModeController : IAsyncDisposable
{
    private static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(3);

    private readonly IAudioCapture _capture;
    private readonly ILogger<CaptureModeController> _logger;
    private readonly CaptureMode _mode;
    private readonly TimeSpan _idleCloseDelay;
    private readonly TimeSpan _preRoll;
    private readonly AudioDeviceId? _device;
    private readonly IAudioCuePlayer? _cuePlayer;
    private readonly ReadyCue _readyCueMode;
    private readonly TimeSpan _readyTimeout;

    private readonly object _gate = new();
    private Timer? _idleCloseTimer;
    private long _timerGeneration;
    private bool _isFirstUtteranceOfBurst = true;
    private bool _disposed;

    public CaptureModeController(
        IAudioCapture capture,
        ILogger<CaptureModeController> logger,
        CaptureMode mode,
        int idleCloseMs,
        int preRollMs,
        AudioDeviceId? device = null,
        IAudioCuePlayer? cuePlayer = null,
        ReadyCue readyCueMode = ReadyCue.Sound,
        TimeSpan? readyTimeout = null)
    {
        _capture = capture;
        _logger = logger;
        _mode = mode;
        _idleCloseDelay = TimeSpan.FromMilliseconds(Math.Max(0, idleCloseMs));
        _preRoll = TimeSpan.FromMilliseconds(Math.Max(0, preRollMs));
        _device = device;
        _cuePlayer = cuePlayer;
        _readyCueMode = readyCueMode;
        _readyTimeout = readyTimeout ?? DefaultReadyTimeout;
    }

    /// <summary>Which mode this instance is driving. Exposed for logging/diagnostics.</summary>
    public CaptureMode Mode => _mode;

    /// <summary>Whether the underlying capture's stream is currently open.</summary>
    public bool IsStreamOpen => _capture.IsRunning;

    /// <summary>Best-effort snapshot of whether a <c>WarmIdle</c> idle-close timer is currently pending.</summary>
    public bool IsIdleCloseTimerPending
    {
        get { lock (_gate) { return _idleCloseTimer != null; } }
    }

    /// <summary>
    /// <c>AlwaysOn</c>: opens the stream immediately (call once at daemon/demo startup). A
    /// no-op for the other two modes, which open lazily on the first
    /// <see cref="BeginUtteranceAsync"/>.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_mode == CaptureMode.AlwaysOn && !_capture.IsRunning)
            await OpenStreamAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Key-down: ensures the stream is open (opening it if this mode requires it, cancelling
    /// any pending <c>WarmIdle</c> idle-close timer) and starts a new utterance capture, with
    /// pre-roll applied per mode/burst-position rules (see class doc).
    /// </summary>
    public async Task BeginUtteranceAsync(CancellationToken ct = default)
    {
        CancelIdleCloseTimer();

        bool wasAlreadyOpen = _capture.IsRunning;
        if (!wasAlreadyOpen)
        {
            await OpenStreamAsync(ct).ConfigureAwait(false);
            _isFirstUtteranceOfBurst = true;
        }

        // Plan §1.5: WarmIdle's pre-roll is "Full, from the 2nd utterance of a burst
        // onward" — the first utterance of a burst has nothing useful buffered yet (the
        // stream just opened), so there is nothing to gain from asking for pre-roll there.
        // AlwaysOn is "always" pre-roll since its stream is never freshly opened mid-burst.
        // OnDemand never has anything buffered (its stream isn't running between
        // utterances), so it always asks for zero regardless of burst position.
        TimeSpan preRoll = _mode switch
        {
            CaptureMode.AlwaysOn => _preRoll,
            CaptureMode.WarmIdle when !_isFirstUtteranceOfBurst => _preRoll,
            _ => TimeSpan.Zero,
        };

        _capture.BeginCapture(preRoll);
        _isFirstUtteranceOfBurst = false;
    }

    /// <summary>Key-up (normal end): ends the capture and applies per-mode close/idle-timer behaviour.</summary>
    public async Task<ReadOnlyMemory<float>> EndUtteranceAsync()
    {
        var result = _capture.EndCapture();
        await AfterUtteranceEndedAsync().ConfigureAwait(false);
        return result;
    }

    /// <summary>Key-up (aborted, e.g. below <c>minDurationMs</c>): discards the capture and applies the same close/idle-timer behaviour as a normal end.</summary>
    public async Task AbortUtteranceAsync()
    {
        _capture.AbortCapture();
        await AfterUtteranceEndedAsync().ConfigureAwait(false);
    }

    private async Task AfterUtteranceEndedAsync()
    {
        switch (_mode)
        {
            case CaptureMode.OnDemand:
                await CloseStreamAsync().ConfigureAwait(false);
                break;
            case CaptureMode.WarmIdle:
                ScheduleIdleClose();
                break;
            case CaptureMode.AlwaysOn:
                break; // never closes
        }
    }

    private void ScheduleIdleClose()
    {
        long generation;
        lock (_gate)
        {
            _idleCloseTimer?.Dispose();
            generation = ++_timerGeneration;
            _idleCloseTimer = new Timer(OnIdleCloseTimerFired, generation, _idleCloseDelay, Timeout.InfiniteTimeSpan);
        }
        _logger.LogInformation(
            "WarmIdle: idle-close timer started ({IdleCloseMs}ms)", _idleCloseDelay.TotalMilliseconds);
    }

    /// <summary>
    /// Cancels any pending idle-close timer. Bumps <see cref="_timerGeneration"/> unconditionally
    /// (even if no timer was pending) so that a callback which has already been dequeued off the
    /// ThreadPool and is racing this call -- <see cref="Timer.Dispose()"/> gives no guarantee that
    /// an in-flight callback invocation is cancelled -- observes a stale generation in
    /// <see cref="OnIdleCloseTimerFired"/> and no-ops instead of tearing down a stream that
    /// <see cref="BeginUtteranceAsync"/> may have just started reusing (the TOCTOU race flagged in
    /// item 4c review).
    /// </summary>
    private void CancelIdleCloseTimer()
    {
        bool hadTimer;
        lock (_gate)
        {
            hadTimer = _idleCloseTimer != null;
            _idleCloseTimer?.Dispose();
            _idleCloseTimer = null;
            _timerGeneration++;
        }
        if (hadTimer)
            _logger.LogInformation("WarmIdle: idle-close timer cancelled (new utterance started before it fired)");
    }

    private void OnIdleCloseTimerFired(object? state)
    {
        lock (_gate)
        {
            // Stale callback: CancelIdleCloseTimer (or a subsequent ScheduleIdleClose) already
            // ran and bumped the generation between this callback being dequeued and it acquiring
            // _gate. Timer.Dispose() does not stop an already-executing/dequeued callback, so this
            // check is the only reliable guard -- proceeding here would close a stream that
            // BeginUtteranceAsync may have already decided to keep open and start capturing on.
            if ((long)state! != _timerGeneration)
                return;

            _idleCloseTimer?.Dispose();
            _idleCloseTimer = null;
        }
        _logger.LogInformation("WarmIdle: idle-close timer fired with no new utterance; closing the stream");
        _ = CloseStreamOnTimerFireAsync();
    }

    private async Task CloseStreamOnTimerFireAsync()
    {
        try
        {
            await CloseStreamAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Mode}: idle-close timer failed to close the stream", _mode);
        }
    }

    private async Task OpenStreamAsync(CancellationToken ct)
    {
        try
        {
            await _capture.StartAsync(_device, ct).ConfigureAwait(false);
            _logger.LogInformation("{Mode}: stream opened", _mode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Mode}: stream failed to open", _mode);
            // Unconditional on readyCueMode: per plan §1.5, the failure cue's whole purpose
            // is to avoid "silence is the worst possible feedback" when the mic is dead --
            // that reasoning is independent of readyCue's routine "you're all set" beep, which
            // readyCue=None is meant to suppress. Only a null cuePlayer (no cue subsystem at
            // all -- e.g. tests) disables it.
            _cuePlayer?.PlayFailure();
            throw;
        }

        if (_cuePlayer != null && _readyCueMode == ReadyCue.Sound)
        {
            if (_capture is IReadySignal readySignal)
                _ = FireReadyCueWhenSignalledAsync(readySignal, ct);
            else
                _cuePlayer.PlayReady(); // no readiness signal available -- best-effort immediate cue
        }
    }

    private async Task FireReadyCueWhenSignalledAsync(IReadySignal readySignal, CancellationToken ct)
    {
        try
        {
            await readySignal.WaitForReadyAsync(_readyTimeout, ct).ConfigureAwait(false);
            _cuePlayer!.PlayReady();
        }
        catch (Exception ex)
        {
            // Cue feedback only -- must never fault the capture path. A timeout here means
            // the stream opened but never produced a non-zero buffer (the same "silent
            // device" case item 4b's WaitForFirstSampleAsync is designed to surface), so no
            // ready cue is the CORRECT behaviour, not a bug -- logged, not escalated.
            _logger.LogWarning(
                ex, "Stream opened but no non-zero audio buffer arrived within {TimeoutMs}ms; ready cue not played",
                _readyTimeout.TotalMilliseconds);
        }
    }

    private async Task CloseStreamAsync()
    {
        if (!_capture.IsRunning) return;
        await _capture.StopAsync().ConfigureAwait(false);
        _logger.LogInformation("{Mode}: stream closed", _mode);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            _idleCloseTimer?.Dispose();
            _idleCloseTimer = null;
        }

        await CloseStreamAsync().ConfigureAwait(false);
    }
}
