using System.Text.RegularExpressions;
using Soneto.Core.Abstractions;
using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Independent verification tests written by a reviewer (not the implementer) to double-check
/// the claims made about <see cref="RegexRuleProcessor"/>, using different example patterns and
/// scenarios from the implementer's own test file
/// (<see cref="RegexRuleProcessorTests"/>).
/// </summary>
public class RegexRuleProcessorIndependentVerificationTests
{
    private static DictionaryEntry Rule(string id, string pattern, string replacement, bool enabled = true) =>
        new RegexRule { Id = id, Pattern = pattern, Replacement = replacement, Enabled = enabled };

    // ------------------------------------------------------------------
    // 3. Malformed pattern rejected at construction, with a DIFFERENT invalid pattern than the
    // implementer used ("(unbalanced" / "[a-"), and we check the actual exception message text.
    // ------------------------------------------------------------------

    [Fact]
    public void MalformedPattern_UnmatchedCloseBracket_ThrowsAtConstruction_WithRuleIdAndPattern()
    {
        // A totally different kind of invalid pattern: unescaped/unmatched ']' quantifier target
        // is fine in .NET regex (']' alone is literal), so use a genuinely malformed construct
        // instead: an invalid named-group back-reference.
        var entries = new[] { Rule("badgroup", @"(?<name>abc)\k<missing>", "x") };

        var ex = Assert.Throws<ArgumentException>(() => new RegexRuleProcessor(entries));

        Assert.Contains("badgroup", ex.Message);
        Assert.Contains(@"(?<name>abc)\k<missing>", ex.Message);
    }

    [Fact]
    public void MalformedPattern_BadQuantifierRange_ThrowsAtConstruction()
    {
        // {2,1} -- min greater than max -- is rejected by .NET's regex parser.
        var entries = new[] { Rule("badquant", "a{2,1}", "x") };

        var ex = Assert.Throws<ArgumentException>(() => new RegexRuleProcessor(entries));

        Assert.Contains("badquant", ex.Message);
        Assert.Contains("a{2,1}", ex.Message);
    }

    [Fact]
    public void MalformedPattern_SecondRuleInList_StillNamesTheOffendingOne()
    {
        // Make sure the exception genuinely names the FAILING rule, not just any rule -- put a
        // valid rule first, invalid second.
        var entries = new[]
        {
            Rule("good", "cat", "dog"),
            Rule("bad-second", "(unterminated[", "x"),
        };

        var ex = Assert.Throws<ArgumentException>(() => new RegexRuleProcessor(entries));

        Assert.Contains("bad-second", ex.Message);
        Assert.DoesNotContain("\"good\"", ex.Message);
    }

    // ------------------------------------------------------------------
    // 4. Deliberate cascading with a DIFFERENT 3-rule chain than the plan's foo/bar example.
    // Rule 1 output feeds rule 2, whose output feeds rule 3.
    // ------------------------------------------------------------------

    [Fact]
    public void Cascading_ThreeRuleChain_EachFeedsTheNext()
    {
        // "start" -> "middle-token" -> contains "token" -> "END" -> contains "END" -> "final"
        var entries = new[]
        {
            Rule("r1", "start", "middle-token"),
            Rule("r2", "token", "END"),
            Rule("r3", "END", "final"),
        };

        var result = new RegexRuleProcessor(entries).Process(new PostProcessResult("start", []));

        // If all three rules fired in sequence: start -> middle-token -> middle-END -> middle-final
        Assert.Equal("middle-final", result.Text);
        Assert.Equal(3, result.Applied.Count);
        Assert.Equal(("r1", "start", "middle-token"), (result.Applied[0].Rule, result.Applied[0].From, result.Applied[0].To));
        Assert.Equal(("r2", "token", "END"), (result.Applied[1].Rule, result.Applied[1].From, result.Applied[1].To));
        Assert.Equal(("r3", "END", "final"), (result.Applied[2].Rule, result.Applied[2].From, result.Applied[2].To));
    }

    // ------------------------------------------------------------------
    // 5. Capture-group substitution with 2 groups, independently checked character-by-character.
    // ------------------------------------------------------------------

