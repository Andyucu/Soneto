using Soneto.Core.Abstractions;
using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Tests for <see cref="FillerWordStripper"/> (Phase 2 work item 7, order 70): proves the
/// default EN/RO filler-word list is stripped case-insensitively, the full-token-boundary rule
/// protects real words like "album" that merely contain a filler word as a substring, the
/// processor's own gap-cleanup logic (needed because it runs AFTER
/// <see cref="Soneto.Core.PostProcessing.WhitespaceCleanerProcessor"/>, order 30) produces
/// clean output for every documented shape, multiple filler words in one transcript are all
/// removed, <see cref="AppliedRule"/> population/accumulation (including the "To is always
/// empty" convention), and the disabled/empty/no-match no-op paths.
/// </summary>
public class FillerWordStripperTests
{
    private static PostProcessResult Run(string text, bool enabled = true) =>
        new FillerWordStripper(enabled).Process(new PostProcessResult(text, []));

    // --- Basic stripping ---

    [Theory]
    [InlineData("I um think so", "I think so")]
    [InlineData("uh let's go", "let's go")]
    [InlineData("ăăă ce faci", "ce faci")]
    [InlineData("păi nu știu", "nu știu")]
    public void FreeStandingFillerWord_IsStripped(string input, string expected)
    {
        Assert.Equal(expected, Run(input).Text);
    }

    [Theory]
    [InlineData("I Um think so", "I think so")]
    [InlineData("I UM think so", "I think so")]
    public void Matching_IsCaseInsensitive(string input, string expected)
    {
        Assert.Equal(expected, Run(input).Text);
    }

    // --- Full-token-boundary safety: the plan's own named adversarial example ---

    [Fact]
    public void SubstringInsideRealWord_IsNotStripped()
    {
        var result = Run("I bought an album yesterday");
        Assert.Equal("I bought an album yesterday", result.Text);
        Assert.Empty(result.Applied);
    }

    [Theory]
    [InlineData("humble opinion")] // contains "um" but not free-standing
    [InlineData("aluminum foil")] // contains "um" at the end, but not free-standing
    public void OtherEmbeddedSubstrings_AreNotStripped(string input)
    {
        Assert.Equal(input, Run(input).Text);
    }

    // --- Whitespace/punctuation cleanup after removal ---

    [Fact]
    public void MidSentence_SurroundedBySpaces_CollapsesToSingleSpace()
    {
        var result = Run("I um think");
        Assert.Equal("I think", result.Text);
    }

    [Fact]
    public void MidSentence_SetOffByCommas_CollapsesCleanly()
    {
        var result = Run("well, um, I think");
        Assert.Equal("well, I think", result.Text);
    }

    [Fact]
    public void StartOfTranscript_SetOffByComma_LeavesNoStrayLeadingPunctuation()
    {
        var result = Run("um, I think so");
        Assert.Equal("I think so", result.Text);
    }

    [Fact]
    public void EndOfTranscript_SetOffByComma_LeavesNoStrayTrailingPunctuation()
    {
        var result = Run("I think so, um");
        Assert.Equal("I think so", result.Text);
    }

    [Theory]
    [InlineData("I think, um.", "I think.")]
    [InlineData("I think, um!", "I think!")]
    [InlineData("I think, um?", "I think?")]
    public void FillerWordBeforeTerminalPunctuation_CollapsesDanglingComma(string input, string expected)
    {
        // Realistic Soneto-specific shape: trailing off with a filler word right as the
        // push-to-talk key is released, with the ASR appending a terminal mark at
        // end-of-utterance.
        Assert.Equal(expected, Run(input).Text);
    }

    [Fact]
    public void StartOfTranscript_PlainSpace_LeavesNoStrayLeadingWhitespace()
    {
        var result = Run("um I think so");
        Assert.Equal("I think so", result.Text);
    }

