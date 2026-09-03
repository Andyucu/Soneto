using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Soneto.Core.Asr;

/// <summary>
/// Resolves the model directory per plan §1.6's resolution order, downloading and
/// verifying the model archive on first run if necessary:
///
///   1. Explicit config path override (<see cref="ResolveOrDownloadAsync"/>'s
///      <c>configModelDir</c> parameter) — must already contain all four required files,
///      or this is treated as a configuration error, not silently ignored.
///   2. The standard per-OS location (<c>%LOCALAPPDATA%\Soneto\models\</c> on Windows,
///      <c>~/.local/share/soneto/models/</c> on Linux).
///   3. If absent from both, download the pinned release archive, verify its SHA-256,
///      extract it, and verify all four required files are present before returning.
///
/// Hash mismatch: delete the bad download and retry once; if it mismatches again, fail
/// loudly (<see cref="ModelHashMismatchException"/>) rather than proceed with unverified
/// weights (plan §1.6 / §1.12, both explicit about this).
/// </summary>
public sealed class ModelManager
{
    public const string ModelFolderName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";

    /// <summary>
    /// The exact archive used to build/validate this model (see spikes/s1-asr/README.md
    /// "Getting the model"). k2-fsa's sherpa-onnx model releases are versioned by tag, not
    /// mutated in place, so pinning both URL and hash together is safe.
    /// </summary>
    public static readonly Uri DefaultDownloadUrl = new(
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/"
        + "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2");

    /// <summary>
    /// SHA-256 of the archive at <see cref="DefaultDownloadUrl"/>, computed independently
    /// with both <c>certutil -hashfile</c> and <c>sha256sum</c> against a fresh download
    /// on 2026-08-31 (both agreed). Never run inference against a download that doesn't
    /// match this.
    /// </summary>
    public const string DefaultSha256 = "5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf";

    public static readonly IReadOnlyList<string> RequiredFiles =
        ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"];

    private readonly ILogger<ModelManager> _logger;
    private readonly IModelArchiveDownloader _downloader;
    private readonly IArchiveExtractor _extractor;
    private readonly Uri _downloadUrl;
    private readonly string _pinnedSha256;
    private readonly string _standardModelsBaseDir;

    public ModelManager(
        ILogger<ModelManager> logger,
        IModelArchiveDownloader? downloader = null,
        IArchiveExtractor? extractor = null,
        Uri? downloadUrl = null,
        string? pinnedSha256 = null,
        string? standardModelsBaseDirOverride = null)
    {
        _logger = logger;
        _downloader = downloader ?? new HttpModelArchiveDownloader();
        _extractor = extractor ?? new TarBz2ArchiveExtractor();
        _downloadUrl = downloadUrl ?? DefaultDownloadUrl;
        _pinnedSha256 = pinnedSha256 ?? DefaultSha256;
        // Overridable so tests can point this at an isolated temp dir instead of the real
        // per-OS location (plan §1.13: tests must not depend on machine state).
        _standardModelsBaseDir = standardModelsBaseDirOverride ?? ResolveStandardModelsBaseDir();
    }

