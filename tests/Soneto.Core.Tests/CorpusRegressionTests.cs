using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Asr;
using Soneto.Core.Evaluation;
using Soneto.Core.Wav;
using Xunit.Abstractions;

namespace Soneto.Core.Tests;

/// <summary>
/// Plan §1.13's "Corpus regression" test: "a test that runs the full S2 corpus through the
/// real transcriber and asserts WER stays within the S2 baseline + 2%. Tagged
/// <c>[Trait("Category","Corpus")]</c>, excluded from the default run, executed manually
/// before any ASR-layer change."
///
/// <para>
/// <b>Honest status as of work item 12: this is real, reusable test INFRASTRUCTURE, not a
/// real regression assertion yet.</b> Spike S2 (the Romanian/English accuracy corpus this
/// test is supposed to run against — 60 WAV files + <c>reference.tsv</c>, per
/// <c>Docs/soneto-implementation-plan-phase0-1.md</c> §S2) was deliberately deferred by an
/// explicit user decision on 2026-08-31 (see <c>Docs/PROJECT-MEMORY.md</c>'s "Key decisions
/// locked in so far") and has never been run. <c>tests/Soneto.Corpus/</c> is empty except for
/// a README explaining the deferral. There is no corpus, no reference transcripts, and — most
/// importantly for this test's own literal spec — no real S2 baseline WER number to assert
/// "within +2%" of.
/// </para>
///
/// <para>
/// <b>What this class does today (with the corpus absent, which is the real, current state):
/// fails loudly with an actionable message</b> pointing at running S2 first, rather than
/// either (a) silently passing and claiming coverage that doesn't exist, or (b) inventing a
/// plausible-looking WER percentage and presenting it as a real measurement. This mirrors the
/// existing convention this file's sibling tests already established for "the thing this test
/// needs doesn't exist yet" (<see cref="SherpaOnnxTranscriberCorpusTests.FindRepoRoot"/>'s
/// <c>Assert.True(Directory.Exists(modelDir), ...)</c> pattern) rather than inventing a new
/// skip mechanism.
/// </para>
///
/// <para>
/// <b>Once S2 actually runs</b> and its 60 WAV files + <c>reference.tsv</c> land in
/// <c>tests/Soneto.Corpus/</c> (per the plan's own "Corpus moved to <c>tests/Soneto.Corpus/</c>"
/// exit-checklist item), this test will find them via <see cref="TryLoadCorpus"/> and run for
/// real: transcribe every WAV through the real <see cref="SherpaOnnxTranscriber"/> (same
/// model-resolution path as <see cref="SherpaOnnxTranscriberCorpusTests"/>: dev-model-dir
/// walk-up via <see cref="FindRepoRoot"/>, falling back to <see cref="ModelManager"/>'s
/// standard resolution), compute per-file and overall WER via
/// <see cref="WordErrorRateCalculator"/>, and assert against <see cref="S2BaselineWer"/> — but
/// that constant is a <c>null</c> placeholder below, deliberately, until a real S2 run
/// provides a real number. If the corpus is ever present but <see cref="S2BaselineWer"/> is
/// still <c>null</c>, this test fails loudly rather than skipping the assertion silently — see
/// the guard in <see cref="Runs_the_S2_corpus_and_asserts_WER_within_baseline_plus_2_percent"/>.
/// </para>
///
/// Run manually with:
///   dotnet test --filter "Category=Corpus"
/// </summary>
[Trait("Category", "Corpus")]
public sealed class CorpusRegressionTests
{
    /// <summary>
    /// S2's measured baseline overall WER (fraction, e.g. 0.08 for 8%), once S2 actually
    /// runs. <c>null</c> is the correct, honest value right now — S2 has NOT run yet (see
    /// class doc comment), so there is no real baseline to assert against. Do NOT replace
    /// this with an invented/estimated number "to make the test pass" — replace it only with
    /// a number that came from actually running S2's corpus through the S1 harness per
    /// <c>Docs/soneto-implementation-plan-phase0-1.md</c> §S2 step 4, and record where the
    /// number came from (date, corpus revision) right next to it when that happens.
    /// </summary>
    private static readonly double? S2BaselineWer = null;

    /// <summary>Plan §1.13's exact tolerance: "within the S2 baseline + 2%" (2 percentage points, absolute).</summary>
    private const double BaselineToleranceAbsolute = 0.02;

    // Aggregation note, worth preserving deliberately: the overall-WER calculation below sums
    // edit distances and reference-token counts across every corpus file first, THEN divides
    // once (totalEditDistance / totalReferenceTokens) -- it does NOT average each file's own
    // WerResult.Wer. This matters: a stray zero-reference-token row (e.g. a corpus entry whose
    // reference text is empty/whitespace-only) would make that one file's own .Wer either 0.0
    // or +Infinity per WerResult's own doc comment, and either value would silently poison a
    // naive per-file average. Summed-counts-then-divide-once is immune to that -- a
    // zero-token file just contributes 0 to both running totals. Do not "simplify" this to
    // averaging per-file WerResult.Wer values in a future edit without re-deriving this safety
    // property.

    private readonly ITestOutputHelper _output;

    public CorpusRegressionTests(ITestOutputHelper output)
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

    /// <summary>
    /// One row of S2's <c>reference.tsv</c>, per its exact documented shape (plan §S2 step 3):
    /// "<c>filename \t language \t exact expected text</c>".
    /// </summary>
    private sealed record CorpusEntry(string FileName, string Language, string ReferenceText);

