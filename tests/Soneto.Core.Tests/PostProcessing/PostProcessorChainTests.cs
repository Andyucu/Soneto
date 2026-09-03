using Soneto.Core.Abstractions;
using Soneto.Core.Dictionary;
using Soneto.Core.PostProcessing;

namespace Soneto.Core.Tests.PostProcessing;

/// <summary>
/// End-to-end tests for <see cref="PostProcessorChain"/> (plan §1.7/§1.14 item 8, updated by
/// Phase 2 item 6): confirms the full default-enabled chain respects order
/// 10 -> 30 -> 60 -> 90 (<see cref="UnicodeNormalizerProcessor"/> -> <see cref="WhitespaceCleanerProcessor"/>
/// -> <see cref="SpokenCommandsExtensionProcessor"/> -> <see cref="TrailingSpaceProcessor"/>),
/// per Phase 2 item 0's renumbering of <see cref="TrailingSpaceProcessor"/> from 40 to 90 and
/// item 6's retirement of Phase 1's <c>SpokenCommandsProcessor</c> (order 20) in favour of
/// <see cref="SpokenCommandsExtensionProcessor"/> (order 60) -- a real reordering relative to
/// <see cref="WhitespaceCleanerProcessor"/> (order 30), which now runs BEFORE spoken commands
/// instead of after. The newline-preservation property still holds through the real composed
/// chain (not just a hand-constructed two-processor sequence, per §1.13's testing requirement) --
/// see <see cref="SpokenCommandsExtensionProcessor"/>'s own doc comment for why this reordering
/// is safe (that class performs its own local whitespace cleanup around freshly-emitted control
/// characters, since nothing downstream of order 60 does it anymore).
/// </summary>
public class PostProcessorChainTests
{
    private static PostProcessorChain DefaultChain() => new(
    [
        new UnicodeNormalizerProcessor(),
        new SpokenCommandsExtensionProcessor([]),
        new WhitespaceCleanerProcessor(),
        new TrailingSpaceProcessor(),
    ]);

    [Fact]
    public void FullChain_RunsStagesInAscendingOrder()
    {
        // Feed cedilla chars (order 10 fixes them) + messy spacing (order 30 cleans it) +
        // spoken command (order 60 emits \n, and cleans its own trailing whitespace) + expect
        // trailing space (order 90). If any stage ran out of order, this composite would not
        // come out right.
        var chain = DefaultChain();
        var result = chain.Process("ştiu,   new line,   ţara");

        Assert.Equal("știu,\n, țara ", result.Text);
    }

    [Fact]
    public void FullChain_NewlinesFromSpokenCommands_SurviveWhitespaceCleaning()
    {
        var chain = DefaultChain();
        var result = chain.Process("first paragraph, new paragraph, second paragraph");

        Assert.Contains("\n\n", result.Text);
        Assert.Equal("first paragraph,\n\n, second paragraph ", result.Text);
    }

    [Fact]
    public void FullChain_PlainSentence_EndsWithTrailingSpace()
    {
        var chain = DefaultChain();
        var result = chain.Process("Hello, world!");
        Assert.Equal("Hello, world! ", result.Text);
    }

    [Fact]
    public void Constructor_SortsProcessorsByOrder_RegardlessOfInputOrder()
    {
        // Deliberately construct in a shuffled order to prove the chain sorts internally.
        var chain = new PostProcessorChain(
        [
            new TrailingSpaceProcessor(),
            new WhitespaceCleanerProcessor(),
            new UnicodeNormalizerProcessor(),
            new SpokenCommandsExtensionProcessor([]),
        ]);

        var result = chain.Process("new line");
        // If SpokenCommandsExtensionProcessor (60) ran before WhitespaceCleaner (30) somehow,
        // or the sort were broken, this composite wouldn't come out right.
        Assert.Equal("\n", result.Text);
    }

