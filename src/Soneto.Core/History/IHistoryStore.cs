namespace Soneto.Core.History;

/// <summary>
/// Append/query persistence for completed dictations (plan §3.5). Unlike
/// <see cref="Soneto.Core.Configuration.ConfigService"/>/<see cref="Soneto.Core.Dictionary.DictionaryService"/>,
/// this is not a hot-reloading "watch a hand-edited file" component — it's an append/query
/// store with no external file for a human to edit, so there's deliberately no
/// <c>FileSystemWatcher</c>/debounce-timer/<c>Current</c>+change-event shape here.
///
/// <para>
/// <b>Disposal contract:</b> every member below is documented as "never throws" with respect
/// to environmental I/O/SQLite failures (a bad path, a locked file, a dropped table, etc.) —
/// that promise is about failures outside the caller's control, not about caller misuse. Once
/// <see cref="IAsyncDisposable.DisposeAsync"/> has completed, calling ANY member below is
/// caller misuse and is expected/documented to throw <see cref="ObjectDisposedException"/>,
/// exactly like any other <see cref="IAsyncDisposable"/> — this is the normal BCL disposal
/// contract, not a violation of the "never throws on I/O failure" promise above.
/// </para>
/// </summary>
public interface IHistoryStore : IAsyncDisposable
{
    /// <summary>
    /// Raised after <see cref="AppendAsync"/>/<see cref="PurgeOlderThanAsync"/>/
    /// <see cref="PanicWipeAsync"/> actually change what a subsequent <see cref="SearchAsync"/>
    /// call would return (plan §3.10's "History view live-refresh" requirement). This is the ONE
    /// piece of plumbing a UI observer (e.g. a running <c>HistoryViewModel</c>) needs to know "go
    /// re-query me" without coupling that UI layer directly to whatever produced the change (e.g.
    /// <c>SessionController.DictationCompleted</c>) -- <see cref="IHistoryStore"/> stays the only
    /// thing a history-browsing UI ever talks to.
    /// <list type="bullet">
    /// <item><see cref="AppendAsync"/> raises this only when the write actually succeeded
    /// (not on a failed/-1 return).</item>
    /// <item><see cref="PurgeOlderThanAsync"/> raises this only when at least one row was
    /// actually deleted (not on a 0-rows-affected/failed return).</item>
    /// <item><see cref="PanicWipeAsync"/> raises this whenever the wipe itself did not fail --
    /// unconditionally, even if the store happened to already be empty, since "the store was
    /// just wiped" is itself meaningful state a UI may want to react to (e.g. clearing a
    /// currently-selected entry).</item>
    /// </list>
    /// Fires on WHATEVER THREAD called the triggering method -- e.g. potentially
    /// <c>SessionController</c>'s own worker thread, if a caller subscribed
    /// <see cref="AppendAsync"/> to <c>DictationCompleted</c> fire-and-forget. A subscriber that
    /// touches UI-observable state MUST marshal through the UI thread itself; this event makes no
    /// threading promise beyond "the thread that made the call." A throwing subscriber is caught
    /// and logged by the implementation, never allowed to propagate back into the caller that
    /// triggered the mutation (the same defensive-backstop discipline
    /// <see cref="Soneto.Core.SessionController.DictationCompleted"/>'s own raise-site already
    /// established) -- see <see cref="SqliteHistoryStore"/>'s doc comment for the concrete
    /// mechanics.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Persists one completed dictation. Runs on the hot dictation-completion path (§3.6/§3.7)
    /// and MUST NEVER THROW and must be safe to call fire-and-forget without awaiting inline
    /// before text is already injected — losing one history row must never affect the
    /// dictation session that produced it. Returns the assigned rowid, or -1 if the write
    /// failed (logged, not thrown). The <paramref name="entry"/>'s own <see cref="HistoryEntry.Id"/>
    /// is ignored on the way in (see that record's doc comment) — the database assigns the
    /// real Id.
    /// </summary>
    Task<long> AppendAsync(HistoryEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Returns entries newest-first, paged by <paramref name="limit"/>/<paramref name="offset"/>.
    /// <list type="bullet">
    /// <item><paramref name="query"/> null/empty/whitespace: a browse-everything mode — every
    /// entry ordered strictly by <c>timestamp_utc DESC</c>, no FTS matching involved at all.</item>
    /// <item><paramref name="query"/> non-blank: matched against <see cref="HistoryEntry.FinalText"/>
    /// (FTS5 full-text match if available on this machine, otherwise a documented
    /// <c>LIKE '%term%'</c> fallback — see <see cref="SqliteHistoryStore"/>'s class doc comment),
    /// still ordered newest-first among the matches.</item>
    /// </list>
    /// <paramref name="offset"/> rows are skipped before <paramref name="limit"/> rows are
    /// returned, in both modes, for simple page-forward paging.
    /// </summary>
    Task<IReadOnlyList<HistoryEntry>> SearchAsync(
        string? query, int limit, int offset, CancellationToken ct = default);

    /// <summary>
    /// Deletes every entry whose <see cref="HistoryEntry.Timestamp"/> is older than
    /// <c>DateTimeOffset.UtcNow - age</c> (the §3.14 auto-delete retention sweep). Returns the
    /// number of rows deleted, or 0 on a failure (logged, not thrown).
    /// </summary>
    Task<int> PurgeOlderThanAsync(TimeSpan age, CancellationToken ct = default);

    /// <summary>
    /// Deletes ALL history rows (the §3.14/§3.15 panic-wipe control). Never throws.
    /// </summary>
    Task PanicWipeAsync(CancellationToken ct = default);
}
