using System.Text.RegularExpressions;
using Soneto.Core.Abstractions;

namespace Soneto.Core.PostProcessing;

/// <summary>
/// Phase 4 item 3 (§4.4): order 80 stage, NOT part of the default chain -- only ever included
/// when a matching, enabled <see cref="Dictionary.PerAppOverride"/> profile has
/// <see cref="Dictionary.PerAppOverride.AutoCapitalize"/> == <c>true</c> for the focused app (see
/// <see cref="PostProcessorChain"/>'s own doc comment for how/where that selection happens).
/// Before this item, <c>AutoCapitalize</c> was schema-only (Phase 2) with zero real consumer
/// anywhere in the codebase -- this is the first one.
///
/// <para>
/// <b>Deliberately minimal, not a full grammar-aware capitalizer:</b> uppercases the first
/// letter of the text and the first letter following any of <c>. ! ?</c> plus whitespace,
/// mirroring the same "narrowly scoped, real, safe-additive" judgment call this item's own
/// report documents for both new per-app-only processors. Does not attempt proper-noun
/// detection, abbreviation handling ("Dr." not treated specially), or any ASR-specific
/// heuristics -- a deliberately small first cut, same spirit as <c>FillerWordStripper</c>'s own
/// "small, low-risk list, extend from real usage" framing.
/// </para>
///
/// <para>
/// <b>Order 80, before <see cref="TrailingPunctuationProcessor"/> (85) and
/// <see cref="TrailingSpaceProcessor"/> (90):</b> capitalization only ever touches existing
/// letters, so its relative order versus those two trailing-only stages doesn't matter for
/// correctness, but it's placed before them to read naturally as "fix casing, then fix
/// punctuation, then fix trailing whitespace" -- last-stage-of-the-chain concerns in the order a
/// human would apply them by hand.
/// </para>
///
/// <para>
/// <b>Bounded match timeout</b> (same discipline as <see cref="Dictionary.RegexRuleProcessor"/>'s
/// own doc comment): the whole post-processor chain runs synchronously with no cancellation
/// path once a stage starts, so this processor's regex carries an explicit, small
/// <see cref="Regex.MatchTimeout"/> rather than <see cref="Regex.InfiniteMatchTimeout"/>. Unlike
/// <c>RegexRuleProcessor</c>, this pattern is fixed (not user-authored via <c>dictionary.json</c>),
/// so a timeout here would indicate a real bug in this class, not adversarial user input --
/// still guarded defensively, matching this project's established regex-safety convention.
/// </para>
/// </summary>
public sealed class AutoCapitalizeProcessor : IPostProcessor
{
    public int Order => 80;
    public string Name => "AutoCapitalize";

    // Review fix (Phase 4 item 3 code review): a fixed rule id, since this processor has no
    // dictionary-authored entries of its own to key an AppliedRule off -- mirrors
    // RegexRuleProcessor's `id` slot in its own AppliedRule(Name, id, From, To) calls.
    private const string RuleId = "sentenceStart";

    // Matches the start of the text, or a sentence-terminator (. ! ?) followed by whitespace,
    // immediately followed by a lowercase letter (\p{Ll} -- any Unicode lowercase letter, not
    // just ASCII, so this works for Romanian diacritics too) to uppercase.
    private static readonly Regex SentenceStart = new(
        @"(^\s*|[.!?]\s+)(\p{Ll})",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// Review fix (Phase 4 item 3 code review): records an <see cref="AppliedRule"/> per genuine
    /// capitalization, mirroring every other text-mutating processor in this codebase
    /// (<see cref="Dictionary.DictionaryEngineProcessor"/>/<see cref="Dictionary.RegexRuleProcessor"/>/
    /// <see cref="Dictionary.FillerWordStripper"/>/<see cref="Dictionary.SpokenCommandsExtensionProcessor"/>)
    /// -- <c>AppliedRule</c> flows through to <c>HistoryEntry.RulesFired</c> (persisted) and
    /// <c>Soneto.App</c>'s History UI diff-highlighting, so silently mutating text without
    /// recording it would make this processor's own changes invisible there. Only recorded when
    /// the matched letter actually changes case (a letter that's already uppercase can't match
    /// <c>\p{Ll}</c> in the first place, but this guard is kept explicit rather than relying on
    /// that regex-level guarantee alone).
    /// </summary>
    public PostProcessResult Process(PostProcessResult input)
    {
        if (string.IsNullOrEmpty(input.Text))
            return input;

        List<AppliedRule>? applied = null;
        string result;
        try
        {
            result = SentenceStart.Replace(input.Text, m =>
            {
                string from = m.Groups[2].Value;
                string to = char.ToUpperInvariant(from[0]).ToString();
                if (!string.Equals(from, to, StringComparison.Ordinal))
                {
                    (applied ??= []).Add(new AppliedRule(Name, RuleId, from, to));
                }
                return m.Groups[1].Value + to;
            });
        }
        catch (RegexMatchTimeoutException)
        {
            // Should not happen against fixed, non-adversarial input -- fail safe (no-op)
            // rather than let a pathological edge case take down the whole chain.
            return input;
        }

        if (applied is null || applied.Count == 0)
            return input;

        var combined = new List<AppliedRule>(input.Applied.Count + applied.Count);
        combined.AddRange(input.Applied);
        combined.AddRange(applied);
        return new PostProcessResult(result, combined);
    }
}
