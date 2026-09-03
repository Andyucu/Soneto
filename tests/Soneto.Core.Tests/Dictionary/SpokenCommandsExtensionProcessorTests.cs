using Soneto.Core.Abstractions;
using Soneto.Core.Dictionary;
using Soneto.Core.PostProcessing;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Tests for <see cref="SpokenCommandsExtensionProcessor"/> (Phase 2 work item 6, order 60):
/// proves the migration of Phase 1's retired <c>SpokenCommandsProcessor</c> preserved its
/// punctuation/utterance-boundary matching fix exactly, that the new class is genuinely
/// user-extensible, its built-in-vs-user-entry collision policy, and -- the single most
/// important test in this item -- that a spoken command's freshly-emitted <c>\n</c>/<c>\n\n</c>
/// survives correctly through the real, fully-composed <see cref="PostProcessorChain"/> given
/// this processor's new order-60 position (running AFTER <see cref="WhitespaceCleanerProcessor"/>,
/// order 30, instead of before it as Phase 1's retired processor did).
/// </summary>
public class SpokenCommandsExtensionProcessorTests
{
    private static PostProcessResult Run(
        string text, IEnumerable<DictionaryEntry>? entries = null, bool enabled = true) =>
        new SpokenCommandsExtensionProcessor(entries ?? [], enabled).Process(new PostProcessResult(text, []));

    // --- Migrated built-ins: ported directly from item 8's SpokenCommandsProcessorTests, proving
    //     the migration preserved the fix, not just that the new class compiles. Expected values
    //     are unchanged from item 8's originals EXCEPT where this processor's own local
    //     whitespace cleanup (see its class doc comment's WhitespaceCleanerProcessor-ordering
    //     paragraph) now additionally strips a stray space that used to precede a freshly-emitted
    //     newline -- that is a deliberate, documented improvement, not a missed migration. ---

    [Theory]
    [InlineData("new line", "\n")]
    [InlineData("new paragraph", "\n\n")]
    [InlineData("linie nouă", "\n")]
    [InlineData("paragraf nou", "\n\n")]
    public void BuiltInCommandPhrase_MapsToControlCharacter(string phrase, string expected)
    {
        var result = Run(phrase);
        Assert.Equal(expected, result.Text);
    }

