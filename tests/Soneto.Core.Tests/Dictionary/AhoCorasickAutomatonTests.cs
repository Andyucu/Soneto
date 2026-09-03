using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Phase 2 work item 3 (§2.5 rules 4-6, §2.11): <see cref="AhoCorasickAutomaton{TValue}"/> is
/// the single most safety-critical piece of the whole matching engine, per the build plan's own
/// callout. <see cref="FullTokenBoundary_ClouddoesNotMatchInsideCloudflare"/> below is the
/// adversarial test that was written and made to pass FIRST, before any other test in this
/// class or any other part of the matcher -- it stays in the permanent regression suite.
/// </summary>
public class AhoCorasickAutomatonTests
{
    // ------------------------------------------------------------------
    // Rule 6 -- full-token boundaries. Written and passing FIRST.
    // ------------------------------------------------------------------

    [Fact]
    public void FullTokenBoundary_ClouddoesNotMatchInsideCloudflare()
    {
        var automaton = new AhoCorasickAutomaton<string>([("cloud", "cloud-rule")]);

        var matches = automaton.Match("I use Cloudflare for DNS.");

        Assert.Empty(matches);
    }

    [Fact]
    public void FullTokenBoundary_StandaloneWordMatches_AtStartMiddleAndEndOfString()
    {
        var automaton = new AhoCorasickAutomaton<string>([("cloud", "cloud-rule")]);

        var atStart = automaton.Match("cloud storage is great");
        var inMiddle = automaton.Match("we use cloud storage");
        var atEnd = automaton.Match("we use the cloud");
        var withPunctuation = automaton.Match("we use the cloud, mostly.");

        Assert.Single(atStart);
        Assert.Equal(0, atStart[0].Start);
        Assert.Equal(5, atStart[0].Length);

        Assert.Single(inMiddle);
        Assert.Equal("cloud-rule", inMiddle[0].Value);
        Assert.Equal("cloud", "we use cloud storage".Substring(inMiddle[0].Start, inMiddle[0].Length));

        Assert.Single(atEnd);
        Assert.Equal("we use the ".Length, atEnd[0].Start);

        Assert.Single(withPunctuation);
        Assert.Equal("we use the ".Length, withPunctuation[0].Start);
        Assert.Equal(5, withPunctuation[0].Length);
    }

    [Fact]
    public void FullTokenBoundary_DoesNotMatchWhenPrecededByLetterOrDigit()
    {
        // "sonarqube" must not match inside "mysonarqube" (prefixed by a letter).
        var automaton = new AhoCorasickAutomaton<string>([("sonarqube", "sq")]);

        var matches = automaton.Match("mysonarqube is running");

        Assert.Empty(matches);
    }

    [Fact]
    public void FullTokenBoundary_DoesNotMatchWhenFollowedByDigit()
    {
        var automaton = new AhoCorasickAutomaton<string>([("as", "as-rule")]);

        var matches = automaton.Match("as2 protocol");

        Assert.Empty(matches);
    }

    // ------------------------------------------------------------------
    // Rule 4 -- longest-match-first, single pass.
    // ------------------------------------------------------------------

    [Fact]
    public void LongestMatchFirst_OverlappingPatternsAtSameStart_PrefersLongerPattern()
    {
        var automaton = new AhoCorasickAutomaton<string>(
        [
            ("new", "just-new"),
            ("new paragraph", "new-paragraph"),
        ]);

        var matches = automaton.Match("start a new paragraph here");

        Assert.Single(matches);
        Assert.Equal("new-paragraph", matches[0].Value);
        Assert.Equal("start a ".Length, matches[0].Start);
        Assert.Equal("new paragraph".Length, matches[0].Length);
    }

    [Fact]
    public void NoCascading_MatchOnlyScansOriginalInput_NeverReMatchesReplacementText()
    {
        // Pattern A: "foo" (conceptually -> "bar baz"); Pattern B: "baz" (conceptually -> "qux").
        // This class has no replacement concept at all -- Match("foo") must only ever find
        // pattern A, since "baz" literally never appears in the input "foo".
        var automaton = new AhoCorasickAutomaton<string>(
        [
            ("foo", "A"),
            ("baz", "B"),
        ]);

        var matches = automaton.Match("foo");

        Assert.Single(matches);
        Assert.Equal("A", matches[0].Value);
    }

    [Fact]
    public void NonOverlappingResult_GenuinelyOverlappingPatterns_ResolvedByLongestThenEarliestStart()
    {
        // "ab cd" and "cd ef" both partially cover "cd" in "ab cd ef" -- genuinely overlapping,
        // neither a prefix/suffix of the other (word-boundary-respecting, unlike a naive
        // "abcd"/"cdef" over a single continuous word, which rule 6 would reject entirely).
        // Both matched spans are length 5, so earliest-start wins.
        var automaton = new AhoCorasickAutomaton<string>(
        [
            ("ab cd", "first"),
            ("cd ef", "second"),
        ]);

        const string input = "ab cd ef";
        var matches = automaton.Match(input);

        Assert.Single(matches);
        Assert.Equal("first", matches[0].Value);
        Assert.Equal("ab cd", input.Substring(matches[0].Start, matches[0].Length));
    }

