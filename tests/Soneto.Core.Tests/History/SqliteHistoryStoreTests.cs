using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Soneto.Core.Abstractions;
using Soneto.Core.History;

namespace Soneto.Core.Tests.History;

/// <summary>
/// Tests for <see cref="SqliteHistoryStore"/> (Phase 3 work item 1, §3.5): real temp-file
/// SQLite databases throughout (no mocks), per this project's established
/// <c>ConfigService</c>/<c>DictionaryService</c> testing convention. Covers §3.16 item 1's
/// "done when" bar: FTS5 verified for real against real content (not just schema-creation not
/// throwing), append/search/purge/panic-wipe round-tripping correctly, and
/// <see cref="IHistoryStore.AppendAsync"/> proven non-throwing on a simulated write failure.
/// </summary>
public sealed class SqliteHistoryStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteHistoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "soneto-history-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "history.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static HistoryEntry MakeEntry(
        string finalText, string rawText, DateTimeOffset timestamp, IReadOnlyList<AppliedRule>? rules = null) =>
        new(
            Id: 0,
            Timestamp: timestamp,
            RawText: rawText,
            FinalText: finalText,
            RulesFired: rules ?? [],
            RecordingDuration: TimeSpan.FromSeconds(3.5),
            ProcessingLatency: TimeSpan.FromMilliseconds(420),
            WasInjected: true);

    [Fact]
    public async Task AppendAsync_and_SearchAsync_round_trip_all_fields_including_rules_fired()
    {
        var logger = new TestLogger<SqliteHistoryStore>();
        await using var sut = new SqliteHistoryStore(logger, _dbPath);

        var rules = new List<AppliedRule>
        {
            new("DictionaryEngineProcessor", "correctionPair:c1", "cloud code", "Claude Code"),
        };
        var entry = MakeEntry(
            "Claude Code wrote this", "cloud code wrote this", DateTimeOffset.UtcNow, rules);

        var id = await sut.AppendAsync(entry);
        Assert.True(id > 0);

        var results = await sut.SearchAsync(null, limit: 10, offset: 0);

        var persisted = Assert.Single(results);
        Assert.Equal(id, persisted.Id);
        Assert.Equal(entry.RawText, persisted.RawText);
        Assert.Equal(entry.FinalText, persisted.FinalText);
        Assert.Equal(entry.RecordingDuration, persisted.RecordingDuration);
        Assert.Equal(entry.ProcessingLatency, persisted.ProcessingLatency);
        Assert.True(persisted.WasInjected);
        Assert.Equal(
            entry.Timestamp.ToUniversalTime().ToString("O"),
            persisted.Timestamp.ToUniversalTime().ToString("O"));

        var persistedRule = Assert.Single(persisted.RulesFired);
        Assert.Equal("DictionaryEngineProcessor", persistedRule.Processor);
        Assert.Equal("correctionPair:c1", persistedRule.Rule);
        Assert.Equal("cloud code", persistedRule.From);
        Assert.Equal("Claude Code", persistedRule.To);
    }

    [Fact]
    public async Task SearchAsync_with_null_query_returns_recent_entries_newest_first_without_matching_content()
    {
        var logger = new TestLogger<SqliteHistoryStore>();
        await using var sut = new SqliteHistoryStore(logger, _dbPath);

        var now = DateTimeOffset.UtcNow;
        var oldest = MakeEntry("first entry, nothing special", "first entry, nothing special", now.AddMinutes(-10));
        var middle = MakeEntry("second entry, also plain", "second entry, also plain", now.AddMinutes(-5));
        var newest = MakeEntry("third entry, still plain", "third entry, still plain", now);

        await sut.AppendAsync(oldest);
        await sut.AppendAsync(middle);
        var newestId = await sut.AppendAsync(newest);

        // null/empty/whitespace all take the browse-everything path, no dictionary-rule
        // content needs to match anything for these to come back.
        foreach (var query in new[] { null, "", "   " })
        {
            var results = await sut.SearchAsync(query, limit: 10, offset: 0);
            Assert.Equal(3, results.Count);
            Assert.Equal(newestId, results[0].Id); // newest first
            Assert.True(results[0].Timestamp >= results[1].Timestamp);
            Assert.True(results[1].Timestamp >= results[2].Timestamp);
        }
    }

    [Fact]
    public async Task SearchAsync_with_a_real_query_term_returns_only_the_matching_entry()
    {
        // This is the FTS5-availability proof per §3.16 item 1's "done when" bar: a search
        // that actually matches real content, not just a schema-creation smoke test.
        var logger = new TestLogger<SqliteHistoryStore>();
        await using var sut = new SqliteHistoryStore(logger, _dbPath);

        var now = DateTimeOffset.UtcNow;
        var matching = MakeEntry("please open the frobnicator settings", "please open the frobnicator settings", now);
        var nonMatching = MakeEntry("please close the window", "please close the window", now.AddSeconds(1));

        await sut.AppendAsync(matching);
        await sut.AppendAsync(nonMatching);

        var results = await sut.SearchAsync("frobnicator", limit: 10, offset: 0);

        var hit = Assert.Single(results);
        Assert.Equal(matching.FinalText, hit.FinalText);
    }

    [Fact]
    public async Task SearchAsync_paging_via_limit_and_offset_works_in_both_browse_and_query_modes()
    {
        var logger = new TestLogger<SqliteHistoryStore>();
        await using var sut = new SqliteHistoryStore(logger, _dbPath);

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            await sut.AppendAsync(MakeEntry($"shared-term entry {i}", $"shared-term entry {i}", now.AddSeconds(i)));

        var page1 = await sut.SearchAsync(null, limit: 2, offset: 0);
        var page2 = await sut.SearchAsync(null, limit: 2, offset: 2);
        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.NotEqual(page1[0].Id, page2[0].Id);

        var queriedPage1 = await sut.SearchAsync("shared-term", limit: 2, offset: 0);
        var queriedPage2 = await sut.SearchAsync("shared-term", limit: 2, offset: 2);
        Assert.Equal(2, queriedPage1.Count);
        Assert.Equal(2, queriedPage2.Count);
        Assert.NotEqual(queriedPage1[0].Id, queriedPage2[0].Id);
    }

    [Fact]
    public async Task PurgeOlderThanAsync_deletes_only_entries_older_than_the_cutoff_and_returns_the_count()
    {
        var logger = new TestLogger<SqliteHistoryStore>();
        await using var sut = new SqliteHistoryStore(logger, _dbPath);

        var now = DateTimeOffset.UtcNow;
        await sut.AppendAsync(MakeEntry("very old entry", "very old entry", now.AddDays(-10)));
        await sut.AppendAsync(MakeEntry("also old entry", "also old entry", now.AddDays(-8)));
        var recentId = await sut.AppendAsync(MakeEntry("recent entry", "recent entry", now.AddMinutes(-1)));

        var deletedCount = await sut.PurgeOlderThanAsync(TimeSpan.FromDays(7));

        Assert.Equal(2, deletedCount);

        var remaining = await sut.SearchAsync(null, limit: 10, offset: 0);
        var survivor = Assert.Single(remaining);
        Assert.Equal(recentId, survivor.Id);
    }

    [Fact]
    public async Task PanicWipeAsync_empties_the_store_completely_including_fts_with_no_phantom_hits()
    {
        var logger = new TestLogger<SqliteHistoryStore>();
        await using var sut = new SqliteHistoryStore(logger, _dbPath);

        var now = DateTimeOffset.UtcNow;
        await sut.AppendAsync(MakeEntry("wipeable content alpha", "wipeable content alpha", now));
        await sut.AppendAsync(MakeEntry("wipeable content beta", "wipeable content beta", now.AddSeconds(1)));

        Assert.Equal(2, (await sut.SearchAsync(null, limit: 10, offset: 0)).Count);

        await sut.PanicWipeAsync();

        Assert.Empty(await sut.SearchAsync(null, limit: 10, offset: 0));
        // Prove there's no stale FTS-only phantom hit left behind by a trigger that didn't
        // fire correctly for the bulk (no-WHERE-clause) DELETE -- a real search against the
        // exact wiped content, not just an empty browse-all, closes that gap.
        Assert.Empty(await sut.SearchAsync("wipeable", limit: 10, offset: 0));
    }

    [Fact]
    public async Task AppendAsync_never_throws_on_a_simulated_write_failure_and_returns_the_sentinel()
    {
        // Simulate an unwritable target path the same way this project's established
        // ConfigService/DictionaryService test pattern simulates a permission-denied/bad path:
        // point the "directory" at a location whose parent segment is actually an existing
        // FILE, so Directory.CreateDirectory (and therefore the whole store) cannot succeed --
        // a deterministic, cross-platform-consistent failure that doesn't depend on OS-specific
        // ACL manipulation.
        var blockerFilePath = Path.Combine(_tempDir, "blocker-file.txt");
        await File.WriteAllTextAsync(blockerFilePath, "not a directory");
        var unwritableDbPath = Path.Combine(blockerFilePath, "sub", "history.db");

        var logger = new TestLogger<SqliteHistoryStore>();
        await using var sut = new SqliteHistoryStore(logger, unwritableDbPath);

        var exception = await Record.ExceptionAsync(
            () => sut.AppendAsync(MakeEntry("doomed entry", "doomed entry", DateTimeOffset.UtcNow)));

        Assert.Null(exception);

        var id = await sut.AppendAsync(MakeEntry("doomed entry 2", "doomed entry 2", DateTimeOffset.UtcNow));
        Assert.Equal(-1, id);
        Assert.True(logger.HasEntry(LogLevel.Error, "Failed to initialize history store"));

        // The store must also not throw for the other members once initialization has failed.
        Assert.Empty(await sut.SearchAsync(null, limit: 10, offset: 0));
        Assert.Equal(0, await sut.PurgeOlderThanAsync(TimeSpan.FromDays(1)));
        var panicException = await Record.ExceptionAsync(() => sut.PanicWipeAsync());
        Assert.Null(panicException);
    }

    [Fact]
    public async Task AppendAsync_never_throws_on_a_write_time_failure_after_successful_initialization()
    {
        // The test above only proves INIT-time failure handling (AppendAsync's _initFailed
        // short-circuit, which returns -1 WITHOUT ever entering AppendAsync's own try block).
        // This test proves the OTHER catch clause -- the one wrapping the actual
        // INSERT/last_insert_rowid() calls at SqliteHistoryStore.cs:119-126 -- by first letting
        // initialization succeed for real (a genuine open connection, real schema), then
        // pulling the rug out from under that already-open connection: a second, independent,
        // short-lived connection to the same db file drops the "history" table entirely.
        // Because the store's own connection is not mid-transaction at that moment (each
        // AppendAsync call commits/completes before returning), the second connection's DROP
        // TABLE acquires and releases its lock immediately with no busy-timeout contention --
        // confirmed empirically to take ~2ms, not the ~30s Microsoft.Data.Sqlite's default busy
        // timeout would otherwise impose if this instead tried to simulate the failure via lock
        // contention (a real alternative that was tried and rejected as impractically slow for
        // a unit test). The store's NEXT write then fails immediately with a genuine
        // SqliteException ("no such table: history") thrown from inside AppendAsync's own try
        // block, which is exactly the code path this test targets.
        var logger = new TestLogger<SqliteHistoryStore>();
        await using var sut = new SqliteHistoryStore(logger, _dbPath);

        var firstId = await sut.AppendAsync(MakeEntry("first entry, before the drop", "first entry, before the drop", DateTimeOffset.UtcNow));
        Assert.True(firstId > 0);

        await using (var saboteur = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await saboteur.OpenAsync();
            await using var drop = saboteur.CreateCommand();
            drop.CommandText = "DROP TABLE history;";
            await drop.ExecuteNonQueryAsync();
        }

        var exception = await Record.ExceptionAsync(
            () => sut.AppendAsync(MakeEntry("doomed write-time entry", "doomed write-time entry", DateTimeOffset.UtcNow)));
        Assert.Null(exception);

        var idAfterDrop = await sut.AppendAsync(MakeEntry("still doomed", "still doomed", DateTimeOffset.UtcNow));
        Assert.Equal(-1, idAfterDrop);
        Assert.True(logger.HasEntry(LogLevel.Error, $"Failed to append history entry to {_dbPath}"));
        // Distinguishes this from the init-failure test above: that one only ever logs the
        // init-failure message, never this one.
        Assert.False(logger.HasEntry(LogLevel.Error, "Failed to initialize history store"));

        // Bonus: one transient write-time failure must not permanently wedge the store. Restore
        // the schema (mirroring SqliteHistoryStore's own idempotent CREATE TABLE DDL exactly)
        // via a fresh short-lived connection, and confirm a later append/search still works.
        await using (var healer = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await healer.OpenAsync();
            await using var recreate = healer.CreateCommand();
            recreate.CommandText = """
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
            await recreate.ExecuteNonQueryAsync();
        }

        var healedId = await sut.AppendAsync(MakeEntry("recovered entry", "recovered entry", DateTimeOffset.UtcNow));
        Assert.True(healedId > 0);
        var results = await sut.SearchAsync(null, limit: 10, offset: 0);
        Assert.Contains(results, e => e.Id == healedId);
    }

    [Fact]
    public async Task Every_public_method_throws_ObjectDisposedException_after_DisposeAsync_not_NullReferenceException_or_a_raw_SqliteException()
    {
        // Regression test for item 1's code-review blocking bug: DisposeAsync used to leave
        // every public method with no _disposed guard at all, so a post-dispose call either
        // hit an accidental ObjectDisposedException thrown by an already-disposed internal
        // SemaphoreSlim (uncaught by the "never throws" catch clauses -- an interface-contract
        // violation) or, in a narrower race, a NullReferenceException from a nulled-out
        // connection field. The fix makes ObjectDisposedException the sole, deliberate,
        // consistent outcome across all four public members -- proven here for each one.
        var logger = new TestLogger<SqliteHistoryStore>();
        var sut = new SqliteHistoryStore(logger, _dbPath);

        // Exercise successful initialization first so EnsureInitializedAsync's fast path
        // (the exact path the review flagged as bypassing the gate entirely) is actually live
        // by the time we dispose.
        var id = await sut.AppendAsync(MakeEntry("pre-dispose entry", "pre-dispose entry", DateTimeOffset.UtcNow));
        Assert.True(id > 0);

        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.AppendAsync(MakeEntry("post-dispose", "post-dispose", DateTimeOffset.UtcNow)));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.SearchAsync(null, limit: 10, offset: 0));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.PurgeOlderThanAsync(TimeSpan.FromDays(1)));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.PanicWipeAsync());

        // Calling DisposeAsync again must remain the normal idempotent no-op IAsyncDisposable
        // contract expects -- it must not itself throw.
        var secondDisposeException = await Record.ExceptionAsync(() => sut.DisposeAsync().AsTask());
        Assert.Null(secondDisposeException);
    }

    [Fact]
    public async Task Concurrent_operations_racing_DisposeAsync_never_throw_anything_but_ObjectDisposedException()
    {
        // The sequential test above (dispose fully completes, THEN call each method) proves the
        // simple case but does not exercise the actual race the fix targets: a caller already
        // inside AppendAsync/SearchAsync's own ThrowIfDisposed-then-gate-acquire window,
        // concurrently with a real DisposeAsync call. This test fires many operations and a
        // concurrent dispose repeatedly, across fresh store instances, so across enough
        // iterations both possible interleavings (caller wins the gate first, or DisposeAsync
        // wins it first) are exercised. A regression that reintroduced the original bug would
        // show up here as an unhandled NullReferenceException or a raw (uncaught)
        // SqliteException/ObjectDisposedException-from-the-semaphore-itself surfacing from
        // Task.WhenAll, rather than every task settling on either success or a clean, deliberate
        // ObjectDisposedException from ThrowIfDisposed.
        for (var iteration = 0; iteration < 25; iteration++)
        {
            var dbPath = Path.Combine(_tempDir, $"race-{iteration}.db");
            var logger = new TestLogger<SqliteHistoryStore>();
            var sut = new SqliteHistoryStore(logger, dbPath);

            // Force real initialization before racing, so EnsureInitializedAsync's fast path
            // (not its slow/first-time path) is what's actually being raced -- matching the
            // exact spot the original bug lived in.
            await sut.AppendAsync(MakeEntry("seed", "seed", DateTimeOffset.UtcNow));

            var operations = new List<Task>();
            for (var i = 0; i < 8; i++)
            {
                operations.Add(Task.Run(() => sut.AppendAsync(MakeEntry($"race-{i}", $"race-{i}", DateTimeOffset.UtcNow))));
                operations.Add(Task.Run(() => sut.SearchAsync(null, limit: 5, offset: 0)));
                operations.Add(Task.Run(() => sut.PurgeOlderThanAsync(TimeSpan.FromDays(365))));
            }

            var disposeTask = Task.Run(() => sut.DisposeAsync().AsTask());
            operations.Add(disposeTask);

            // WhenAll re-throws the FIRST faulted task's exception; iterate each task's own
            // Exception property instead so every task's actual outcome is checked, not just
            // whichever one WhenAll happens to surface first.
            await Task.WhenAll(operations.Select(async t =>
            {
                try
                {
                    await t;
                }
                catch (ObjectDisposedException)
                {
                    // Expected outcome for an operation that lost the race to DisposeAsync.
                }
            }));

            foreach (var op in operations)
            {
                Assert.True(op.IsCompletedSuccessfully || op.Exception?.InnerException is ObjectDisposedException,
                    $"Unexpected exception surfaced from a racing operation: {op.Exception}");
            }
        }
    }
}
