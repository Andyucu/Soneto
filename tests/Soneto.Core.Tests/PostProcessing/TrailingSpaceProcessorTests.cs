using Soneto.Core.Abstractions;
using Soneto.Core.PostProcessing;

namespace Soneto.Core.Tests.PostProcessing;

/// <summary>
/// Tests for <see cref="TrailingSpaceProcessor"/> (plan §1.7, order 90 -- renumbered from 40 in
/// Phase 2 item 0 to reserve 40-70 for the dictionary engine, per
/// <c>Docs/soneto-implementation-plan-phase2.md</c>).
/// </summary>
public class TrailingSpaceProcessorTests
{
    private static PostProcessResult Run(string text, bool enabled = true) =>
        new TrailingSpaceProcessor(enabled).Process(new PostProcessResult(text, []));

    [Fact]
    public void TextEndingInWordCharacter_GetsExactlyOneTrailingSpaceAppended()
    {
        var result = Run("hello world");
        Assert.Equal("hello world ", result.Text);
    }

    [Fact]
    public void TextAlreadyEndingInSpace_IsNoOp()
    {
        var result = Run("hello world ");
        Assert.Equal("hello world ", result.Text);
    }

    [Fact]
    public void TextEndingInNewline_IsNoOp()
    {
        // Appending a space after a trailing paragraph break would corrupt it.
        var result = Run("hello world\n\n");
        Assert.Equal("hello world\n\n", result.Text);
    }

    [Fact]
    public void TextEndingInPunctuation_StillGetsTrailingSpace()
    {
        var result = Run("hello world.");
        Assert.Equal("hello world. ", result.Text);
    }

    [Fact]
    public void EmptyText_IsNoOp()
    {
        var result = Run(string.Empty);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void Disabled_IsPassthrough()
    {
        var result = Run("hello world", enabled: false);
        Assert.Equal("hello world", result.Text);
    }
}
