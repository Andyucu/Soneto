using Soneto.Core.Evaluation;

namespace Soneto.Core.Tests.Evaluation;

/// <summary>
/// Unit tests for <see cref="WordErrorRateCalculator"/> with known, hand-computed WER
/// values — fully testable right now with no corpus/model needed, per work item 12's
/// instructions ("this part is fully testable right now, no corpus needed, it's just an
/// algorithm"). Every hand-computed expectation below is worked out by hand in the test's
/// own comment, not just asserted against the implementation's own output.
/// </summary>
public sealed class WordErrorRateCalculatorTests
{
    [Fact]
    public void Identical_strings_have_zero_WER()
    {
        var result = WordErrorRateCalculator.Compute(
            "the quick brown fox jumps over the lazy dog",
            "the quick brown fox jumps over the lazy dog");

        Assert.Equal(0, result.EditDistance);
        Assert.Equal(9, result.ReferenceTokenCount);
        Assert.Equal(0.0, result.Wer);
    }

    [Fact]
    public void Single_substitution_in_ten_word_sentence_is_ten_percent_WER()
    {
        // Reference: 10 words. Hypothesis: word 5 ("five") replaced with "V" -- exactly one
        // substitution, zero insertions/deletions. WER = 1/10 = 10%.
        var reference = "one two three four five six seven eight nine ten";
        var hypothesis = "one two three four V six seven eight nine ten";

        var result = WordErrorRateCalculator.Compute(reference, hypothesis);

        Assert.Equal(1, result.Substitutions);
        Assert.Equal(0, result.Insertions);
        Assert.Equal(0, result.Deletions);
        Assert.Equal(10, result.ReferenceTokenCount);
        Assert.Equal(0.10, result.Wer, precision: 10);
    }

    [Fact]
    public void Single_deletion_in_ten_word_sentence_is_ten_percent_WER()
    {
        // Reference: 10 words. Hypothesis: word "five" dropped entirely (9 words left) --
        // one deletion. WER = 1/10 = 10%.
        var reference = "one two three four five six seven eight nine ten";
        var hypothesis = "one two three four six seven eight nine ten";

        var result = WordErrorRateCalculator.Compute(reference, hypothesis);

        Assert.Equal(0, result.Substitutions);
        Assert.Equal(0, result.Insertions);
        Assert.Equal(1, result.Deletions);
        Assert.Equal(10, result.ReferenceTokenCount);
        Assert.Equal(0.10, result.Wer, precision: 10);
    }

    [Fact]
    public void Single_insertion_in_ten_word_sentence_is_ten_percent_WER()
    {
        // Reference: 10 words. Hypothesis: an extra word "really" inserted (11 words) --
        // one insertion. WER = 1/10 = 10% (denominator is always the REFERENCE count).
        var reference = "one two three four five six seven eight nine ten";
        var hypothesis = "one two three four five really six seven eight nine ten";

        var result = WordErrorRateCalculator.Compute(reference, hypothesis);

        Assert.Equal(0, result.Substitutions);
        Assert.Equal(1, result.Insertions);
        Assert.Equal(0, result.Deletions);
        Assert.Equal(10, result.ReferenceTokenCount);
        Assert.Equal(0.10, result.Wer, precision: 10);
    }

    [Fact]
    public void Completely_wrong_hypothesis_of_same_length_is_one_hundred_percent_WER()
    {
        // 5 reference words, 5 completely different hypothesis words -- 5 substitutions.
        // WER = 5/5 = 100%.
        var result = WordErrorRateCalculator.Compute("alpha bravo charlie delta echo", "one two three four five");

        Assert.Equal(5, result.Substitutions);
        Assert.Equal(5, result.ReferenceTokenCount);
        Assert.Equal(1.0, result.Wer, precision: 10);
    }

