using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Soneto.Core.Abstractions;

namespace Soneto.Core.Dictionary;

/// <summary>
/// Phase 2 work item 5 (§2.4/§2.5/§2.11): order 50 stage of the post-processing chain, running
/// AFTER <see cref="DictionaryEngineProcessor"/> (order 40) -- regex rules see already
/// dictionary-corrected text, composing with the correction-pair pass rather than racing it.
///
/// <para>
/// <b>Which entry types this processor consumes:</b> only <see cref="RegexRule"/> entries.
/// <see cref="CorrectionPair"/>/<see cref="VocabularyTerm"/> (item 4's job),
/// <see cref="SpokenCommand"/> (item 6, order 60), and <see cref="PerAppOverride"/>
/// (data-model only in Phase 2, per §2.1/§2.9) are all ignored entirely by this class;
/// disabled entries (<see cref="DictionaryEntry.Enabled"/> == false) are filtered out at
/// construction time, so a disabled rule can never fire.
/// </para>
///
/// <para>
/// <b>DELIBERATELY THE OPPOSITE of <see cref="DictionaryEngineProcessor"/>'s single-pass,
/// no-cascading guarantee.</b> <see cref="DictionaryEngineProcessor"/> (item 4, order 40)
/// guarantees a match's OWN replacement output is never re-matched by another correction-pair
/// rule -- that guards against accidental cascading in the common case. This processor is the
/// opposite on purpose: regex rules apply SEQUENTIALLY, and each rule's <see cref="Regex.Replace"/>
/// runs against the OUTPUT of the previous regex rule, not the original input. Per §2.5's own
/// reasoning, regex is explicitly "the advanced tab, power-user escape hatch" -- a power user
/// writing several regex rules has a reasonable expectation they compose/cascade against each
/// other, unlike the accidental-cascading risk the trie-based pass specifically guards against
/// for ordinary correction pairs. If a future reader is comparing this class to
/// <see cref="DictionaryEngineProcessor"/> and the two appear to behave differently on
/// cascading, that is intentional -- neither class has a bug.
/// </para>
///
/// <para>
/// <b>Patterns are validated and compiled at construction time</b> (§2.7: "catch
/// <see cref="RegexParseException"/> at load time, not at first-match time"), mirroring
/// <see cref="AhoCorasickAutomaton{TValue}"/>'s established pattern of validating-and-throwing
/// clearly at construction time for bad input data known at construction. Each valid pattern is
/// compiled once (<see cref="RegexOptions.Compiled"/>) and cached, rather than re-parsed on
/// every <see cref="Process"/> call -- this runs on every transcript.
/// </para>
///
/// <para>
/// <b>Rule order:</b> rules apply in the order given to the constructor's
/// <c>IEnumerable&lt;DictionaryEntry&gt;</c> -- which, in the real dictionary, should be file
/// order. This class does not sort or reorder rules itself.
/// </para>
///
/// <para>
/// <b>Bounded match timeout, not <see cref="Regex.InfiniteMatchTimeout"/>:</b> each compiled
/// <see cref="Regex"/> is given an explicit <see cref="MatchTimeout"/> (see
/// <see cref="_matchTimeout"/>) rather than the default unbounded timeout. `dictionary.json` is
/// hand-edited with no backtracking-safety validation on `Pattern`, and the whole
/// post-processor chain runs synchronously on the single session worker thread with no
/// cancellation path once a <see cref="Regex.Replace(string, MatchEvaluator)"/> call starts --
/// a pathological pattern (e.g. nested-quantifier catastrophic backtracking) run against
/// adversarial non-matching input could otherwise hang the entire daemon indefinitely. A
/// <see cref="RegexMatchTimeoutException"/> is caught around each rule's <c>Replace</c> call in
/// <see cref="Process"/> and treated the same way a non-match is treated -- that rule is skipped
/// for this call (text left as it was before that rule ran), a warning is logged, and the
/// remaining rules still run.
/// </para>
///
/// <para>
/// <b>Scale assumption:</b> this design -- one sequential, full linear-scan
/// <see cref="Regex.Replace(string, MatchEvaluator)"/> call per rule -- is O(rule count x
/// transcript length) per <see cref="Process"/> call. That is appropriately simple for this
/// phase's actual scope (a modest personal dictionary, on the order of tens of regex rules, over
/// transcripts a few hundred characters long); it is not designed to scale to hundreds of rules
/// or very long inputs without revisiting.
/// </para>
/// </summary>
public sealed class RegexRuleProcessor : IPostProcessor
{
    public int Order => 50;
    public string Name => "RegexRule";