    [Fact]
    public void CaptureGroupSubstitution_TwoGroups_SwappedOrder_IsCorrect()
    {
        var entries = new[] { Rule("swap", @"(\w+)@(\w+)", "$2 at $1") };

        var result = new RegexRuleProcessor(entries).Process(new PostProcessResult("contact jdoe@example please", []));

        Assert.Equal("contact example at jdoe please", result.Text);
        Assert.Single(result.Applied);
        Assert.Equal("jdoe@example", result.Applied[0].From);
        Assert.Equal("example at jdoe", result.Applied[0].To);
    }

    [Fact]
    public void CaptureGroupSubstitution_TwoGroups_MultipleOccurrences_EachSubstitutedIndependently()
    {
        var entries = new[] { Rule("swap", @"(\w+)@(\w+)", "$2 at $1") };

        var result = new RegexRuleProcessor(entries).Process(new PostProcessResult("first alice@wonderland second bob@builder", []));

        Assert.Equal("first wonderland at alice second builder at bob", result.Text);
        Assert.Equal(2, result.Applied.Count);
        Assert.Equal("alice@wonderland", result.Applied[0].From);
        Assert.Equal("wonderland at alice", result.Applied[0].To);
        Assert.Equal("bob@builder", result.Applied[1].From);
        Assert.Equal("builder at bob", result.Applied[1].To);
    }

    // ------------------------------------------------------------------
    // 6. Per-occurrence AppliedRule granularity -- guard against closure-capture bugs where all
    // entries end up showing the same (e.g. last or first) match's data.
    // ------------------------------------------------------------------

    [Fact]
    public void PerOccurrence_ThreeMatchesOfSameRule_EachAppliedRuleHasItsOwnDistinctValues()
    {
        // Pattern with a capture group so each occurrence's substituted text genuinely differs.
        var entries = new[] { Rule("num", @"#(\d+)", "item-$1") };

        var result = new RegexRuleProcessor(entries)
            .Process(new PostProcessResult("see #1 then #22 then #333 done", []));

        Assert.Equal("see item-1 then item-22 then item-333 done", result.Text);
        Assert.Equal(3, result.Applied.Count);

        // Each entry's From/To must correspond to ITS OWN occurrence, not all three showing the
        // same (e.g. first or last) occurrence's values -- this is exactly the closure-capture
        // bug this test is designed to catch.
        Assert.Equal("#1", result.Applied[0].From);
        Assert.Equal("item-1", result.Applied[0].To);

        Assert.Equal("#22", result.Applied[1].From);
        Assert.Equal("item-22", result.Applied[1].To);

        Assert.Equal("#333", result.Applied[2].From);
        Assert.Equal("item-333", result.Applied[2].To);

        // Also assert they are NOT all identical (would indicate a captured-last-value bug).
        Assert.NotEqual(result.Applied[0].From, result.Applied[1].From);
        Assert.NotEqual(result.Applied[1].From, result.Applied[2].From);
        Assert.NotEqual(result.Applied[0].To, result.Applied[2].To);

        // All entries correctly attribute the rule id and processor name.
        Assert.All(result.Applied, a => Assert.Equal("num", a.Rule));
        Assert.All(result.Applied, a => Assert.Equal("RegexRule", a.Processor));
    }

    [Fact]
    public void PerOccurrence_FourMatchesAcrossTwoCascadingRules_AllDistinctAndCorrectlyAttributed()
    {
        // Rule 1 matches 2 times up front; rule 2 then matches the (transformed) output 2 times,
        // exercising per-occurrence granularity together with cascading in one scenario.
        var entries = new[]
        {
            Rule("digits", @"D(\d)", "N$1"),
            Rule("letter", @"N(\d)", "X$1!"),
        };

        var result = new RegexRuleProcessor(entries)
            .Process(new PostProcessResult("D1 gap D2 gap", []));

        Assert.Equal("X1! gap X2! gap", result.Text);
        Assert.Equal(4, result.Applied.Count);

        Assert.Equal(("digits", "D1", "N1"), (result.Applied[0].Rule, result.Applied[0].From, result.Applied[0].To));
        Assert.Equal(("digits", "D2", "N2"), (result.Applied[1].Rule, result.Applied[1].From, result.Applied[1].To));
        Assert.Equal(("letter", "N1", "X1!"), (result.Applied[2].Rule, result.Applied[2].From, result.Applied[2].To));
        Assert.Equal(("letter", "N2", "X2!"), (result.Applied[3].Rule, result.Applied[3].From, result.Applied[3].To));
    }