    /// <summary>
    /// Phase 2 item 10's §2.11 "done when" criterion: the FULL chain -- all four Phase 1
    /// processors plus all three of Phase 2's remaining dictionary-backed processors
    /// (<see cref="DictionaryEngineProcessor"/> order 40, <see cref="RegexRuleProcessor"/> order
    /// 50, <see cref="FillerWordStripper"/> order 70), alongside the already-covered
    /// <see cref="SpokenCommandsExtensionProcessor"/> (order 60) -- produces correct output for
    /// one representative multi-feature transcript exercising: diacritic normalization (cedilla
    /// -> comma-below), a real seed-dictionary correction (mis-cased "webmethods" -> "webMethods"),
    /// a regex rule (cascading against the dictionary engine's own output, per §2.5's documented
    /// asymmetry), a spoken command ("new paragraph" -> "\n\n"), filler-word stripping ("um"), and
    /// the trailing-space processor running last (order 90).
    /// </summary>
    [Fact]
    public void FullChain_AllSevenProcessors_MultiFeatureTranscript_ProducesCorrectOutput()
    {
        var dictionaryEntries = new List<Soneto.Core.Dictionary.DictionaryEntry>
        {
            new Soneto.Core.Dictionary.CorrectionPair
            {
                Id = "test.corr.webmethods", From = "webmethods", To = "webMethods",
            },
            new Soneto.Core.Dictionary.RegexRule
            {
                Id = "test.regex.is-number", Pattern = @"\bIS(\d+)\b", Replacement = "IS $1",
            },
        };

        var chain = new PostProcessorChain(
        [
            new UnicodeNormalizerProcessor(),
            new WhitespaceCleanerProcessor(),
            new DictionaryEngineProcessor(dictionaryEntries),
            new RegexRuleProcessor(dictionaryEntries),
            new SpokenCommandsExtensionProcessor([]),
            new FillerWordStripper(),
            new TrailingSpaceProcessor(),
        ]);

        // ştiu (cedilla ş -> comma-below ş via order 10) + "webmethods" (dictionary correction,
        // order 40) + "IS7" (order 50's regex rule inserts the missing space) + comma-bounded
        // "um" filler (order 70, collapsing the resulting comma-run per that class's own
        // documented cleanup) + "New paragraph" spoken command (order 60) + trailing space
        // (order 90).
        var result = chain.Process(
            "ştiu că webmethods rulează pe IS7, um, e bine. New paragraph. Gata.");

        Assert.Equal(
            "știu că webMethods rulează pe IS 7, e bine.\n\n. Gata. ",
            result.Text);
    }

    /// <summary>
    /// Independent-verification follow-up (Phase 2 item 10): the test above proves chain
    /// ORDERING with hand-picked, synthetic dictionary entries -- it does NOT prove that the
    /// REAL <c>seed-dictionary.json</c> shipped with the app actually composes correctly through
    /// the real chain, since none of its entries (a <see cref="VocabularyTerm"/>, not a
    /// <see cref="CorrectionPair"/>; zero <see cref="RegexRule"/>s) match what that test
    /// constructs by hand. This test closes that gap: it loads the seed dictionary through the
    /// REAL <see cref="DictionaryService"/> first-run path (same mechanism as
    /// <c>SeedDictionaryTests</c>), builds the full 7-processor chain from those loaded entries
    /// exactly as <c>Program.cs</c>'s <c>BuildPostProcessors</c> does, and exercises one real
    /// seed <see cref="VocabularyTerm"/> casing correction ("webmethods" -> "webMethods") and one
    /// real seed <see cref="SpokenCommand"/> ("new paragraph" -> "\n\n") end-to-end. There are no
    /// <see cref="RegexRule"/> entries in the real seed file, so -- per this item's own
    /// instruction not to fabricate a regex assertion here -- this test does not exercise
    /// <see cref="RegexRuleProcessor"/>'s matching behaviour; that is already covered by its own
    /// dedicated unit tests from item 5, and its "no matching rules means no-op" behaviour is
    /// implicitly exercised here since it runs in the chain with zero applicable rules.
    /// </summary>
    [Fact]
    public async Task FullChain_RealSeedDictionary_MultiFeatureTranscript_ProducesCorrectOutput()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(), "soneto-postprocessor-chain-seed-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var dictionaryPath = Path.Combine(tempDir, "dictionary.json");
            var logger = new TestLogger<DictionaryService>();
            var dictionaryService = new DictionaryService(logger, dictionaryPath);

            var ok = await dictionaryService.LoadAsync();
            Assert.True(ok);

            var entries = dictionaryService.Current.Entries;

            var chain = new PostProcessorChain(
            [
                new UnicodeNormalizerProcessor(),
                new WhitespaceCleanerProcessor(),
                new DictionaryEngineProcessor(entries),
                new RegexRuleProcessor(entries),
                new SpokenCommandsExtensionProcessor(entries),
                new FillerWordStripper(),
                new TrailingSpaceProcessor(),
            ]);

            // ştiu (cedilla ş -> comma-below ş via order 10) + "webmethods" (real seed
            // VocabularyTerm casing correction, order 40) + comma-bounded "um" filler (order 70)
            // + real seed "new paragraph" SpokenCommand (order 60) + trailing space (order 90).
            var result = chain.Process(
                "ştiu că folosim webmethods, um, zilnic. New paragraph. Gata.");