    /// <summary>
    /// Per-rule match timeout. 250ms is generous relative to the plan's own "&lt;5ms for the
    /// dictionary pass" target, but bounds a pathological pattern's worst case to something
    /// finite and loud (a logged, caught <see cref="RegexMatchTimeoutException"/>) instead of
    /// infinite and silent.
    /// </summary>
    private static readonly TimeSpan _matchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly bool _enabled;
    private readonly ILogger<RegexRuleProcessor>? _logger;
    private readonly List<(string Id, Regex Regex, string Replacement)> _rules;

    /// <summary>
    /// Filters <paramref name="entries"/> down to enabled <see cref="RegexRule"/> entries
    /// (preserving their relative order) and compiles each one's <see cref="RegexRule.Pattern"/>
    /// immediately.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if any enabled <see cref="RegexRule.Pattern"/> fails to compile, naming the
    /// offending rule's <see cref="DictionaryEntry.Id"/> and literal pattern text, and including
    /// the underlying <see cref="RegexParseException"/>'s message.
    /// </exception>
    public RegexRuleProcessor(
        IEnumerable<DictionaryEntry> entries, bool enabled = true, ILogger<RegexRuleProcessor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _enabled = enabled;
        _logger = logger;

        _rules = [];
        foreach (var entry in entries)
        {
            if (!entry.Enabled)
                continue;

            if (entry is not RegexRule rule)
                continue; // CorrectionPair / VocabularyTerm / SpokenCommand / PerAppOverride: not this processor's job.

            Regex compiled;
            try
            {
                compiled = new Regex(rule.Pattern, RegexOptions.Compiled, _matchTimeout);
            }
            catch (RegexParseException ex)
            {
                throw new ArgumentException(
                    $"RegexRule \"{rule.Id}\" has an invalid pattern \"{rule.Pattern}\": {ex.Message}",
                    nameof(entries), ex);
            }

            _rules.Add((rule.Id, compiled, rule.Replacement));
        }
    }

    public PostProcessResult Process(PostProcessResult input)
    {
        if (!_enabled || string.IsNullOrEmpty(input.Text) || _rules.Count == 0)
            return input;

        var text = input.Text;
        List<AppliedRule>? applied = null;

        foreach (var (id, regex, replacementPattern) in _rules)
        {
            // Collected into a per-rule-attempt buffer, not directly into `applied`, so that a
            // mid-replace timeout (below) can't leave AppliedRule entries claiming a change that
            // never actually made it into `text` -- either the whole rule's Replace call
            // succeeds and its matches are committed, or it times out and none of its
            // in-progress matches are committed.
            List<AppliedRule>? ruleMatches = null;
            string result;
            try
            {
                result = regex.Replace(text, match =>
                {
                    // Real matched span and real substituted text (with $1/capture-group
                    // substitution already resolved), per occurrence -- not the abstract rule
                    // pattern/replacement text. A single rule can match multiple times in one
                    // transcript; each occurrence gets its own AppliedRule entry.
                    var replacement = match.Result(replacementPattern);
                    (ruleMatches ??= []).Add(new AppliedRule(Name, id, match.Value, replacement));
                    return replacement;
                });
            }
            catch (RegexMatchTimeoutException)
            {
                // Treated like a non-match for this rule: skip it, leave `text` as it was
                // before this rule ran, move on to the remaining rules -- never let one
                // pathological pattern hang (or fail) the whole chain. Logged, not silent.
                _logger?.LogWarning(
                    "RegexRule {RuleId} exceeded its {TimeoutMs}ms match timeout and was skipped " +
                    "for this transcript (possible catastrophic backtracking). Text snippet: {Snippet}",
                    id, _matchTimeout.TotalMilliseconds, Truncate(text));
                continue;
            }

            text = result;
            if (ruleMatches is { Count: > 0 })
                (applied ??= []).AddRange(ruleMatches);
        }

        if (applied is null || applied.Count == 0)
            return new PostProcessResult(text, input.Applied);

        var combined = new List<AppliedRule>(input.Applied.Count + applied.Count);
        combined.AddRange(input.Applied);
        combined.AddRange(applied);
        return new PostProcessResult(text, combined);
    }

    /// <summary>
    /// Truncates <paramref name="text"/> for logging so a timeout warning doesn't dump a
    /// potentially long full transcript into the log.
    /// </summary>
    private static string Truncate(string text, int maxLength = 60) =>
        text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
}
