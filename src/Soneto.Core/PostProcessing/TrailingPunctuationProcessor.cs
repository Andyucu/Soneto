using Soneto.Core.Abstractions;

namespace Soneto.Core.PostProcessing;

/// <summary>
/// Phase 4 item 3 (§4.4): order 85 stage, NOT part of the default chain -- only ever included
/// when a matching, enabled <see cref="Dictionary.PerAppOverride"/> profile has
/// <see cref="Dictionary.PerAppOverride.TrailingPunctuation"/> == <c>true</c> for the focused
/// app (see <see cref="PostProcessorChain"/>'s own doc comment for how/where that selection
/// happens). Before this item, <c>TrailingPunctuation</c> was schema-only (Phase 2) with zero
/// real consumer anywhere in the codebase -- this is the first one.
///
/// <para>
/// <b>Deliberately minimal:</b> appends a single <c>.</c> immediately after the last
/// non-whitespace character, unless that character is already one of <c>. ! ? : ; ,</c>. Does
/// not attempt to distinguish a genuinely finished sentence from a trailing clause/abbreviation
/// -- same "narrowly scoped, real, safe-additive" judgment call as
/// <see cref="AutoCapitalizeProcessor"/>'s own doc comment documents.
/// </para>
///
/// <para>
/// <b>Order 85 -- BEFORE <see cref="TrailingSpaceProcessor"/> (90), deliberately.</b>
/// <see cref="TrailingSpaceProcessor"/>'s own established contract is "runs last, appends a
/// single space if the transcript ends in a non-whitespace character" (see its own doc
/// comment). Running this processor after it would risk splitting that contract across two
/// stages in a confusing order; running it before means the period this processor inserts
/// becomes the new "last non-whitespace character" that <see cref="TrailingSpaceProcessor"/>
/// then correctly appends its inter-utterance space after -- composes cleanly with zero special
/// casing needed in either processor for the other's existence.
/// </para>
///
/// <para>
/// <b>Trailing whitespace preserved verbatim:</b> the period is inserted right after the last
/// non-whitespace character, not naively appended to the whole string -- so any trailing
/// whitespace the transcript already had (there shouldn't normally be any at this point in the
/// chain, but this processor doesn't assume that) survives unchanged rather than ending up
/// after the newly-inserted period.
/// </para>
/// </summary>
public sealed class TrailingPunctuationProcessor : IPostProcessor
{
    public int Order => 85;
    public string Name => "TrailingPunctuation";

    // Review fix (Phase 4 item 3 code review): fixed rule id, same reasoning as
    // AutoCapitalizeProcessor.RuleId -- this processor has no dictionary-authored entries of
    // its own to key an AppliedRule off.
    private const string RuleId = "trailingPeriod";

    private static readonly char[] TerminalPunctuation = ['.', '!', '?', ':', ';', ','];

    /// <summary>
    /// Review fix (Phase 4 item 3 code review): records an <see cref="AppliedRule"/> whenever a
    /// period is actually inserted, same reasoning/precedent as
    /// <see cref="AutoCapitalizeProcessor.Process"/>'s own doc comment -- <c>From</c> is the
    /// empty string (this is an insertion, not a substitution), <c>To</c> is the inserted
    /// <c>"."</c>.
    /// </summary>
    public PostProcessResult Process(PostProcessResult input)
    {
        if (string.IsNullOrEmpty(input.Text))
            return input;

        int lastNonWhitespace = input.Text.Length - 1;
        while (lastNonWhitespace >= 0 && char.IsWhiteSpace(input.Text[lastNonWhitespace]))
            lastNonWhitespace--;

        if (lastNonWhitespace < 0)
            return input; // Whole text is whitespace -- nothing to punctuate.

        if (Array.IndexOf(TerminalPunctuation, input.Text[lastNonWhitespace]) >= 0)
            return input; // Already ends in terminal punctuation.

        string newText =
            input.Text[..(lastNonWhitespace + 1)] + "." + input.Text[(lastNonWhitespace + 1)..];

        var combined = new List<AppliedRule>(input.Applied.Count + 1);
        combined.AddRange(input.Applied);
        combined.Add(new AppliedRule(Name, RuleId, string.Empty, "."));
        return new PostProcessResult(newText, combined);
    }
}
