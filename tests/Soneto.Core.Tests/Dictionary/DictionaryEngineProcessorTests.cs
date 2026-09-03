using Soneto.Core.Abstractions;
using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Phase 2 work item 4 (§2.5/§2.11): end-to-end tests for <see cref="DictionaryEngineProcessor"/>
/// (order 40) -- the automaton + rule-3 casing logic + <see cref="AppliedRule"/> population
/// wired together as a real <see cref="IPostProcessor"/>.
/// </summary>
public class DictionaryEngineProcessorTests
{
    private static DictionaryEntry CorrectionPair(string id, string from, string to, bool enabled = true) =>
        new Soneto.Core.Dictionary.CorrectionPair { Id = id, From = from, To = to, Enabled = enabled };

    private static DictionaryEntry Vocab(string id, string term, bool enabled = true) =>
        new VocabularyTerm { Id = id, Term = term, Enabled = enabled };

    private static PostProcessResult Run(
        IEnumerable<DictionaryEntry> entries, string text, bool enabled = true, IReadOnlyList<AppliedRule>? applied = null) =>
        new DictionaryEngineProcessor(entries, enabled).Process(new PostProcessResult(text, applied ?? []));

    // ------------------------------------------------------------------
    // Vocabulary-term casing correction.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("webmethods")]
    [InlineData("WEBMETHODS")]
    [InlineData("Webmethods")]
    public void VocabularyTerm_CorrectsCasingVariants_ToCanonicalForm(string variant)
    {
        var result = Run([Vocab("v1", "webMethods")], $"I use {variant} daily.");
        Assert.Equal("I use webMethods daily.", result.Text);
    }

    // ------------------------------------------------------------------
    // Disabled entries never match.
    // ------------------------------------------------------------------

    [Fact]
    public void DisabledCorrectionPair_NeverFires()
    {
        var entries = new[] { CorrectionPair("c1", "cloud code", "Claude Code", enabled: false) };
        var result = Run(entries, "please open cloud code now");

        Assert.Equal("please open cloud code now", result.Text);
        Assert.Empty(result.Applied);
    }

    // ------------------------------------------------------------------
    // AppliedRule population.
    // ------------------------------------------------------------------

    [Fact]
    public void MultipleMatches_PopulateAppliedRule_WithCorrectFields()
    {
        var entries = new[]
        {
            CorrectionPair("c1", "cloud code", "Claude Code"),
            Vocab("v1", "webMethods"),
        };
        var existing = new AppliedRule[] { new("Earlier", "e1", "x", "y") };

        var result = Run(entries, "open cloud code and check webmethods docs", applied: existing);

        Assert.Equal("open Claude Code and check webMethods docs", result.Text);

        // Existing AppliedRules from earlier stages are preserved, not discarded.
        Assert.Contains(result.Applied, a => a is { Processor: "Earlier", Rule: "e1", From: "x", To: "y" });

        Assert.Equal(3, result.Applied.Count);
        Assert.Contains(result.Applied, a =>
            a.Processor == "DictionaryEngine" && a.Rule == "c1" && a.From == "cloud code" && a.To == "Claude Code");
        Assert.Contains(result.Applied, a =>
            a.Processor == "DictionaryEngine" && a.Rule == "v1" && a.From == "webmethods" && a.To == "webMethods");
    }

    // ------------------------------------------------------------------
    // Multiple non-overlapping matches spliced correctly (boundary/off-by-one coverage).
    // ------------------------------------------------------------------

    [Fact]
    public void MultipleMatches_SplicedCorrectly_AtBothStringBoundaries()
    {
        var entries = new[]
        {
            CorrectionPair("c1", "cloud code", "Claude Code"),
            CorrectionPair("c2", "web methods", "webMethods"),
        };

        // First match at the very start of the string, second match at the very end.
        var result = Run(entries, "cloud code is great, so is web methods");

        Assert.Equal("Claude Code is great, so is webMethods", result.Text);
        Assert.Equal(2, result.Applied.Count);
    }

