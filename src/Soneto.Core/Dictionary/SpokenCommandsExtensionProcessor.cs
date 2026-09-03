using System.Text.RegularExpressions;
using Soneto.Core.Abstractions;

namespace Soneto.Core.Dictionary;

/// <summary>
/// Phase 2 work item 6 (§2.4/§2.6/§2.11): order 60 stage of the post-processing chain. Fully
/// retires Phase 1's <c>SpokenCommandsProcessor</c> (order 20, a small fixed EN/RO table) per
/// §2.6's recommendation (b) -- that class and its tests are deleted in the same session this
/// class was added, and its 4 built-in phrases are migrated below as this processor's own
/// bundled defaults (<see cref="BuiltInDefaults"/>), so spoken commands never stop working.
///
/// <para>
/// <b>Name kept as <c>"SpokenCommands"</c>, not renamed</b> -- deliberately, for log-message/
/// <see cref="AppliedRule.Processor"/> continuity across the Phase 1 -> Phase 2 boundary; a
/// user or a log-history diff looking at "which stage did this" shouldn't see a name change for
/// what is, functionally, the same feature gaining user-extensibility.
/// </para>
///
/// <para>
/// <b>Which entry types this processor consumes:</b> only <see cref="SpokenCommand"/> entries
/// (plus the hardcoded <see cref="BuiltInDefaults"/> below, which are not read from
/// <c>dictionary.json</c> at all -- see the constructor's remarks). <see cref="CorrectionPair"/>/
/// <see cref="VocabularyTerm"/> (item 4), <see cref="RegexRule"/> (item 5), and
/// <see cref="PerAppOverride"/> (data-model only, §2.1/§2.9) are all ignored entirely by this
/// class; disabled <see cref="SpokenCommand"/> entries (<see cref="DictionaryEntry.Enabled"/>
/// == false) are filtered out at construction, so a disabled entry can never fire (it also
/// cannot suppress a same-phrase built-in -- see the collision policy below).
/// </para>
///
/// <para>
/// <b>Matching rule -- copied verbatim from item 8's <c>SpokenCommandsProcessor</c>, not
/// reimplemented and NOT routed through <see cref="AhoCorasickAutomaton{TValue}"/>:</b> a phrase
/// fires only when it is set off as its own clause -- each side of the matched phrase must be
/// bounded by either (a) the start/end of the whole utterance, or (b) one of the clause
/// punctuation marks <c>,.!?;:</c> (with any amount of whitespace between the punctuation and
/// the phrase). Matching is case-insensitive. This is deliberately NOT the same boundary rule as
/// item 3's automaton (its rule 6 only guards letter/digit adjacency, e.g. rejecting "cloud"
/// inside "Cloudflare") -- that weaker rule would happily let "new line" fire inside "my new
/// line of business" (a space is not a letter/digit), which is exactly the bug item 8 had to fix
/// on its first pass (see <c>Docs/PROJECT-MEMORY.md</c>'s item 8 entry). Spoken commands need
/// the stronger punctuation/utterance-boundary rule, so this stays its own small, focused,
/// regex-based mechanism, mirroring item 8's validated shape rather than reusing item 3/5's
/// machinery.
/// </para>
///
/// <para>
/// <b>Single, non-cascading pass:</b> like item 8's original, each command's regex runs once
/// against the evolving text in phrase-length-descending order (longer phrases first, so a
/// shorter phrase can never shadow a longer overlapping one -- not load-bearing for the current
/// 4 built-ins, none of which overlap, but kept safe as the table grows). The literal
/// control-character output (<c>\n</c>/<c>\n\n</c>, or whatever a user-defined command emits) is
/// never re-fed through any command's pattern -- there is nothing to cascade into, since these
/// patterns match words, not the control characters commands emit.
/// </para>
///
/// <para>
/// <b>Built-in defaults vs. user-provided entries -- collision policy:</b> the 4
/// <see cref="BuiltInDefaults"/> are merged in FIRST, then enabled <see cref="SpokenCommand"/>
/// entries from the constructor's <paramref name="entries"><c>entries</c></paramref> parameter
/// are merged in on top, keyed on <see cref="SpokenCommand.Phrase"/> (case-insensitive,
/// per this class's own case-insensitive matching). <b>A user-provided entry whose phrase
/// collides with a built-in's phrase wins</b> -- e.g. a user redefining <c>"new line"</c> to
/// emit something other than <c>"\n"</c> is honoured verbatim, not silently ignored in favour of
/// the built-in. This is the same "user config overrides shipped defaults" precedent the rest of
/// this project follows (e.g. <c>config.json</c> overriding compiled-in defaults) and is the
/// more useful behaviour for a power user who deliberately wants to repurpose a built-in phrase.
/// <b>User-vs-user collisions are a different case and are NOT silently resolved:</b> if two
/// enabled entries BOTH from <paramref name="entries"><c>entries</c></paramref> claim the same
/// phrase (case-insensitively), the constructor throws <see cref="ArgumentException"/> naming
/// both colliding <see cref="DictionaryEntry.Id"/>s, rather than silently letting whichever one
/// enumerates last win -- a hand-edited <c>dictionary.json</c> with two commands sharing a
/// phrase is far more likely to be an accidental typo/duplicate than a deliberate choice, unlike
/// the built-in-override case above, which IS a legitimate, deliberate pattern. A disabled
/// duplicate never triggers this (disabled entries are filtered out before the check runs).
/// </para>
///
/// <para>
/// <b>Order 60 -- reordering relative to items 4/5, and why it's fine:</b> Phase 1's
/// <c>SpokenCommandsProcessor</c> ran at order 20, before anything dictionary-related existed.
/// This processor now runs at order 60, AFTER <see cref="DictionaryEngineProcessor"/> (order 40)
/// and <see cref="RegexRuleProcessor"/> (order 50) -- a real reordering, considered explicitly
/// rather than accepted by default: could a correction-pair/regex-rule output ever accidentally
/// produce text that looks like a spoken-command phrase (or vice versa)? In practice this is very
/// unlikely to matter -- spoken commands are structural, literal, closed-class phrases
/// ("new line"/"new paragraph" and their RO equivalents, plus whatever short literal phrases a
/// user adds) matched only when set off by clause punctuation on both sides, while dictionary
/// entries/regex rules correct vocabulary (typically single technical terms or acronyms, not
/// whole punctuation-bounded clauses) -- a `CorrectionPair`/`RegexRule` emitting the literal,
/// punctuation-bounded text "new paragraph" as its `To`/`Replacement` would be a deliberately
/// contrived rule, not a realistic vocabulary correction. Running spoken commands LAST among the
/// dictionary-family processors is also arguably more correct: it lets a user's vocabulary
/// corrections apply to the surrounding prose before structural commands are peeled out, rather
/// than the other way around.
/// </para>
///
/// <para>
/// <b><c>WhitespaceCleanerProcessor</c> (order 30) ordering hazard -- investigated and handled,
/// not silently introduced as a regression:</b> in Phase 1, <c>WhitespaceCleanerProcessor</c> ran
/// AFTER <c>SpokenCommandsProcessor</c> (order 30 after order 20) specifically so it could clean
/// up any stray horizontal whitespace left touching a freshly-emitted <c>\n</c>/<c>\n\n</c> (e.g.
/// turning <c>"okay, \n\n, next"</c> into <c>"okay,\n\n, next"</c> by trimming the space that used
/// to precede the phrase). With this processor now at order 60, <c>WhitespaceCleanerProcessor</c>
/// runs BEFORE it instead -- it never sees this processor's freshly-emitted control characters at
/// all, so that cleanup would be silently lost if nothing else did it (and nothing later in the
/// chain does: <c>TrailingSpaceProcessor</c>, order 90, only ever looks at the very last character
/// of the whole transcript, not at interior whitespace around an interior <c>\n</c>). Concretely
/// verified with a full end-to-end <see cref="PostProcessing.PostProcessorChain"/> test (see this
/// class's test file): without any fix, <c>"okay, new paragraph, next item"</c> would come out as
/// <c>"okay, \n\n, next item "</c> (stray space before the paragraph break survives, because
/// nothing runs after this processor to trim it). <b>Fix:</b> this processor performs a small,
/// narrowly-scoped local cleanup of its own -- after any command fires, it strips horizontal
/// whitespace immediately preceding any <c>\n</c> in the resulting text (see
/// <see cref="TrailingHorizontalWhitespaceBeforeNewline"/>). This exactly reproduces the one
/// piece of <c>WhitespaceCleanerProcessor</c>'s behaviour that mattered for freshly-emitted
/// control characters, scoped ONLY to whitespace touching a <c>\n</c> (never touching horizontal
/// whitespace elsewhere in the text, which is not this processor's job). It is also a safe no-op
/// on any PRE-EXISTING <c>\n</c> that already passed through <c>WhitespaceCleanerProcessor</c> at
/// order 30 (that processor already trimmed horizontal whitespace off every line's edges, so
/// there is nothing left for this second pass to remove there) -- there is no need for a matching
/// "whitespace immediately after a `\n`" cleanup, because the phrase's own right-boundary pattern
/// never leaves a space between the matched phrase and a following punctuation mark (punctuation
/// always attaches directly to the preceding word, and any space that used to precede punctuation
/// was already removed by <c>WhitespaceCleanerProcessor</c> before this processor ever runs).
/// Newline-run capping (3+ consecutive <c>\n</c> collapsed to 2) is also not needed here: two
/// commands can never emit adjacent bare <c>\n</c> runs, because the punctuation-boundary rule
/// requires real non-newline content (a clause punctuation mark) between any two matches in the
/// same input, which always remains as a separator between the two emitted control-character
/// runs.
/// </para>
/// </summary>
public sealed class SpokenCommandsExtensionProcessor : IPostProcessor
{
    public int Order => 60;
    public string Name => "SpokenCommands";

