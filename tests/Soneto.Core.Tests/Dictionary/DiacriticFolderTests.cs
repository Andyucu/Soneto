using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Phase 2 work item 2 (§2.5 rule 2, §2.11): <see cref="DiacriticFolder"/> is a pure,
/// match-only transform -- never used for emitted/injected text. Covers ș/ş and ț/ţ folding
/// to the same match key, ă/â/î folding, case preservation (rule 3 is a separate concern owned
/// by a later component), idempotence, and a real-world mixed sentence (the same S4 test
/// string used elsewhere in this project for diacritic correctness checks).
/// </summary>
public class DiacriticFolderTests
{
    [Fact]
    public void CommaBelowS_And_CedillaS_FoldToSameKey()
    {
        Assert.Equal(DiacriticFolder.FoldForMatching("ș"), DiacriticFolder.FoldForMatching("ş"));
    }

    [Fact]
    public void CommaBelowT_And_CedillaT_FoldToSameKey()
    {
        Assert.Equal(DiacriticFolder.FoldForMatching("ț"), DiacriticFolder.FoldForMatching("ţ"));
    }

    [Fact]
    public void UppercaseCommaBelowS_And_CedillaS_FoldToSameKey()
    {
        Assert.Equal(DiacriticFolder.FoldForMatching("Ș"), DiacriticFolder.FoldForMatching("Ş"));
    }

    [Fact]
    public void UppercaseCommaBelowT_And_CedillaT_FoldToSameKey()
    {
        Assert.Equal(DiacriticFolder.FoldForMatching("Ț"), DiacriticFolder.FoldForMatching("Ţ"));
    }

    [Theory]
    [InlineData("ă", "a")]
    [InlineData("â", "a")]
    [InlineData("î", "i")]
    [InlineData("Ă", "A")]
    [InlineData("Â", "A")]
    [InlineData("Î", "I")]
    public void VowelDiacritics_FoldToPlainVowel(string input, string expected)
    {
        Assert.Equal(expected, DiacriticFolder.FoldForMatching(input));
    }

    [Fact]
    public void PlainEnglishText_IsUnchanged()
    {
        const string text = "The quick brown fox jumps over the lazy dog.";
        Assert.Equal(text, DiacriticFolder.FoldForMatching(text));
    }

    [Fact]
    public void MixedSentence_FoldsCorrectly_EndToEnd()
    {
        // Same S4 test string used elsewhere in this project (spikes/s4-inject-win/TestData.cs)
        // for exactly this kind of diacritic correctness check.
        const string input = "Ăsta e un test: șoseaua Ștefan cel Mare, țară, îngheț";
        var folded = DiacriticFolder.FoldForMatching(input);

        Assert.Equal("Asta e un test: șoseaua Ștefan cel Mare, țara, ingheț", folded);
    }

    [Fact]
    public void Folding_IsIdempotent()
    {
        const string input = "Ăsta e un test: șoseaua Ștefan cel Mare, țară, îngheț cu ş şi ţ";
        var once = DiacriticFolder.FoldForMatching(input);
        var twice = DiacriticFolder.FoldForMatching(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Folding_PreservesCase_UppercaseFoldsToUppercaseKey()
    {
        var upper = DiacriticFolder.FoldForMatching("Ș");
        var lower = DiacriticFolder.FoldForMatching("ș");

        Assert.Equal("Ș", upper);
        Assert.NotEqual(upper, lower);
    }

    [Fact]
    public void FoldChar_MatchesFoldForMatching_PerCharacter()
    {
        const string input = "Ăsta ştiu ţara";
        var expected = DiacriticFolder.FoldForMatching(input);

        var actual = new System.Text.StringBuilder();
        foreach (var c in input)
            actual.Append(DiacriticFolder.FoldChar(c));

        Assert.Equal(expected, actual.ToString());
    }

    [Fact]
    public void DecomposedCombiningSequence_IsLeftUnfolded_ByDesign()
    {
        // "s" + standalone U+0326 COMBINING COMMA BELOW -- a decomposed rendering of ș that is
        // NOT the precomposed U+0219 codepoint FoldChar recognizes. DiacriticFolder assumes
        // NFC-normalized (precomposed) input, guaranteed upstream by
        // UnicodeNormalizerProcessor (order 10, before the dictionary engine at order 40+) --
        // see the class-level doc comment. This test locks in that this decomposed sequence is
        // silently left unfolded rather than throwing or being normalized, as a documented,
        // regression-proof fact rather than something a future reader has to re-derive.
        var decomposed = "ș";

        Assert.Equal(decomposed, DiacriticFolder.FoldForMatching(decomposed));
    }

    [Theory]
    [InlineData('ș', true)]
    [InlineData('ț', true)]
    [InlineData('Ș', true)]
    [InlineData('Ț', true)]
    [InlineData('ş', false)]
    [InlineData('ţ', false)]
    [InlineData('a', false)]
    public void IsCanonicalForm_DetectsCommaBelowFormsOnly(char c, bool expected)
    {
        Assert.Equal(expected, DiacriticFolder.IsCanonicalForm(c));
    }
}
