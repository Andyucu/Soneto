using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Independent verification tests written by a test-runner/reviewer agent, NOT the implementer,
/// for Phase 2 work item 3 (§2.5 rules 4-6). These deliberately do not reuse or re-derive from
/// the implementer's own test file -- constructed fresh against the plan spec, specifically to
/// catch bugs the implementer's own tests might share a blind spot with.
/// </summary>
public class AhoCorasickAutomatonIndependentVerificationTests
{
    // ------------------------------------------------------------------
    // Point 3: rule 6 full-token boundaries -- adversarial, Unicode-aware.
    // ------------------------------------------------------------------

    [Fact]
    public void Boundary_PatternInsideRomanianWordWithDiacritics_DoesNotMatch()
    {
        // "cod" must not match inside "codul" (Romanian for "the code").
        var automaton = new AhoCorasickAutomaton<string>([("cod", "cod-rule")]);

        var matches = automaton.Match("codul sursă este aici");

        Assert.Empty(matches);
    }

    [Fact]
    public void Boundary_PatternInsideWordContainingTrailingDiacriticLetter_DoesNotMatch()
    {
        // The character immediately after the match is a diacritic LETTER (ă), which must be
        // recognized as a letter by a Unicode-aware check, not just ASCII [a-zA-Z0-9].
        var automaton = new AhoCorasickAutomaton<string>([("cas", "cas-rule")]);

        // "casă" (house) = "cas" + "ă" (diacritic letter immediately following).
        var matches = automaton.Match("o casă mare");

        Assert.Empty(matches);
    }

    [Fact]
    public void Boundary_MatchAtVeryStartOfInput_NoPrecedingCharacter_StillMatches()
    {
        var automaton = new AhoCorasickAutomaton<string>([("cloud", "cloud-rule")]);

        var matches = automaton.Match("cloud");

        Assert.Single(matches);
        Assert.Equal(0, matches[0].Start);
        Assert.Equal(5, matches[0].Length);
    }

    [Fact]
    public void Boundary_MatchAtVeryEndOfInput_NoFollowingCharacter_StillMatches()
    {
        var automaton = new AhoCorasickAutomaton<string>([("cloud", "cloud-rule")]);
        const string input = "we use the cloud";

        var matches = automaton.Match(input);

        Assert.Single(matches);
        Assert.Equal("we use the ".Length, matches[0].Start);
        Assert.Equal(input.Length, matches[0].Start + matches[0].Length);
    }

    [Fact]
    public void Boundary_MatchImmediatelyAfterComma_IsAllowed()
    {
        // Punctuation is not letter-or-digit, so a match right after a comma must fire.
        var automaton = new AhoCorasickAutomaton<string>([("cloud", "cloud-rule")]);

        var matches = automaton.Match("hello,cloud");

        Assert.Single(matches);
        Assert.Equal(6, matches[0].Start);
        Assert.Equal(5, matches[0].Length);
    }

    [Fact]
    public void Boundary_MatchImmediatelyBeforeComma_IsAllowed()
    {
        var automaton = new AhoCorasickAutomaton<string>([("cloud", "cloud-rule")]);

        var matches = automaton.Match("cloud,storage");

        Assert.Single(matches);
        Assert.Equal(0, matches[0].Start);
        Assert.Equal(5, matches[0].Length);
    }

    [Fact]
    public void Boundary_WholeInputIsExactlyThePattern_SingleWordInput_Matches()
    {
        var automaton = new AhoCorasickAutomaton<string>([("sonarqube", "sq")]);

        var matches = automaton.Match("sonarqube");

        Assert.Single(matches);
    }

    // ------------------------------------------------------------------
    // Point 4: rule 4 longest-match-first / no cascading, own construction.
    // ------------------------------------------------------------------

    [Fact]
    public void LongestMatchFirst_ThreePatternsSharingCommonPrefix_LongestWins()
    {
        // "a", "ab", "abc" -- classic shared-prefix trie stress case, distinct from the
        // implementer's "new"/"new paragraph" two-pattern test.
        var automaton = new AhoCorasickAutomaton<string>(
        [
            ("a", "one"),
            ("ab", "two"),
            ("abc", "three"),
        ]);

        var matches = automaton.Match("abc");

        Assert.Single(matches);
        Assert.Equal("three", matches[0].Value);
        Assert.Equal(0, matches[0].Start);
        Assert.Equal(3, matches[0].Length);
    }