    /// <summary>
    /// The 4 phrases migrated verbatim from item 8's <c>SpokenCommandsProcessor</c> fixed table.
    /// Hardcoded here (not read from <c>dictionary.json</c>/item 10's seed dictionary, which
    /// doesn't exist yet) so spoken commands keep working the moment this item lands, with zero
    /// functional gap versus Phase 1. Item 10 will later fold these same 4 entries into the full
    /// seed dictionary too, as an additional, later, non-functional-change step.
    /// </summary>
    private static readonly SpokenCommand[] BuiltInDefaults =
    [
        new SpokenCommand { Id = "builtin.spoken-command.en.new-paragraph", Phrase = "new paragraph", Emits = "\n\n" },
        new SpokenCommand { Id = "builtin.spoken-command.ro.paragraf-nou", Phrase = "paragraf nou", Emits = "\n\n" },
        new SpokenCommand { Id = "builtin.spoken-command.en.new-line", Phrase = "new line", Emits = "\n" },
        new SpokenCommand { Id = "builtin.spoken-command.ro.linie-noua", Phrase = "linie nouă", Emits = "\n" },
    ];

    // Strips horizontal whitespace (not \n itself) immediately before any \n -- the local
    // whitespace-cleanup fix described in the class doc comment above.
    private static readonly Regex TrailingHorizontalWhitespaceBeforeNewline =
        new(@"[^\S\n]+(?=\n)", RegexOptions.Compiled);