    [Theory]
    [InlineData("New Line", "\n")]
    [InlineData("NEW PARAGRAPH", "\n\n")]
    [InlineData("Linie Nouă", "\n")]
    [InlineData("PARAGRAF NOU", "\n\n")]
    public void CommandMatching_IsCaseInsensitive(string phrase, string expected)
    {
        var result = Run(phrase);
        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public void CommandSetOffByPunctuationOnBothSides_StillTriggers()
    {
        // Comma before ("okay, ") and comma after (",") give the phrase clear clause
        // boundaries, so the model's own punctuation signal marks this as an intentional
        // command even though it's mid-utterance, not the whole utterance. Note: no space
        // survives before "\n\n" here (unlike item 8's original processor run standalone) --
        // this processor's own local whitespace cleanup strips it, since nothing downstream of
        // order 60 does that anymore. See the full end-to-end chain test below for why.
        var result = Run("okay, new paragraph, next item");
        Assert.Equal("okay,\n\n, next item", result.Text);
    }

    [Fact]
    public void TabCharacterBeforeCommandPhrase_IsStrippedLikeASpace()
    {
        // TrailingHorizontalWhitespaceBeforeNewline's [^\S\n]+ class covers tabs, not just
        // spaces -- same "horizontal whitespace" definition WhitespaceCleanerProcessor itself
        // uses. Exercised directly (bypassing WhitespaceCleanerProcessor, which would otherwise
        // collapse the tab to a space before this processor ever saw it) to pin that parity
        // explicitly rather than relying on reading the regex.
        var result = Run("okay,\t new paragraph, next item");
        Assert.Equal("okay,\n\n, next item", result.Text);
    }

    [Fact]
    public void CommandAtStartOfUtterance_FollowedByPunctuation_StillTriggers()
    {
        var result = Run("new line, then continue");
        Assert.Equal("\n, then continue", result.Text);
    }

    [Fact]
    public void CommandAtEndOfUtterance_PrecededByPunctuation_StillTriggers()
    {
        var result = Run("first item, new paragraph");
        Assert.Equal("first item,\n\n", result.Text);
    }

    [Fact]
    public void PhraseAsSubstringInsideALargerWord_DoesNotTrigger()
    {
        // "renewliner" contains "new line"'s letters but not as a free-standing word -- must
        // not be treated as a command.
        var result = Run("Please renewliner your subscription.");
        Assert.Equal("Please renewliner your subscription.", result.Text);
    }

    [Theory]
    [InlineData("my new line of business")]
    [InlineData("he opened a new line of credit")]
    [InlineData("put this on a new line item")]
    public void PhraseEmbeddedInProseWithNoPunctuationBoundary_DoesNotTrigger(string text)
    {
        // Critical regression case (plan §1.13, item 8's own fix): these idiomatic sentences
        // contain the exact trigger words but have no punctuation break setting them off as a
        // clause, so they must pass through completely unmangled. This is the single test that
        // proves this migration didn't regress item 8's original bug fix.
        var result = Run(text);
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public void Disabled_IsPassthrough()
    {
        var result = Run("new line", enabled: false);
        Assert.Equal("new line", result.Text);
    }

    [Fact]
    public void PlainTextWithNoCommandWords_IsUnchanged()
    {
        const string text = "The quick brown fox jumps over the lazy dog.";
        var result = Run(text);
        Assert.Equal(text, result.Text);
    }

    // --- New user-defined commands ---

    [Fact]
    public void UserDefinedCommand_NotInBuiltInTable_Fires()
    {
        // Emits a non-whitespace literal ("<TAB>") rather than an actual \n/\t control
        // character, so this test stays focused on "does a brand-new user command fire" without
        // entangling it with this processor's \n-specific local whitespace cleanup (covered by
        // its own dedicated tests below).
        var entries = new DictionaryEntry[]
        {
            new SpokenCommand { Id = "user.tab-stop", Phrase = "tab stop", Emits = "<TAB>" },
        };

        var result = Run("indent here, tab stop, then text", entries);
        Assert.Equal("indent here, <TAB>, then text", result.Text);
    }

    [Fact]
    public void UserDefinedCommand_RespectsPunctuationBoundaryRuleToo()
    {
        var entries = new DictionaryEntry[]
        {
            new SpokenCommand { Id = "user.tab-stop", Phrase = "tab stop", Emits = "<TAB>" },
        };

        // No punctuation boundary around "tab stop" here -- must not fire, same discipline as
        // the built-ins.
        var result = Run("please tab stop over there", entries);
        Assert.Equal("please tab stop over there", result.Text);
    }

    // --- Collision policy: a user-provided entry whose phrase matches a built-in wins ---

    [Fact]
    public void UserDefinedEntry_OverridesBuiltInPhrase()
    {
        var entries = new DictionaryEntry[]
        {
            new SpokenCommand { Id = "user.redefined-new-line", Phrase = "new line", Emits = "<BR>" },
        };

        var result = Run("first, new line, second", entries);
        Assert.Equal("first, <BR>, second", result.Text);
    }

    [Fact]
    public void UserDefinedEntry_CollisionIsCaseInsensitive()
    {
        var entries = new DictionaryEntry[]
        {
            new SpokenCommand { Id = "user.redefined", Phrase = "NEW LINE", Emits = "<BR>" },
        };

        var result = Run("first, new line, second", entries);
        Assert.Equal("first, <BR>, second", result.Text);
    }

    [Fact]
    public void DisabledUserEntry_CannotSuppressBuiltIn()
    {
        // A disabled entry never fires, and (documented, known behaviour) cannot suppress a
        // same-phrase built-in either -- disabled entries are dropped entirely before merging.
        var entries = new DictionaryEntry[]
        {
            new SpokenCommand { Id = "user.disabled", Phrase = "new line", Emits = "<BR>", Enabled = false },
        };

        var result = Run("first, new line, second", entries);
        Assert.Equal("first,\n, second", result.Text);
    }

    // --- Disabled entries never fire; other entry types ignored ---

    [Fact]
    public void DisabledSpokenCommandEntry_NeverFires()
    {
        var entries = new DictionaryEntry[]
        {
            new SpokenCommand { Id = "user.disabled", Phrase = "tab stop", Emits = "\t", Enabled = false },
        };

        var result = Run("please, tab stop, now", entries);
        Assert.Equal("please, tab stop, now", result.Text);
    }

    [Fact]
    public void OtherEntryTypes_AreIgnored()
    {
        var entries = new DictionaryEntry[]
        {
            new CorrectionPair { Id = "cp1", From = "new line", To = "SHOULD NOT APPLY" },
            new VocabularyTerm { Id = "vt1", Term = "webMethods" },
            new RegexRule { Id = "rr1", Pattern = @"\bnew line\b", Replacement = "SHOULD NOT APPLY EITHER" },
        };

        var result = Run("first, new line, second", entries);
        // The built-in "new line" -> "\n" still applies (none of the above are SpokenCommand
        // entries, so none of them can override it), and none of the ignored entries fire.
        Assert.Equal("first,\n, second", result.Text);
    }

    // --- AppliedRule population and accumulation ---

    [Fact]
    public void AppliedRule_IsPopulatedWithMatchedTextAndEmittedControlCharacters()
    {
        var result = Run("okay, new paragraph, next");

        var rule = Assert.Single(result.Applied);
        Assert.Equal("SpokenCommands", rule.Processor);
        Assert.Equal("builtin.spoken-command.en.new-paragraph", rule.Rule);
        Assert.Equal("new paragraph", rule.From);
        Assert.Equal("\n\n", rule.To);
    }

    [Fact]
    public void AppliedRule_AccumulatesOnTopOfPriorChainStageEntries()
    {
        var prior = new AppliedRule("UnicodeNormalizer", "some-rule", "ş", "ș");
        var input = new PostProcessResult("okay, new line, next", [prior]);

        var result = new SpokenCommandsExtensionProcessor([]).Process(input);

        Assert.Equal(2, result.Applied.Count);
        Assert.Same(prior, result.Applied[0]);
        Assert.Equal("SpokenCommands", result.Applied[1].Processor);
    }

    [Fact]
    public void NoMatch_LeavesAppliedRuleListUntouched()
    {
        var prior = new AppliedRule("UnicodeNormalizer", "some-rule", "ş", "ș");
        var input = new PostProcessResult("plain text, no commands here", [prior]);

        var result = new SpokenCommandsExtensionProcessor([]).Process(input);

        Assert.Same(input, result);
    }

    // --- Construction-time validation ---

    [Fact]
    public void EmptyOrWhitespacePhrase_ThrowsAtConstruction()
    {
        var entries = new DictionaryEntry[]
        {
            new SpokenCommand { Id = "bad", Phrase = "   ", Emits = "\n" },
        };

        var ex = Assert.Throws<ArgumentException>(() => new SpokenCommandsExtensionProcessor(entries));
        Assert.Contains("bad", ex.Message);
    }

    [Fact]
    public void TwoEnabledUserEntries_CollidingOnSamePhrase_ThrowsAtConstruction()
    {
        // Distinct from a user entry overriding a built-in (that's a legitimate, silent
        // override) -- two USER entries sharing a phrase is much more likely to be an
        // accidental typo/duplicate in a hand-edited dictionary.json, so it fails loudly.
        var entries = new DictionaryEntry[]
        {
            new SpokenCommand { Id = "user.first", Phrase = "tab stop", Emits = "<A>" },
            new SpokenCommand { Id = "user.second", Phrase = "TAB STOP", Emits = "<B>" }, // case-insensitive collision
        };

        var ex = Assert.Throws<ArgumentException>(() => new SpokenCommandsExtensionProcessor(entries));
        Assert.Contains("user.first", ex.Message);
        Assert.Contains("user.second", ex.Message);
    }

    [Fact]
    public void DisabledDuplicate_DoesNotTriggerUserVsUserCollisionCheck()
    {
        // Only enabled-vs-enabled should throw; a disabled entry is filtered out before the
        // collision check ever runs, same as everywhere else in this class.
        var entries = new DictionaryEntry[]
        {
            new SpokenCommand { Id = "user.first", Phrase = "tab stop", Emits = "<A>" },
            new SpokenCommand { Id = "user.second", Phrase = "tab stop", Emits = "<B>", Enabled = false },
        };

        var result = Run("please, tab stop, now", entries);
        Assert.Equal("please, <A>, now", result.Text);
    }

    // --- The whitespace-cleaner-ordering interaction: the single most important test in this
    //     item, run through the ACTUAL PostProcessorChain, all current processors, real order. ---

    [Fact]
    public void FullChain_SpokenCommandNewline_SurvivesWhitespaceCleaningGivenOrder60Position()
    {
        var chain = new PostProcessorChain(
        [
            new UnicodeNormalizerProcessor(),
            new WhitespaceCleanerProcessor(),
            new SpokenCommandsExtensionProcessor([]),
            new TrailingSpaceProcessor(),
        ]);

        // Messy spacing BEFORE the command phrase, exercised through the real chain: order 30
        // (WhitespaceCleaner) runs first and normalizes it, THEN order 60 (this processor) fires
        // the command and cleans up its own freshly-emitted newline's surrounding whitespace --
        // proving the two stages compose correctly even though their relative order flipped from
        // Phase 1.
        var result = chain.Process("okay,   new paragraph,   next item");

        Assert.Contains("\n\n", result.Text);
        Assert.Equal("okay,\n\n, next item ", result.Text);
    }

    [Fact]
    public void FullChain_PreExistingNewlinesFromTypedText_StillPreservedAndCapped()
    {
        // A newline that was already literally present in the input (not emitted by a spoken
        // command) is still handled correctly by WhitespaceCleaner at order 30, unaffected by
        // this processor running later at order 60.
        var chain = new PostProcessorChain(
        [
            new UnicodeNormalizerProcessor(),
            new WhitespaceCleanerProcessor(),
            new SpokenCommandsExtensionProcessor([]),
            new TrailingSpaceProcessor(),
        ]);

        var result = chain.Process("first line\n\n\n\nsecond line");
        Assert.Equal("first line\n\nsecond line ", result.Text);
    }
}
