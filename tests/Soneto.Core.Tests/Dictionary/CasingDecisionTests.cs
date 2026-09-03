using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Phase 2 work item 4 (§2.5 rule 3, §2.11): direct unit tests for
/// <see cref="DictionaryEngineProcessor.ApplyCasing"/>, the standalone casing-decision helper,
/// exercised without going through the whole processor/automaton.
///
/// <para>
/// <b>On the build plan's literal "Webmethods at a sentence start" example:</b> that example
/// does NOT actually exercise the "replacement adopts the original span's casing pattern"
/// branch under this implementation's own decision table, because the real seed-dictionary
/// replacement text is <c>webMethods</c>, which contains both an uppercase <c>M</c> and lowercase
/// letters -- i.e. it HAS explicit internal casing per §2.5 rule 3's own test, so it is used
/// verbatim regardless of how <c>"Webmethods"</c> was cased in the input (see
/// <see cref="ExplicitMixedCaseTarget_WinsOverAllCapsOriginal"/> /
/// <see cref="ExplicitMixedCaseTarget_WinsOverTitleCaseOriginal"/> below for that case). To
/// genuinely exercise the "no explicit internal casing -> adopt original casing pattern" branch,
/// this class instead uses a deliberately all-lowercase replacement target (a filler/typo
/// normalization rule, "teh" -> "the", the kind of rule that's all-lowercase because there's
/// nothing inherently mixed-case about the correct spelling) matched against a Title-Case
/// original span at a sentence start, and against an all-uppercase original span.
/// </para>
/// </summary>
public class CasingDecisionTests
{
    // ------------------------------------------------------------------
    // Explicit internal casing in the replacement always wins.
    // ------------------------------------------------------------------

    [Fact]
    public void ExplicitMixedCaseTarget_WinsOverLowercaseOriginal()
    {
        var result = DictionaryEngineProcessor.ApplyCasing("cloud code", "Claude Code");
        Assert.Equal("Claude Code", result);
    }

    [Fact]
    public void ExplicitMixedCaseTarget_WinsOverAllCapsOriginal()
    {
        // The exact "shouted" case from the plan: even ALL-CAPS input must not force the
        // explicitly-cased replacement into caps.
        var result = DictionaryEngineProcessor.ApplyCasing("CLOUD CODE", "Claude Code");
        Assert.Equal("Claude Code", result);
    }

    [Fact]
    public void ExplicitMixedCaseTarget_WinsOverTitleCaseOriginal()
    {
        // webMethods itself has explicit internal casing (upper M, rest lower) -- so per this
        // processor's own rule-3 logic, "Webmethods" at a sentence start does NOT force
        // lowercase/title-case adaptation; the rule's own casing always wins here. This is the
        // build plan's literal example, and it fits this branch (not the "adopt original
        // casing" branch, contrary to a naive first reading of the example -- see the class doc
        // comment above).
        var result = DictionaryEngineProcessor.ApplyCasing("Webmethods", "webMethods");
        Assert.Equal("webMethods", result);
    }

    [Fact]
    public void ExplicitMixedCaseTarget_WinsEvenWithAllCapsOriginal_Webmethods()
    {
        var result = DictionaryEngineProcessor.ApplyCasing("WEBMETHODS", "webMethods");
        Assert.Equal("webMethods", result);
    }

    // ------------------------------------------------------------------
    // No explicit internal casing in the replacement -> adopt the original span's casing
    // pattern. Uses "teh" -> "the" (a genuinely all-lowercase replacement target) since
    // webMethods/Claude Code-style examples don't exercise this branch (see class doc comment).
    // ------------------------------------------------------------------

    [Fact]
    public void AllLowercaseTarget_AllLowercaseOriginal_StaysLowercase()
    {
        var result = DictionaryEngineProcessor.ApplyCasing("teh", "the");
        Assert.Equal("the", result);
    }

    [Fact]
    public void AllLowercaseTarget_AllUppercaseOriginal_BecomesAllUppercase()
    {
        var result = DictionaryEngineProcessor.ApplyCasing("TEH", "the");
        Assert.Equal("THE", result);
    }

    [Fact]
    public void AllLowercaseTarget_TitleCaseOriginalAtSentenceStart_CapitalizesFirstLetterOnly()
    {
        // "Teh" (title-case, sentence start) should NOT force the whole replacement to stay
        // lowercase, and should NOT uppercase the whole thing either -- just the first letter.
        var result = DictionaryEngineProcessor.ApplyCasing("Teh", "the");
        Assert.Equal("The", result);
    }

    [Fact]
    public void AllLowercaseTarget_TitleCaseOriginal_MultiWordReplacement_CapitalizesFirstCharOnly()
    {
        var result = DictionaryEngineProcessor.ApplyCasing("Kinda", "kind of");
        Assert.Equal("Kind of", result);
    }

    [Fact]
    public void AllUppercaseTarget_TreatedAsNoExplicitInternalCasing_AdoptsOriginalPattern()
    {
        // "MFT" (all-caps target, e.g. an acronym VocabularyTerm) has no lowercase letters at
        // all, so it does not qualify as "explicit internal casing" (needs BOTH cases) -- an
        // all-lowercase original should map it to all-lowercase per rule 3's own decision table.
        var result = DictionaryEngineProcessor.ApplyCasing("mft", "MFT");
        Assert.Equal("mft", result);

        // But the common real-world case -- input already typed/spoken in a casing that isn't
        // relevant since MFT has no cased-letter ambiguity to adopt from an all-caps original --
        // still resolves sensibly: an all-caps original keeps the all-caps replacement as-is
        // (both branches agree here).
        var allCaps = DictionaryEngineProcessor.ApplyCasing("MFT", "MFT");
        Assert.Equal("MFT", allCaps);
    }

    // ------------------------------------------------------------------
    // Edge cases.
    // ------------------------------------------------------------------

    [Fact]
    public void OriginalSpanWithNoCasedLetters_DefaultsToLowercaseReplacement()
    {
        // e.g. matched span is all digits/punctuation-ish glue text with no letters at all --
        // shouldn't happen in practice for a real dictionary pattern, but the helper must not
        // throw and must fall back to a sane default (lowercase).
        var result = DictionaryEngineProcessor.ApplyCasing("123", "the");
        Assert.Equal("the", result);
    }

    [Fact]
    public void MixedButNotTitleCaseOriginal_FallsBackToLowercase()
    {
        // "tEh" -- first letter lowercase, later letter uppercase: neither all-upper nor
        // title-case, falls back to lowercase per the documented default branch.
        var result = DictionaryEngineProcessor.ApplyCasing("tEh", "the");
        Assert.Equal("the", result);
    }
}
