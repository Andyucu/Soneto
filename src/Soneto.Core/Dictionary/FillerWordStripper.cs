using System.Text.RegularExpressions;
using Soneto.Core.Abstractions;

namespace Soneto.Core.Dictionary;

/// <summary>
/// Phase 2 work item 7 (§2.6/§2.11, and originally §6.4 of
/// <c>Docs/dictation-app-build-plan.md</c>): order 70 stage of the post-processing chain --
/// strips a small, fixed(-ish) set of EN/RO filler words ("um", "ăăă", ...) out of the
/// transcript entirely.
///
/// <para>
/// <b>Not backed by <c>dictionary.json</c> -- unlike items 4/5/6.</b> There is no "filler word"
/// entry type in <see cref="DictionaryEntry"/>'s schema (§2.4) at all: per the plan's own words,
/// filler stripping is "a fixed-ish list of EN/RO filler tokens... extend from real usage, this
/// is genuinely a short, low-risk list unlike spoken commands." This class therefore does NOT
/// take an <c>IEnumerable&lt;DictionaryEntry&gt;</c> the way <see cref="DictionaryEngineProcessor"/>/
/// <see cref="RegexRuleProcessor"/>/<see cref="SpokenCommandsExtensionProcessor"/> do -- its
/// simple <see cref="FillerWordStripper(bool)"/> constructor just takes an <c>enabled</c> flag,
/// matching Phase 1's <c>TrailingSpaceProcessor(bool enabled = true)</c> convention, and falls
/// back to a hardcoded, built-in default filler-word list (<see cref="DefaultFillerWords"/>). A
/// second constructor overload accepts a caller-supplied word list instead, for testability and
/// because "extend from real usage" implies this list will grow over time -- but note that
/// overload REPLACES the built-in defaults rather than merging with them (see its own doc
/// comment); there is no dictionary-file-backed override path for this processor at all.
/// </para>
///
/// <para>
/// <b>Default filler-word list, and why it's scoped this small:</b> <c>um</c>, <c>uh</c> (EN)
/// and <c>ăăă</c>, <c>păi</c> (RO) -- exactly the plan's own named examples (<c>um</c>, <c>ăăă</c>)
/// plus the two it explicitly calls out as "worth adding" (<c>uh</c>, <c>păi</c>). Deliberately
/// NOT a large invented list of dozens of filler words: the plan frames this as a small,
/// low-risk starting set meant to grow from observed real usage, not something to over-engineer
/// up front.
/// </para>
///
/// <para>
/// <b>Full-token-boundary requirement (same discipline as <see cref="AhoCorasickAutomaton{TValue}"/>'s
/// rule 6 and <see cref="SpokenCommandsExtensionProcessor"/>'s punctuation-boundary regex):</b> a
/// filler word is only stripped when it is a free-standing token -- bounded on both sides by
/// either the start/end of the string or a non-letter/non-digit character (<c>\p{L}</c>/<c>\p{N}</c>
/// Unicode categories, mirroring <see cref="char.IsLetterOrDigit(char)"/>'s intent). Matching is
/// case-insensitive. The plan's own adversarial example is the guiding test: <c>"album"</c> must
/// NEVER have its embedded "um" stripped, because "um" there is not bounded by a non-letter on its
/// left (the preceding character is "b", a letter).
/// </para>
///
/// <para>
/// <b>Order 70 -- runs AFTER <c>WhitespaceCleanerProcessor</c> (order 30), same hazard item 6's
/// <see cref="SpokenCommandsExtensionProcessor"/> (order 60) already found and had to fix for
/// itself, and the same principle applies here: this processor runs after the general whitespace
/// cleaner already did its one pass, so nothing downstream will ever clean up whatever gap
/// removing a filler word's own text leaves behind -- this processor must clean up after its own
/// edits itself, narrowly, rather than relying on cleanup that already happened earlier in the
/// chain.</b> Concretely, removing just the filler word's own characters (and nothing else) can
/// leave three kinds of artifact, all handled by a small, explicitly-scoped cleanup pass that runs
/// once, after every filler-word removal, over the whole (already-modified) text:
/// <list type="bullet">
/// <item><b>A run of 2+ commas</b> (with optional horizontal whitespace between them), e.g.
/// <c>"well, um, I think"</c> -&gt; (after removing "um") -&gt; <c>"well, , I think"</c> -&gt; collapsed
/// to a single comma -&gt; <c>"well, I think"</c>.</item>
/// <item><b>A run of 2+ horizontal whitespace characters</b> (space/tab, deliberately never
/// <c>\n</c>, mirroring <see cref="SpokenCommandsExtensionProcessor"/>'s own <c>[^\S\n]</c>
/// convention), e.g. <c>"I um think"</c> -&gt; (after removing "um") -&gt; <c>"I  think"</c> -&gt;
/// collapsed to one space -&gt; <c>"I think"</c>.</item>
/// <item><b>A stray leading or trailing comma (with any adjacent horizontal whitespace), or a
/// stray leading or trailing plain horizontal-whitespace run</b>, left when the removed filler
/// word was the very first or last token of the transcript, e.g. <c>"um, I think so"</c> -&gt;
/// (after removing "um") -&gt; <c>", I think so"</c> -&gt; leading comma+space trimmed -&gt;
/// <c>"I think so"</c>; and <c>"I think so, um"</c> -&gt; (after removing "um") -&gt;
/// <c>"I think so, "</c> -&gt; trailing comma+space trimmed -&gt; <c>"I think so"</c>.</item>
/// <item><b>A trailing comma left directly before terminal sentence punctuation</b>
/// (<c>.</c>/<c>!</c>/<c>?</c>), left when the removed filler word was the last WORD of the
/// utterance but the ASR had already appended a terminal mark after it, e.g.
/// <c>"I think, um."</c> -&gt; (after removing "um") -&gt; <c>"I think, ."</c> -&gt; the
/// now-dangling <c>", "</c> immediately before the terminal mark is collapsed away -&gt;
/// <c>"I think."</c>. Added specifically because trailing off with a filler word right before
/// releasing the push-to-talk key -- with the ASR then appending a terminal period at
/// end-of-utterance -- is a realistic real-world shape for this app's own usage pattern (a user
/// audibly hesitating right as they let go of the hotkey), not a hypothetical edge case.</item>
/// </list>
/// This is deliberately scoped down, not an attempt to handle every conceivable punctuation
/// permutation: it only reasons about commas and horizontal whitespace immediately touching the
/// removed word's own gap (plus the one terminal-punctuation case above), because a
/// comma-set-off pause is the realistic shape a filler word takes when spoken mid-utterance (the
/// same reasoning <see cref="SpokenCommandsExtensionProcessor"/> applies to its own
/// punctuation-boundary rule) -- semicolons, colons, or other non-terminal punctuation directly
/// touching a removed filler word are NOT specially collapsed by this pass (an
/// unrealistic-to-encounter shape for a filler word, e.g. <c>"well; um; I think"</c>, is left with
/// whatever punctuation surrounds the gap rather than being specially cleaned up). One further gap
/// is KNOWINGLY left unhandled: an ASYMMETRIC single comma on only one side of a removed filler
/// word (e.g. <c>"I um, think"</c> -&gt; <c>"I , think"</c>) is judged genuinely low-frequency --
/// it requires an unusual ASR punctuation decision (a comma landing on only one side of a filler
/// word) -- and is shipped documented-but-unfixed rather than adding a broader punctuation-aware
/// cleanup pass to chase it.
/// </para>
///
/// <para>
/// <b><see cref="AppliedRule"/> convention -- <c>To</c> is always empty string, unlike items
/// 4/5/6:</b> because this processor only ever REMOVES text, never replaces it with different
/// text, each populated <see cref="AppliedRule"/>'s <see cref="AppliedRule.To"/> is
/// <see cref="string.Empty"/> and <see cref="AppliedRule.From"/> is the real matched filler-word
/// text exactly as it appeared in the input (preserving whatever casing the ASR emitted, e.g.
/// <c>"Um"</c>), not the canonical lowercase form from the filler-word list. This is a deliberate,
/// documented deviation from items 4/5/6, which always populate a non-empty <c>To</c>.
/// </para>
/// </summary>
public sealed class FillerWordStripper : IPostProcessor
{
    public int Order => 70;
    public string Name => "FillerWordStripper";

