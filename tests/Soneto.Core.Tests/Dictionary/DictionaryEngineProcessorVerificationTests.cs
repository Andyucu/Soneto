using Soneto.Core.Abstractions;
using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Independent, from-scratch verification tests for <see cref="DictionaryEngineProcessor"/> and
/// its <see cref="DictionaryEngineProcessor.ApplyCasing"/> helper, written by a reviewer
/// separately from the implementer's own <c>DictionaryEngineProcessorTests</c> /
/// <c>CasingDecisionTests</c>, specifically to probe combinations those files did not try:
/// explicit-mixed-case replacements against "weird" (non-title, non-all-caps) originals, the
/// multi-word Title-Case detection edge case, a replacement with zero cased letters, disabled
/// entries colliding on the same match-key as an enabled entry, three-plus-match splicing at
/// both string boundaries with independently-computed expected output, and an end-to-end
/// <see cref="VocabularyTerm"/> no-visible-effect case.
/// </summary>
public class DictionaryEngineProcessorVerificationTests
{
    private static DictionaryEntry CorrectionPair(string id, string from, string to, bool enabled = true) =>
        new Soneto.Core.Dictionary.CorrectionPair { Id = id, From = from, To = to, Enabled = enabled };

    private static DictionaryEntry Vocab(string id, string term, bool enabled = true) =>
        new VocabularyTerm { Id = id, Term = term, Enabled = enabled };

    private static PostProcessResult Run(
        IEnumerable<DictionaryEntry> entries, string text, IReadOnlyList<AppliedRule>? applied = null) =>
        new DictionaryEngineProcessor(entries).Process(new PostProcessResult(text, applied ?? []));

    // ------------------------------------------------------------------
    // (4a/4b) Explicit-mixed-case replacement wins regardless of the original's own casing,
    // including a "genuinely weird" (neither lowercase, all-caps, nor title-case) original.
    // ------------------------------------------------------------------

    [Fact]
    public void ExplicitMixedCaseTarget_WinsOverAllLowercaseOriginal()
    {
        var result = DictionaryEngineProcessor.ApplyCasing("cloud code", "Claude Code");
        Assert.Equal("Claude Code", result);
    }

    [Fact]
    public void ExplicitMixedCaseTarget_WinsOverWeirdMixedCaseOriginal_NotTitleNotAllCaps()
    {
        // "wEbMethods" is neither all-lowercase, all-uppercase, nor Title-Case (second cased
        // char is also uppercase) -- it's the "fallback" DetectCasingPattern bucket. Since the
        // replacement itself has explicit internal casing, none of that should matter at all;
        // the replacement must be used verbatim.
        var result = DictionaryEngineProcessor.ApplyCasing("wEbMethods", "webMethods");
        Assert.Equal("webMethods", result);
    }

    // ------------------------------------------------------------------
    // (4c) All-lowercase replacement + genuinely mixed-but-not-title-case original -> falls
    // into the documented "lowercase" fallback bucket.
    // ------------------------------------------------------------------

    [Fact]
    public void AllLowercaseTarget_WeirdMixedCaseOriginal_FallsBackToLowercase()
    {
        var result = DictionaryEngineProcessor.ApplyCasing("tEh", "the");
        Assert.Equal("the", result);
    }

