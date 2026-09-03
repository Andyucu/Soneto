using System.Text;

namespace Soneto.Core.Dictionary;

/// <summary>
/// Phase 2 work item 2 (§2.5 rule 2): a pure, match-only diacritic-folding transform for the
/// Aho-Corasick automaton (item 3) to build/match against.
///
/// This is deliberately DISTINCT from <see cref="PostProcessing.UnicodeNormalizerProcessor"/>
/// (order 10, item 8 Phase 1), which CORRECTS the actual output text by mapping the
/// Turkish/legacy cedilla forms ş/ţ to the correct Romanian comma-below forms ș/ț before the
/// dictionary engine ever runs. <see cref="DiacriticFolder"/> does the opposite job: it never
/// touches what gets emitted. It only computes a throwaway "match key" used to decide whether a
/// dictionary pattern (a <c>CorrectionPair.From</c>, etc.) matches a span of input text.
///
/// <para>
/// <b>Folding rules (match-only, never emitted):</b>
/// <list type="bullet">
/// <item>ș (U+0219) and ş (U+015F) both fold to ș — the comma-below form is the chosen
/// canonical match key. Which of the two forms is chosen as the key is arbitrary (folding is
/// match-only, so the key itself is never visible in output) but must stay consistent, since
/// item 3's automaton will build its trie over folded pattern text and needs the same choice
/// applied to both pattern and input text.</item>
/// <item>ț (U+021B) and ţ (U+0163) both fold to ț, for the same reason.</item>
/// <item>ă (U+0103) and â (U+00E2) both fold to a; î (U+00EE) folds to i — so a dictionary rule
/// author can write a pattern with no diacritics at all (e.g. "cuvant") and still have it match
/// real diacritic-containing input (e.g. "cuvânt").</item>
/// <item>Everything else, including plain ASCII, passes through unchanged.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Case is deliberately NOT touched here.</b> §2.5 rule 3 ("case-insensitive match,
/// case-preserving replace") is a separate concern owned by a later component in the matching
/// pipeline (item 3's automaton). Folding an uppercase diacritic (Ș, Ş, Ț, Ţ, Ă, Â, Î) produces
/// the uppercase folded form (Ș, Ș, Ț, Ț, A, A, I) -- it is NOT lower-cased here. This keeps the
/// two rules composable: item 3 can apply <see cref="FoldChar"/>/<see cref="FoldForMatching"/>
/// first and its own case-insensitive comparison second (or in either order), without this
/// class silently doing half of rule 3's job and causing surprises.
/// </para>
///
/// <para>
/// <b>NEVER apply this to text that will be emitted/injected.</b> The actual replacement text
/// substituted into the output must always be each rule's own literal <c>To</c>/<c>Emits</c>
/// text, written by the rule author with correct canonical comma-below diacritics -- folding
/// must never leak into what gets emitted. This class only answers "does this match", never
/// "what should be written".
/// </para>
///
/// <para>
/// <b>Assumes NFC-normalized (precomposed) input.</b> Per §2.5 rule 1,
/// <see cref="PostProcessing.UnicodeNormalizerProcessor"/> (order 10) runs before the
/// dictionary engine (order 40+) in the post-processing chain and guarantees NFC-normalized,
/// precomposed input by construction -- this class trusts that ordering rather than
/// re-normalizing itself. <see cref="FoldChar"/> only recognizes precomposed diacritic
/// codepoints (e.g. ș U+0219); it does NOT detect or fold decomposed combining-mark sequences
/// (e.g. a plain "s" followed by a standalone U+0326 COMBINING COMMA BELOW). Decomposed input
/// will silently fail to match rather than throwing or being normalized -- if the chain's
/// ordering ever changes such that this class can see non-NFC input, this assumption breaks
/// silently and needs to be revisited here.
/// </para>
/// </summary>
public static class DiacriticFolder
{
    /// <summary>
    /// Folds a single character to its match-only key, per the class-level doc comment's
    /// rules. Exposed alongside the string-level <see cref="FoldForMatching"/> because the
    /// Aho-Corasick automaton (item 3) will likely want to fold characters one at a time while
    /// walking the trie, rather than pre-folding a whole string on every extension step.
    /// </summary>
    public static char FoldChar(char c) => c switch
    {
        'ş' => 'ș', // ş (U+015F) -> ș (U+0219), match-key only
        'ţ' => 'ț', // ţ (U+0163) -> ț (U+021B), match-key only
        'Ş' => 'Ș', // Ş (U+015E) -> Ș (U+0218), match-key only, case preserved
        'Ţ' => 'Ț', // Ţ (U+0162) -> Ț (U+021A), match-key only, case preserved
        'ă' => 'a', // ă (U+0103) -> a, match-only
        'â' => 'a', // â (U+00E2) -> a, match-only
        'î' => 'i', // î (U+00EE) -> i, match-only
        'Ă' => 'A', // case preserved
        'Â' => 'A', // case preserved
        'Î' => 'I', // case preserved
        _ => c,
    };

    /// <summary>
    /// Folds an entire string to its match-only form by applying <see cref="FoldChar"/> to
    /// every character. Never use the result for anything that gets emitted/injected -- see
    /// the class-level doc comment.
    /// </summary>
    public static string FoldForMatching(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Only allocate a new string when a fold actually changes something.
        var needsFold = false;
        foreach (var c in text)
        {
            if (FoldChar(c) != c)
            {
                needsFold = true;
                break;
            }
        }
        if (!needsFold)
            return text;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(FoldChar(c));
        return sb.ToString();
    }

    /// <summary>
    /// True if <paramref name="c"/> is already in its canonical, correctly-formed Romanian
    /// comma-below form (ș/ț/Ș/Ț) -- i.e. it is NOT one of the legacy cedilla forms that
    /// <see cref="PostProcessing.UnicodeNormalizerProcessor"/> corrects. A small, natural
    /// building block that item 9's dictionary-load-time validation (§2.7/§2.8 -- warning if a
    /// <c>CorrectionPair.To</c> contains a cedilla-form diacritic) can reuse; this class does
    /// not itself perform that validation pass.
    /// </summary>
    public static bool IsCanonicalForm(char c) =>
        c is 'ș' or 'ț' or 'Ș' or 'Ț';
}
