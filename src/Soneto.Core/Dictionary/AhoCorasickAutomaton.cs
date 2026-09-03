namespace Soneto.Core.Dictionary;

/// <summary>
/// A single non-overlapping match returned by <see cref="AhoCorasickAutomaton{TValue}.Match"/>.
///
/// <para>
/// <b><see cref="Start"/>/<see cref="Length"/> always refer to the ORIGINAL, untouched input
/// string</b> passed to <c>Match</c> -- never to the folded/lowercased/glue-stripped internal
/// match-key form the automaton actually walks. This is deliberate (Phase 2 work item 3, per
/// the build plan's §2.5 rule 3 split): matching is case-insensitive and diacritic-folded, but
/// callers (e.g. a future <c>DictionaryEngineProcessor</c>, item 4) need the real original-case
/// text at the matched span to decide how to case a replacement. Slicing
/// <c>input.Substring(match.Start, match.Length)</c> always yields the genuine original text,
/// with its real casing and real internal glue characters (spaces/hyphens) intact.
/// </para>
/// </summary>
public sealed record AhoCorasickMatch<TValue>(int Start, int Length, TValue Value);

/// <summary>
/// Phase 2 work item 3 (§2.5 rules 4-6): a generic, reusable Aho-Corasick trie + matcher.
///
/// <para>
/// Deliberately decoupled from <see cref="DictionaryEntry"/>/<c>DictionaryDocument</c> -- this
/// class knows nothing about correction pairs, vocabulary terms, or any other dictionary
/// concept. It is constructed from plain <c>(string Pattern, TValue Value)</c> pairs, which
/// keeps it independently unit-testable with plain strings and reusable by whatever a future
/// <c>DictionaryEngineProcessor</c> (item 4) needs, with <c>TValue</c> most likely a
/// <see cref="DictionaryEntry"/> reference or an entry ID at that call site.
/// </para>
///
/// <para>
/// <b>Match-key transform (rules 2 + 3):</b> patterns are inserted into the trie, and input
/// text is walked, using a match-key that is both diacritic-folded (via
/// <see cref="DiacriticFolder.FoldChar"/>) and lower-invariant-cased. This makes matching
/// case-insensitive and Romanian-diacritic-equivalence-aware, but the transform is used ONLY to
/// decide whether/where a match occurs -- every <see cref="AhoCorasickMatch{TValue}"/> returned
/// carries positions into the real, original, unfolded, original-case input string. The folded
/// match key itself is never exposed.
/// </para>
///
/// <para>
/// <b>Glue-tolerant boundaries (rule 5):</b> internal whitespace and hyphen ('-') runs are
/// treated as pure glue and are STRIPPED ENTIRELY from the match-key stream, on both the
/// pattern side (at construction time) and the input side (at match time) -- so a pattern
/// written as <c>"web methods"</c> canonicalizes to the same key stream as <c>"web-methods"</c>
/// and <c>"webmethods"</c> (zero separator), and all three forms of input text hit the same
/// rule. Stripped glue characters are never inserted into the trie and never advance the
/// automaton's state; instead, this class tracks a parallel mapping from each surviving
/// match-key character back to its original input index, so a match's <see cref="AhoCorasickMatch{TValue}.Start"/>/<see cref="AhoCorasickMatch{TValue}.Length"/>
/// can be reconstructed to span the REAL original text -- including any interior glue
/// characters between the match's first and last surviving key character, since those are part
/// of what a human reading the original input would consider "the matched text". Rule 6's
/// full-token-boundary check (below) is applied to this reconstructed original span, not to the
/// glue-stripped internal key stream, so glue-stripping never changes token-boundary semantics.
/// </para>
///
/// <para>
/// <b>Full-token boundaries (rule 6, the safety-critical one):</b> a candidate match is
/// discarded unless the character immediately before its original-text start (if any) and the
/// character immediately after its original-text end (if any) are both NOT
/// <see cref="char.IsLetterOrDigit(char)"/> -- consistent with
/// <see cref="Evaluation.WordErrorRateCalculator"/>'s Unicode letter/digit tokenization
/// convention elsewhere in this codebase. This is what makes a rule for <c>"cloud"</c> correctly
/// refuse to match inside <c>"Cloudflare"</c>.
/// </para>
///
/// <para>
/// <b>Longest-match-first, single pass, no cascading (rule 4):</b> <see cref="Match"/> walks the
/// ENTIRE input exactly once. Every position where multiple patterns could match (overlapping
/// start positions, or multiple patterns ending at the same position) is resolved by picking
/// the surviving candidate with the greatest original-text <see cref="AhoCorasickMatch{TValue}.Length"/>
/// first, then (for ties) the earliest <see cref="AhoCorasickMatch{TValue}.Start"/>; already-claimed
/// input positions are then excluded from every subsequent, shorter/later candidate, so the
/// final returned list is guaranteed non-overlapping. <see cref="Match"/> is a pure function of
/// its input string: it never re-invokes itself, and it has no concept of "replacement text" at
/// all -- it only ever scans the ORIGINAL string passed in. A caller that replaces matched spans
/// and then calls <see cref="Match"/> again on the replaced text would be doing so of its own
/// accord; this class provides no looping/cascading behavior itself, by design (see the build
/// plan's own "that way lies infinite cascades" reasoning, §2.5 rule 4).
/// </para>
///
/// <para>
/// <b>Immutable after construction, safe for concurrent use.</b> All mutable trie state (nodes,
/// children, failure links, per-node output lists) is populated exclusively inside the
/// constructor and never mutated again. <see cref="Match"/> only reads that fixed state and
/// allocates its own local, per-call working data (the key stream, the candidate list, the
/// occupied-positions array) -- it never touches any shared mutable field. Consequently a single
/// <see cref="AhoCorasickAutomaton{TValue}"/> instance can be reused and called from
/// <see cref="Match"/> repeatedly, including concurrently from multiple threads, with no
/// synchronization required.
/// </para>
///
/// <para>
/// <b>Construction complexity caveat.</b> <see cref="BuildFailureLinks"/> builds each node's
/// output list by eagerly copying its failure node's already-computed output list (rather than
/// lazily following an output-chain link at match time). This is the standard, simplest
/// Aho-Corasick construction and is O(total pattern length) for realistic rule sets, but it
/// degrades toward O(k^2) in the length of the longest chain of patterns that are each other's
/// suffix (e.g. "a", "ba", "cba", "dcba", ...), since every node in the chain re-copies its
/// predecessor's whole output list. At the dictionary sizes this engine targets (tens to
/// low-hundreds of entries, per item 4's ~100-case suite) this is a non-issue in practice; if a
/// dictionary ever scales into the low thousands of entries with many deep suffix chains and
/// construction/hot-reload time becomes measurable, this is the first place to look and the fix
/// is switching to lazy output-chain traversal at match time instead of eager copying here.
/// </para>
/// </summary>
public sealed class AhoCorasickAutomaton<TValue>
{
    private readonly Node _root = new();

