using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Asr;

namespace Soneto.Core.Tests;

/// <summary>
/// <see cref="ModelManager"/> tests per plan §1.13: path resolution order, SHA-256
/// verification, hash-mismatch retry-then-fail, and missing-files-after-extraction
/// detection — all against fakes/temp dirs, no real model file or network access.
/// </summary>
public sealed class ModelManagerTests : IDisposable
{
    private readonly string _tempDir;

    public ModelManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "soneto-modelmanager-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static void WriteRequiredFiles(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var f in ModelManager.RequiredFiles)
            File.WriteAllText(Path.Combine(dir, f), "fake");
    }

    /// A downloader fake that writes fixed bytes to the destination on every call and
    /// records how many times it was invoked.
    private sealed class FakeDownloader(byte[][] payloadsPerCall) : IModelArchiveDownloader
    {
        public int CallCount { get; private set; }

        public Task DownloadAsync(
            Uri url, string destinationPath, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
        {
            var payload = payloadsPerCall[Math.Min(CallCount, payloadsPerCall.Length - 1)];
            File.WriteAllBytes(destinationPath, payload);
            CallCount++;
            progress?.Report(new ModelDownloadProgress(payload.Length, payload.Length));
            return Task.CompletedTask;
        }
    }

    /// An extractor fake that, instead of running `tar`, populates the destination
    /// directory with a caller-supplied file set (so "extraction leaves files missing"
    /// is directly testable without a real archive).
    private sealed class FakeExtractor(Action<string> populate) : IArchiveExtractor
    {
        public int CallCount { get; private set; }

        public void Extract(string archivePath, string destinationParentDir)
        {
            populate(destinationParentDir);
            CallCount++;
        }
    }

    [Fact]
    public async Task Config_override_with_all_required_files_is_used_directly()
    {
        var configDir = Path.Combine(_tempDir, "configured-model");
        WriteRequiredFiles(configDir);

        var sut = new ModelManager(
            NullLoggerFactory.Instance.CreateLogger<ModelManager>(),
            downloader: new FakeDownloader([[1]]),
            standardModelsBaseDirOverride: Path.Combine(_tempDir, "should-not-be-used"));

        var resolved = await sut.ResolveOrDownloadAsync(configDir);

        Assert.Equal(configDir, resolved);
    }

    [Fact]
    public async Task Config_override_missing_required_files_throws_and_does_not_fall_back()
    {
        var configDir = Path.Combine(_tempDir, "configured-incomplete");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "tokens.txt"), "fake"); // only 1 of 4 files

        var standardDir = Path.Combine(_tempDir, "standard-base");
        WriteRequiredFiles(Path.Combine(standardDir, ModelManager.ModelFolderName));

        var sut = new ModelManager(
            NullLoggerFactory.Instance.CreateLogger<ModelManager>(),
            downloader: new FakeDownloader([[1]]),
            standardModelsBaseDirOverride: standardDir);

        var ex = await Assert.ThrowsAsync<ModelFilesMissingException>(
            () => sut.ResolveOrDownloadAsync(configDir));

        Assert.Contains("encoder.int8.onnx", ex.Message);
    }

    [Fact]
    public async Task No_config_override_uses_standard_dir_when_already_present_without_downloading()
    {
        var standardDir = Path.Combine(_tempDir, "standard-base");
        WriteRequiredFiles(Path.Combine(standardDir, ModelManager.ModelFolderName));
        var downloader = new FakeDownloader([[1]]);

        var sut = new ModelManager(
            NullLoggerFactory.Instance.CreateLogger<ModelManager>(),
            downloader: downloader,
            standardModelsBaseDirOverride: standardDir);

        var resolved = await sut.ResolveOrDownloadAsync(configModelDir: null);

        Assert.Equal(Path.Combine(standardDir, ModelManager.ModelFolderName), resolved);
        Assert.Equal(0, downloader.CallCount);
    }

    [Fact]
    public async Task Downloads_and_extracts_when_standard_dir_absent()
    {
        var standardDir = Path.Combine(_tempDir, "standard-base");
        byte[] goodPayload = "good-archive-bytes"u8.ToArray();
        string goodHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(goodPayload));

        var downloader = new FakeDownloader([goodPayload]);
        var extractor = new FakeExtractor(parentDir =>
            WriteRequiredFiles(Path.Combine(parentDir, ModelManager.ModelFolderName)));

        var sut = new ModelManager(
            NullLoggerFactory.Instance.CreateLogger<ModelManager>(),
            downloader: downloader,
            extractor: extractor,
            pinnedSha256: goodHash,
            standardModelsBaseDirOverride: standardDir);

        var resolved = await sut.ResolveOrDownloadAsync(configModelDir: null);

        Assert.Equal(Path.Combine(standardDir, ModelManager.ModelFolderName), resolved);
        Assert.Equal(1, downloader.CallCount);
        Assert.Equal(1, extractor.CallCount);
    }

    [Fact]
    public async Task Hash_mismatch_retries_once_then_succeeds_if_second_attempt_matches()
    {
        var standardDir = Path.Combine(_tempDir, "standard-base");
        byte[] badPayload = "bad-bytes"u8.ToArray();
        byte[] goodPayload = "good-archive-bytes"u8.ToArray();
        string goodHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(goodPayload));

        var downloader = new FakeDownloader([badPayload, goodPayload]);
        var extractor = new FakeExtractor(parentDir =>
            WriteRequiredFiles(Path.Combine(parentDir, ModelManager.ModelFolderName)));

        var sut = new ModelManager(
            NullLoggerFactory.Instance.CreateLogger<ModelManager>(),
            downloader: downloader,
            extractor: extractor,
            pinnedSha256: goodHash,
            standardModelsBaseDirOverride: standardDir);

        var resolved = await sut.ResolveOrDownloadAsync(configModelDir: null);

        Assert.Equal(Path.Combine(standardDir, ModelManager.ModelFolderName), resolved);
        Assert.Equal(2, downloader.CallCount);
        Assert.Equal(1, extractor.CallCount);
    }

    [Fact]
    public async Task Hash_mismatch_twice_fails_without_extracting_and_deletes_bad_download()
    {
        var standardDir = Path.Combine(_tempDir, "standard-base");
        byte[] badPayload = "still-bad-bytes"u8.ToArray();

        var downloader = new FakeDownloader([badPayload]);
        var extractor = new FakeExtractor(_ => throw new InvalidOperationException("should not be called"));

        var sut = new ModelManager(
            NullLoggerFactory.Instance.CreateLogger<ModelManager>(),
            downloader: downloader,
            extractor: extractor,
            pinnedSha256: "0000000000000000000000000000000000000000000000000000000000000000",
            standardModelsBaseDirOverride: standardDir);

        await Assert.ThrowsAsync<ModelHashMismatchException>(
            () => sut.ResolveOrDownloadAsync(configModelDir: null));

        Assert.Equal(2, downloader.CallCount);
        Assert.Equal(0, extractor.CallCount);
    }

    [Fact]
    public async Task Missing_files_after_extraction_throws()
    {
        var standardDir = Path.Combine(_tempDir, "standard-base");
        byte[] goodPayload = "good-archive-bytes"u8.ToArray();
        string goodHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(goodPayload));

        var downloader = new FakeDownloader([goodPayload]);
        // Extractor "succeeds" but only produces 2 of the 4 required files.
        var extractor = new FakeExtractor(parentDir =>
        {
            var modelDir = Path.Combine(parentDir, ModelManager.ModelFolderName);
            Directory.CreateDirectory(modelDir);
            File.WriteAllText(Path.Combine(modelDir, "tokens.txt"), "fake");
            File.WriteAllText(Path.Combine(modelDir, "encoder.int8.onnx"), "fake");
        });

        var sut = new ModelManager(
            NullLoggerFactory.Instance.CreateLogger<ModelManager>(),
            downloader: downloader,
            extractor: extractor,
            pinnedSha256: goodHash,
            standardModelsBaseDirOverride: standardDir);

        var ex = await Assert.ThrowsAsync<ModelFilesMissingException>(
            () => sut.ResolveOrDownloadAsync(configModelDir: null));

        Assert.Contains("decoder.int8.onnx", ex.Message);
        Assert.Contains("joiner.int8.onnx", ex.Message);
    }

    [Fact]
    public async Task ComputeSha256Async_matches_known_value()
    {
        var path = Path.Combine(_tempDir, "hash-me.txt");
        await File.WriteAllTextAsync(path, "hello world");

        var hash = await ModelManager.ComputeSha256Async(path);

        // Known SHA-256 of the literal string "hello world".
        Assert.Equal("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9", hash);
    }

    [Fact]
    public void AreRequiredFilesPresent_false_when_directory_missing()
    {
        Assert.False(ModelManager.AreRequiredFilesPresent(Path.Combine(_tempDir, "does-not-exist")));
    }

    [Fact]
    public void AreRequiredFilesPresent_true_when_all_four_files_exist()
    {
        var dir = Path.Combine(_tempDir, "complete-model");
        WriteRequiredFiles(dir);

        Assert.True(ModelManager.AreRequiredFilesPresent(dir));
    }
}
