using Soneto.Core.Abstractions;
using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Phase 2 work item 5 (§2.5/§2.11): tests for <see cref="RegexRuleProcessor"/> (order 50).
///
/// <para>
/// The headline property under test here is the DELIBERATE ASYMMETRY with
/// <see cref="DictionaryEngineProcessor"/> (order 40): that processor's
/// <c>NoCascading_ReplacementOutputIsNotReMatched</c>-style tests (see
/// <c>DictionaryEngineProcessorTests</c>) prove correction pairs never cascade -- a match's own
/// replacement text is never re-fed through the trie. This processor proves the OPPOSITE:
/// regex rules DO cascade -- each rule runs against the OUTPUT of the previous regex rule, by
/// design (§2.5's "Regex rules run as a separate, later pass"). See
/// <see cref="RegexRules_CascadeDeliberately_UnlikeCorrectionPairs"/> below.
/// </para>
/// </summary>
public class RegexRuleProcessorTests
{
    private static DictionaryEntry Rule(string id, string pattern, string replacement, bool enabled = true) =>
        new RegexRule { Id = id, Pattern = pattern, Replacement = replacement, Enabled = enabled };

    private static PostProcessResult Run(
        IEnumerable<DictionaryEntry> entries, string text, bool enabled = true, IReadOnlyList<AppliedRule>? applied = null) =>
        new RegexRuleProcessor(entries, enabled).Process(new PostProcessResult(text, applied ?? []));

    // ------------------------------------------------------------------
    // Malformed pattern rejected at CONSTRUCTION time, not at first Process call.
    // ------------------------------------------------------------------

    [Fact]
    public void MalformedPattern_RejectedAtConstruction_NotAtFirstProcessCall()
    {
        var entries = new[] { Rule("r1", "(unbalanced", "x") };

        var ex = Assert.Throws<ArgumentException>(() => new RegexRuleProcessor(entries));

        Assert.Contains("r1", ex.Message);
        Assert.Contains("(unbalanced", ex.Message);
    }

    [Fact]
    public void MalformedPattern_ConstructorThrows_ProcessNeverGetsCalled()
    {
        // Explicitly demonstrate the constructor itself throws -- Process is never reached.
        DictionaryEntry[] entries = [Rule("bad", "[a-", "x")];

        Assert.Throws<ArgumentException>(() =>
        {
            var processor = new RegexRuleProcessor(entries);
            // If we get here, construction did NOT throw -- test should have already failed.
            processor.Process(new PostProcessResult("anything", []));
        });
    }

    // ------------------------------------------------------------------
    // Basic substitution with capture groups (plan's own literal example).
    // ------------------------------------------------------------------

    [Fact]
    public void CaptureGroupSubstitution_ReplacesUsingMatchedGroupValue()
    {
        var entries = new[] { Rule("r1", @"\bIS (\d+)\b", "IS $1") };
        // Note: this is the plan's literal example; "IS $1" round-trips to the same text for a
        // pattern that already has a space, but proves capture-group resolution genuinely works
        // (not literal string replacement) since the replacement text is built from the captured
        // group, not copy-pasted from the pattern.
        var result = Run(entries, "please check IS 42 now");

        Assert.Equal("please check IS 42 now", result.Text);
        Assert.Single(result.Applied);
        Assert.Equal("IS 42", result.Applied[0].From);
        Assert.Equal("IS 42", result.Applied[0].To);
    }

    [Fact]
    public void CaptureGroupSubstitution_ReordersOrTransformsUsingCapturedGroup()
    {
        var entries = new[] { Rule("r1", @"\bIS(\d+)\b", "IS $1") };
        var result = Run(entries, "please check IS42 now");

        Assert.Equal("please check IS 42 now", result.Text);
        Assert.Single(result.Applied);
        Assert.Equal("IS42", result.Applied[0].From);
        Assert.Equal("IS 42", result.Applied[0].To);
    }

    // ------------------------------------------------------------------
    // Deliberate cascading -- the headline asymmetry with DictionaryEngineProcessor.
    // ------------------------------------------------------------------