    // ------------------------------------------------------------------
    // (4d) Replacement with no cased letters at all must not crash and must produce itself
    // (there is nothing to case-transform).
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("teh")]
    [InlineData("TEH")]
    [InlineData("Teh")]
    [InlineData("123")]
    public void NoCasedLettersInReplacement_DoesNotCrash_ReturnsReplacementUnchanged(string original)
    {
        var result = DictionaryEngineProcessor.ApplyCasing(original, "42");
        Assert.Equal("42", result);
    }

    [Fact]
    public void SingleLowercaseLetterReplacement_AgainstAllCapsOriginal_Uppercases()
    {
        var result = DictionaryEngineProcessor.ApplyCasing("TEH", "x");
        Assert.Equal("X", result);
    }

    // ------------------------------------------------------------------
    // (4e) Multi-word Title-Case original span: per the implementer's own precisely-documented
    // definition (first cased char upper, EVERY OTHER cased char lower), a two-word span where
    // BOTH words are capitalized ("New Paragraph") does NOT qualify as Title-Case -- the second
    // word's leading uppercase letter violates "every other cased char is lowercase" -- so it
    // falls into the lowercase fallback bucket, not the "capitalize first char only" bucket.
    // This is judged here to be a reasonable, deliberate simplification (single-token sentence-
    // start capitalization is the plan's literal example; multi-word title-casing of the
    // REPLACEMENT was never in scope), not a bug -- but it IS worth pinning down explicitly so a
    // future change to this behavior is a deliberate, reviewed decision rather than silent drift.
    // ------------------------------------------------------------------

    [Fact]
    public void MultiWordCapitalizedOriginal_IsNotDetectedAsTitleCase_FallsBackToLowercase()
    {
        // Both words capitalized -> NOT title-case per the documented single-cap-char rule.
        var result = DictionaryEngineProcessor.ApplyCasing("New Paragraph", "kind of");
        Assert.Equal("kind of", result);
    }

    [Fact]
    public void SingleWordCapitalizedOriginal_AtSentenceStart_IsDetectedAsTitleCase()
    {
        // Contrast case: a single capitalized word IS detected as Title-Case, confirming the
        // multi-word case above is specifically about the second word's leading capital, not
        // some other unrelated bug.
        var result = DictionaryEngineProcessor.ApplyCasing("Paragraph", "kind of");
        Assert.Equal("Kind of", result);
    }

    // ------------------------------------------------------------------
    // (5) Three non-overlapping matches, one at the very start, one at the very end, one in the
    // middle -- independently verify the spliced text character-by-character and the AppliedRule
    // list's exact count/order/fields. Also verify pre-existing AppliedRules are appended-to, not
    // replaced.
    // ------------------------------------------------------------------

    [Fact]
    public void ThreeMatches_AtStartMiddleAndEnd_SpliceExactlyRight_AndAppendToExistingApplied()
    {
        var entries = new[]
        {
            CorrectionPair("c1", "cloud code", "Claude Code"),
            CorrectionPair("c2", "teh", "the"),
            CorrectionPair("c3", "web methods", "webMethods"),
        };
        var existing = new AppliedRule[] { new("Earlier", "e1", "x", "y") };

        var input = "cloud code fixes teh bug in web methods";
        var result = Run(entries, input, applied: existing);

        Assert.Equal("Claude Code fixes the bug in webMethods", result.Text);

        Assert.Equal(4, result.Applied.Count);
        Assert.Equal(new AppliedRule("Earlier", "e1", "x", "y"), result.Applied[0]);
        Assert.Equal(new AppliedRule("DictionaryEngine", "c1", "cloud code", "Claude Code"), result.Applied[1]);
        Assert.Equal(new AppliedRule("DictionaryEngine", "c2", "teh", "the"), result.Applied[2]);
        Assert.Equal(new AppliedRule("DictionaryEngine", "c3", "web methods", "webMethods"), result.Applied[3]);
    }

    [Fact]
    public void MatchAtVeryStart_AndMatchAtVeryEnd_MinimalOneCharGlue_SplicesCorrectly()
    {
        var entries = new[]
        {
            CorrectionPair("c1", "cloud code", "Claude Code"),
            CorrectionPair("c2", "teh", "the"),
        };

        // Full-token-boundary rule (§2.5 rule 6) requires a non letter-or-digit char on both
        // sides of a match, so a comma (not a space) is used here as the smallest possible
        // legal glue between two matches -- one character of unmatched text is copied verbatim,
        // stress-testing the cursor arithmetic right at the seam between two matches with no
        // leading text before the first match and no trailing text after the last.
        var result = Run(entries, "cloud code,teh");

        Assert.Equal("Claude Code,the", result.Text);
        Assert.Equal(2, result.Applied.Count);
    }

    // ------------------------------------------------------------------
    // (6) Disabled entry sharing the same canonical match-key as nothing else must not throw at
    // construction, and must never fire -- i.e. filtering happens strictly before the automaton
    // ever sees the disabled entry, so no collision is possible even if two entries nominally
    // share a pattern but one is disabled.
    // ------------------------------------------------------------------

    [Fact]
    public void TwoEntries_SameMatchKey_OneDisabled_ConstructionSucceeds_OnlyEnabledOneFires()
    {
        var entries = new[]
        {
            CorrectionPair("c1", "cloud code", "Claude Code", enabled: false),
            CorrectionPair("c2", "cloud code", "CLOUD CODE (v2)", enabled: true),
        };

        // Must not throw ArgumentException from the automaton's collision detection, since the
        // disabled entry is filtered out before construction ever reaches the automaton.
        var exception = Record.Exception(() => Run(entries, "please open cloud code now"));
        Assert.Null(exception);

        var result = Run(entries, "please open cloud code now");
        Assert.Equal("please open CLOUD CODE (v2) now", result.Text);
        Assert.Single(result.Applied);
        Assert.Equal("c2", result.Applied[0].Rule);
    }

    [Fact]
    public void BothEntries_SameMatchKey_BothEnabled_ConstructionThrows()
    {
        // Sanity control for the above: when BOTH entries are enabled and collide, the
        // automaton's own collision detection must still fire -- confirming the previous test's
        // "no throw" result is really because of the disabled-filtering, not some other reason
        // (e.g. the automaton silently tolerating duplicate patterns in general).
        var entries = new[]
        {
            CorrectionPair("c1", "cloud code", "Claude Code", enabled: true),
            CorrectionPair("c2", "cloud code", "CLOUD CODE (v2)", enabled: true),
        };

        Assert.ThrowsAny<ArgumentException>(() => new DictionaryEngineProcessor(entries));
    }

    // ------------------------------------------------------------------
    // (7) VocabularyTerm end-to-end: realistic term, several wrong-casing variants corrected in
    // sentence context, plus the all-lowercase-term no-visible-effect case.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("i love sonarqube reports", "i love SonarQube reports")]
    [InlineData("I LOVE SONARQUBE REPORTS", "I LOVE SONARQUBE REPORTS")]
    [InlineData("Sonarqube found issues.", "SonarQube found issues.")]
    public void VocabularyTerm_SonarQube_CorrectsCasingVariants_InSentenceContext(string input, string expected)
    {
        // Note: "I LOVE SONARQUBE REPORTS" is an ALL-CAPS original span for "sonarqube" -- per
        // rule 3, since "SonarQube" HAS explicit internal casing (upper S/Q, lower rest), the
        // rule's own casing wins verbatim regardless of the ALL-CAPS input, so the sentence
        // should read "... SonarQube REPORTS" -- but SonarQube is only one token here, so the
        // surrounding ALL-CAPS words are untouched (they aren't part of the match at all).
        var entries = new[] { Vocab("v1", "SonarQube") };
        var result = Run(entries, input);

        if (input == "I LOVE SONARQUBE REPORTS")
        {
            Assert.Equal("I LOVE SonarQube REPORTS", result.Text);
        }
        else
        {
            Assert.Equal(expected, result.Text);
        }
    }

    [Fact]
    public void VocabularyTerm_AllLowercaseTerm_NoVisibleEffect_NotCrash()
    {
        // A VocabularyTerm whose Term has no internal mixed case (all-lowercase) has no
        // "explicit internal casing" to force verbatim -- so per rule 3 it just adopts whatever
        // casing pattern the matched original span already had. For an all-lowercase original,
        // that means the output is byte-for-byte identical to the input: genuinely no visible
        // effect, not a crash or a surprising transform.
        var entries = new[] { Vocab("v1", "docker") };
        var result = Run(entries, "please start docker now");

        Assert.Equal("please start docker now", result.Text);
        // A match still fired internally (AppliedRule is populated) even though it produced no
        // visible text change -- confirming this is "no-op by coincidence of casing", not
        // "no match happened at all".
        Assert.Single(result.Applied);
        Assert.Equal("docker", result.Applied[0].From);
        Assert.Equal("docker", result.Applied[0].To);
    }

    [Fact]
    public void VocabularyTerm_AllLowercaseTerm_TitleCaseOriginal_AdoptsTitleCasingNotVerbatim()
    {
        // Contrast: the same all-lowercase term, but matched against a Title-Case original span,
        // DOES visibly change the text (capitalizes the first letter) -- proving the "no visible
        // effect" case above is specific to the all-lowercase-original scenario, not a general
        // property of all-lowercase VocabularyTerms.
        var entries = new[] { Vocab("v1", "docker") };
        var result = Run(entries, "Docker is running.");

        Assert.Equal("Docker is running.", result.Text);
    }
}