    // ------------------------------------------------------------------
    // Rule 5 -- glue-tolerant boundaries.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("we use web methods here", "web methods")]
    [InlineData("we use web-methods here", "web-methods")]
    [InlineData("we use webmethods here", "webmethods")]
    [InlineData("we use web   methods here", "web   methods")]
    public void GlueTolerantBoundaries_AllSeparatorFormsMatch_AndSpanTheRealOriginalText(
        string input, string expectedOriginalSubstring)
    {
        var automaton = new AhoCorasickAutomaton<string>([("web methods", "wm")]);

        var matches = automaton.Match(input);

        Assert.Single(matches);
        Assert.Equal("wm", matches[0].Value);
        Assert.Equal(expectedOriginalSubstring, input.Substring(matches[0].Start, matches[0].Length));
    }

    // ------------------------------------------------------------------
    // Diacritic folding composition (rule 2, reused from item 2's DiacriticFolder).
    // ------------------------------------------------------------------

    [Fact]
    public void DiacriticFolding_CommaBelowPattern_MatchesCedillaFormInput()
    {
        // Pattern written with comma-below diacritics ("ș"); input written with the legacy
        // cedilla form ("ş") -- both must fold to the same match key.
        var automaton = new AhoCorasickAutomaton<string>([("șase", "six")]);
        const string input = "am spus şase din nou";

        var matches = automaton.Match(input);

        Assert.Single(matches);
        Assert.Equal("six", matches[0].Value);
        Assert.Equal("şase", input.Substring(matches[0].Start, matches[0].Length));
    }

    // ------------------------------------------------------------------
    // Case-insensitive matching, case-preserving match spans.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("WebMethods")]
    [InlineData("WEBMETHODS")]
    [InlineData("webmethods")]
    public void CaseInsensitiveMatch_PreservesOriginalCaseInReturnedSpan(string original)
    {
        var automaton = new AhoCorasickAutomaton<string>([("webmethods", "wm")]);
        string input = $"I use {original} daily";

        var matches = automaton.Match(input);

        Assert.Single(matches);
        Assert.Equal(original, input.Substring(matches[0].Start, matches[0].Length));
    }

    // ------------------------------------------------------------------
    // Degenerate cases.
    // ------------------------------------------------------------------

    [Fact]
    public void EmptyInput_NoMatches()
    {
        var automaton = new AhoCorasickAutomaton<string>([("cloud", "cloud-rule")]);

        var matches = automaton.Match("");

        Assert.Empty(matches);
    }

    [Fact]
    public void NoPatterns_NoMatches()
    {
        var automaton = new AhoCorasickAutomaton<string>([]);

        var matches = automaton.Match("cloud storage is great");

        Assert.Empty(matches);
    }

    [Fact]
    public void EmptyPattern_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new AhoCorasickAutomaton<string>([("", "x")]));
    }

    [Fact]
    public void GlueOnlyPattern_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new AhoCorasickAutomaton<string>([("  -  ", "x")]));
    }

    // ------------------------------------------------------------------
    // Colliding-canonical-key detection (deterministic tie-break should-fix).
    // ------------------------------------------------------------------

    [Fact]
    public void CollidingPatterns_DifferentDiacriticForms_ThrowArgumentException_NamingBothPatterns()
    {
        // "șase" (comma-below) and "şase" (cedilla) fold to the same match key.
        var ex = Assert.Throws<ArgumentException>(() =>
            new AhoCorasickAutomaton<string>([("șase", "A"), ("şase", "B")]));

        Assert.Contains("șase", ex.Message);
        Assert.Contains("şase", ex.Message);
    }

    [Fact]
    public void CollidingPatterns_DifferentGlueForms_ThrowArgumentException()
    {
        // "web methods" and "web-methods" (and "webmethods") all glue-strip to the same key.
        var ex = Assert.Throws<ArgumentException>(() =>
            new AhoCorasickAutomaton<string>([("web methods", "A"), ("web-methods", "B")]));

        Assert.Contains("web methods", ex.Message);
        Assert.Contains("web-methods", ex.Message);
    }

    [Fact]
    public void CollidingPatterns_DifferentCasing_ThrowArgumentException()
    {
        // "webMethods" and "WEBMETHODS" both lowercase to the same key.
        var ex = Assert.Throws<ArgumentException>(() =>
            new AhoCorasickAutomaton<string>([("webMethods", "A"), ("WEBMETHODS", "B")]));

        Assert.Contains("webMethods", ex.Message);
        Assert.Contains("WEBMETHODS", ex.Message);
    }
}