    /// <summary>
    /// Builds the trie and failure links once, up front, from the given (pattern, value) pairs.
    /// Not meant to be rebuilt per call -- construct one instance per rule set and reuse it for
    /// every <see cref="Match"/> call.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A pattern is null/empty, canonicalizes (after glue-stripping) to an empty match key and
    /// could therefore never usefully match anything, or canonicalizes to the SAME match key as
    /// another pattern already added (e.g. two entries differing only by diacritic form, or by
    /// glue-character choice) -- which pattern would win at match time would otherwise be an
    /// unspecified, non-deterministic implementation detail, unacceptable for this class's
    /// safety-critical role, so this is rejected at construction time instead.
    /// </exception>
    public AhoCorasickAutomaton(IEnumerable<(string Pattern, TValue Value)> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var seenKeys = new Dictionary<string, string>();

        foreach (var (pattern, value) in patterns)
        {
            if (string.IsNullOrEmpty(pattern))
                throw new ArgumentException("A dictionary pattern must not be null or empty.", nameof(patterns));

            var key = BuildKeyStream(pattern, trackOriginalIndices: false).KeyChars;
            if (key.Count == 0)
            {
                throw new ArgumentException(
                    $"Pattern \"{pattern}\" canonicalizes to an empty match key (it consists " +
                    "entirely of glue characters -- whitespace/hyphen) and could never match.",
                    nameof(patterns));
            }

            var keyString = new string(key.ToArray());
            if (seenKeys.TryGetValue(keyString, out var earlierPattern))
            {
                throw new ArgumentException(
                    $"Pattern \"{pattern}\" collides with pattern \"{earlierPattern}\" -- both " +
                    "canonicalize (after diacritic-folding, lowercasing, and glue-stripping) to " +
                    $"the same match key \"{keyString}\". Which of the two would win at match " +
                    "time is unspecified; rename/merge one of these patterns in the dictionary.",
                    nameof(patterns));
            }
            seenKeys[keyString] = pattern;

            Insert(key, value);
        }

        BuildFailureLinks();
    }

