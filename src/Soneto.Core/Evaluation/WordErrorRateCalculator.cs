using System.Text.RegularExpressions;

namespace Soneto.Core.Evaluation;

/// <summary>
/// Word Error Rate (WER) result for a single reference/hypothesis pair: standard
/// word-level Levenshtein distance (substitutions + insertions + deletions) over the
/// reference token count, per plan §1.13's exact spec for the corpus regression test —
/// "Levenshtein on tokens after lowercasing, stripping punctuation" — and per spike S2's
/// own method description (§"S2 — Romanian accuracy on your voice", step 4).
/// </summary>
public sealed record WerResult(
    int Substitutions,
    int Insertions,
    int Deletions,
    int ReferenceTokenCount)
{
    /// <summary>Total edit distance (substitutions + insertions + deletions).</summary>
    public int EditDistance => Substitutions + Insertions + Deletions;

    /// <summary>
    /// WER = edit distance / reference token count, as a fraction (0.0-1.0, not a
    /// percentage — multiply by 100 for the percentage form used in reporting).
    /// <see cref="double.PositiveInfinity"/> if the reference has zero tokens (a
    /// degenerate case: WER is undefined, not zero, when there's nothing to compare
    /// against and the hypothesis isn't empty; 0 if both are empty).
    /// </summary>
    public double Wer => ReferenceTokenCount == 0
        ? (EditDistance == 0 ? 0.0 : double.PositiveInfinity)
        : (double)EditDistance / ReferenceTokenCount;
}

/// <summary>
/// Pure, platform-agnostic Word Error Rate calculator — belongs alongside
/// <see cref="Soneto.Core.PostProcessing"/>'s other pure text-comparison logic, not
/// tied to <c>ITranscriber</c>/ASR in any way, so it's independently unit-testable
/// with hand-computed values and reusable outside the corpus-regression test harness
/// (e.g. from a future CLI diagnostic).
///
/// <para>
/// <b>Tokenization (plan §1.13 / §S2's exact method):</b> lowercase, strip punctuation,
/// split on whitespace. "Strip punctuation" removes any character that is not a Unicode
/// letter, digit, or whitespace — this deliberately keeps diacritics (ș/ț/ă/â/î and
/// their cedilla variants) as part of the word they belong to, since collapsing them
/// away would make WER blind to exactly the accuracy differences (Romanian diacritics)
/// this calculator exists to measure.
/// </para>
///
/// <para>
/// <b>Deliberate simplification: apostrophes and hyphens are stripped as punctuation,
/// splitting contractions/compounds into separate tokens</b> — e.g. <c>"don't"</c> →
/// <c>["don","t"]</c>, <c>"well-known"</c> → <c>["well","known"]</c>. This is not a bug (the
/// edit-distance math over whatever tokens come out is still correct), but it means a
/// reference/hypothesis pair a human would consider equivalent (reference <c>"don't"</c> vs.
/// ASR output <c>"dont"</c>) can still count as a real edit once tokenized this way, since
/// <c>"don't"</c> → two tokens <c>"don"</c>/<c>"t"</c> while <c>"dont"</c> → one token
/// <c>"dont"</c> — an alignment mismatch, not a clean match. Once real corpus data (S2) lands
/// and produces a WER higher than expected, check whether apostrophe/hyphen tokenization
/// artifacts explain some of the gap before assuming it's a genuine ASR regression.
/// </para>
///
/// <para>
/// <b>Edit distance:</b> classic word-level Levenshtein via full dynamic-programming
/// matrix (not Damerau — transpositions are not a separate operation here, matching the
/// plan's plain "Levenshtein" wording). O(m·n) time/space, which is fine at
/// sentence/utterance-length token counts (tens of words), not intended for
/// corpus-scale/document-scale token counts.
/// </para>
/// </summary>
public static class WordErrorRateCalculator
{
    private static readonly Regex NonWordChar = new(@"[^\p{L}\p{Nd}\s]", RegexOptions.Compiled);

    /// <summary>
    /// Tokenizes per this class's doc comment (lowercase, strip punctuation, split on
    /// whitespace) and returns the resulting word tokens. Exposed publicly since callers
    /// occasionally want the tokens themselves (e.g. for per-bucket aggregation), not just
    /// a single WER number.
    /// </summary>
    public static string[] Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var stripped = NonWordChar.Replace(text.ToLowerInvariant(), " ");
        return stripped.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Computes WER between a reference and hypothesis string, tokenizing both first.</summary>
    public static WerResult Compute(string reference, string hypothesis)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(hypothesis);

        return Compute(Tokenize(reference), Tokenize(hypothesis));
    }

    /// <summary>
    /// Computes WER between already-tokenized reference/hypothesis sequences. Standard
    /// Levenshtein DP over the two token arrays, then backtracks the chosen path once to
    /// classify each edit as a substitution, insertion, or deletion (rather than just
    /// reporting a single combined distance), since the plan's <c>WerResult</c>-shaped
    /// reporting benefits from knowing the breakdown, not just the total.
    /// </summary>
    public static WerResult Compute(IReadOnlyList<string> referenceTokens, IReadOnlyList<string> hypothesisTokens)
    {
        ArgumentNullException.ThrowIfNull(referenceTokens);
        ArgumentNullException.ThrowIfNull(hypothesisTokens);

        int refLen = referenceTokens.Count;
        int hypLen = hypothesisTokens.Count;

        // dp[i, j] = edit distance between referenceTokens[0..i) and hypothesisTokens[0..j)
        var dp = new int[refLen + 1, hypLen + 1];
        for (int i = 0; i <= refLen; i++) dp[i, 0] = i;
        for (int j = 0; j <= hypLen; j++) dp[0, j] = j;

        for (int i = 1; i <= refLen; i++)
        {
            for (int j = 1; j <= hypLen; j++)
            {
                if (referenceTokens[i - 1] == hypothesisTokens[j - 1])
                {
                    dp[i, j] = dp[i - 1, j - 1];
                }
                else
                {
                    int substitution = dp[i - 1, j - 1] + 1;
                    int deletion = dp[i - 1, j] + 1;      // reference token has no match -> deletion
                    int insertion = dp[i, j - 1] + 1;      // hypothesis token is extra -> insertion
                    dp[i, j] = Math.Min(substitution, Math.Min(deletion, insertion));
                }
            }
        }

        // Backtrack from (refLen, hypLen) to (0, 0) to classify each edit, preferring a
        // match when tokens are equal (mirrors the forward recurrence's own preference).
        int substitutions = 0, insertions = 0, deletions = 0;
        int r = refLen, h = hypLen;
        while (r > 0 || h > 0)
        {
            if (r > 0 && h > 0 && referenceTokens[r - 1] == hypothesisTokens[h - 1])
            {
                r--; h--;
                continue;
            }

            int diagCost = r > 0 && h > 0 ? dp[r - 1, h - 1] : int.MaxValue;
            int upCost = r > 0 ? dp[r - 1, h] : int.MaxValue;
            int leftCost = h > 0 ? dp[r, h - 1] : int.MaxValue;
            int current = dp[r, h];

            if (r > 0 && h > 0 && diagCost + 1 == current)
            {
                substitutions++;
                r--; h--;
            }
            else if (r > 0 && upCost + 1 == current)
            {
                deletions++;
                r--;
            }
            else if (h > 0 && leftCost + 1 == current)
            {
                insertions++;
                h--;
            }
            else
            {
                // Defensive; should be unreachable given the DP recurrence above.
                throw new InvalidOperationException("Levenshtein backtrack reached an inconsistent state.");
            }
        }

        return new WerResult(substitutions, insertions, deletions, refLen);
    }
}
