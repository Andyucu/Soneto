using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Asr;
using Soneto.Core.Wav;
using Xunit.Abstractions;

namespace Soneto.Core.Tests;

/// <summary>
/// Plan §1.14, item 3b ("Stream lifecycle + native memory") and §1.13's "Leak/stress
/// tests" convention: 1000 sequential decodes against the real transcriber, asserting
/// the process working set stays flat (within 5%) from iteration 50 onward — i.e. some
/// initial JIT/warm-up growth in the first ~50 iterations is expected and allowed, but
/// growth beyond that would indicate every-decode <c>OfflineStream</c> native memory isn't
/// being released (see <see cref="SherpaOnnxTranscriber"/>'s <c>Decode</c> method, which
/// wraps the per-utterance <c>OfflineStream</c> from <c>OfflineRecognizer.CreateStream()</c>
/// in a <c>using</c> so it's disposed after every decode, success or failure).
///
/// Tagged <c>[Trait("Category","Corpus")]</c> — same convention (and same
/// <c>VSTestTestCaseFilter</c> default exclusion) as
/// <see cref="SherpaOnnxTranscriberCorpusTests"/>, since this also requires the real
/// downloaded model and, on top of that, takes several minutes for 1000 real decodes —
/// unsuitable for the default `dotnet test` run.
///
/// Run manually with:
///   dotnet test --filter "Category=Corpus"
///
/// Measurement caveat: this samples <c>WorkingSet64</c> (resident memory), not
/// <c>PrivateMemorySize64</c> (commit charge). If leaked native allocations went cold and
/// were paged out by the OS, working set could stay flat even with a genuine unbounded
/// leak. Not a concern for the defect class this test actually targets (a missing/incorrect
/// <c>using</c> on the per-decode <c>OfflineStream</c> would leak "hot" memory that's
/// touched every iteration, not something that goes idle and gets paged out), but worth
/// knowing if this test is ever repurposed to chase a different, slower leak.
/// </summary>
[Trait("Category", "Corpus")]
public sealed class SherpaOnnxTranscriberStressTests
{
    private const int TotalIterations = 1000;
    private const int WarmupIterations = 50;
    private const int SampleEvery = 50;
    private const double FlatnessTolerance = 0.05;

    private readonly ITestOutputHelper _output;

    public SherpaOnnxTranscriberStressTests(ITestOutputHelper output)
    {
        _output = output;
    }

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
    public async Task Working_set_stays_flat_across_1000_sequential_decodes()
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
        var samples = wav.Samples;

        // Checkpoint 0 is taken before any decode (baseline); checkpoints thereafter are
        // taken every SampleEvery iterations, plus one right at WarmupIterations so the
        // "flat from iteration 50 onward" checkpoints line up exactly.
        var checkpoints = new List<(int Iteration, long WorkingSetBytes)>();

        void Sample(int iteration)
        {
            // Force a GC before sampling so what we observe reflects live native + managed
            // memory rather than not-yet-collected managed garbage, which would otherwise
            // make a healthy native-memory story look like a leak.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var currentProcess = Process.GetCurrentProcess();
            var ws = currentProcess.WorkingSet64;
            checkpoints.Add((iteration, ws));
            _output.WriteLine($"iteration={iteration,4}  workingSet={ws,12:N0} bytes ({ws / 1024.0 / 1024.0,8:F2} MB)");
        }

        var totalSw = Stopwatch.StartNew();
        Sample(0);
        for (int i = 1; i <= TotalIterations; i++)
        {
            var result = await transcriber.TranscribeAsync(samples, CancellationToken.None);
            Assert.False(result.IsEmpty);

            if (i == WarmupIterations || i % SampleEvery == 0)
                Sample(i);
        }
        totalSw.Stop();

        _output.WriteLine($"Total wall-clock for {TotalIterations} decodes: {totalSw.Elapsed.TotalSeconds:F1}s");

        // Assert flat (within FlatnessTolerance) from iteration WarmupIterations onward:
        // every post-warm-up checkpoint's working set must be within tolerance of the
        // iteration-WarmupIterations baseline.
        var postWarmup = checkpoints.Where(c => c.Iteration >= WarmupIterations).ToList();
        Assert.True(postWarmup.Count >= 2,
            "Expected at least 2 post-warm-up checkpoints to compare for flatness.");

        var baseline = postWarmup[0].WorkingSetBytes;
        var maxAllowedDelta = baseline * FlatnessTolerance;

        foreach (var (iteration, ws) in postWarmup)
        {
            var delta = Math.Abs(ws - baseline);
            _output.WriteLine(
                $"  checkpoint iteration={iteration,4}: workingSet={ws:N0}, delta from baseline={delta:N0} "
                + $"({delta / (double)baseline:P2}), allowed={maxAllowedDelta:N0} ({FlatnessTolerance:P0})");

            Assert.True(delta <= maxAllowedDelta,
                $"Working set at iteration {iteration} ({ws:N0} bytes) deviated from the "
                + $"iteration-{WarmupIterations} baseline ({baseline:N0} bytes) by {delta:N0} bytes "
                + $"({delta / (double)baseline:P2}), exceeding the {FlatnessTolerance:P0} flatness "
                + "tolerance — possible native memory leak in per-decode OfflineStream lifecycle.");
        }
    }
}