    [Fact]
    public void Empty_hypothesis_against_nonempty_reference_deletes_every_token()
    {
        var result = WordErrorRateCalculator.Compute("one two three four", "");

        Assert.Equal(4, result.Deletions);
        Assert.Equal(0, result.Substitutions);
        Assert.Equal(0, result.Insertions);
        Assert.Equal(4, result.ReferenceTokenCount);
        Assert.Equal(1.0, result.Wer, precision: 10);
    }

    [Fact]
    public void Empty_reference_and_empty_hypothesis_is_zero_WER_not_undefined()
    {
        var result = WordErrorRateCalculator.Compute("", "");

        Assert.Equal(0, result.ReferenceTokenCount);
        Assert.Equal(0, result.EditDistance);
        Assert.Equal(0.0, result.Wer);
    }

    [Fact]
    public void Empty_reference_with_nonempty_hypothesis_is_positive_infinity_not_zero()
    {
        // Degenerate case explicitly called out in the class doc comment: WER is undefined
        // (not zero) when there's nothing to compare against but the hypothesis isn't empty.
        var result = WordErrorRateCalculator.Compute("", "some words here");

        Assert.Equal(0, result.ReferenceTokenCount);
        Assert.True(double.IsPositiveInfinity(result.Wer));
    }

    [Fact]
    public void Tokenization_lowercases_before_comparing()
    {
        var result = WordErrorRateCalculator.Compute("The Quick Brown Fox", "the quick brown fox");

        Assert.Equal(0, result.EditDistance);
    }

    [Fact]
    public void Tokenization_strips_punctuation_before_comparing()
    {
        var result = WordErrorRateCalculator.Compute(
            "Hello, world! How are you?",
            "hello world how are you");

        Assert.Equal(0, result.EditDistance);
        Assert.Equal(5, result.ReferenceTokenCount);
    }

    [Fact]
    public void Tokenization_preserves_diacritics_as_part_of_the_word()
    {
        // Romanian comma-below diacritics must NOT be stripped as "punctuation" -- doing so
        // would make WER blind to exactly the accuracy differences it exists to measure
        // (e.g. "ș" vs plain "s" is a real, meaningful ASR error for Romanian).
        var tokens = WordErrorRateCalculator.Tokenize("Ștefan cel Mare, șoseaua Ștefan cel Mare.");

        Assert.Equal(["ștefan", "cel", "mare", "șoseaua", "ștefan", "cel", "mare"], tokens);

        // A diacritic-stripped hypothesis is a real, counted substitution error, not silently
        // treated as equal to the correctly-diacritic'd reference.
        var result = WordErrorRateCalculator.Compute("Ștefan cel Mare", "Stefan cel Mare");
        Assert.Equal(1, result.Substitutions);
        Assert.Equal(1.0 / 3.0, result.Wer, precision: 10);
    }

    [Fact]
    public void Multiple_mixed_edits_produce_the_expected_breakdown()
    {
        // Reference (6 tokens): "a b c d e f"
        // Hypothesis:            "a x c e f g"
        // Alignment: a=a (match), b->x (substitution), c=c (match), d deleted,
        //            e=e (match), f=f (match), g inserted at the end.
        // Edit distance = 1 substitution + 1 deletion + 1 insertion = 3. WER = 3/6 = 50%.
        var result = WordErrorRateCalculator.Compute("a b c d e f", "a x c e f g");

        Assert.Equal(1, result.Substitutions);
        Assert.Equal(1, result.Deletions);
        Assert.Equal(1, result.Insertions);
        Assert.Equal(3, result.EditDistance);
        Assert.Equal(6, result.ReferenceTokenCount);
        Assert.Equal(0.5, result.Wer, precision: 10);
    }

    [Fact]
    public void Pretokenized_overload_matches_string_overload()
    {
        string[] reference = ["one", "two", "three"];
        string[] hypothesis = ["one", "two"];

        var result = WordErrorRateCalculator.Compute(reference, hypothesis);

        Assert.Equal(1, result.Deletions);
        Assert.Equal(3, result.ReferenceTokenCount);
    }
}