    /// <summary>
    /// The built-in default filler-word list -- see the class doc comment's "Default
    /// filler-word list" paragraph for why exactly these four and no more.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultFillerWords = ["um", "uh", "ăăă", "păi"];

    // Matches one comma-run collapse: two or more commas (optionally separated by horizontal
    // whitespace) collapse to a single comma. Applied BEFORE the plain horizontal-whitespace
    // collapse below, so a comma-run's interior whitespace never needs a separate pass.
    private static readonly Regex CommaRun = new(@",(?:[^\S\n]*,)+", RegexOptions.Compiled);

    // Collapses a run of 2+ horizontal whitespace characters (never \n) down to a single space --
    // mirrors SpokenCommandsExtensionProcessor's own [^\S\n] horizontal-whitespace convention.
    private static readonly Regex HorizontalWhitespaceRun = new(@"[^\S\n]{2,}", RegexOptions.Compiled);

    // A single leading/trailing comma (with any adjacent horizontal whitespace) left over from a
    // filler word that was the first/last token of the transcript.
    private static readonly Regex LeadingComma = new(@"^[^\S\n]*,[^\S\n]*", RegexOptions.Compiled);
    private static readonly Regex TrailingComma = new(@"[^\S\n]*,[^\S\n]*$", RegexOptions.Compiled);

