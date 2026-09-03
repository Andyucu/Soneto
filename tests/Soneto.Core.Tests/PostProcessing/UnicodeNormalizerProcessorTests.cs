using System.Text;
using Soneto.Core.Abstractions;
using Soneto.Core.PostProcessing;

namespace Soneto.Core.Tests.PostProcessing;

/// <summary>
/// Tests for <see cref="UnicodeNormalizerProcessor"/> (plan §1.7, order 10, always on):
/// cedilla-to-comma-below mapping (asserted by codepoint, not by eyeballing glyphs, matching
/// this project's convention for diacritics), NFC idempotence, and no mangling of plain EN
/// text.
/// </summary>
public class UnicodeNormalizerProcessorTests
{
    private static PostProcessResult Run(string text) =>
        new UnicodeNormalizerProcessor().Process(new PostProcessResult(text, []));

    [Fact]
    public void CedillaS_MapsToCommaBelowS_ByCodepoint()
    {
        var result = Run("ş"); // ş (cedilla)
        Assert.Equal(0x0219, result.Text[0]); // ș (comma-below)
    }

    [Fact]
    public void CedillaT_MapsToCommaBelowT_ByCodepoint()
    {
        var result = Run("ţ"); // ţ (cedilla)
        Assert.Equal(0x021B, result.Text[0]); // ț (comma-below)
    }

    [Fact]
    public void CedillaChars_InWord_AreMapped()
    {
        // "ştiu" (I know) using cedilla forms -> should come out with comma-below forms.
        var result = Run("ştiu ţara");
        Assert.Equal("știu țara", result.Text);
    }

    [Fact]
    public void RunningTwice_IsIdempotent()
    {
        var once = Run("ştiu ţara café");
        var twice = new UnicodeNormalizerProcessor().Process(once);
        Assert.Equal(once.Text, twice.Text);
    }

    [Fact]
    public void NfcNormalization_IsIdempotent_OnDecomposedInput()
    {
        // "café" with a combining acute accent (NFD) should normalise to the precomposed
        // NFC form, and running it again should be a no-op.
        var decomposed = "café";
        var once = Run(decomposed);
        Assert.Equal("café", once.Text);

        var twice = new UnicodeNormalizerProcessor().Process(once);
        Assert.Equal(once.Text, twice.Text);
    }

    [Fact]
    public void PlainEnglishText_IsUnchanged()
    {
        const string text = "The quick brown fox jumps over the lazy dog.";
        var result = Run(text);
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public void EmptyText_IsNoOp()
    {
        var result = Run(string.Empty);
        Assert.Equal(string.Empty, result.Text);
    }
}