    /// <summary>
    /// The standard per-OS models directory (parent of <see cref="ModelFolderName"/>),
    /// mirroring <c>Soneto.Core.Configuration.ConfigPaths</c>'s use of
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/>, which .NET maps to
    /// <c>$XDG_DATA_HOME</c> (<c>~/.local/share</c> by default) on Linux — exactly plan
    /// §1.6's target path.
    /// </summary>
    public static string ResolveStandardModelsBaseDir()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return OperatingSystem.IsWindows()
            ? Path.Combine(baseDir, "Soneto", "models")
            : Path.Combine(baseDir, "soneto", "models");
    }

    public static bool AreRequiredFilesPresent(string dir) =>
        Directory.Exists(dir) && RequiredFiles.All(f => File.Exists(Path.Combine(dir, f)));

    public static IReadOnlyList<string> MissingFiles(string dir) =>
        RequiredFiles.Where(f => !File.Exists(Path.Combine(dir, f))).ToArray();

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Resolves the model directory per the order documented on the class, downloading and
    /// extracting it if necessary. Never returns a directory that doesn't have all four
    /// required files present.
    /// </summary>
    /// <param name="configModelDir">
    /// The <c>asr.modelDir</c> config override, or null/empty if unset.
    /// </param>
    public async Task<string> ResolveOrDownloadAsync(string? configModelDir, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(configModelDir))
        {
            if (AreRequiredFilesPresent(configModelDir))
            {
                _logger.LogInformation("Using model dir from config override: {Dir}", configModelDir);
                return configModelDir;
            }

            var missing = MissingFiles(configModelDir);
            throw new ModelFilesMissingException(
                $"Configured asr.modelDir '{configModelDir}' is missing required model file(s): "
                + $"{string.Join(", ", missing)}. Fix the config path or remove the override to use "
                + "the standard model location instead.");
        }

        string standardDir = Path.Combine(_standardModelsBaseDir, ModelFolderName);
        if (AreRequiredFilesPresent(standardDir))
        {
            _logger.LogInformation("Using model dir: {Dir}", standardDir);
            return standardDir;
        }

        _logger.LogWarning(
            "Model not found at {Dir} (or incomplete); downloading from {Url}", standardDir, _downloadUrl);
        await DownloadAndExtractAsync(standardDir, ct);

        if (!AreRequiredFilesPresent(standardDir))
        {
            var missing = MissingFiles(standardDir);
            throw new ModelFilesMissingException(
                $"Model archive extracted to {standardDir}, but required file(s) are still missing: "
                + $"{string.Join(", ", missing)}. The archive may be a different sherpa-onnx model "
                + "release than expected.");
        }

        return standardDir;
    }

    /// <summary>
    /// Downloads the pinned archive into a temp file, verifying SHA-256 with one retry on
    /// mismatch (plan §1.6/§1.12), then extracts it so <paramref name="targetModelDir"/>
    /// (the model folder itself) ends up populated.
    /// </summary>
    private async Task DownloadAndExtractAsync(string targetModelDir, CancellationToken ct)
    {
        string parentDir = Path.GetDirectoryName(Path.GetFullPath(targetModelDir))!;
        Directory.CreateDirectory(parentDir);

        // Process-unique filename: a fixed shared name would collide between concurrent
        // invocations (e.g. two daemon instances, or a manual --transcribe run concurrent
        // with a real daemon's first-run download).
        string archivePath = Path.Combine(
            Path.GetTempPath(), $"soneto-model-download-{Environment.ProcessId}.tar.bz2");

        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            _logger.LogInformation(
                "Downloading model archive (attempt {Attempt}/{MaxAttempts}) from {Url}",
                attempt, maxAttempts, _downloadUrl);

            var progress = new Progress<ModelDownloadProgress>(p =>
                _logger.LogDebug(
                    "Download progress: {Downloaded} / {Total} bytes ({Percent:F1}%)",
                    p.BytesDownloaded, p.TotalBytes, p.PercentComplete ?? double.NaN));

            await _downloader.DownloadAsync(_downloadUrl, archivePath, progress, ct);

            string actualHash = await ComputeSha256Async(archivePath, ct);
            if (string.Equals(actualHash, _pinnedSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Model archive SHA-256 verified ({Hash})", actualHash);
                break;
            }

            _logger.LogError(
                "Model archive SHA-256 mismatch on attempt {Attempt}/{MaxAttempts} "
                + "(expected {Expected}, got {Actual})",
                attempt, maxAttempts, _pinnedSha256, actualHash);

            File.Delete(archivePath);

            if (attempt == maxAttempts)
            {
                throw new ModelHashMismatchException(
                    $"Downloaded model archive from {_downloadUrl} failed SHA-256 verification "
                    + $"{maxAttempts} times in a row (expected {_pinnedSha256}). Refusing to run "
                    + "inference against unverified weights. This may mean the release archive "
                    + "changed upstream, your network connection is corrupting the download, or "
                    + "something is intercepting the request — check your connection and, if the "
                    + "problem persists, download manually per spikes/s1-asr/README.md and point "
                    + "asr.modelDir at the result.");
            }
        }

        try
        {
            _extractor.Extract(archivePath, parentDir);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }
}
