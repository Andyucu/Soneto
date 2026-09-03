using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Abstractions;
using Soneto.Core.Asr;
using Soneto.Core.Wav;

namespace Soneto.Core.Tests;

/// <summary>
/// Manual verification path for <see cref="SherpaOnnxTranscriber"/>, per plan §1.13's
/// corpus-test convention: tagged <c>[Trait("Category","Corpus")]</c>, excluded from the
/// default `dotnet test` run (which must pass with no model file present), and run
/// manually against the real downloaded model.
///
/// S2's actual corpus (`tests/Soneto.Corpus/`) doesn't exist yet (deferred by decision —
/// see Docs/PROJECT-MEMORY.md), so this uses a 16kHz-resampled copy of the sherpa-onnx
/// model release's own bundled `test_wavs/en.wav` (real recorded speech, a JFK quote,
/// natively 24kHz) as the real-clip smoke test. The copy is pre-resampled with ffmpeg at
/// authoring time and checked in as a test asset — <see cref="ITranscriber"/>'s contract
/// requires samples that are already 16kHz mono (the polyphase resampler is item 4, not
/// built yet), so feeding the file's native 24kHz samples directly here would silently
/// violate that contract rather than testing it honestly.
///
/// Run manually with:
///   dotnet test --filter "Category=Corpus"
/// </summary>
[Trait("Category", "Corpus")]
public sealed class SherpaOnnxTranscriberCorpusTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "models")) &&
                File.Exists(Path.Combine(dir.FullName, "soneto.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not find repo root (looked for soneto.slnx + models/ walking up from "
            + AppContext.BaseDirectory + "). This test requires the real downloaded model.");
    }

    [Fact]
    public async Task Transcribes_real_recorded_speech_clip_with_non_empty_punctuated_output()
    {
        var repoRoot = FindRepoRoot();
        var modelDir = Path.Combine(repoRoot, "models", ModelManager.ModelFolderName);
        var clipPath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "en-16k.wav");

        Assert.True(Directory.Exists(modelDir), $"Model dir not found: {modelDir}");
        Assert.True(File.Exists(clipPath), $"Test clip not found: {clipPath}");

        await using var transcriber = new SherpaOnnxTranscriber(
            NullLogger<SherpaOnnxTranscriber>.Instance, modelDir, numThreads: 4);

        await transcriber.InitializeAsync(CancellationToken.None);
        Assert.True(transcriber.IsReady);

        var wav = WavReader.Read(clipPath);
        var result = await transcriber.TranscribeAsync(wav.Samples, CancellationToken.None);

        Assert.False(result.IsEmpty);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        // Real recorded speech through Parakeet should come back capitalized/punctuated
        // with no post-processing (plan §S1 pass criterion).
        Assert.Matches(@"[.!?]\s*$", result.Text.Trim());
        Assert.True(char.IsUpper(result.Text.TrimStart()[0]));
    }
}
