using System.Reflection;

namespace Soneto.Core.Dictionary;

/// <summary>
/// Phase 2 work item 8 (§2.8 of <c>Docs/soneto-implementation-plan-phase2.md</c>, originally
/// §6.2 rule 6 of <c>Docs/dictation-app-build-plan.md</c>): a bundled EN+RO word-frequency list
/// used ONLY by <see cref="DictionaryCollisionWarnings"/>'s authoring-time collision check (see
/// that class's doc comment) -- this is deliberately NOT the same thing as
/// <see cref="AhoCorasickAutomaton{TValue}"/>'s runtime full-token-boundary safety net (that one
/// protects a single already-authored <c>CorrectionPair</c> like "cloud" -&gt; "Cloudflare" from
/// firing inside unrelated words at transcription time; THIS class exists to warn a human
/// editing <c>dictionary.json</c> by hand that a pattern they just typed, e.g. <c>From: "cloud"</c>,
/// IS itself an ordinary common word and therefore risky as a correction target in the first
/// place).
///
/// <para>
/// <b>Provenance and scope -- read before extending or trusting this list for anything beyond
/// its stated purpose:</b> this is a hand-curated STARTING set of a few hundred of the most
/// common EN/RO function and content words (articles, prepositions, pronouns, common
/// verbs/nouns/adjectives, days/months/numbers), written from general, well-known linguistic
/// knowledge -- NOT sourced from an external frequency corpus or dataset. Per the plan's own
/// explicit allowance ("a few thousand of the most common words in each language is enough,
/// doesn't need to be exhaustive"), this is intentionally smaller and non-exhaustive; the exact
/// same honest-scoping principle item 7's <see cref="FillerWordStripper.DefaultFillerWords"/>
/// used ("extend from real usage" rather than trying to be exhaustive up front) applies here.
/// If this proves too permissive (misses common words that should have warned) or too
/// restrictive (flags words that aren't actually risky in practice) once real dictionary
/// authoring happens, extend <c>Resources/common-words-en.txt</c> / <c>common-words-ro.txt</c>
/// with real observed cases -- do not attempt to hand-author "a few thousand words" up front.
/// </para>
///
/// <para>
/// <b>Storage/loading:</b> two plain embedded text resources, one lowercase word per line,
/// mirroring the established <c>warmup-en.wav</c> / <c>silero_vad.onnx</c> embedded-resource
/// convention from Phase 1 items 3/5 (see the csproj's <c>EmbeddedResource</c> entries). Loaded
/// once into an immutable, case-insensitive <see cref="HashSet{T}"/> the first time
/// <see cref="Instance"/> is touched (mirrors <see cref="Asr.SileroVadDetector"/>'s and
/// <see cref="AhoCorasickAutomaton{TValue}"/>'s immutable-after-construction pattern) -- there is
/// no reload/mutation path, since this list only ever changes by shipping a new build.
/// </para>
/// </summary>
public sealed class WordFrequencyList
{
    private const string EnglishResourceName = "Soneto.Core.Dictionary.Resources.common-words-en.txt";
    private const string RomanianResourceName = "Soneto.Core.Dictionary.Resources.common-words-ro.txt";

    private readonly HashSet<string> _words;

    /// <summary>
    /// The process-wide singleton, lazily built from the embedded EN+RO resources the first
    /// time it's touched. There is exactly one list (EN and RO combined) rather than two
    /// separate per-language lists, because the collision check (§2.8) doesn't need to know
    /// or care which language a given common word belongs to -- either language colliding is
    /// equally worth warning about.
    /// </summary>
    public static WordFrequencyList Instance { get; } = new(
        LoadResourceWords(EnglishResourceName),
        LoadResourceWords(RomanianResourceName));

    /// <summary>
    /// Exposed for tests that want a list built from an explicit word set rather than the
    /// embedded resources.
    /// </summary>
    public WordFrequencyList(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        _words = new HashSet<string>(words.Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length > 0), StringComparer.OrdinalIgnoreCase);
    }

    private WordFrequencyList(IEnumerable<string> englishWords, IEnumerable<string> romanianWords)
        : this(englishWords.Concat(romanianWords))
    {
    }

    /// <summary>Number of distinct words loaded (case-insensitively deduplicated).</summary>
    public int Count => _words.Count;

    /// <summary>
    /// True if <paramref name="word"/> IS one of the bundled common EN/RO words, verbatim
    /// (case-insensitive exact match -- not "contains", not a substring check). Whitespace
    /// around <paramref name="word"/> is trimmed before comparison; a null/empty/whitespace-only
    /// input always returns false.
    /// </summary>
    public bool IsCommonWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return false;

        return _words.Contains(word.Trim());
    }

    private static List<string> LoadResourceWords(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded word-frequency resource '{resourceName}' not found in {asm.FullName}.");
        using var reader = new StreamReader(stream);

        var words = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                words.Add(trimmed);
        }

        return words;
    }
}