    [Fact]
    public void RegexRules_CascadeDeliberately_UnlikeCorrectionPairs()
    {
        // Rule A's output ("bar baz") contains text Rule B's pattern matches ("baz").
        // Contrast directly with DictionaryEngineProcessorTests' equivalent
        // NoCascading_ReplacementOutputIsNotReMatched-style test, which proves correction pairs
        // do NOT do this -- this asymmetry is deliberate, per §2.5, not a bug in either class.
        var entries = new[]
        {
            Rule("a", "foo", "bar baz"),
            Rule("b", "baz", "qux"),
        };

        var result = Run(entries, "foo");

        // Rule B's replacement DOES fire against Rule A's output.
        Assert.Equal("bar qux", result.Text);
        Assert.Equal(2, result.Applied.Count);
        Assert.Contains(result.Applied, a => a.Processor == "RegexRule" && a.Rule == "a" && a.From == "foo" && a.To == "bar baz");
        Assert.Contains(result.Applied, a => a.Processor == "RegexRule" && a.Rule == "b" && a.From == "baz" && a.To == "qux");
    }

    // ------------------------------------------------------------------
    // Rule order matters and is respected (constructor's IEnumerable order).
    // ------------------------------------------------------------------

    [Fact]
    public void RuleOrder_AppliedInConstructorOrder_AToB()
    {
        // Order: replace "cat" -> "dog", THEN replace "dog" -> "fish".
        // Applying A then B turns "cat" into "fish" via the intermediate "dog".
        var entries = new[]
        {
            Rule("a", "cat", "dog"),
            Rule("b", "dog", "fish"),
        };

        var result = Run(entries, "cat");

        Assert.Equal("fish", result.Text);
    }

    [Fact]
    public void RuleOrder_AppliedInConstructorOrder_ReversedProducesDifferentResult()
    {
        // Same two rules, reversed order: replace "dog" -> "fish" first (no "dog" present yet,
        // no-op), THEN replace "cat" -> "dog". Final result is "dog", not "fish" -- proving
        // order is genuinely respected, not coincidentally identical either way.
        var entries = new[]
        {
            Rule("b", "dog", "fish"),
            Rule("a", "cat", "dog"),
        };

        var result = Run(entries, "cat");

        Assert.Equal("dog", result.Text);
    }

    // ------------------------------------------------------------------
    // Multiple occurrences of the same rule's pattern within one transcript.
    // ------------------------------------------------------------------

    [Fact]
    public void MultipleOccurrences_OfSameRule_AllReplaced_OneAppliedRulePerOccurrence()
    {
        var entries = new[] { Rule("r1", @"\bIS (\d+)\b", "IS $1") };
        var result = Run(entries, "IS 4 and IS 7 today");

        Assert.Equal("IS 4 and IS 7 today", result.Text);
        Assert.Equal(2, result.Applied.Count);
        Assert.Equal("IS 4", result.Applied[0].From);
        Assert.Equal("IS 4", result.Applied[0].To);
        Assert.Equal("IS 7", result.Applied[1].From);
        Assert.Equal("IS 7", result.Applied[1].To);
    }

    [Fact]
    public void MultipleOccurrences_WithActualTransformation_AllReplaced()
    {
        var entries = new[] { Rule("r1", @"\bIS(\d+)\b", "IS $1") };
        var result = Run(entries, "check IS4 then IS9 please");

        Assert.Equal("check IS 4 then IS 9 please", result.Text);
        Assert.Equal(2, result.Applied.Count);
        Assert.Equal("IS4", result.Applied[0].From);
        Assert.Equal("IS 4", result.Applied[0].To);
        Assert.Equal("IS9", result.Applied[1].From);
        Assert.Equal("IS 9", result.Applied[1].To);
    }

    // ------------------------------------------------------------------
    // Bounded match timeout -- a catastrophic-backtracking pattern must not hang the chain.
    // ------------------------------------------------------------------

