using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Soneto.Core.Abstractions;

namespace Soneto.Core.History;

/// <summary>
/// SQLite-backed <see cref="IHistoryStore"/> implementation (plan §3.5). Constructor takes a
/// file path and creates the schema (idempotent <c>CREATE TABLE/VIRTUAL TABLE IF NOT EXISTS</c>
/// — safe to construct against an existing populated database) lazily, on first use, rather than
/// in the constructor itself, so a bad path never makes construction throw — only the first real
/// call fails (logged, non-throwing return), consistent with every method below.
///
/// <para>
/// <b>FTS5 availability, verified for real (plan §3.5's own explicit instruction) — result: FTS5
/// IS available.</b> This class attempts <c>CREATE VIRTUAL TABLE ... USING fts5(...)</c> against a
/// real connection on first initialization; on this machine's <c>Microsoft.Data.Sqlite</c> 10.0.0
/// + <c>SQLitePCLRaw.bundle_e_sqlite3</c> 3.0.5 bundle this succeeds (confirmed both by the
/// statement not throwing and by a real content round-trip through <c>MATCH</c> in
/// <c>SqliteHistoryStoreTests</c>). The fallback path below exists and is exercised by a
/// dedicated test that forces it, but it is NOT the path this build actually ships on.
/// </para>
///
/// <para>
/// <b>If FTS5 is unavailable</b> (a different machine/bundle without it), <see cref="SearchAsync"/>
/// falls back to a plain <c>final_text LIKE '%term%'</c> scan (escaped for literal
/// <c>%</c>/<c>_</c>/<c>\</c> characters in the query) — slower, no relevance ranking, but
/// functionally adequate for a personal history of a few thousand rows, exactly as the plan's own
/// contingency describes. This is detected once, at initialization, by trying the FTS5 DDL and
/// catching the resulting <see cref="SqliteException"/> if the extension isn't compiled in; it is
/// not re-checked on every call.
/// </para>
///
/// <para>
/// <b>Concurrency model (write side):</b> a single long-lived write <see cref="SqliteConnection"/>,
/// created lazily on first use and held for the store's lifetime, with all mutating access
/// (<see cref="AppendAsync"/>/<see cref="PurgeOlderThanAsync"/>/<see cref="PanicWipeAsync"/>)
/// serialized through one <see cref="SemaphoreSlim"/> (<c>_gate</c>, capacity 1). These three
/// genuinely need to be serialized against each other (they mutate the same tables), and
/// opening a fresh connection (plus re-running the idempotent schema DDL) on every single call
/// is unnecessary overhead for what is, in practice, a low-frequency personal-use store (at most
/// a few writes per minute, driven by actual human dictation speed).
/// </para>
///
/// <para>
/// <b>Concurrency model (read side) — should-fix from item 1's code review:</b>
/// <see cref="SearchAsync"/> originally shared <c>_gate</c> with the write path, which meant a
/// slow search (a large <c>limit</c>, or the <c>LIKE</c> fallback if FTS5 were ever unavailable)
/// could block <see cref="AppendAsync"/>'s hot dictation-completion-path call for the search's
/// full duration — directly contradicting §3.5's "must never add latency to the injection path"
/// requirement, and defeating the whole point of the <c>PRAGMA journal_mode=WAL</c> already set
/// below (WAL's actual benefit is that readers never block the writer and vice versa — but only
/// if reads and writes use separate connections; one shared connection behind one semaphore
/// serializes them regardless of what WAL itself would otherwise allow). Fixed by giving
/// <see cref="SearchAsync"/> its own dedicated read-only <see cref="SqliteConnection"/>
/// (<c>_readConnection</c>, opened with <see cref="SqliteOpenMode.ReadOnly"/> so this path can
/// never itself mutate the database) and its own separate <see cref="SemaphoreSlim"/>
/// (<c>_readGate</c>). A search-side gate is still kept (rather than no gate at all) because a
/// single <see cref="SqliteConnection"/> is not safe for the truly concurrent command execution
/// multiple in-flight <see cref="SearchAsync"/> calls could otherwise attempt against it — but
/// since it is now a SEPARATE gate from the write path's <c>_gate</c>, concurrent searches only
/// ever serialize against each other, never against <see cref="AppendAsync"/>. This was done now,
/// while nothing yet calls this store concurrently (item 2's <c>SessionController</c> and item
/// 6's UI search box don't exist yet), specifically to avoid needing a harder retrofit once real
/// concurrent load shows up.
/// </para>
///
/// <para>
/// <b>Disposal:</b> both connections and both gates are guarded by the same "flip <c>_disposed</c>
/// to <c>true</c> under the same lock that guards the resource, and check it again immediately
/// after acquiring that lock, before touching the resource" pattern
/// <see cref="Soneto.Core.Configuration.ConfigService.Dispose"/> already established for itself —
/// see <see cref="DisposeAsync"/>'s own doc comment for the mechanics and why neither
/// <see cref="SemaphoreSlim"/> is itself ever disposed.
/// </para>
///
/// <para>
/// <b><see cref="AppendAsync"/> never throws</b> — see its own doc comment on <see cref="IHistoryStore"/>.
/// Every public method here follows the same "catch <see cref="SqliteException"/>/<see cref="IOException"/>
/// around the real I/O, log via the injected <see cref="ILogger"/>, return a safe default" shape
/// <c>ConfigService</c>/<c>DictionaryService</c> established, adapted to this store's genuinely
/// different (append/query, not hot-reloading config) shape per the work item's own instruction not
/// to force-fit patterns that don't apply (no <c>FileSystemWatcher</c>, no debounce timer, no
/// <c>Current</c>/change-event pair — there is no external file for a human to hand-edit here).
/// That "never throws" promise is specifically about environmental I/O/SQLite failures — it does
/// NOT cover calling any method after <see cref="DisposeAsync"/>, which is caller misuse and is
/// expected to throw <see cref="ObjectDisposedException"/> (see <see cref="IHistoryStore"/>'s own
/// doc comment).
/// </para>
/// </summary>
public sealed class SqliteHistoryStore : IHistoryStore
{
    private readonly ILogger<SqliteHistoryStore> _logger;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _readGate = new(1, 1);

