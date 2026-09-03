using Soneto.Core.Abstractions;
using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Independent verification tests for <see cref="FillerWordStripper"/> (Phase 2 work item 7),
/// written by a separate test-runner pass against the implementer's own
/// <c>FillerWordStripperTests.cs</c> -- deliberately uses fresh adversarial examples (not the
/// implementer's own "album"/"well, um, I think" cases) to independently confirm full-token
/// boundary safety, asymmetric-punctuation/end-of-sentence cleanup behavior (including the one
/// gap -- an asymmetric single comma on only one side of a removed filler word -- the cleanup
/// pass deliberately does NOT handle, per its own doc comment; the previously-observed
/// comma-before-terminal-punctuation gap was subsequently fixed per code review), the mixed-case
/// AppliedRule.From/Rule-id conventions, multi-filler-word end-to-end correctness, the
/// custom-list "replaces not merges" behavior, and no-op passthrough identity preservation.
/// </summary>
public class FillerWordStripperVerificationTests
{
    private static PostProcessResult Run(string text, bool enabled = true) =>
        new FillerWordStripper(enabled).Process(new PostProcessResult(text, []));

    // --- Point 3: full-token-boundary safety, fresh substring-collision words ---

    [Theory]
    [InlineData("plumber called about the sink")] // contains "um" mid-word, not free-standing
    [InlineData("Wuhan is a large city")] // contains "uh" mid-word (real proper noun), not free-standing
    [InlineData("she was clearly the leader of the group")] // no filler substrings at all -- control
    public void FreshSubstringCollisionWords_AreNotStripped(string input)
    {
        var result = Run(input);
        Assert.Equal(input, result.Text);
        Assert.Empty(result.Applied);
    }

    [Fact]
    public void RomanianWord_ContainingPaiAsSubstring_IsNotStripped()
    {
        // "împăiat" (Romanian: "stuffed/taxidermied") contains "păi" as a substring
        // (î-m-p-ă-i-a-t), bounded on both sides by letters -- must not be touched.
        var result = Run("animalul era împăiat de mult timp");
        Assert.Equal("animalul era împăiat de mult timp", result.Text);
        Assert.Empty(result.Applied);
    }

    // --- Point 4: asymmetric punctuation / end-of-sentence punctuation / all-filler input ---

    [Fact]
    public void FillerWord_CommaOnlyOnLeftSide_CollapsesExtraSpaceCleanly()
    {
        // "well, um I think" -- comma+space on the LEFT of "um", plain space on the RIGHT only.
        var result = Run("well, um I think");
        Assert.Equal("well, I think", result.Text);
    }

    [Fact]
    public void FillerWord_CommaOnlyOnRightSide_LeavesStrayMidSentenceCommaSpacing()
    {
        // "I um, think" -- plain space on the LEFT of "um", comma immediately on the RIGHT.
        // The cleanup pass only special-cases comma RUNS (2+) and LEADING/TRAILING single commas
        // at the string's own boundaries (per the class doc comment) -- a single stray
        // " , " left mid-string is NOT specially collapsed. Documenting the actual (gap) behavior.
        var result = Run("I um, think");
        Assert.Equal("I , think", result.Text);
    }

    [Fact]
    public void TwoAdjacentFillerWords_SeparatedOnlyByComma_CollapseToCleanText()
    {
        var result = Run("um, uh, I think");
        Assert.Equal("I think", result.Text);
    }

    [Fact]
    public void FillerWord_ImmediatelyFollowedByEndOfSentencePeriod_CollapsesDanglingComma()
    {
        // "I think, um." -- comma+space precede "um", a period follows immediately with no
        // space. Per code review's should-fix, this specific shape (trailing off with a filler
        // word right as push-to-talk is released, with the ASR appending a terminal mark) is
        // realistic enough for this app that a small, narrowly-scoped cleanup pass now handles
        // it explicitly -- CommaBeforeTerminalPunctuation collapses the dangling ", " directly
        // before a terminal `.`/`!`/`?` down to just the terminal mark. This is now a HANDLED
        // case, not an out-of-scope one (see the class doc comment's updated scope description).
        var result = Run("I think, um.");
        Assert.Equal("I think.", result.Text);
    }

    [Fact]
    public void TranscriptIsEntirelyOneFillerWord_ProducesCleanEmptyString()
    {
        var result = Run("um");
        Assert.Equal(string.Empty, result.Text);
        var rule = Assert.Single(result.Applied);
        Assert.Equal("um", rule.From);
        Assert.Equal(string.Empty, rule.To);
    }

    // --- Point 5 / 5b: casing preservation in From, canonical lowercase Rule id ---

    [Fact]
    public void MixedCaseFillerWord_PreservesExactOriginalCasingInFrom()
    {
        var result = Run("well Uh I guess");
        var rule = Assert.Single(result.Applied);
        Assert.Equal("Uh", rule.From);
    }

    [Fact]
    public void AllCapsFillerWord_ProducesLowercaseCanonicalRuleId()
    {
        var result = Run("well UH I guess");
        var rule = Assert.Single(result.Applied);
        Assert.Equal("UH", rule.From);
        Assert.Equal("filler.uh", rule.Rule);
    }

    [Fact]
    public void SameFillerWord_DifferentCasingOccurrences_ShareTheSameRuleId()
    {
        var result = Run("um, then UM again");
        Assert.Equal(2, result.Applied.Count);
        Assert.Equal("filler.um", result.Applied[0].Rule);
        Assert.Equal("filler.um", result.Applied[1].Rule);
        Assert.Equal("um", result.Applied[0].From);
        Assert.Equal("UM", result.Applied[1].From);
    }

    // --- Point 6: 3+ distinct filler words at start/middle/end, full end-to-end correctness ---

    [Fact]
    public void ThreeDistinctFillerWords_AtStartMiddleEnd_AllRemovedWithCleanSpacing()
    {
        var result = Run("uh, first we um discuss then păi decide");
        Assert.Equal("first we discuss then decide", result.Text);
        Assert.Equal(3, result.Applied.Count);
        Assert.Equal("uh", result.Applied[0].From);
        Assert.Equal("um", result.Applied[1].From);
        Assert.Equal("păi", result.Applied[2].From);
        Assert.All(result.Applied, r => Assert.Equal(string.Empty, r.To));
    }

    // --- Point 7: custom filler list replaces (not merges) the defaults ---

    [Fact]
    public void CustomFillerList_WithoutUm_LeavesUmUnstrippedButStripsCustomWords()
    {
        var processor = new FillerWordStripper(["like", "actually"], enabled: true);

        var stripped = processor.Process(new PostProcessResult("it's like actually fine", []));
        Assert.Equal("it's fine", stripped.Text);

        var unaffected = processor.Process(new PostProcessResult("um, it's fine", []));
        Assert.Equal("um, it's fine", unaffected.Text);
        Assert.Empty(unaffected.Applied);
    }

    // --- Point 8: no-op passthrough preserves prior chain AppliedRules by reference ---

    [Fact]
    public void NoFillerPresent_WithPriorChainAppliedRules_PassesThroughSameInstanceUnchanged()
    {
        var priorRule = new AppliedRule("SomeEarlierStage", "rule-9", "foo", "bar");
        var input = new PostProcessResult("this transcript has nothing to strip", [priorRule]);

        var result = new FillerWordStripper(enabled: true).Process(input);

        Assert.Same(input, result);
        Assert.Same(priorRule, Assert.Single(result.Applied));
    }
}