    [Fact]
    public void CatastrophicBacktrackingPattern_TimesOutPromptly_RuleSkipped_RestOfChainUnaffected()
    {
        // (a+)+$ against a long run of "a"s with a trailing non-matching char is the textbook
        // catastrophic-backtracking construct -- without a bounded Regex.MatchTimeout this would
        // hang indefinitely (Regex.InfiniteMatchTimeout). A second, well-behaved rule proves a
        // timeout on one rule doesn't corrupt or block the rest of the chain.
        var adversarialInput = new string('a', 40) + "!";
        var entries = new[]
        {
            Rule("bad", "(a+)+$", "X"),
            Rule("good", "!", "?"),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = Run(entries, adversarialInput);
        sw.Stop();

        // Generous upper bound relative to the processor's own 250ms per-rule timeout budget --
        // proves Process returns promptly rather than hanging.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Process took {sw.Elapsed} -- expected it to return promptly via the bounded match timeout.");

        // The pathological rule was skipped for this call (text unaffected by it), but the
        // well-behaved rule after it still ran.
        Assert.Equal(new string('a', 40) + "?", result.Text);
        Assert.Single(result.Applied);
        Assert.Equal("good", result.Applied[0].Rule);
    }

    // ------------------------------------------------------------------
    // Disabled entries never fire.
    // ------------------------------------------------------------------

    [Fact]
    public void DisabledRule_NeverFires()
    {
        var entries = new[] { Rule("r1", "foo", "bar", enabled: false) };
        var result = Run(entries, "foo bar foo");

        Assert.Equal("foo bar foo", result.Text);
        Assert.Empty(result.Applied);
    }

    // ------------------------------------------------------------------
    // Other entry types ignored -- no throw, no effect.
    // ------------------------------------------------------------------

    [Fact]
    public void OtherEntryTypes_AreIgnored_NoThrowNoEffect()
    {
        DictionaryEntry[] entries =
        [
            new CorrectionPair { Id = "cp1", From = "cloud code", To = "Claude Code" },
            new VocabularyTerm { Id = "v1", Term = "webMethods" },
            new SpokenCommand { Id = "sc1", Phrase = "new paragraph", Emits = "\n\n" },
            new PerAppOverride { Id = "pa1", ProcessName = "wt.exe" },
        ];

        var result = Run(entries, "cloud code and webmethods, new paragraph please");

        Assert.Equal("cloud code and webmethods, new paragraph please", result.Text);
        Assert.Empty(result.Applied);
    }

    // ------------------------------------------------------------------
    // AppliedRule accumulation -- prior chain-stage entries preserved, ours appended.
    // ------------------------------------------------------------------

    [Fact]
    public void AppliedRule_AccumulatesOnTopOfPriorStages()
    {
        var entries = new[] { Rule("r1", "foo", "bar") };
        var existing = new AppliedRule[] { new("Earlier", "e1", "x", "y") };

        var result = Run(entries, "foo", applied: existing);

        Assert.Equal(2, result.Applied.Count);
        Assert.Contains(result.Applied, a => a is { Processor: "Earlier", Rule: "e1", From: "x", To: "y" });
        Assert.Contains(result.Applied, a => a is { Processor: "RegexRule", Rule: "r1", From: "foo", To: "bar" });
    }

    // ------------------------------------------------------------------
    // No-op passthrough.
    // ------------------------------------------------------------------

    [Fact]
    public void Disabled_Processor_NoOpPassthrough()
    {
        var entries = new[] { Rule("r1", "foo", "bar") };
        var result = Run(entries, "foo", enabled: false);

        Assert.Equal("foo", result.Text);
        Assert.Empty(result.Applied);
    }

    [Fact]
    public void EmptyInput_NoOpPassthrough()
    {
        var entries = new[] { Rule("r1", "foo", "bar") };
        var result = Run(entries, "");

        Assert.Equal("", result.Text);
        Assert.Empty(result.Applied);
    }

    [Fact]
    public void NoRuleMatches_NoOpPassthrough_TextUnchanged_AppliedUnchanged()
    {
        var entries = new[] { Rule("r1", "foo", "bar") };
        var existing = new AppliedRule[] { new("Earlier", "e1", "x", "y") };
        var result = Run(entries, "nothing matches here", applied: existing);

        Assert.Equal("nothing matches here", result.Text);
        Assert.Single(result.Applied);
        Assert.Equal(existing[0], result.Applied[0]);
    }
}