    private SqliteConnection? _connection;
    private SqliteConnection? _readConnection;
    private bool _ftsAvailable;
    private bool _initAttempted;
    private bool _initFailed;
    private bool _disposed;

    public SqliteHistoryStore(ILogger<SqliteHistoryStore> logger, string dbPath)
    {
        _logger = logger;
        _dbPath = dbPath;
    }

    public string DbPath => _dbPath;

    /// <inheritdoc />
    public event EventHandler? Changed;

    public async Task<long> AppendAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (!await EnsureInitializedAsync(ct))
            return -1;

        long id;
        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            var connection = _connection!;
            var rulesJson = JsonSerializer.Serialize(entry.RulesFired ?? []);

            using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = """
                INSERT INTO history
                    (timestamp_utc, raw_text, final_text, rules_fired_json,
                     recording_duration_ms, processing_latency_ms, was_injected)
                VALUES
                    ($timestamp, $raw, $final, $rules, $recMs, $procMs, $injected);
                """;
            insertCommand.Parameters.AddWithValue("$timestamp", ToSortableUtc(entry.Timestamp));
            insertCommand.Parameters.AddWithValue("$raw", entry.RawText);
            insertCommand.Parameters.AddWithValue("$final", entry.FinalText);
            insertCommand.Parameters.AddWithValue("$rules", rulesJson);
            insertCommand.Parameters.AddWithValue(
                "$recMs", (long)Math.Round(entry.RecordingDuration.TotalMilliseconds));
            insertCommand.Parameters.AddWithValue(
                "$procMs", (long)Math.Round(entry.ProcessingLatency.TotalMilliseconds));
            insertCommand.Parameters.AddWithValue("$injected", entry.WasInjected ? 1 : 0);

