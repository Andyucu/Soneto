using System.Text;
using Soneto.Core.Abstractions;

namespace Soneto.Core.Dictionary;

/// <summary>
/// Phase 2 work item 4 (§2.4/§2.5/§2.11): order 40 stage of the post-processing chain. The
/// first real dictionary-engine processor -- wires <see cref="AhoCorasickAutomaton{TValue}"/>
/// (item 3) into an <see cref="IPostProcessor"/> that performs actual correction-pair and
/// vocabulary-casing replacement, and is the first processor to genuinely populate
/// <see cref="AppliedRule"/> (every prior Phase 1 processor left it empty).
///
/// <para>
/// <b>Which entry types this processor consumes:</b> only <see cref="CorrectionPair"/> and
/// <see cref="VocabularyTerm"/> entries. <see cref="RegexRule"/> (item 5's
/// <c>RegexRuleProcessor</c>, order 50), <see cref="SpokenCommand"/> (item 6, order 60), and
/// <see cref="PerAppOverride"/> (data-model only in Phase 2, per §2.1/§2.9 -- not consumed by
/// anything this phase) are all ignored entirely by this class; disabled entries
/// (<see cref="DictionaryEntry.Enabled"/> == false) are filtered out before ever being handed
/// to the automaton, so a disabled entry can never match.
/// </para>
///
/// <para>
/// <b>Vocabulary terms as implicit self-correction pairs:</b> per §2.4's <see cref="VocabularyTerm"/>
/// doc comment, a vocabulary term seeds casing correction even with no explicit
/// <see cref="CorrectionPair"/> for it. Each enabled <see cref="VocabularyTerm"/> is inserted into
/// the automaton with pattern = <c>Term</c> and treated, for replacement purposes, as if it had
/// its own <c>Term</c> as its replacement text -- i.e. it runs through the exact same rule-3
/// casing logic (<see cref="ApplyCasing"/>) as a real <see cref="CorrectionPair"/>. Vocabulary
/// terms are NOT wired into ASR hotwords in this phase -- that's explicitly deferred (§2.1).
/// </para>
///
/// <para>
/// <b>Single-pass, no cascading (§2.5 rule 4):</b> <see cref="Process"/> calls
/// <see cref="AhoCorasickAutomaton{TValue}.Match"/> exactly once per call, then splices the
/// output by walking the returned (already non-overlapping, ascending-<c>Start</c>) matches,
/// copying each unmatched span of the original text verbatim and substituting a casing-decided
/// replacement at each match. The spliced output is never re-fed through the automaton.
/// </para>
///
/// <para>
/// <b>Assumes NFC-normalized input</b> (§2.5 rule 1): this processor runs at order 40, after
/// <see cref="PostProcessing.UnicodeNormalizerProcessor"/> (order 10) by construction, and does
/// not re-normalize its input -- if the chain's ordering ever changes such that this processor
/// runs before order 10, this assumption would silently break.
/// </para>
/// </summary>
public sealed class DictionaryEngineProcessor : IPostProcessor
{
    public int Order => 40;
    public string Name => "DictionaryEngine";

    private readonly bool _enabled;
    private readonly AhoCorasickAutomaton<DictionaryEntry>? _automaton;

    /// <summary>
    /// Filters <paramref name="entries"/> down to enabled <see cref="CorrectionPair"/>/
    /// <see cref="VocabularyTerm"/> entries and builds a single <see cref="AhoCorasickAutomaton{TValue}"/>
    /// over them, keyed on the matched <see cref="DictionaryEntry"/> itself as the automaton's
    /// <c>TValue</c> so <see cref="Process"/> can read <c>Id</c>/<c>To</c>/<c>Term</c> directly
    /// off each match's <see cref="AhoCorasickMatch{TValue}.Value"/> with no separate lookup.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Propagated verbatim from <see cref="AhoCorasickAutomaton{TValue}"/>'s constructor if two
    /// entries' patterns collide on the same canonical match-key (e.g. two entries differing
    /// only by diacritic form). Graceful degradation on a bad dictionary file is item 9's job,
    /// not this constructor's.
    /// </exception>
    public DictionaryEngineProcessor(IEnumerable<DictionaryEntry> entries, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _enabled = enabled;

        var patterns = new List<(string Pattern, DictionaryEntry Value)>();
        foreach (var entry in entries)
        {
            if (!entry.Enabled)
                continue;

            switch (entry)
            {
                case CorrectionPair pair:
                    patterns.Add((pair.From, pair));
                    break;
                case VocabularyTerm term:
                    patterns.Add((term.Term, term));
                    break;
                default:
                    // RegexRule / SpokenCommand / PerAppOverride: not this processor's job.
                    break;
            }
        }

        _automaton = patterns.Count > 0 ? new AhoCorasickAutomaton<DictionaryEntry>(patterns) : null;
    }

