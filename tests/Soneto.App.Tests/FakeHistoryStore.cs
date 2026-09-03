using Soneto.Core.History;

namespace Soneto.App.Tests;

/// <summary>
/// A simple in-memory <see cref="IHistoryStore"/> test double (per §3.15's philosophy -- real
/// SQLite/FTS5 behavior is already covered by <c>SqliteHistoryStoreTests</c> in
/// <c>Soneto.Core.Tests</c>; this fake exists purely so <see cref="Soneto.App.ViewModels.HistoryViewModel"/>
/// can be unit-tested without a real database). Records every <see cref="SearchAsync"/> call's
/// arguments so tests can assert the ViewModel queried with the right query/limit/offset, and
/// exposes <see cref="RaiseChanged"/> so a test can simulate a live append without a real
/// <see cref="AppendAsync"/> call.
/// </summary>
public sealed class FakeHistoryStore : IHistoryStore
{
    private readonly List<HistoryEntry> _entries = [];
    private readonly Dictionary<string, TaskCompletionSource> _gates = new();
    private TaskCompletionSource? _panicWipeGate;

    /// <summary>
    /// Item 10 (§3.14) addition: registers a gate <see cref="PanicWipeAsync"/> will `await`
    /// before returning -- lets a test observe a caller's own "in progress" flag (e.g.
    /// <c>SettingsViewModel.IsWiping</c>) actually being true while a wipe is genuinely still
    /// in flight, mirroring <see cref="AddGate"/>'s existing pattern for <see cref="SearchAsync"/>.
    /// </summary>
    public void AddPanicWipeGate() => _panicWipeGate = new TaskCompletionSource();

    /// <summary>Releases a previously-added panic-wipe gate.</summary>
    public void ReleasePanicWipeGate() => _panicWipeGate?.SetResult();

    public event EventHandler? Changed;

    /// <summary>Every (query, limit, offset) tuple <see cref="SearchAsync"/> was called with, in
    /// call order.</summary>
    public List<(string? Query, int Limit, int Offset)> SearchCalls { get; } = [];

    /// <summary>
    /// Post-review addition: registers a gate that <see cref="SearchAsync"/> will `await` before
    /// returning, for the given <paramref name="query"/> (blank/null both map to the same "" key).
    /// Lets a test construct an exact out-of-order-completion race (an earlier, slower query's
    /// results resolving AFTER a later, faster one's) to prove <c>HistoryViewModel</c>'s
    /// generation-guard actually discards the stale one instead of clobbering newer results.
    /// </summary>
    public void AddGate(string? query) => _gates[query ?? string.Empty] = new TaskCompletionSource();

    /// <summary>Releases a previously-added gate, letting that query's <see cref="SearchAsync"/>
    /// call finally return.</summary>
    public void ReleaseGate(string? query) => _gates[query ?? string.Empty].SetResult();

    /// <summary>Seeds the fake's backing store directly, bypassing <see cref="AppendAsync"/>
    /// (which would raise <see cref="Changed"/> -- most tests want to seed data quietly before
    /// asserting on a later, deliberate change).</summary>
    public void Seed(params HistoryEntry[] entries) => _entries.AddRange(entries);

    /// <summary>Simulates a live append/purge/wipe from elsewhere without actually mutating this
    /// fake's data -- exactly what a test needs to drive
    /// <see cref="Soneto.App.ViewModels.HistoryViewModel"/>'s live-refresh path.</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public Task<long> AppendAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        var withId = entry with { Id = _entries.Count + 1 };
        _entries.Insert(0, withId);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(withId.Id);
    }

    public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(
        string? query, int limit, int offset, CancellationToken ct = default)
    {
        SearchCalls.Add((query, limit, offset));

        if (_gates.TryGetValue(query ?? string.Empty, out var gate))
            await gate.Task;

        IEnumerable<HistoryEntry> matches = string.IsNullOrWhiteSpace(query)
            ? _entries
            : _entries.Where(e => e.FinalText.Contains(query, StringComparison.OrdinalIgnoreCase));

        IReadOnlyList<HistoryEntry> page = matches
            .OrderByDescending(e => e.Timestamp)
            .Skip(offset)
            .Take(limit)
            .ToList();

        return page;
    }

    public Task<int> PurgeOlderThanAsync(TimeSpan age, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - age;
        var removed = _entries.RemoveAll(e => e.Timestamp < cutoff);
        if (removed > 0)
            Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(removed);
    }

    public async Task PanicWipeAsync(CancellationToken ct = default)
    {
        if (_panicWipeGate is { } gate)
            await gate.Task;

        _entries.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
