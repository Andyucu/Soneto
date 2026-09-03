using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Audio;
using Soneto.Core.Wav;

namespace Soneto.Core.Tests.Audio;

/// <summary>
/// Real unit tests for <see cref="DebugAudioStore"/> (Phase 3 item 10, §3.14) against a real
/// temp directory with real WAV files -- no mocked file I/O, mirroring
/// <c>SqliteHistoryStoreTests</c>'/<c>ConfigService</c>/<c>DictionaryService</c> tests' own
/// "real temp files" precedent.
/// </summary>
public sealed class DebugAudioStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"soneto-debug-audio-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task SaveClipAsync_WritesARealWavFileNamedByHistoryId_ReadableByWavReader()
    {
        float[] samples = [0f, 0.5f, -0.5f, 0.25f];

        await DebugAudioStore.SaveClipAsync(_dir, historyId: 42, samples, sampleRate: 16000, maxClips: 20, NullLogger.Instance);

        var path = Path.Combine(_dir, "42.wav");
        Assert.True(File.Exists(path));

        var read = WavReader.Read(path);
        Assert.Equal(16000, read.SampleRate);
        Assert.Equal(samples.Length, read.Samples.Length);
        for (int i = 0; i < samples.Length; i++)
            Assert.Equal(samples[i], read.Samples[i], precision: 3);
    }

    [Fact]
    public async Task SaveClipAsync_CreatesTheDirectoryIfMissing()
    {
        Assert.False(Directory.Exists(_dir));

        await DebugAudioStore.SaveClipAsync(_dir, 1, new float[] { 0f }, 16000, 20, NullLogger.Instance);

        Assert.True(Directory.Exists(_dir));
        Assert.True(File.Exists(Path.Combine(_dir, "1.wav")));
    }

    [Fact]
    public async Task SaveClipAsync_NeverThrows_WhenTheTargetPathIsUnwritable()
    {
        // A file (not a directory) at the "directory" path makes Directory.CreateDirectory fail
        // -- the same deterministic, cross-platform-consistent simulated-write-failure shape
        // SqliteHistoryStoreTests' own AppendAsync-never-throws test uses.
        var blockingFilePath = Path.Combine(Path.GetTempPath(), $"soneto-debug-audio-blocker-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blockingFilePath, "not a directory");
        try
        {
            var badDir = Path.Combine(blockingFilePath, "nested");

            // Must not throw.
            await DebugAudioStore.SaveClipAsync(badDir, 1, new float[] { 0f }, 16000, 20, NullLogger.Instance);
        }
        finally
        {
            File.Delete(blockingFilePath);
        }
    }

    [Fact]
    public async Task SaveClipAsync_PurgesToMaxClips_KeepingTheHighestNumberedFiles()
    {
        for (long id = 1; id <= 5; id++)
            await DebugAudioStore.SaveClipAsync(_dir, id, new float[] { 0f }, 16000, maxClips: 3, NullLogger.Instance);

        var remaining = Directory.GetFiles(_dir, "*.wav")
            .Select(p => long.Parse(Path.GetFileNameWithoutExtension(p)))
            .OrderBy(id => id)
            .ToList();

        Assert.Equal([3L, 4L, 5L], remaining);
    }

    [Fact]
    public void PurgeToMaxClips_LeavesFilesThatDoNotParseAsALongAlone()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "not-a-number.wav"), "x");
        File.WriteAllText(Path.Combine(_dir, "1.wav"), "x");
        File.WriteAllText(Path.Combine(_dir, "2.wav"), "x");

        DebugAudioStore.PurgeToMaxClips(_dir, maxClips: 1, NullLogger.Instance);

        var remainingNames = Directory.GetFiles(_dir, "*.wav").Select(Path.GetFileName).ToHashSet();
        Assert.Contains("not-a-number.wav", remainingNames);
        Assert.Contains("2.wav", remainingNames);
        Assert.DoesNotContain("1.wav", remainingNames);
    }

    [Fact]
    public void PurgeToMaxClips_OnAMissingDirectory_IsANoOp_NeverThrows()
    {
        DebugAudioStore.PurgeToMaxClips(Path.Combine(_dir, "does-not-exist"), maxClips: 3, NullLogger.Instance);
    }

    [Fact]
    public async Task WipeAll_DeletesEveryWavFile()
    {
        await DebugAudioStore.SaveClipAsync(_dir, 1, new float[] { 0f }, 16000, 20, NullLogger.Instance);
        await DebugAudioStore.SaveClipAsync(_dir, 2, new float[] { 0f }, 16000, 20, NullLogger.Instance);
        Assert.Equal(2, Directory.GetFiles(_dir, "*.wav").Length);

        DebugAudioStore.WipeAll(_dir, NullLogger.Instance);

        Assert.Empty(Directory.GetFiles(_dir, "*.wav"));
    }

    [Fact]
    public void WipeAll_OnAMissingDirectory_IsANoOp_NeverThrows()
    {
        DebugAudioStore.WipeAll(Path.Combine(_dir, "does-not-exist"), NullLogger.Instance);
    }
}
