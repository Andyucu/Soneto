using System.Net;
using System.Net.Http.Headers;

namespace Soneto.Core.Asr;

/// <summary>
/// Real, resumable HTTP downloader for the sherpa-onnx model archive (plan §1.6: "download
/// the sherpa-onnx int8 archive with a resumable HTTP request, show progress"). Not used
/// by any default-run unit test — <see cref="ModelManager"/>'s tests inject a fake
/// <see cref="IModelArchiveDownloader"/> instead, per plan §1.13.
/// </summary>
public sealed class HttpModelArchiveDownloader : IModelArchiveDownloader
{
    // One static, long-lived HttpClient for the process lifetime — the standard .NET
    // guidance to avoid socket exhaustion from creating one per download.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    // Independent of total download time (which can legitimately be long for a 500MB+
    // file on a slow connection): if no bytes at all arrive within this window, the
    // connection is presumed stalled/hung and the download is cancelled so the caller
    // can recover instead of hanging forever.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    public async Task DownloadAsync(
        Uri url, string destinationPath, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        long existingLength = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingLength > 0)
            request.Headers.Range = new RangeHeaderValue(existingLength, null);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        bool resumed = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (existingLength > 0 && !resumed)
        {
            // Server ignored our Range header (or something about the remote file
            // changed) — restart the download from scratch rather than risk corrupting
            // it with a mismatched append.
            existingLength = 0;
        }

        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength is long contentLength
            ? contentLength + existingLength
            : null;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(
            destinationPath,
            resumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        var buffer = new byte[81920];
        long downloaded = existingLength;
        int read;

        // Stall/inactivity protection: reset a per-read timeout so a connection that just
        // stops sending bytes (as opposed to being refused outright) gets cancelled rather
        // than hanging forever, independent of total download time.
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            stallCts.CancelAfter(StallTimeout);
            try
            {
                read = await contentStream.ReadAsync(buffer, stallCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Model archive download from {url} stalled: no data received for "
                    + $"{StallTimeout.TotalSeconds:F0}s.");
            }

            if (read <= 0)
                break;

            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            progress?.Report(new ModelDownloadProgress(downloaded, totalBytes));
        }
    }
}
