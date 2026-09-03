using Soneto.Core.Abstractions;
using Soneto.Core.Dictionary;
using Soneto.Core.PostProcessing;

namespace Soneto.Core.Tests.PostProcessing;

/// <summary>
/// Tests for <see cref="WhitespaceCleanerProcessor"/> (plan §1.7, order 30): punctuation
/// spacing, horizontal-whitespace collapse/trim, and -- explicitly flagged by the plan as the
/// thing that silently regresses -- newline preservation.
///
/// <para>
/// <b>Ordering note (Phase 2 item 6):</b> this processor now runs BEFORE, not after,
/// <see cref="SpokenCommandsExtensionProcessor"/> (order 60, superseding Phase 1's
/// <c>SpokenCommandsProcessor</c> which ran at order 20, before this processor's order 30). The
/// stray-whitespace-around-a-freshly-emitted-newline cleanup that used to be this processor's
/// job (running after spoken commands) is now <see cref="SpokenCommandsExtensionProcessor"/>'s
/// own responsibility, since nothing runs after it to do so -- see that class's doc comment for
/// the full investigation. The test below is kept to document/pin the new order's composed
/// behaviour, updated accordingly.
/// </para>
/// </summary>
public class WhitespaceCleanerProcessorTests
{
    private static PostProcessResult Run(string text, bool enabled = true) =>
        new WhitespaceCleanerProcessor(enabled).Process(new PostProcessResult(text, []));

    [Fact]
    public void SpaceBeforePunctuation_IsRemoved()
    {
        var result = Run("Hello , world !");
        Assert.Equal("Hello, world!", result.Text);
    }

    [Fact]
    public void MissingSpaceAfterPunctuation_IsAdded()
    {
        var result = Run("Hello,world.Next sentence;here:done");
        Assert.Equal("Hello, world. Next sentence; here: done", result.Text);
    }

    [Fact]
    public void MultipleSpaces_AreCollapsedToOne()
    {
        var result = Run("Too    many     spaces");
        Assert.Equal("Too many spaces", result.Text);
    }

    [Fact]
    public void Tabs_AreCollapsedLikeSpaces()
    {
        var result = Run("A\t\tB");
        Assert.Equal("A B", result.Text);
    }

    [Fact]
    public void LeadingAndTrailingHorizontalWhitespace_IsTrimmed()
    {
        var result = Run("   leading and trailing   ");
        Assert.Equal("leading and trailing", result.Text);
    }

    [Fact]
    public void SingleNewline_SurvivesIntact()
    {
        var result = Run("First line\nSecond line");
        Assert.Equal("First line\nSecond line", result.Text);
    }

    [Fact]
    public void DoubleNewline_SurvivesIntact()
    {
        var result = Run("First paragraph\n\nSecond paragraph");
        Assert.Equal("First paragraph\n\nSecond paragraph", result.Text);
    }

    [Fact]
    public void WhitespaceSeparatedNewlines_AreExposedThenCappedAtTwo()
    {
        // Regression test: newline runs separated by stray horizontal whitespace on
        // otherwise-blank lines (e.g. "\n \n \n") must still be capped at two once the
        // intervening spaces are trimmed away and the newlines become truly adjacent. This
        // requires the per-line horizontal-whitespace trim to run BEFORE the newline cap --
        // capping first would never see these as 3+ adjacent newlines and would leave 3
        // raw newlines in the output.
        Assert.Equal("A\n\nB", Run("A\n \n \nB").Text);
        Assert.Equal("\n\n", Run("\n \n \n").Text);
    }

    [Fact]
    public void ThreeOrMoreConsecutiveNewlines_AreCappedAtTwo()
    {
        Assert.Equal("A\n\nB", Run("A\n\n\nB").Text);
        Assert.Equal("A\n\nB", Run("A\n\n\n\n\nB").Text);
    }

    [Fact]
    public void SpacesAroundNewlines_AreCollapsedButNewlineSurvives()
    {
        var result = Run("First line   \n   Second line");
        Assert.Equal("First line\nSecond line", result.Text);
    }

    [Fact]
    public void Disabled_IsPassthrough()
    {
        var result = Run("  too   many spaces  ", enabled: false);
        Assert.Equal("  too   many spaces  ", result.Text);
    }

    [Fact]
    public void EmptyText_IsNoOp()
    {
        Assert.Equal(string.Empty, Run(string.Empty).Text);
    }

    // --- Composed with the new order-60 SpokenCommandsExtensionProcessor (cleaner now runs
    //     FIRST, commands second -- see the class doc comment above) ---

    [Theory]
    [InlineData("first sentence, new line, second sentence", "first sentence,\n, second sentence")]
    [InlineData("first paragraph, new paragraph, second paragraph", "first paragraph,\n\n, second paragraph")]
    public void WhitespaceCleanerThenSpokenCommands_NewlinesSurviveCorrectly(string input, string expected)
    {
        var afterCleaner = new WhitespaceCleanerProcessor().Process(new PostProcessResult(input, []));
        var afterCommands = new SpokenCommandsExtensionProcessor([]).Process(afterCleaner);

        Assert.Contains('\n', afterCommands.Text);
        Assert.Equal(expected, afterCommands.Text);
    }
}
