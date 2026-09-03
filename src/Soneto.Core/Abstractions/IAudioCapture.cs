namespace Soneto.Core.Abstractions;

/// <summary>
/// Captures microphone audio and exposes on-demand snapshot semantics for a single
/// push-to-talk utterance. Implementations own the underlying audio stream lifecycle
/// (open/close per capture mode) — see plan §1.5 for the OnDemand / WarmIdle / AlwaysOn
/// capture-mode distinction, which is a concern of the implementation, not this interface.
/// </summary>
public interface IAudioCapture : IAsyncDisposable
{
    bool IsRunning { get; }

    /// Raised at ~20 Hz while running, for later HUD level metering. Unused in Phase 1.
    /// Implementations may raise this from any internal thread (e.g. a background audio
    /// consumer thread, not necessarily the caller's thread or the underlying audio API's
    /// real-time callback thread) — handlers must return quickly and must not block, since a
    /// slow handler can delay that thread's other work.
    event EventHandler<AudioLevelEventArgs>? LevelChanged;

    /// <remarks>
    /// Implementations are not required to synchronize <see cref="StartAsync"/>,
    /// <see cref="StopAsync"/>, and <see cref="IsRunning"/> against each other — callers must
    /// drive a given instance's lifecycle from a single thread, strictly sequentially.
    /// </remarks>
    Task StartAsync(AudioDeviceId? device, CancellationToken ct);

    Task StopAsync();

    /// Snapshot from (now - preRoll) to now, then keep appending until EndCapture.
    void BeginCapture(TimeSpan preRoll);

    /// Returns 16 kHz mono float32 in [-1, 1].
    ReadOnlyMemory<float> EndCapture();

    void AbortCapture();
}

/// <summary>
/// Optional companion capability an <see cref="IAudioCapture"/> implementation may support: a
/// signal for when the underlying stream is genuinely delivering real (non-silent) audio, not
/// just "opened" — see plan §1.5's mandatory readiness-cue requirement, and item 4b's "first
/// non-zero buffer" metric this is meant to reuse. Deliberately NOT part of the core
/// <see cref="IAudioCapture"/> contract: a fake/test double used to unit-test capture-mode
/// orchestration has no meaningful "first real sample" concept to implement, so this stays an
/// optional, separately-checked capability (<c>capture as IReadySignal</c>) rather than
/// something every implementation/fake is forced to provide.
/// </summary>
public interface IReadySignal
{
    /// Completes once the stream has delivered its first non-silent buffer, or throws
    /// <see cref="TimeoutException"/> if none arrives within <paramref name="timeout"/>.
    Task WaitForReadyAsync(TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// Identifies an audio input device across platforms. Implementations resolve this to
/// whatever their underlying audio API (PortAudio) needs at capture-open time; the
/// device is re-resolved fresh on every key-down per plan §1.5 ("Device changes").
/// </summary>
public sealed record AudioDeviceId(int Index, string Name);

/// <summary>
/// A level-metering sample raised while capture is running. RMS over a buffer,
/// converted to dBFS, at ~20 Hz — wired now so Phase 3's VU meter is free (plan §1.5).
/// </summary>
public sealed record AudioLevelEventArgs(double Dbfs, DateTimeOffset Timestamp);