    // A stray leading/trailing plain horizontal-whitespace run (no comma involved), left over
    // from a filler word removed at the very start/end of the transcript.
    private static readonly Regex LeadingWhitespace = new(@"^[^\S\n]+", RegexOptions.Compiled);
    private static readonly Regex TrailingWhitespace = new(@"[^\S\n]+$", RegexOptions.Compiled);

    // A trailing comma (with any adjacent horizontal whitespace) left directly before terminal
    // sentence punctuation -- e.g. "I think, um." -> (after removing "um") -> "I think, ." ->
    // this collapses the dangling ", " away, leaving just "I think.". See the class doc
    // comment's ordering-hazard paragraph for why this specific shape is realistic for this app.
    private static readonly Regex CommaBeforeTerminalPunctuation =
        new(@",[^\S\n]*(?=[.!?])", RegexOptions.Compiled);

    private readonly bool _enabled;
    private readonly Regex _fillerPattern;

    /// <summary>
    /// Uses <see cref="DefaultFillerWords"/> as the filler-word list.
    /// </summary>
    public FillerWordStripper(bool enabled = true) : this(DefaultFillerWords, enabled)
    {
    }

    /// <summary>
    /// Uses <paramref name="fillerWords"/> as the filler-word list INSTEAD of
    /// <see cref="DefaultFillerWords"/> -- this REPLACES the built-in defaults, it does not merge
    /// with them (kept simple deliberately, per the class doc comment: this processor has no
    /// dictionary-file-backed override/collision policy to mirror the way items 4/5/6 do).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="fillerWords"/> is empty, or contains an empty/whitespace-only
    /// entry -- fail fast, at construction, mirroring this project's established pattern
    /// elsewhere (e.g. <see cref="SpokenCommandsExtensionProcessor"/>'s empty-phrase check).
    /// </exception>
    public FillerWordStripper(IEnumerable<string> fillerWords, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(fillerWords);
        _enabled = enabled;

        var words = fillerWords.ToList();
        if (words.Count == 0)
            throw new ArgumentException("fillerWords must contain at least one entry.", nameof(fillerWords));

        foreach (var word in words)
        {
            if (string.IsNullOrWhiteSpace(word))
                throw new ArgumentException("fillerWords must not contain an empty/whitespace-only entry.", nameof(fillerWords));
        }

        // Longest-first, same defensive precedent as SpokenCommandsExtensionProcessor's
        // phrase-length ordering -- not load-bearing for the current 4 defaults (none overlap),
        // but kept safe as the list grows.
        var alternation = string.Join(
            '|', words.OrderByDescending(w => w.Length).Select(Regex.Escape));

        _fillerPattern = new Regex(
            $@"(?<![\p{{L}}\p{{N}}])(?:{alternation})(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public PostProcessResult Process(PostProcessResult input)
    {
        if (!_enabled || string.IsNullOrEmpty(input.Text))
            return input;

        List<AppliedRule>? applied = null;

        var text = _fillerPattern.Replace(input.Text, match =>
        {
            var ruleId = $"filler.{match.Value.ToLowerInvariant()}";
            (applied ??= []).Add(new AppliedRule(Name, ruleId, match.Value, string.Empty));
            return string.Empty;
        });

        if (applied is null || applied.Count == 0)
            return input;

        // See the class doc comment's ordering-hazard paragraph: this processor runs AFTER
        // WhitespaceCleanerProcessor (order 30), so it must clean up any comma-run/whitespace-run/
        // leading-or-trailing artifact its own removals just created, itself.
        text = CommaRun.Replace(text, ",");
        text = CommaBeforeTerminalPunctuation.Replace(text, string.Empty);
        text = HorizontalWhitespaceRun.Replace(text, " ");
        text = LeadingComma.Replace(text, string.Empty);
        text = TrailingComma.Replace(text, string.Empty);
        text = LeadingWhitespace.Replace(text, string.Empty);
        text = TrailingWhitespace.Replace(text, string.Empty);

        var combined = new List<AppliedRule>(input.Applied.Count + applied.Count);
        combined.AddRange(input.Applied);
        combined.AddRange(applied);
        return new PostProcessResult(text, combined);
    }
}
