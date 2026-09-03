using Soneto.Core.Abstractions;
using Soneto.Core.PostProcessing;

namespace Soneto.Core.Tests.PostProcessing;

/// <summary>
/// Phase 4 item 3 (§4.4): unit tests for <see cref="AutoCapitalizeProcessor"/> -- the first real
/// consumer of the dictionary schema's <c>PerAppOverride.AutoCapitalize</c> flag. Deliberately
/// minimal coverage matching the processor's own deliberately minimal scope (see its class doc
/// comment) -- not a claim of grammar-aware completeness.
/// </summary>
public class AutoCapitalizeProcessorTests
{
    private static readonly AutoCapitalizeProcessor Processor = new();

    private static string Run(string text) =>
        Processor.Process(new PostProcessResult(text, Array.Empty<AppliedRule>())).Text;

    private static PostProcessResult RunFull(string text) =>
        Processor.Process(new PostProcessResult(text, Array.Empty<AppliedRule>()));

    [Fact]
    public void CapitalizesFirstLetterOfText()
    {
        Assert.Equal("Hello world", Run("hello world"));
    }

    [Fact]
    public void CapitalizesAfterSentenceTerminators()
    {
        Assert.Equal("Hello. World! How are you? Fine.", Run("hello. world! how are you? fine."));
    }

    [Fact]
    public void LeavesAlreadyCapitalizedTextUnchanged()
    {
        Assert.Equal("Hello world.", Run("Hello world."));
    }

    [Fact]
    public void HandlesRomanianDiacritics()
    {
        Assert.Equal("Știu că e bine.", Run("știu că e bine."));
    }

    [Fact]
    public void EmptyText_IsNoOp()
    {
        Assert.Equal("", Run(""));
    }

    [Fact]
    public void DoesNotTouchNonLetterFirstCharacters()
    {
        Assert.Equal("123 hello", Run("123 hello"));
    }

    // ── Review fix (Phase 4 item 3 code review): AppliedRule recording ──

    [Fact]
    public void GenuineChange_RecordsAppliedRule()
    {
        var result = RunFull("hello world");

        var rule = Assert.Single(result.Applied);
        Assert.Equal("AutoCapitalize", rule.Processor);
        Assert.Equal("h", rule.From);
        Assert.Equal("H", rule.To);
    }

    [Fact]
    public void MultipleGenuineChanges_RecordsOneAppliedRulePerChange()
    {
        var result = RunFull("hello. world!");

        Assert.Equal(2, result.Applied.Count);
        Assert.Equal("h", result.Applied[0].From);
        Assert.Equal("H", result.Applied[0].To);
        Assert.Equal("w", result.Applied[1].From);
        Assert.Equal("W", result.Applied[1].To);
    }

    [Fact]
    public void NoChange_RecordsNoAppliedRule()
    {
        var result = RunFull("Hello world.");

        Assert.Empty(result.Applied);
    }

    [Fact]
    public void AppliedRules_AreAppendedAfterAnyExistingOnes()
    {
        var existing = new AppliedRule("SomeEarlierStage", "rule1", "x", "y");
        var input = new PostProcessResult("hello world", [existing]);

        var result = Processor.Process(input);

        Assert.Equal(2, result.Applied.Count);
        Assert.Same(existing, result.Applied[0]);
        Assert.Equal("AutoCapitalize", result.Applied[1].Processor);
    }
}
