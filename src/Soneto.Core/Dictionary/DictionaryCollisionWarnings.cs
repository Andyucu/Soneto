using Microsoft.Extensions.Logging;

namespace Soneto.Core.Dictionary;

/// <summary>
/// Phase 2 work item 8 (§2.8, originally §6.2 rule 6 of
/// <c>Docs/dictation-app-build-plan.md</c>): "on rule creation, run the pattern against a
/// bundled word frequency list for both EN and RO and show a warning if it collides with a
/// common word." This is the AUTHORING-TIME safety net -- a human hand-editing
/// <c>dictionary.json</c> gets a log signal that a rule they just wrote might be risky, distinct
/// from <see cref="AhoCorasickAutomaton{TValue}"/>'s RUNTIME full-token-boundary safety net
/// (already built in item 3) that stops an already-authored rule from firing inside unrelated
/// words at transcription time.
///
/// <para>
/// <b>Not wired into any live reload flow yet.</b> <c>DictionaryService</c> (the hot-reload/
/// validation service that will actually call this on every <c>dictionary.json</c> load) is
/// Phase 2 item 9 and does not exist yet -- this class is a standalone, directly testable
/// utility with a trivial call-site shape (<see cref="Check"/> takes the already-deserialized
/// entries plus an <see cref="ILogger"/> and returns nothing) so item 9 can call it in one line
/// once it exists.
/// </para>
///
/// <para>
/// <b>Advisory only.</b> Never throws for any dictionary-entry input, never blocks, never
/// mutates <c>entries</c> -- it only logs a <see cref="LogLevel.Warning"/> per colliding entry
/// and returns. The sole theoretical exception path is an all-but-impossible failure loading
/// the embedded word-list resource at first <see cref="WordFrequencyList.Instance"/> access
/// (see that class's doc comment) -- a build-time invariant (the resource is embedded in the
/// assembly at compile time) not expected to fail in practice, and not worth adding a try/catch
/// around here.
/// </para>
///
/// <para>
/// <b>What counts as a candidate pattern:</b> only <see cref="CorrectionPair.From"/> and
/// <see cref="VocabularyTerm.Term"/> are checked -- <see cref="RegexRule"/>,
/// <see cref="SpokenCommand"/>, and <see cref="PerAppOverride"/> are ignored entirely (a regex
/// pattern isn't a literal word to begin with, a spoken command phrase is a deliberately
/// multi-word structural trigger, and a per-app override's <c>ProcessName</c> isn't user-facing
/// dictation text at all). Disabled entries (<see cref="DictionaryEntry.Enabled"/> == false) are
/// skipped, same as every <see cref="Soneto.Core.Abstractions.IPostProcessor"/> in this project
/// already does for disabled entries.
/// </para>
///
/// <para>
/// <b>"Single word" definition for this check.</b> Per the plan's own explicit carve-out ("a
/// multi-word pattern containing a common word as one of several tokens is fine and expected"),
/// only a pattern that is itself a single word is even a candidate for the warning -- a pattern
/// like <c>"web methods"</c> can never trigger it, regardless of whether "web" happens to be a
/// common word, because the risk this check exists to catch is a rule whose ENTIRE pattern IS an
/// ordinary word (the <c>cloud</c> -&gt; <c>Cloudflare</c> example), not a rule that merely
/// contains one as a token. This class defines "single word" as simply "the trimmed pattern
/// contains no whitespace" -- deliberately NOT reusing <see cref="AhoCorasickAutomaton{TValue}"/>
/// 's glue-tolerance/word-boundary machinery, which exists to answer a materially different
/// question (does this rule's match respect token boundaries in ARBITRARY surrounding text at
/// match time?). Here the only question is "is the pattern itself, in isolation, one token or
/// several?", and a plain whitespace check answers that directly and far more simply. A hyphenated
/// pattern like <c>"web-methods"</c> is therefore treated as ONE token by this definition (no
/// internal whitespace) -- which is fine, since the check's actual verdict comes from
/// <see cref="WordFrequencyList.IsCommonWord"/>, and <c>"web-methods"</c> (hyphen and all) isn't
/// itself a common word regardless of whether a human would call it "one word" or "two" --
/// the whitespace heuristic only needs to correctly EXCLUDE genuinely multi-word patterns like
/// <c>"web methods"</c> from being candidates at all, which it does.
/// </para>
///
/// <para>
/// <b>Match semantics: case-insensitive exact match, no diacritic folding.</b>
/// <see cref="WordFrequencyList.IsCommonWord"/> already does a case-insensitive exact-string
/// comparison, so <c>"Cloud"</c>/<c>"CLOUD"</c>/<c>"cloud"</c> all collide with the bundled
/// <c>cloud</c> entry. Diacritic-folding (the kind <see cref="DiacriticFolder"/> does for the
/// runtime matching engine) is deliberately NOT applied here: this check's whole purpose is
/// "does the literal pattern text a human just typed happen to equal a common word," and folding
/// diacritics away would risk false-positive warnings on RO patterns that only coincidentally
/// become a common word once their diacritics are stripped (e.g. a hypothetical technical term
/// that folds down to an unrelated common Romanian word) -- a much simpler, more conservative
/// exact-string check is the right level of aggressiveness for an advisory, non-blocking signal.
/// </para>
/// </summary>
public static class DictionaryCollisionWarnings
{
    /// <summary>
    /// Checks every enabled <see cref="CorrectionPair"/>/<see cref="VocabularyTerm"/> in
    /// <paramref name="entries"/> against <paramref name="wordList"/> (defaults to
    /// <see cref="WordFrequencyList.Instance"/> if null) and logs a <see cref="LogLevel.Warning"/>
    /// for each single-word pattern that collides with a common EN/RO word. Purely advisory --
    /// never throws, never blocks, never mutates <paramref name="entries"/>.
    /// </summary>
    public static void Check(
        IEnumerable<DictionaryEntry> entries, ILogger logger, WordFrequencyList? wordList = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(logger);
        wordList ??= WordFrequencyList.Instance;

        foreach (var entry in entries)
        {
            if (!entry.Enabled)
                continue;

            var (pattern, kind) = entry switch
            {
                CorrectionPair pair => (pair.From, "CorrectionPair.From"),
                VocabularyTerm term => (term.Term, "VocabularyTerm.Term"),
                _ => (null, null),
            };

            if (pattern is null)
                continue;

            var trimmed = pattern.Trim();
            if (trimmed.Length == 0)
                continue;

            // Only a single-word pattern (no internal whitespace) is even a candidate -- see the
            // class doc comment's "single word" definition paragraph.
            if (ContainsWhitespace(trimmed))
                continue;

            if (!wordList.IsCommonWord(trimmed))
                continue;

            logger.LogWarning(
                "Dictionary entry {EntryId} ({Kind} = \"{Pattern}\") is itself a common EN/RO " +
                "word (\"{Word}\"). This is advisory only -- it doesn't block loading -- but a " +
                "single-word rule whose pattern IS an ordinary common word is risky the same way " +
                "a hypothetical \"cloud\" -> \"Cloudflare\" CorrectionPair would be: it can " +
                "over-correct legitimate, unrelated uses of that common word. Double-check this " +
                "rule is intentional.",
                entry.Id, kind, trimmed, trimmed.ToLowerInvariant());
        }
    }

    private static bool ContainsWhitespace(string s)
    {
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
                return true;
        }
        return false;
    }
}