    [Fact]
    public void LongestMatchFirst_EqualLengthMatchesAtDifferentPositions_EarliestStartWins()
    {
        // A naive "bcd" continuous-letter-run overlap (patterns "bc"/"cd") is INVALID under
        // rule 6 -- there's no token boundary anywhere inside "bcd", so rule 6 would reject any
        // candidate whose letter-neighbor abuts it (see report point 7's reasoning). Use
        // word-boundary-respecting patterns instead: "x y" and "y z" as separate space-delimited
        // "words" that share the middle token "y", both length 3, genuinely overlapping.
        var automaton = new AhoCorasickAutomaton<string>(
        [
            ("x y", "first"),
            ("y z", "second"),
        ]);

        var matches = automaton.Match("x y z");

        Assert.Single(matches);
        Assert.Equal("first", matches[0].Value); // earliest start wins on a length tie
    }

    [Fact]
    public void NoCascading_ReplacementTextNeverActuallyConstructed_HypotheticalContainmentIgnored()
    {
        // Pattern A "hello" is hypothetically replaced by "world wide", which contains pattern
        // B "wide" verbatim -- but that replacement is never actually performed by this class.
        // Match() on the original input "hello" must report ONLY A, since "wide" is not
        // literally present anywhere in the real original input.
        var automaton = new AhoCorasickAutomaton<string>(
        [
            ("hello", "A"),
            ("wide", "B"),
        ]);

        var matches = automaton.Match("hello");

        Assert.Single(matches);
        Assert.Equal("A", matches[0].Value);

        // Sanity: if "wide" genuinely appears in real original input, it DOES get found --
        // proving the class isn't just broken/blind to pattern B in general.
        var matches2 = automaton.Match("a wide hello");
        Assert.Equal(2, matches2.Count);
    }

    // ------------------------------------------------------------------
    // Point 5: rule 5 glue-tolerant boundaries -- hand-computed position arithmetic.
    // ------------------------------------------------------------------

    [Fact]
    public void GlueTolerant_WebHyphenMethods_ReconstructsExactOriginalSpan()
    {
        // Input: "web-methods" (indices 0-10, length 11). Pattern "web methods" folds/glue-strips
        // to key stream "webmethods" (10 chars); surviving original indices are 0-2 (web) and
        // 4-10 (methods) -- index 3 ('-') is glue and dropped. First surviving key char maps to
        // original index 0; last surviving key char maps to original index 10. Expected:
        // Start=0, Length = 10 - 0 + 1 = 11 (the WHOLE original string, hyphen included).
        var automaton = new AhoCorasickAutomaton<string>([("web methods", "wm")]);
        const string input = "web-methods";

        var matches = automaton.Match(input);

        Assert.Single(matches);
        Assert.Equal(0, matches[0].Start);
        Assert.Equal(11, matches[0].Length);
        Assert.Equal("web-methods", input.Substring(matches[0].Start, matches[0].Length));
    }

    [Fact]
    public void GlueTolerant_MultipleGluePoints_ThreeWordPattern_HyphenSeparatedInput_ReconstructsExactSpan()
    {
        // Pattern "a b c" folds to key stream "abc". Input "x a-b-c y":
        // indices: x=0, ' '=1, a=2, -=3, b=4, -=5, c=6, ' '=7, y=8 (length 9).
        // Surviving key chars (glue-stripped): a(idx2), b(idx4), c(idx6) -> key stream "abc".
        // Match spans key indices 0..2 (the whole "abc"), mapping to original indices 2 and 6.
        // Expected Start=2, Length = 6 - 2 + 1 = 5 ("a-b-c").
        var automaton = new AhoCorasickAutomaton<string>([("a b c", "abc-rule")]);
        const string input = "x a-b-c y";

        var matches = automaton.Match(input);

        Assert.Single(matches);
        Assert.Equal(2, matches[0].Start);
        Assert.Equal(5, matches[0].Length);
        Assert.Equal("a-b-c", input.Substring(matches[0].Start, matches[0].Length));
    }