            Assert.Equal(
                "știu că folosim webMethods, zilnic.\n\n. Gata. ",
                result.Text);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Phase 4 item 3 (§4.4): per-app profile selection ──

    private static Soneto.Core.Dictionary.PerAppOverride Profile(
        string processName, bool autoCapitalize, bool trailingPunctuation) =>
        new()
        {
            Id = "test." + processName,
            ProcessName = processName,
            AutoCapitalize = autoCapitalize,
            TrailingPunctuation = trailingPunctuation,
        };

    /// <summary>
    /// The core "safe additive scope" guarantee this item's own report documents: a chain built
    /// WITH a per-app table, called via the new two-argument <c>Process</c> overload with a
    /// process name that has NO matching profile, must behave identically to the base
    /// single-argument <c>Process(string)</c> call -- proves the default/no-match path is not
    /// merely value-identical but reaches the exact same base processor list.
    /// </summary>
    [Fact]
    public void Process_WithPerAppTable_NoMatch_BehavesIdenticallyToBaseChain()
    {
        var perApp = new Dictionary<string, Soneto.Core.Dictionary.PerAppOverride>
        {
            ["wt.exe"] = Profile("wt.exe", autoCapitalize: true, trailingPunctuation: true),
        };
        var chain = new PostProcessorChain([new UnicodeNormalizerProcessor()], perApp);

        var noTable = chain.Process("hello world");
        var noMatch = chain.Process("hello world", "notepad.exe");
        var nullProcessName = chain.Process("hello world", null);

        Assert.Equal(noTable.Text, noMatch.Text);
        Assert.Equal(noTable.Text, nullProcessName.Text);
        Assert.Equal("hello world", noTable.Text);
    }

    [Fact]
    public void Process_MatchingProfile_AutoCapitalizeOnly_AppliesOnlyCapitalization()
    {
        var perApp = new Dictionary<string, Soneto.Core.Dictionary.PerAppOverride>
        {
            ["wt.exe"] = Profile("wt.exe", autoCapitalize: true, trailingPunctuation: false),
        };
        var chain = new PostProcessorChain([], perApp);

        var result = chain.Process("hello world", "wt.exe");

        Assert.Equal("Hello world", result.Text);
    }

    [Fact]
    public void Process_MatchingProfile_TrailingPunctuationOnly_AppliesOnlyPunctuation()
    {
        var perApp = new Dictionary<string, Soneto.Core.Dictionary.PerAppOverride>
        {
            ["wt.exe"] = Profile("wt.exe", autoCapitalize: false, trailingPunctuation: true),
        };
        var chain = new PostProcessorChain([], perApp);

        var result = chain.Process("hello world", "wt.exe");

        Assert.Equal("hello world.", result.Text);
    }

    [Fact]
    public void Process_MatchingProfile_Both_AppliesCapitalizationAndPunctuation()
    {
        var perApp = new Dictionary<string, Soneto.Core.Dictionary.PerAppOverride>
        {
            ["wt.exe"] = Profile("wt.exe", autoCapitalize: true, trailingPunctuation: true),
        };
        var chain = new PostProcessorChain([], perApp);

        var result = chain.Process("hello world", "wt.exe");

        Assert.Equal("Hello world.", result.Text);
    }

    [Fact]
    public void Process_MatchingProfile_BothFalse_BehavesLikeNoMatch()
    {
        var perApp = new Dictionary<string, Soneto.Core.Dictionary.PerAppOverride>
        {
            ["wt.exe"] = Profile("wt.exe", autoCapitalize: false, trailingPunctuation: false),
        };
        var chain = new PostProcessorChain([], perApp);

        var result = chain.Process("hello world", "wt.exe");

        Assert.Equal("hello world", result.Text);
    }

    /// <summary>
    /// Composes correctly with the real full chain's existing <see cref="TrailingSpaceProcessor"/>
    /// (order 90): <see cref="TrailingPunctuationProcessor"/> (order 85) runs before it, so the
    /// inserted period becomes the new "last non-whitespace character" the trailing-space stage
    /// then correctly appends its own space after.
    /// </summary>
    [Fact]
    public void Process_TrailingPunctuation_ComposesWithTrailingSpaceProcessor()
    {
        var perApp = new Dictionary<string, Soneto.Core.Dictionary.PerAppOverride>
        {
            ["wt.exe"] = Profile("wt.exe", autoCapitalize: false, trailingPunctuation: true),
        };
        var chain = new PostProcessorChain([new TrailingSpaceProcessor()], perApp);

        var result = chain.Process("hello world", "wt.exe");

        Assert.Equal("hello world. ", result.Text);
    }
}
