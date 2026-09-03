using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Tests for <see cref="WordFrequencyList"/> (Phase 2 work item 8, §2.8): proves the bundled
/// EN+RO embedded resources load, a genuinely common word matches case-insensitively, and a
/// clearly uncommon/technical word does not match.
/// </summary>
public class WordFrequencyListTests
{
    [Theory]
    [InlineData("the")]
    [InlineData("and")]
    [InlineData("cloud")]
    [InlineData("și")]
    public void CommonWord_IsRecognized(string word)
    {
        Assert.True(WordFrequencyList.Instance.IsCommonWord(word));
    }

    [Theory]
    [InlineData("webMethods")]
    [InlineData("SonarQube")]
    [InlineData("Cloudflare")]
    public void UncommonWord_IsNotRecognized(string word)
    {
        Assert.False(WordFrequencyList.Instance.IsCommonWord(word));
    }

    [Theory]
    [InlineData("THE")]
    [InlineData("The")]
    [InlineData("the")]
    public void Matching_IsCaseInsensitive(string word)
    {
        Assert.True(WordFrequencyList.Instance.IsCommonWord(word));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrWhitespaceOrNull_IsNeverCommon(string? word)
    {
        Assert.False(WordFrequencyList.Instance.IsCommonWord(word!));
    }

    [Fact]
    public void Instance_LoadsANonTrivialNumberOfWords()
    {
        // Deliberately non-exhaustive per the class doc comment -- just proves the embedded
        // resources actually loaded rather than the list silently being empty.
        Assert.True(WordFrequencyList.Instance.Count > 100);
    }

    [Fact]
    public void CustomWordList_OnlyContainsWhatWasPassedIn()
    {
        var sut = new WordFrequencyList(["foo", "BAR"]);

        Assert.True(sut.IsCommonWord("foo"));
        Assert.True(sut.IsCommonWord("bar"));
        Assert.True(sut.IsCommonWord("Bar"));
        Assert.False(sut.IsCommonWord("baz"));
        Assert.Equal(2, sut.Count);
    }
}
