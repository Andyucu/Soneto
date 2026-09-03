namespace Soneto.Core.Dictionary;

/// <summary>
/// The root shape of <c>dictionary.json</c> -- what actually gets deserialized from the
/// file's raw text (Phase 2 plan §2.4/build order item 1).
///
/// Deliberately an OBJECT wrapper around the entry array, not a bare JSON array at the
/// document root, even though a bare array would be marginally simpler for a first cut.
/// Reasoning: <c>dictionary.json</c> is a hand-edited config file (per plan §2.1, no UI
/// exists yet) in the same family as <c>config.json</c>, and object-wrapped roots are more
/// forward-compatible for that kind of file -- it leaves room to add further top-level
/// fields later (a schema version for migrations being the obvious one, added below
/// pre-emptively since "will I need to version this schema" is not really an open question
/// for a hand-edited file that's going to gain fields over Phase 2/3/4) without a
/// breaking root-shape change. A bare top-level array can never grow sibling fields
/// without becoming this same wrapper shape anyway, so there's no real cost to choosing
/// the wrapper now.
///
/// This type is intentionally just the JSON DTO -- config-vs-runtime split (validation,
/// duplicate-Id rejection, automaton construction, etc.) is item 9's
/// <c>DictionaryConfig</c>/<c>DictionaryService</c> job (§2.7), not this item's.
///
/// CONSTRAINT for item 9: <c>JsonSerializer.Deserialize&lt;DictionaryDocument&gt;(...)</c>
/// is all-or-nothing -- a single entry with an unrecognized <c>type</c> discriminator or a
/// missing required field fails deserialization of the WHOLE document (no entries come
/// back at all, not even the valid ones surrounding the bad one). That's fine here, but
/// item 9's "never crash on a bad file, skip and log the one bad entry" requirement (§2.7)
/// can't be built on top of this type's deserialization as-is; it will need to parse
/// <c>entries</c> as a raw <c>JsonElement</c> array and deserialize each element
/// individually inside its own try/catch instead.
///
/// NOTE: this is a plain class, not a record -- comparing two instances with
/// <c>Assert.Equal</c>/<c>==</c> uses reference equality, not structural equality. Making
/// it a record wouldn't fix this either, since <see cref="Entries"/> is a
/// <c>List&lt;DictionaryEntry&gt;</c> and <c>List&lt;T&gt;</c> doesn't override
/// <c>Equals</c>, so record-synthesized equality would still fall back to the list's own
/// reference equality. Compare <see cref="Entries"/> element-by-element instead (see
/// <c>DictionaryEntryTests</c>'s round-trip test for the established pattern).
/// </summary>
public sealed class DictionaryDocument
{
    /// Schema version for this file's shape, bumped only on a breaking change to the
    /// entry-type schema itself. Not validated/enforced by anything in Phase 2 -- purely
    /// a forward-compatibility hook for the loader item 9 builds.
    public int SchemaVersion { get; init; } = 1;

    public List<DictionaryEntry> Entries { get; init; } = new();
}