    /// <summary>
    /// Runs the entire <paramref name="input"/> string through the automaton in a single pass
    /// and returns the resolved, non-overlapping set of matches (longest-match-first, then
    /// earliest-start-position on length ties -- see the class-level doc comment for the full
    /// resolution/boundary/glue rules). Returned in ascending <see cref="AhoCorasickMatch{TValue}.Start"/>
    /// order. Never re-invokes itself and never scans anything other than the literal
    /// <paramref name="input"/> passed in -- a caller must never feed a post-replacement string
    /// back into this method expecting cascading behavior; it does not exist here.
    /// </summary>
    public IReadOnlyList<AhoCorasickMatch<TValue>> Match(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Length == 0)
            return [];

        var (keyChars, originalIndex) = BuildKeyStream(input, trackOriginalIndices: true);

        var candidates = new List<(int Start, int Length, TValue Value)>();

        var node = _root;
        for (int i = 0; i < keyChars.Count; i++)
        {
            char c = keyChars[i];

            while (node != _root && !node.Children.ContainsKey(c))
                node = node.Fail!;

            node = node.Children.TryGetValue(c, out var next) ? next : _root;

            foreach (var (keyLength, value) in node.Output)
            {
                int startKeyIndex = i - keyLength + 1;
                if (startKeyIndex < 0)
                    continue; // defensive; should be unreachable given how Output is built

                int originalStart = originalIndex[startKeyIndex];
                int originalLastCharIndex = originalIndex[i];
                int originalLength = originalLastCharIndex - originalStart + 1;

                if (!IsFullTokenBoundary(input, originalStart, originalLength))
                    continue;

                candidates.Add((originalStart, originalLength, value));
            }
        }