    /// <summary>
    /// Attempts to load <c>tests/Soneto.Corpus/reference.tsv</c> and every WAV file it
    /// references. Returns <c>null</c> (not an exception) if the corpus directory or the
    /// reference file don't exist — the current, honest, expected state pending S2 — so the
    /// one test method below can distinguish "corpus doesn't exist" (fail loud with an
    /// actionable message) from "corpus exists but something else is wrong" (a normal
    /// assertion failure on the real data).
    /// </summary>
    private static IReadOnlyList<CorpusEntry>? TryLoadCorpus(string repoRoot)
    {
        var corpusDir = Path.Combine(repoRoot, "tests", "Soneto.Corpus");
        var referenceTsv = Path.Combine(corpusDir, "reference.tsv");
        if (!File.Exists(referenceTsv))
            return null;

        var entries = new List<CorpusEntry>();
        foreach (var line in File.ReadAllLines(referenceTsv))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t', 3);
            if (parts.Length != 3)
                throw new FormatException(
                    $"Malformed reference.tsv line (expected 'filename\\tlanguage\\ttext'): {line}");
            entries.Add(new CorpusEntry(parts[0].Trim(), parts[1].Trim(), parts[2]));
        }

        return entries;
    }

    [Fact]
    public async Task Runs_the_S2_corpus_and_asserts_WER_within_baseline_plus_2_percent()
    {
        var repoRoot = FindRepoRoot();

        IReadOnlyList<CorpusEntry>? corpus;
        try
        {
            corpus = TryLoadCorpus(repoRoot);
        }
        catch (FormatException ex)
        {
            // Same "something's wrong, here's how to fix it" framing as every other failure
            // path in this test, rather than letting a raw FormatException surface instead.
            Assert.Fail(
                "tests/Soneto.Corpus/reference.tsv exists but is malformed -- fix the offending "
                + $"line and re-run. Details: {ex.Message}");
            return;
        }

        if (corpus is null)
        {
            Assert.Fail(
                "S2's corpus (tests/Soneto.Corpus/reference.tsv + WAV files) does not exist yet. "
                + "This is EXPECTED as of Phase 1 work item 12 -- S2 was deliberately deferred by "
                + "an explicit user decision (see Docs/PROJECT-MEMORY.md's 'Key decisions locked in "
                + "so far'). This test is real, reusable infrastructure waiting for S2's data, not a "
                + "broken test: run S2 first (Docs/soneto-implementation-plan-phase0-1.md, section "
                + "'S2 -- Romanian accuracy on your voice'), move its 60 WAV files + reference.tsv "
                + "into tests/Soneto.Corpus/, then re-run `dotnet test --filter \"Category=Corpus\"`.");
            return;
        }

        Assert.True(corpus.Count > 0, "reference.tsv exists but contains no entries -- nothing to test.");

        if (S2BaselineWer is null)
        {
            Assert.Fail(
                "S2's corpus is present, but CorpusRegressionTests.S2BaselineWer is still the "
                + "unset placeholder (null). A real S2 baseline WER must be measured (run the "
                + "corpus once, record the resulting overall WER) and that exact number set as "
                + "S2BaselineWer before this test can meaningfully assert 'stays within baseline + "
                + "2%' per plan §1.13. Do not invent a plausible-looking number here -- measure it.");
            return;
        }

        var corpusDir = Path.Combine(repoRoot, "tests", "Soneto.Corpus");
        var modelDir = Path.Combine(repoRoot, "models", ModelManager.ModelFolderName);
        Assert.True(Directory.Exists(modelDir), $"Model dir not found: {modelDir}");

        await using var transcriber = new SherpaOnnxTranscriber(
            NullLogger<SherpaOnnxTranscriber>.Instance, modelDir, numThreads: 4);
        await transcriber.InitializeAsync(CancellationToken.None);
        Assert.True(transcriber.IsReady);

        int totalEditDistance = 0;
        int totalReferenceTokens = 0;

        foreach (var entry in corpus)
        {
            var wavPath = Path.Combine(corpusDir, entry.FileName);
            Assert.True(File.Exists(wavPath), $"Corpus WAV referenced by reference.tsv not found: {wavPath}");

            var wav = WavReader.Read(wavPath);
            var result = await transcriber.TranscribeAsync(wav.Samples, CancellationToken.None);
            var wer = WordErrorRateCalculator.Compute(entry.ReferenceText, result.Text);

            totalEditDistance += wer.EditDistance;
            totalReferenceTokens += wer.ReferenceTokenCount;

            _output.WriteLine(
                $"{entry.FileName} [{entry.Language}]: WER={wer.Wer:P1} "
                + $"(sub={wer.Substitutions} ins={wer.Insertions} del={wer.Deletions} refTokens={wer.ReferenceTokenCount})");
        }

        double overallWer = totalReferenceTokens == 0 ? 0.0 : (double)totalEditDistance / totalReferenceTokens;
        double allowedWer = S2BaselineWer.Value + BaselineToleranceAbsolute;

        _output.WriteLine($"Overall WER: {overallWer:P1}  (S2 baseline: {S2BaselineWer:P1}, allowed up to: {allowedWer:P1})");

        Assert.True(overallWer <= allowedWer,
            $"Overall corpus WER {overallWer:P1} exceeds the S2 baseline ({S2BaselineWer:P1}) + "
            + $"{BaselineToleranceAbsolute:P0} tolerance ({allowedWer:P1}) -- an ASR-layer change "
            + "regressed transcription accuracy.");
    }
}
