using System.Text.RegularExpressions;
using Soneto.Core.Abstractions;

namespace Soneto.Core.PostProcessing;

/// <summary>
/// Order 30 stage of the plan §1.7 post-processing chain: collapses runs of HORIZONTAL
/// whitespace (spaces/tabs) only, trims leading/trailing horizontal whitespace, removes any
/// space immediately before <c>,.!?;:</c>, and ensures exactly one space after those
/// punctuation marks (when followed by non-whitespace, non-end-of-string content).
///
/// <para>
/// <b>Newlines are always significant, never collapsed:</b> this implementation treats
/// <c>\n</c> as significant -- it is never touched by the horizontal-whitespace collapse/trim
/// logic, and runs of 3+ consecutive newlines are capped down to exactly 2 (a paragraph break),
/// while 1 or 2 consecutive newlines pass through untouched. Originally (plan §1.7, Phase 1) this
/// mattered because this stage ran immediately after the fixed-table spoken-commands processor
/// (order 20 then, before this stage's order 30) which emitted literal <c>\n</c>/<c>\n\n</c>
/// control characters that a naive cleaner would have collapsed back into spaces. As of Phase 2
/// item 6, spoken commands moved to <see cref="Dictionary.SpokenCommandsExtensionProcessor"/> at
/// order 60 -- AFTER this stage, not before -- so this processor no longer sees freshly-emitted
/// command newlines at all; it only ever sees newlines that were already literally present in the
/// input text (e.g. pasted/typed multi-line text passed through dictation). This processor's own
/// newline-preserving behaviour is unchanged and still correct for that case; the freshly-emitted-
/// command-newline cleanup this comment used to describe is now <c>SpokenCommandsExtensionProcessor</c>'s
/// own responsibility (see its doc comment for the full investigation of that ordering change).
/// </para>
/// </summary>
public sealed class WhitespaceCleanerProcessor : IPostProcessor
{
    public int Order => 30;
    public string Name => "WhitespaceCleaner";

    private readonly bool _enabled;

    // Horizontal whitespace only: space, tab (and other Unicode horizontal space, but not \n/\r).
    private static readonly Regex HorizontalWhitespaceRun = new(@"[^\S\n]+", RegexOptions.Compiled);
    private static readonly Regex ThreeOrMoreNewlines = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforePunctuation = new(@"[^\S\n]+(?=[,.!?;:])", RegexOptions.Compiled);
    private static readonly Regex MissingSpaceAfterPunctuation =
        new(@"(?<=[,.!?;:])(?![^\S\n]|\n|$)", RegexOptions.Compiled);
    private static readonly Regex EdgeHorizontalWhitespace =
        new(@"^[^\S\n]+|[^\S\n]+$", RegexOptions.Compiled | RegexOptions.Multiline);

    public WhitespaceCleanerProcessor(bool enabled = true)
    {
        _enabled = enabled;
    }

    public PostProcessResult Process(PostProcessResult input)
    {
        if (!_enabled || string.IsNullOrEmpty(input.Text))
            return input;

        var text = input.Text;

        // 1. Collapse horizontal whitespace runs (spaces/tabs), leaving \n alone.
        text = HorizontalWhitespaceRun.Replace(text, " ");

        // 2. Trim leading/trailing horizontal whitespace around the whole string and around
        //    each line (so "line\n  \nline" doesn't leave stray spaces on blank lines).
        //    Newlines are NOT trimmed here -- they are significant (a leading/trailing
        //    "new line"/"new paragraph" command must still produce its control character in
        //    the output, not be silently swallowed).
        //
        //    This MUST run before step 3's newline cap: input like "\n \n \n" has no three
        //    literally-adjacent '\n' characters until the intervening single spaces on those
        //    otherwise-blank lines are stripped here first. Capping before trimming would
        //    miss whitespace-separated newline runs entirely (they'd never become adjacent),
        //    silently letting 3+ "blank-ish" lines through uncapped -- exactly the ordering
        //    bug this comment used to have.
        text = EdgeHorizontalWhitespace.Replace(text, string.Empty);

        // 3. Cap 3+ consecutive newlines down to exactly 2 (paragraph break). Must run after
        //    step 2 above (see that step's comment).
        text = ThreeOrMoreNewlines.Replace(text, "\n\n");

        // 4. Remove space before punctuation, ensure exactly one space after.
        text = SpaceBeforePunctuation.Replace(text, string.Empty);
        text = MissingSpaceAfterPunctuation.Replace(text, " ");

        return text == input.Text ? input : input with { Text = text };
    }
}