    public PostProcessResult Process(PostProcessResult input)
    {
        if (!_enabled || string.IsNullOrEmpty(input.Text) || _automaton is null)
            return input;

        var text = input.Text;
        var matches = _automaton.Match(text);
        if (matches.Count == 0)
            return input;

        var sb = new StringBuilder(text.Length);
        var applied = new List<AppliedRule>(input.Applied.Count + matches.Count);
        applied.AddRange(input.Applied);

        int cursor = 0;
        foreach (var match in matches)
        {
            // Copy the unmatched span between the previous match (or the start of the text)
            // and this match, verbatim.
            sb.Append(text, cursor, match.Start - cursor);

            var originalSpan = text.Substring(match.Start, match.Length);
            var replacementTarget = match.Value switch
            {
                CorrectionPair pair => pair.To,
                VocabularyTerm term => term.Term,
                _ => originalSpan, // unreachable given the constructor's filtering
            };

            var replacement = ApplyCasing(originalSpan, replacementTarget);
            sb.Append(replacement);

            applied.Add(new AppliedRule(Name, match.Value.Id, originalSpan, replacement));

            cursor = match.Start + match.Length;
        }

        // Copy the trailing unmatched span after the last match.
        sb.Append(text, cursor, text.Length - cursor);

        return new PostProcessResult(sb.ToString(), applied);
    }

    /// <summary>
    /// §2.5 rule 3's exact casing decision, as a standalone, directly unit-testable helper.
    ///
    /// <para>
    /// <b>"Explicit internal casing":</b> <paramref name="replacementText"/> has explicit
    /// internal casing if it contains BOTH an uppercase letter (<see cref="char.IsUpper(char)"/>)
    /// AND a lowercase letter (<see cref="char.IsLower(char)"/>) anywhere in it -- e.g.
    /// <c>webMethods</c> and <c>Claude Code</c> both qualify. If it has explicit internal casing,
    /// it is used VERBATIM, regardless of how <paramref name="originalMatchedSpan"/> was cased in
    /// the input (even if the input was ALL CAPS or all lowercase) -- the rule author's explicit
    /// casing always wins.
    /// </para>
    ///
    /// <para>
    /// <b>Otherwise</b> (the replacement is entirely one case -- all-upper, all-lower, or has no
    /// cased letters at all), the ORIGINAL matched span's casing PATTERN is applied to the
    /// replacement:
    /// <list type="bullet">
    /// <item>If the original span is ALL-UPPERCASE (every cased letter in it is uppercase, and
    /// at least one cased letter exists), the whole replacement is uppercased.</item>
    /// <item><b>Title-Case</b> (defined here, precisely, since the plan leaves this a judgment
    /// call beyond its one example: the FIRST cased character of the original span is uppercase,
    /// and every OTHER cased character in the span is lowercase -- e.g. a single capitalized word
    /// at a sentence start like <c>"Webmethods"</c>) -- only the first character of the
    /// replacement is capitalized, the rest is lowercased.</item>
    /// <item>Otherwise (all-lowercase original, or a genuinely mixed-but-not-title-case original
    /// that isn't all-upper either), the whole replacement is lowercased.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static string ApplyCasing(string originalMatchedSpan, string replacementText)
    {
        if (string.IsNullOrEmpty(replacementText))
            return replacementText;

        if (HasExplicitInternalCasing(replacementText))
            return replacementText;

        var casingPattern = DetectCasingPattern(originalMatchedSpan);
        return casingPattern switch
        {
            CasingPattern.AllUpper => replacementText.ToUpperInvariant(),
            CasingPattern.TitleCase => CapitalizeFirstOnly(replacementText),
            _ => replacementText.ToLowerInvariant(),
        };
    }

    private static bool HasExplicitInternalCasing(string text)
    {
        bool hasUpper = false;
        bool hasLower = false;
        foreach (var c in text)
        {
            if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsLower(c)) hasLower = true;

            if (hasUpper && hasLower)
                return true;
        }
        return false;
    }

    private enum CasingPattern
    {
        AllUpper,
        TitleCase,
        Lower,
    }

    private static CasingPattern DetectCasingPattern(string span)
    {
        bool sawFirstCased = false;
        bool firstIsUpper = false;
        bool allUpper = true;
        bool restAllLower = true;
        bool anyCased = false;

        foreach (var c in span)
        {
            if (!char.IsUpper(c) && !char.IsLower(c))
                continue;

            anyCased = true;
            bool isUpper = char.IsUpper(c);

            if (!isUpper)
                allUpper = false;

            if (!sawFirstCased)
            {
                sawFirstCased = true;
                firstIsUpper = isUpper;
            }
            else if (isUpper)
            {
                restAllLower = false;
            }
        }

        if (!anyCased)
            return CasingPattern.Lower;

        if (allUpper)
            return CasingPattern.AllUpper;

        if (firstIsUpper && restAllLower)
            return CasingPattern.TitleCase;

        return CasingPattern.Lower;
    }

    private static string CapitalizeFirstOnly(string text)
    {
        var lower = text.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
    }
}
