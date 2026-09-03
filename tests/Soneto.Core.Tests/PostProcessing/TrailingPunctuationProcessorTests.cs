using Soneto.Core.Abstractions;
using Soneto.Core.PostProcessing;

namespace Soneto.Core.Tests.PostProcessing;

/// <summary>
/// Phase 4 item 3 (§4.4): unit tests for <see cref="TrailingPunctuationProcessor"/> -- the first
/// real consumer of the dictionary schema's <c>PerAppOverride.TrailingPunctuation</c> flag.
/// </summary>
public class TrailingPunctuationProcessorTests
{
    private static readonly TrailingPunctuationProcessor Processor = new();

    private static string Run(string text) =>
        Processor.Process(new PostProcessResult(text, Array.Empty<AppliedRule>())).Text;

    private static PostProcessResult RunFull(string text) =>
        Processor.Process(new PostProcessResult(text, Array.Empty<AppliedRule>()));

    [Fact]
    public void AppendsPeriod_WhenTextHasNoTerminalPunctuation()
    {
        Assert.Equal("hello world.", Run("hello world"));
    }

    [Theory]
    [InlineData("hello world.")]
    [InlineData("hello world!")]
    [InlineData("hello world?")]
    [InlineData("hello world;")]
    [InlineData("hello world:")]
    [InlineData("hello world,")]
    public void LeavesAlreadyTerminatedText_Unchanged(string text)
    {
        Assert.Equal(text, Run(text));
    }

    [Fact]
    public void PreservesExistingTrailingWhitespace_InsertsBeforeIt()
    {
        Assert.Equal("hello world. ", Run("hello world "));
    }

    [Fact]
    public void EmptyText_IsNoOp()
    {
        Assert.Equal("", Run(""));
    }

    [Fact]
    public void WhitespaceOnlyText_IsNoOp()
    {
        Assert.Equal("   ", Run("   "));
    }

    // ── Review fix (Phase 4 item 3 code review): AppliedRule recording ──

    [Fact]
    public void GenuineInsertion_RecordsAppliedRule()
    {
        var result = RunFull("hello world");

        var rule = Assert.Single(result.Applied);
        Assert.Equal("TrailingPunctuation", rule.Processor);
        Assert.Equal("", rule.From);
        Assert.Equal(".", rule.To);
    }

    [Fact]
    public void AlreadyTerminated_RecordsNoAppliedRule()
    {
        var result = RunFull("hello world.");

        Assert.Empty(result.Applied);
    }

    [Fact]
    public void AppliedRule_IsAppendedAfterAnyExistingOnes()
    {
        var existing = new AppliedRule("SomeEarlierStage", "rule1", "x", "y");
        var input = new PostProcessResult("hello world", [existing]);

        var result = Processor.Process(input);

        Assert.Equal(2, result.Applied.Count);
        Assert.Same(existing, result.Applied[0]);
        Assert.Equal("TrailingPunctuation", result.Applied[1].Processor);
    }
}
