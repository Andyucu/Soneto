using Soneto.Core.Abstractions;

namespace Soneto.Core.History;

/// <summary>
/// One completed dictation, persisted by <see cref="IHistoryStore"/> (plan §3.5).
/// </summary>
/// <param name="Id">
/// The SQLite rowid. <b>Sentinel decision:</b> rather than introduce a separate
/// "new entry" input type without an Id, this single record is reused for both writing and
/// reading, with <c>Id = 0</c> conventionally meaning "not yet persisted" for a caller
/// constructing a fresh entry to pass to <see cref="IHistoryStore.AppendAsync"/>. This is safe
/// because SQLite's own <c>INTEGER PRIMARY KEY AUTOINCREMENT</c> rowids start at 1, so 0 can
/// never collide with a real persisted Id, and <see cref="SqliteHistoryStore.AppendAsync"/>
/// ignores whatever Id value it's handed on the way in (the database always assigns the real
/// one). A second DTO type was judged not worth the duplication for a record this small,
/// especially since every other field is identical between the "new" and "persisted" shapes —
/// unlike, say, <c>DictionaryEntry</c>'s type hierarchy, there's no structural difference to
/// justify two types here.
/// </param>
/// <param name="Timestamp">When the dictation completed (UTC on write; stored as sortable
/// ISO-8601 text).</param>
/// <param name="RawText">Pre-post-processing ASR output.</param>
/// <param name="FinalText">Post-chain text, what was actually injected (or attempted).</param>
/// <param name="RulesFired">Dictionary/post-processing rules that fired, in application order.
/// Serialized as a JSON array column; empty (never null) when nothing fired.</param>
/// <param name="RecordingDuration">Wall-clock length of the recorded audio.</param>
/// <param name="ProcessingLatency">Key-up to injected — the plan's own §4 budget metric.</param>
/// <param name="WasInjected">False if injection failed or was skipped; still worth keeping in
/// history.</param>
public sealed record HistoryEntry(
    long Id,
    DateTimeOffset Timestamp,
    string RawText,
    string FinalText,
    IReadOnlyList<AppliedRule> RulesFired,
    TimeSpan RecordingDuration,
    TimeSpan ProcessingLatency,
    bool WasInjected);