    // ------------------------------------------------------------------
    // 7. Rule order matters -- a different order-dependent example than the implementer's
    // cat->dog->fish chain.
    // ------------------------------------------------------------------

    [Fact]
    public void RuleOrder_ForwardOrder_ProducesExpectedResult()
    {
        // Rule X turns "red" into "green"; rule Y turns "green" into "blue".
        // Forward order (X then Y): "red" -> "green" -> "blue".
        var entries = new[]
        {
            Rule("x", "red", "green"),
            Rule("y", "green", "blue"),
        };

        var result = new RegexRuleProcessor(entries).Process(new PostProcessResult("red light", []));

        Assert.Equal("blue light", result.Text);
    }

    [Fact]
    public void RuleOrder_ReversedOrder_ProducesDifferentResult()
    {
        // Same two rules, reversed: Y first (no "green" present yet, no-op), then X: "red" -> "green".
        // Final result is "green", not "blue" -- proves the processor honors whatever order is
        // passed in, not a fixed internal order.
        var entries = new[]
        {
            Rule("y", "green", "blue"),
            Rule("x", "red", "green"),
        };

        var result = new RegexRuleProcessor(entries).Process(new PostProcessResult("red light", []));

        Assert.Equal("green light", result.Text);
    }

    // ------------------------------------------------------------------
    // 8. Other entry types silently ignored; disabled rules never fire.
    // ------------------------------------------------------------------

    [Fact]
    public void CorrectionPairAndSpokenCommand_PassedToConstructor_NoThrow_NoEffect()
    {
        DictionaryEntry[] entries =
        [
            new CorrectionPair { Id = "cp-verify", From = "teh", To = "the" },
            new SpokenCommand { Id = "sc-verify", Phrase = "scratch that", Emits = "" },
        ];

        // Constructing must not throw even though neither is a RegexRule.
        var processor = new RegexRuleProcessor(entries);

        var result = processor.Process(new PostProcessResult("teh scratch that quick fox", []));

        Assert.Equal("teh scratch that quick fox", result.Text);
        Assert.Empty(result.Applied);
    }

    [Fact]
    public void DisabledRegexRule_PatternPresentInText_DoesNotFire()
    {
        var entries = new[] { Rule("disabled-verify", @"\bfoo(bar)?\b", "REPLACED", enabled: false) };

        var result = new RegexRuleProcessor(entries)
            .Process(new PostProcessResult("this has foo and foobar in it", []));

        Assert.Equal("this has foo and foobar in it", result.Text);
        Assert.Empty(result.Applied);
    }

    [Fact]
    public void DisabledRule_MixedWithEnabledRule_OnlyEnabledFires()
    {
        var entries = new[]
        {
            Rule("disabled-verify", "cat", "SHOULD-NOT-APPEAR", enabled: false),
            Rule("enabled-verify", "dog", "puppy"),
        };

        var result = new RegexRuleProcessor(entries)
            .Process(new PostProcessResult("cat and dog", []));

        Assert.Equal("cat and puppy", result.Text);
        Assert.Single(result.Applied);
        Assert.Equal("enabled-verify", result.Applied[0].Rule);
    }

    // ------------------------------------------------------------------
    // 9. Sanity-check compiled-once behavior: the same processor instance, invoked many times
    // in a loop, produces correct results each time -- consistent with a cached compiled Regex
    // rather than any per-call recompilation-with-state-corruption.
    // ------------------------------------------------------------------

    [Fact]
    public void RepeatedProcessCalls_OnSameProcessorInstance_ProduceConsistentResults()
    {
        var entries = new[] { Rule("repeat", @"(\w+)-(\w+)", "$2-$1") };
        var processor = new RegexRuleProcessor(entries);

        for (var i = 0; i < 50; i++)
        {
            var result = processor.Process(new PostProcessResult("alpha-beta", []));
            Assert.Equal("beta-alpha", result.Text);
            Assert.Single(result.Applied);
            Assert.Equal("alpha-beta", result.Applied[0].From);
            Assert.Equal("beta-alpha", result.Applied[0].To);
        }
    }
}