    [Fact]
    public void EndOfTranscript_PlainSpace_LeavesNoStrayTrailingWhitespace()
    {
        var result = Run("I think so um");
        Assert.Equal("I think so", result.Text);
    }

    // --- Multiple filler words in one transcript ---

    [Fact]
    public void MultipleFillerWords_AllRemoved_WithCleanSpacingThroughout()
    {
        var result = Run("well, um, uh, I think");
        Assert.Equal("well, I think", result.Text);
    }

    [Fact]
    public void MultipleFillerWords_PlainSpaceSeparated_CollapseToSingleSpaces()
    {
        var result = Run("I um uh think");
        Assert.Equal("I think", result.Text);
    }

    // --- AppliedRule population and accumulation ---

    [Fact]
    public void RemovedFillerWord_PopulatesAppliedRule_WithEmptyTo()
    {
        var result = Run("I um think");

        var rule = Assert.Single(result.Applied);
        Assert.Equal("FillerWordStripper", rule.Processor);
        Assert.Equal("um", rule.From);
        Assert.Equal(string.Empty, rule.To);
    }

    [Fact]
    public void RemovedFillerWord_PreservesOriginalCasingInFrom()
    {
        var result = Run("I Um think");

        var rule = Assert.Single(result.Applied);
        Assert.Equal("Um", rule.From);
        Assert.Equal(string.Empty, rule.To);
    }

    [Fact]
    public void MultipleFillerWords_AppliedRuleAccumulatesOnePerOccurrence()
    {
        var result = Run("well, um, uh, I think");

        Assert.Equal(2, result.Applied.Count);
        Assert.Equal("um", result.Applied[0].From);
        Assert.Equal("uh", result.Applied[1].From);
        Assert.All(result.Applied, r => Assert.Equal(string.Empty, r.To));
    }

    [Fact]
    public void AppliedRules_AccumulateOnTopOfPriorChainStages()
    {
        var priorRule = new AppliedRule("SomeEarlierStage", "rule-1", "foo", "bar");
        var input = new PostProcessResult("I um think", [priorRule]);

        var result = new FillerWordStripper(enabled: true).Process(input);

        Assert.Equal(2, result.Applied.Count);
        Assert.Same(priorRule, result.Applied[0]);
        Assert.Equal("um", result.Applied[1].From);
    }

    // --- No-op paths ---

    [Fact]
    public void Disabled_IsNoOpPassthrough()
    {
        var input = new PostProcessResult("I um think", []);
        var result = new FillerWordStripper(enabled: false).Process(input);

        Assert.Same(input, result);
    }

    [Fact]
    public void EmptyInput_IsNoOpPassthrough()
    {
        var input = new PostProcessResult("", []);
        var result = new FillerWordStripper(enabled: true).Process(input);

        Assert.Same(input, result);
    }

    [Fact]
    public void NoFillerWordsPresent_TextUnchanged()
    {
        var result = Run("this transcript has nothing to strip");
        Assert.Equal("this transcript has nothing to strip", result.Text);
        Assert.Empty(result.Applied);
    }

    // --- Custom filler-word list constructor ---

    [Fact]
    public void CustomFillerWordList_IsUsedInsteadOfDefaults()
    {
        var processor = new FillerWordStripper(["basically"], enabled: true);

        var result = processor.Process(new PostProcessResult("this is, basically, correct", []));
        Assert.Equal("this is, correct", result.Text);

        // "um", a default filler word, is NOT stripped when a custom list is supplied --
        // the custom list replaces the defaults rather than merging with them.
        var unaffected = processor.Process(new PostProcessResult("I um think", []));
        Assert.Equal("I um think", unaffected.Text);
    }

    [Fact]
    public void CustomFillerWordList_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => new FillerWordStripper([]));
    }

    [Fact]
    public void CustomFillerWordList_WhitespaceOnlyEntry_Throws()
    {
        Assert.Throws<ArgumentException>(() => new FillerWordStripper(["  "]));
    }
}