        return ResolveOverlaps(candidates, input.Length);
    }

    /// <summary>
    /// Rule 6: the character immediately before <paramref name="start"/> (if any) and
    /// immediately after the matched span (if any), in the ORIGINAL input string, must not be a
    /// Unicode letter or digit.
    /// </summary>
    private static bool IsFullTokenBoundary(string input, int start, int length)
    {
        if (start > 0 && char.IsLetterOrDigit(input[start - 1]))
            return false;

        int afterIndex = start + length;
        if (afterIndex < input.Length && char.IsLetterOrDigit(input[afterIndex]))
            return false;

        return true;
    }

    /// <summary>
    /// Rule 4's overlap resolution: sort surviving candidates by original-text length
    /// descending, then start ascending; greedily accept each candidate whose span doesn't
    /// overlap any already-accepted span. Returns the accepted set in ascending start order.
    /// </summary>
    private static IReadOnlyList<AhoCorasickMatch<TValue>> ResolveOverlaps(
        List<(int Start, int Length, TValue Value)> candidates, int inputLength)
    {
        if (candidates.Count == 0)
            return [];

        candidates.Sort((a, b) =>
        {
            int byLength = b.Length.CompareTo(a.Length);
            return byLength != 0 ? byLength : a.Start.CompareTo(b.Start);
        });

        var occupied = new bool[inputLength];
        var accepted = new List<(int Start, int Length, TValue Value)>();

        foreach (var candidate in candidates)
        {
            bool overlaps = false;
            for (int p = candidate.Start; p < candidate.Start + candidate.Length; p++)
            {
                if (occupied[p])
                {
                    overlaps = true;
                    break;
                }
            }

            if (overlaps)
                continue;

            for (int p = candidate.Start; p < candidate.Start + candidate.Length; p++)
                occupied[p] = true;

            accepted.Add(candidate);
        }

        accepted.Sort((a, b) => a.Start.CompareTo(b.Start));

        var result = new List<AhoCorasickMatch<TValue>>(accepted.Count);
        foreach (var (start, length, value) in accepted)
            result.Add(new AhoCorasickMatch<TValue>(start, length, value));

        return result;
    }

    /// <summary>
    /// Builds the match-key character stream for <paramref name="text"/>: each character is
    /// fold-then-lowercased via <see cref="DiacriticFolder.FoldChar"/> +
    /// <see cref="char.ToLowerInvariant(char)"/>, and glue characters (whitespace or '-') are
    /// dropped entirely rather than mapped to a canonical separator, per rule 5's
    /// zero-separator requirement. When <paramref name="trackOriginalIndices"/> is true, also
    /// returns a parallel list mapping each surviving key-stream character back to its index in
    /// the original <paramref name="text"/>, so match spans can be reconstructed against the
    /// real input.
    /// </summary>
    private static (List<char> KeyChars, List<int> OriginalIndex) BuildKeyStream(string text, bool trackOriginalIndices)
    {
        var keyChars = new List<char>(text.Length);
        var originalIndex = trackOriginalIndices ? new List<int>(text.Length) : [];

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (IsGlue(c))
                continue;

            keyChars.Add(char.ToLowerInvariant(DiacriticFolder.FoldChar(c)));
            if (trackOriginalIndices)
                originalIndex.Add(i);
        }

        return (keyChars, originalIndex);
    }

    private static bool IsGlue(char c) => char.IsWhiteSpace(c) || c == '-';

    private void Insert(List<char> key, TValue value)
    {
        var node = _root;
        foreach (char c in key)
        {
            if (!node.Children.TryGetValue(c, out var next))
            {
                next = new Node();
                node.Children[c] = next;
            }
            node = next;
        }
        node.OwnEntries.Add((key.Count, value));
    }

    /// <summary>
    /// Standard textbook Aho-Corasick BFS failure-link construction, plus output-chaining: each
    /// node's <see cref="Node.Output"/> is the union of its own directly-inserted patterns and
    /// its failure node's (already-computed, by BFS order) <see cref="Node.Output"/> -- this is
    /// what lets a single position in the input yield every pattern that ends there, including
    /// shorter patterns reachable only via a suffix/failure link, not just the deepest one.
    /// </summary>
    private void BuildFailureLinks()
    {
        var queue = new Queue<Node>();
        _root.Fail = _root;

        foreach (var child in _root.Children.Values)
        {
            child.Fail = _root;
            queue.Enqueue(child);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            current.Output.AddRange(current.OwnEntries);
            current.Output.AddRange(current.Fail!.Output);

            foreach (var (c, child) in current.Children)
            {
                var fail = current.Fail;
                while (fail != _root && !fail!.Children.ContainsKey(c))
                    fail = fail.Fail;

                child.Fail = fail!.Children.TryGetValue(c, out var target) && target != child
                    ? target
                    : _root;

                queue.Enqueue(child);
            }
        }
    }

    private sealed class Node
    {
        public Dictionary<char, Node> Children { get; } = new();
        public Node? Fail;

        /// <summary>Patterns whose match-key ends exactly at this node (own trie depth == pattern key length).</summary>
        public List<(int KeyLength, TValue Value)> OwnEntries { get; } = new();

        /// <summary>Own entries plus everything reachable via the failure-link chain, computed once during BFS.</summary>
        public List<(int KeyLength, TValue Value)> Output { get; } = new();
    }
}
