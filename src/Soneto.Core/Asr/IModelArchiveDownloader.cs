namespace Soneto.Core.Asr;

/// <summary>Progress report for a model archive download.</summary>
public sealed record ModelDownloadProgress(long BytesDownloaded, long? TotalBytes)
{
    public double? PercentComplete => TotalBytes is > 0
        ? Math.Clamp(BytesDownloaded / (double)TotalBytes.Value * 100.0, 0, 100)
        : null;
}

/// <summary>
/// Downloads a model archive to a local path. Abstracted so <see cref="ModelManager"/>'s
/// retry/verify/extract orchestration can be unit-tested without a real network call —
/// see plan §1.13 ("must run with no audio device and no model file").
/// </summary>
public interface IModelArchiveDownloader
{
    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destinationPath"/>. If a
    /// partial file already exists there, implementations should attempt a resumable
    /// (HTTP Range) request; if the server doesn't support resume, fall back to a full
    /// re-download.
    /// </summary>
    Task DownloadAsync(
        Uri url, string destinationPath, IProgress<ModelDownloadProgress>? progress, CancellationToken ct);
}