            await insertCommand.ExecuteNonQueryAsync(ct);

            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT last_insert_rowid();";
            id = (long)(await idCommand.ExecuteScalarAsync(ct))!;
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            // Hard requirement (§3.5): a lost history row must never propagate and affect the
            // dictation session that produced it.
            _logger.LogError(ex,
                "Failed to append history entry to {DbPath}; dropping this entry", _dbPath);
            return -1;
        }
        finally
        {
            _gate.Release();
        }

        // Raised AFTER releasing _gate (item 6, §3.10's live-refresh mechanism) -- a subscriber
        // is arbitrary caller code (e.g. a UI ViewModel's re-query) that must never be allowed to
        // hold up the hot dictation-completion append path for longer than the write itself took.
        RaiseChanged();
        return id;
    }

    public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(
        string? query, int limit, int offset, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (!await EnsureInitializedAsync(ct))
            return [];

        await _readGate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            var connection = _readConnection!;
            using var command = connection.CreateCommand();

            if (string.IsNullOrWhiteSpace(query))
            {
                // Browse-everything mode (§3.10 "newest first") -- no FTS matching at all.
                command.CommandText = """
                    SELECT id, timestamp_utc, raw_text, final_text, rules_fired_json,
                           recording_duration_ms, processing_latency_ms, was_injected
                    FROM history
                    ORDER BY timestamp_utc DESC
                    LIMIT $limit OFFSET $offset;
                    """;
            }
            else if (_ftsAvailable)
            {
                // Quoted as a single FTS5 phrase (embedded quotes doubled) so arbitrary user
                // input -- hyphens, punctuation, etc. -- can never be misparsed as FTS5
                // query-syntax operators; a search box has no business exposing FTS5's operator
                // grammar to the end user.
                var phrase = "\"" + query.Replace("\"", "\"\"") + "\"";
                command.CommandText = """
                    SELECT h.id, h.timestamp_utc, h.raw_text, h.final_text, h.rules_fired_json,
                           h.recording_duration_ms, h.processing_latency_ms, h.was_injected
                    FROM history_fts f
                    JOIN history h ON h.id = f.rowid
                    WHERE history_fts MATCH $query
                    ORDER BY h.timestamp_utc DESC
                    LIMIT $limit OFFSET $offset;
                    """;
                command.Parameters.AddWithValue("$query", phrase);
            }
            else
            {
                // Documented FTS5-unavailable fallback (see class doc comment) -- plain LIKE
                // scan, no ranking.
                command.CommandText = """
                    SELECT id, timestamp_utc, raw_text, final_text, rules_fired_json,
                           recording_duration_ms, processing_latency_ms, was_injected
                    FROM history
                    WHERE final_text LIKE $pattern ESCAPE '\'
                    ORDER BY timestamp_utc DESC
                    LIMIT $limit OFFSET $offset;
                    """;
                var escaped = query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                command.Parameters.AddWithValue("$pattern", $"%{escaped}%");
            }

            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);

            var results = new List<HistoryEntry>();
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(ReadEntry(reader));

            return results;
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogError(ex, "Failed to search history in {DbPath}; returning no results", _dbPath);
            return [];
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async Task<int> PurgeOlderThanAsync(TimeSpan age, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (!await EnsureInitializedAsync(ct))
            return 0;

        int deleted;
        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            var cutoff = DateTimeOffset.UtcNow - age;

            using var command = _connection!.CreateCommand();
            command.CommandText = "DELETE FROM history WHERE timestamp_utc < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", ToSortableUtc(cutoff));

            deleted = await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogError(ex, "Failed to purge history older than {Age} in {DbPath}", age, _dbPath);
            return 0;
        }
        finally
        {
            _gate.Release();
        }

        // Only raised when something actually changed (item 6, §3.10's live-refresh mechanism) --
        // a no-op purge (nothing old enough to delete) has nothing for a UI observer to re-query for.
        if (deleted > 0)
            RaiseChanged();

        return deleted;
    }

    public async Task PanicWipeAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (!await EnsureInitializedAsync(ct))
            return;

        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();

            // A bulk DELETE with no WHERE clause still fires history_ad once per deleted row
            // -- SQLite row triggers fire per-row regardless of whether the statement used a
            // WHERE clause -- verified directly by SqliteHistoryStoreTests (a search after a
            // panic wipe returns nothing, proving history_fts has no stale phantom rows left
            // over from rows history itself no longer has). No separate "also clear
            // history_fts" step is needed when FTS5 is available; when it isn't, there is no
            // history_fts table to clear at all.
            using var command = _connection!.CreateCommand();
            command.CommandText = "DELETE FROM history;";
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogError(ex, "Failed to panic-wipe history in {DbPath}", _dbPath);
            return;
        }
        finally
        {
            _gate.Release();
        }

        // Unconditional on success (item 6, §3.10's live-refresh mechanism) -- unlike
        // PurgeOlderThanAsync, "the store was just wiped" is meaningful even if it was already
        // empty (a UI may need to clear a currently-selected entry, for instance).
        RaiseChanged();
    }

    /// <summary>
    /// Mirrors <see cref="Soneto.Core.Configuration.ConfigService.Dispose"/>'s own fix for the
    /// exact same bug class: <c>_disposed</c> is flipped to <c>true</c> under the SAME
    /// synchronization primitive that guards the resource being disposed (<c>_gate</c> for
    /// <c>_connection</c>, <c>_readGate</c> for <c>_readConnection</c>), and every other access
    /// path (<see cref="EnsureInitializedAsync"/>'s fast path plus every public method's own
    /// <see cref="ThrowIfDisposed"/> calls, both before AND after acquiring the relevant gate)
    /// checks <c>_disposed</c> under that same primitive before touching the resource. That
    /// closes both failure modes the code review found: a caller can no longer race past
    /// <see cref="EnsureInitializedAsync"/>'s fast path and hit a disposed/null <c>_connection</c>
    /// (it now re-checks <c>_disposed</c> too), and a caller already holding the gate when this
    /// method starts waiting is guaranteed to finish and release before this method can flip
    /// <c>_disposed</c> or touch the connection out from under it.
    ///
    /// <para>
    /// Deliberately, unlike <see cref="_connection"/>/<see cref="_readConnection"/>, NEITHER
    /// <see cref="SemaphoreSlim"/> gate is ever disposed here. If a gate were disposed, any
    /// caller concurrently blocked in (or about to call) <c>WaitAsync</c> would get an
    /// <see cref="ObjectDisposedException"/> thrown BY THE SEMAPHORE ITSELF, racing against
    /// (and potentially winning ahead of) the deliberate, checked
    /// <see cref="ObjectDisposedException"/> <see cref="ThrowIfDisposed"/> raises under the same
    /// gate above — two different code paths that could each throw the same exception type for
    /// subtly different reasons, which is exactly the kind of accidental/undefined-order
    /// disposal race this fix exists to eliminate. Leaving the gates undisposed makes every
    /// post-dispose throw come from one deliberate source. The only cost is one un-disposed
    /// <see cref="SemaphoreSlim"/> wait handle per gate for the lifetime of the process — cheap,
    /// and worth it for a guaranteed-single-source disposal error. (Note: this is NOT the same
    /// situation as <c>ConfigService</c>'s own undisposed <c>_gate</c> — that one is a plain
    /// <c>object</c> used only with <c>lock</c>, which has no <c>Dispose</c> method at all and so
    /// never faced this trade-off in the first place. The reasoning above stands on its own.)
    /// </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;

            await _readGate.WaitAsync();
            try
            {
                _disposed = true;

                if (_connection is not null)
                {
                    await _connection.DisposeAsync();
                    _connection = null;
                }

                if (_readConnection is not null)
                {
                    await _readConnection.DisposeAsync();
                    _readConnection = null;
                }
            }
            finally
            {
                _readGate.Release();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if <see cref="DisposeAsync"/> has already
    /// completed. Called both BEFORE acquiring a gate (a fast, best-effort check — avoids
    /// pointless work when disposal already happened) and AGAIN immediately after acquiring the
    /// relevant gate, before touching <see cref="_connection"/>/<see cref="_readConnection"/> —
    /// only the second check is what actually closes the race, since <see cref="DisposeAsync"/>
    /// also flips <c>_disposed</c> under that same gate (see its own doc comment).
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SqliteHistoryStore));
    }

    /// <summary>
    /// Raises <see cref="Changed"/>, catching and logging any subscriber exception rather than
    /// letting it propagate -- the same defensive-backstop discipline
    /// <see cref="Soneto.Core.SessionController"/>'s own <c>DictationCompleted</c> raise-site
    /// already established (see that class's build-order addendum for why this specific bug
    /// class -- a throwing event subscriber breaking the caller that raised the event -- is worth
    /// guarding against explicitly rather than trusting every future subscriber to never throw).
    /// A throwing <see cref="Changed"/> subscriber must never be able to make
    /// <see cref="AppendAsync"/> (on the hot dictation-completion path) return an exception
    /// instead of the row id it already successfully wrote.
    /// </summary>
    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "IHistoryStore.Changed subscriber threw; ignoring so it cannot affect the " +
                "operation that raised it");
        }
    }

    private async Task<bool> EnsureInitializedAsync(CancellationToken ct)
    {
        ThrowIfDisposed();

        if (_initAttempted)
            return !_initFailed;

        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();

            if (_initAttempted)
                return !_initFailed;

            try
            {
                var dir = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync(ct);

                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA journal_mode=WAL;";
                    await pragma.ExecuteNonQueryAsync(ct);
                }

                using (var createTable = connection.CreateCommand())
                {
                    createTable.CommandText = """
                        CREATE TABLE IF NOT EXISTS history (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            timestamp_utc TEXT NOT NULL,
                            raw_text TEXT NOT NULL,
                            final_text TEXT NOT NULL,
                            rules_fired_json TEXT NOT NULL,
                            recording_duration_ms INTEGER NOT NULL,
                            processing_latency_ms INTEGER NOT NULL,
                            was_injected INTEGER NOT NULL
                        );
                        """;
                    await createTable.ExecuteNonQueryAsync(ct);
                }

                var ftsAvailable = true;
                try
                {
                    using var createFts = connection.CreateCommand();
                    createFts.CommandText = """
                        CREATE VIRTUAL TABLE IF NOT EXISTS history_fts USING fts5(
                            final_text, content='history', content_rowid='id'
                        );
                        """;
                    await createFts.ExecuteNonQueryAsync(ct);

                    using var createTriggers = connection.CreateCommand();
                    createTriggers.CommandText = """
                        CREATE TRIGGER IF NOT EXISTS history_ai AFTER INSERT ON history BEGIN
                            INSERT INTO history_fts(rowid, final_text) VALUES (new.id, new.final_text);
                        END;
                        CREATE TRIGGER IF NOT EXISTS history_ad AFTER DELETE ON history BEGIN
                            INSERT INTO history_fts(history_fts, rowid, final_text) VALUES('delete', old.id, old.final_text);
                        END;
                        CREATE TRIGGER IF NOT EXISTS history_au AFTER UPDATE ON history BEGIN
                            INSERT INTO history_fts(history_fts, rowid, final_text) VALUES('delete', old.id, old.final_text);
                            INSERT INTO history_fts(rowid, final_text) VALUES (new.id, new.final_text);
                        END;
                        """;
                    await createTriggers.ExecuteNonQueryAsync(ct);
                }
                catch (SqliteException ex)
                {
                    // Plan §3.5's own explicit contingency: verified for real, not assumed --
                    // see class doc comment's "FTS5 availability" paragraph.
                    ftsAvailable = false;
                    _logger.LogWarning(ex,
                        "FTS5 is not available in this Microsoft.Data.Sqlite/SQLitePCLRaw bundle; " +
                        "falling back to a LIKE '%term%' search over final_text (slower, no " +
                        "ranking -- see SqliteHistoryStore's class doc comment)");
                }

                // Dedicated read-only connection for SearchAsync (see class doc comment's
                // "Concurrency model (read side)" paragraph) -- opened AFTER the write
                // connection above has already created the file and schema, so this never
                // races against file/table creation. Mode=ReadOnly means this connection can
                // never itself mutate the database, even by accident.
                var readConnectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = _dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString();
                var readConnection = new SqliteConnection(readConnectionString);
                await readConnection.OpenAsync(ct);

                _connection = connection;
                _readConnection = readConnection;
                _ftsAvailable = ftsAvailable;
                _initFailed = false;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex,
                    "Failed to initialize history store at {DbPath}; history persistence disabled for this session",
                    _dbPath);
                _initFailed = true;
            }
            finally
            {
                _initAttempted = true;
            }

            return !_initFailed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ToSortableUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static HistoryEntry ReadEntry(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var timestamp = DateTimeOffset.Parse(
            reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var rawText = reader.GetString(2);
        var finalText = reader.GetString(3);
        var rules = JsonSerializer.Deserialize<List<AppliedRule>>(reader.GetString(4)) ?? [];
        var recordingMs = reader.GetInt64(5);
        var processingMs = reader.GetInt64(6);
        var wasInjected = reader.GetInt64(7) != 0;

        return new HistoryEntry(
            id, timestamp, rawText, finalText, rules,
            TimeSpan.FromMilliseconds(recordingMs), TimeSpan.FromMilliseconds(processingMs), wasInjected);
    }
}