    [Fact]
    public void GlueTolerant_ExtraWhitespace_MultipleSpacesBetweenWords_ReconstructsExactSpan()
    {
        // Pattern "web methods" against input "I use web   methods here" (3 spaces).
        // "I use " = indices 0-5 (I,' ',u,s,e,' '), "web" = indices 6-8, "   " = indices 9-11
        // (glue, dropped), "methods" = indices 12-18, " here" follows.
        // Expected Start=6, Length = 18 - 6 + 1 = 13 ("web   methods").
        var automaton = new AhoCorasickAutomaton<string>([("web methods", "wm")]);
        const string input = "I use web   methods here";

        var matches = automaton.Match(input);

        Assert.Single(matches);
        Assert.Equal(6, matches[0].Start);
        Assert.Equal(13, matches[0].Length);
        Assert.Equal("web   methods", input.Substring(matches[0].Start, matches[0].Length));
    }

    [Fact]
    public void GlueTolerant_ThreeWordPattern_MultiSpaceInput_ReconstructsExactSpan()
    {
        // Pattern "a b c" against input "a  b  c" (double spaces between each word).
        // indices: a=0, ' '=1, ' '=2, b=3, ' '=4, ' '=5, c=6 (length 7).
        // Expected Start=0, Length = 6 - 0 + 1 = 7 (the whole string).
        var automaton = new AhoCorasickAutomaton<string>([("a b c", "abc-rule")]);
        const string input = "a  b  c";

        var matches = automaton.Match(input);

        Assert.Single(matches);
        Assert.Equal(0, matches[0].Start);
        Assert.Equal(7, matches[0].Length);
        Assert.Equal(input, input.Substring(matches[0].Start, matches[0].Length));
    }

    // ------------------------------------------------------------------
    // Point 6: case-insensitive matching + case-preserving spans, composed with diacritic fold.
    // ------------------------------------------------------------------

    [Fact]
    public void CaseInsensitive_DifferentCaseInput_ReturnsExactOriginalCaseSubstring_NotFoldedOrLowercased()
    {
        var automaton = new AhoCorasickAutomaton<string>([("HeLLo", "greeting")]);
        const string input = "say HELLO now";

        var matches = automaton.Match(input);

        Assert.Single(matches);
        // Must be the REAL original substring "HELLO", not "hello" (lowercased) nor any folded
        // form.
        Assert.Equal("HELLO", input.Substring(matches[0].Start, matches[0].Length));
    }

    [Fact]
    public void DiacriticAndCaseComposeTogether_CedillaUppercaseInput_MatchesCommaBelowLowercasePattern()
    {
        // Pattern written lowercase, comma-below form: "țară" (country).
        // Input: uppercase, cedilla-legacy form -- constructed explicitly via uppercase cedilla
        // codepoints so both transforms (fold + case-fold) must compose correctly.
        // Ţ (U+0162) + A + R + Ă (U+0102 uppercase ă) -> "ŢARĂ"
        var automaton = new AhoCorasickAutomaton<string>([("țară", "country")]);
        string input = "numele ŢARĂ este aici"; // "ȚARĂ" via cedilla Ţ + uppercase Ă

        var matches = automaton.Match(input);

        Assert.Single(matches);
        Assert.Equal("country", matches[0].Value);
        // The returned span must be the REAL original text -- cedilla + uppercase, untouched.
        string expected = "ŢARĂ";
        Assert.Equal(expected, input.Substring(matches[0].Start, matches[0].Length));
    }

    [Fact]
    public void DiacriticAndCaseComposeTogether_MixedCedillaCommaBelowAndCase_AllVariantsMatchSameRule()
    {
        var automaton = new AhoCorasickAutomaton<string>([("Șase", "six")]); // comma-below, mixed case pattern

        // ş (cedilla lower) + ase, uppercase throughout, etc.
        var matches1 = automaton.Match("spui şase");   // cedilla lowercase
        var matches2 = automaton.Match("spui ȘASE");    // comma-below uppercase
        var matches3 = automaton.Match("spui ŞASE"); // Ş (cedilla uppercase) + ASE

        Assert.Single(matches1);
        Assert.Single(matches2);
        Assert.Single(matches3);
    }
}
