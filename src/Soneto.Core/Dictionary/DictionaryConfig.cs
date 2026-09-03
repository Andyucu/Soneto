namespace Soneto.Core.Dictionary;

/// <summary>
/// The VALIDATED, ready-to-consume runtime shape of a loaded <c>dictionary.json</c> -- the
/// config-vs-runtime split <see cref="DictionaryDocument"/>'s own doc comment calls out:
/// <see cref="DictionaryDocument"/> is the raw, all-or-nothing JSON DTO produced by a single
/// whole-document deserialize; this type is what <see cref="DictionaryService"/> actually hands
/// out via <see cref="IDictionaryService.Current"/> AFTER per-entry JSON error isolation and
/// §2.7's three load-time validation rules have already run.
///
/// <para>
/// Deliberately a plain, immutable data holder -- no behavior, no service methods. It carries
/// both the entries that made it through validation and enough diagnostics about what got
/// rejected (and why) for a caller/log/future-UI to explain a load's outcome without having to
/// re-derive it. <see cref="RejectedEntries"/> is informational only; <see cref="Entries"/> is
/// the only thing any <c>IPostProcessor</c> should ever consume.
/// </para>
/// </summary>
public sealed class DictionaryConfig
{
    public static DictionaryConfig Empty { get; } = new(
        entries: [],
        rejectedEntries: [],
        schemaVersion: 1);

    public DictionaryConfig(
        IReadOnlyList<DictionaryEntry> entries,
        IReadOnlyList<RejectedDictionaryEntry> rejectedEntries,
        int schemaVersion)
    {
        Entries = entries;
        RejectedEntries = rejectedEntries;
        SchemaVersion = schemaVersion;
    }

    /// The entries that survived per-entry JSON parsing AND §2.7's validation rules (regex
    /// compile check, duplicate-Id whole-file rejection, empty From/Phrase rejection). Includes
    /// both enabled and disabled entries -- exactly as the four <c>IPostProcessor</c>s'
    /// constructors already expect (they each filter <see cref="DictionaryEntry.Enabled"/>
    /// themselves).
    public IReadOnlyList<DictionaryEntry> Entries { get; }

    /// Individually-rejected entries (per-entry JSON parse failures, unparseable RegexRule
    /// patterns, empty From/Phrase) with a human-readable reason each. Empty when the whole
    /// file was rejected outright (duplicate Ids) -- in that case there is no partial entry
    /// list to report on, since the previous good <see cref="DictionaryConfig"/> is retained
    /// wholesale instead.
    public IReadOnlyList<RejectedDictionaryEntry> RejectedEntries { get; }

    public int SchemaVersion { get; }
}

/// <summary>
/// One entry that failed to load, for diagnostics. <see cref="Index"/> is the entry's position
/// in the raw <c>entries</c> JSON array (0-based); <see cref="Id"/> is null when the entry
/// couldn't even be parsed far enough to recover its own Id.
/// </summary>
public sealed record RejectedDictionaryEntry(int Index, string? Id, string Reason);
