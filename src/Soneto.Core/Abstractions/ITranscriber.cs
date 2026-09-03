namespace Soneto.Core.Abstractions;

/// <summary>
/// Speech-to-text engine over a single already-captured, already-resampled utterance.
/// Implementations own model loading, warm-up, and thread configuration (plan §1.6).
/// </summary>
public interface ITranscriber : IAsyncDisposable
{
    bool IsReady { get; }

    /// Load the model and run warm-up decode. Must complete before IsReady is true.
    Task InitializeAsync(CancellationToken ct);

    /// Decode 16 kHz mono float32 samples in [-1, 1] and return the transcript.
    Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> samples16k, CancellationToken ct);
}

public sealed record TranscriptionResult(
    string Text,
    TimeSpan AudioDuration,
    TimeSpan DecodeTime,
    bool IsEmpty);