    [Fact]
    public void MultipleMatches_WithUnmatchedTextBetweenAndAroundThem()
    {
        var entries = new[]
        {
            CorrectionPair("c1", "cloud code", "Claude Code"),
            CorrectionPair("c2", "web methods", "webMethods"),
        };

        var result = Run(entries, "before cloud code middle web methods after");

        Assert.Equal("before Claude Code middle webMethods after", result.Text);
    }

    // ------------------------------------------------------------------
    // No-op passthrough.
    // ------------------------------------------------------------------

    [Fact]
    public void Disabled_IsPassthrough()
    {
        var entries = new[] { CorrectionPair("c1", "cloud code", "Claude Code") };
        var existing = new AppliedRule[] { new("Earlier", "e1", "x", "y") };

        var result = Run(entries, "cloud code here", enabled: false, applied: existing);

        Assert.Equal("cloud code here", result.Text);
        Assert.Same(existing, result.Applied);
    }

    [Fact]
    public void EmptyText_IsPassthrough()
    {
        var entries = new[] { CorrectionPair("c1", "cloud code", "Claude Code") };
        var result = Run(entries, string.Empty);

        Assert.Equal(string.Empty, result.Text);
        Assert.Empty(result.Applied);
    }

    [Fact]
    public void NoMatches_IsPassthrough_TextAndAppliedUnchanged()
    {
        var entries = new[] { CorrectionPair("c1", "cloud code", "Claude Code") };
        var existing = new AppliedRule[] { new("Earlier", "e1", "x", "y") };

        var result = Run(entries, "nothing to see here", applied: existing);

        Assert.Equal("nothing to see here", result.Text);
        Assert.Same(existing, result.Applied);
    }

    [Fact]
    public void NoEnabledEntries_IsPassthrough()
    {
        var result = Run([], "cloud code here");
        Assert.Equal("cloud code here", result.Text);
    }

    // ------------------------------------------------------------------
    // End-to-end diacritic + case + boundary composition.
    // ------------------------------------------------------------------

    [Fact]
    public void DiacriticVariant_CaseVariant_AndFalsePositiveBoundaryRisk_AllHandledInOneSentence()
    {
        var entries = new[]
        {
            // Pattern written with the canonical comma-below ș; the emitted replacement uses
            // the correct canonical ș form regardless of which folded variant matched.
            CorrectionPair("c1", "șofer", "Șofer profesionist"),
            // Vocabulary casing correction.
            Vocab("v1", "SonarQube"),
            // Deliberately risky short pattern that must NOT match inside a longer real word.
            CorrectionPair("c2", "cloud", "Cloud"),
        };

        // "şofer" is spelled here with the legacy cedilla ş form to prove fold-matching still
        // matches the ș-form pattern; "SONARQUBE" is shouted to prove the mixed-case target
        // wins regardless of input casing; "Cloudflare" must be left completely untouched
        // (rule 6, full-token boundary) while the standalone "cloud" at the end IS corrected.
        var input = "şofer bun: SONARQUBE pe Cloudflare, nu pe cloud.";

        var result = Run(entries, input);

        Assert.Contains("Șofer profesionist", result.Text);
        Assert.Contains("SonarQube", result.Text);
        Assert.Contains("Cloudflare", result.Text); // untouched
        Assert.DoesNotContain("CloudCloudflare", result.Text);
        Assert.Contains("Cloud.", result.Text); // the standalone "cloud." at the end WAS corrected
    }

    // ------------------------------------------------------------------
    // Entry-type filtering: only CorrectionPair/VocabularyTerm are consumed.
    // ------------------------------------------------------------------

    [Fact]
    public void OtherEntryTypes_AreIgnored_NoThrowNoMatch()
    {
        DictionaryEntry[] entries =
        [
            new RegexRule { Id = "r1", Pattern = @"\bfoo\b", Replacement = "bar" },
            new SpokenCommand { Id = "s1", Phrase = "new paragraph", Emits = "\n\n" },
            new PerAppOverride { Id = "p1", ProcessName = "wt.exe" },
            CorrectionPair("c1", "cloud code", "Claude Code"),
        ];

        var result = Run(entries, "foo new paragraph cloud code");

        Assert.Equal("foo new paragraph Claude Code", result.Text);
        Assert.Single(result.Applied);
    }
}