    private readonly bool _enabled;
    private readonly List<(string Id, Regex Pattern, string Emits)> _commands;

    /// <summary>
    /// Merges <see cref="BuiltInDefaults"/> with enabled <see cref="SpokenCommand"/> entries from
    /// <paramref name="entries"/> (a user-provided entry's phrase wins over a built-in's on
    /// collision -- see the class doc comment's "collision policy"), then compiles one boundary
    /// pattern per resulting command, longest phrase first.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if any enabled <see cref="SpokenCommand.Phrase"/> is empty or whitespace-only,
    /// naming the offending entry's <see cref="DictionaryEntry.Id"/> -- fail fast, at
    /// construction, mirroring items 3/5's established "clear message, at construction not
    /// first-use" pattern. This class's pattern is BUILT from <c>Phrase</c> rather than accepting
    /// a user-supplied raw regex (unlike item 5's <see cref="RegexRule.Pattern"/>), so there is no
    /// analogous "fails to compile" case to guard against -- only a degenerate phrase.
    /// <para>
    /// Also thrown if TWO enabled user-provided entries (i.e. two entries both from
    /// <paramref name="entries"/> -- built-ins are exempt, see below) collide on the same phrase
    /// (case-insensitively), naming both colliding <see cref="DictionaryEntry.Id"/>s, mirroring
    /// <see cref="AhoCorasickAutomaton{TValue}"/>'s own collision-exception style (item 3). This
    /// is a DIFFERENT case from "a user entry overrides a built-in" (which is a legitimate,
    /// silent, override-not-throw power-user pattern -- see the class doc comment) --
    /// user-vs-user is far more likely to be an accidental typo/duplicate in a hand-edited
    /// <c>dictionary.json</c> than a deliberate choice, so it fails loudly instead of silently
    /// picking whichever entry happened to enumerate last. A DISABLED duplicate never triggers
    /// this (disabled entries are skipped before the collision check even runs, same as they are
    /// everywhere else in this class).
    /// </para>
    /// </exception>
    public SpokenCommandsExtensionProcessor(
        IEnumerable<DictionaryEntry> entries, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _enabled = enabled;

        var merged = new Dictionary<string, SpokenCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var builtIn in BuiltInDefaults)
            merged[builtIn.Phrase] = builtIn;

        // Tracks phrases claimed by an ENABLED user-provided entry specifically (never a
        // built-in), so a second enabled user entry sharing that phrase can be told apart from
        // "this phrase happens to also be a built-in's" -- only the former is an error.
        var userPhrases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // phrase -> owning Id

        foreach (var entry in entries)
        {
            if (!entry.Enabled)
                continue;

            if (entry is not SpokenCommand command)
                continue; // CorrectionPair / VocabularyTerm / RegexRule / PerAppOverride: not this processor's job.

            if (string.IsNullOrWhiteSpace(command.Phrase))
            {
                throw new ArgumentException(
                    $"SpokenCommand \"{command.Id}\" has an empty/whitespace-only Phrase.",
                    nameof(entries));
            }

            if (userPhrases.TryGetValue(command.Phrase, out var owningId))
            {
                throw new ArgumentException(
                    $"SpokenCommand \"{command.Id}\" and \"{owningId}\" both claim the phrase " +
                    $"\"{command.Phrase}\" (case-insensitively) -- give each command a distinct " +
                    "phrase, or disable one of them.",
                    nameof(entries));
            }
            userPhrases[command.Phrase] = command.Id;

            merged[command.Phrase] = command; // user entry wins over a same-phrase built-in.
        }

        _commands = merged.Values
            .OrderByDescending(c => c.Phrase.Length)
            .Select(c => (c.Id, BuildPattern(c.Phrase), c.Emits))
            .ToList();
    }

    public PostProcessResult Process(PostProcessResult input)
    {
        if (!_enabled || string.IsNullOrEmpty(input.Text) || _commands.Count == 0)
            return input;

        var text = input.Text;
        List<AppliedRule>? applied = null;

        foreach (var (id, pattern, emits) in _commands)
        {
            text = pattern.Replace(text, match =>
            {
                (applied ??= []).Add(new AppliedRule(Name, id, match.Value, emits));
                return emits;
            });
        }

        if (applied is null || applied.Count == 0)
            return input;

        // See the class doc comment's WhitespaceCleanerProcessor-ordering paragraph: this
        // processor now runs AFTER WhitespaceCleanerProcessor (order 30), so nothing downstream
        // will clean up horizontal whitespace left touching a freshly-emitted \n unless this
        // processor does it itself.
        text = TrailingHorizontalWhitespaceBeforeNewline.Replace(text, string.Empty);

        var combined = new List<AppliedRule>(input.Applied.Count + applied.Count);
        combined.AddRange(input.Applied);
        combined.AddRange(applied);
        return new PostProcessResult(text, combined);
    }

    private static Regex BuildPattern(string phrase) =>
        new(
            $@"(?<=^\s*|[,.!?;:]\s*)\b{Regex.Escape(phrase)}\b(?=\s*(?:$|[,.!?;:]))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
