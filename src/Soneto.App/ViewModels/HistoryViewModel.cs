using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia.Threading;
using Soneto.Core.History;

namespace Soneto.App.ViewModels;

/// <summary>
/// Plan §3.10's History view ViewModel — a plain C# class, not Avalonia-coupled beyond its
/// production-default use of <see cref="Dispatcher"/> for UI-thread marshaling (this project has
/// no reactive-UI/MVVM package pinned, so a hand-rolled <see cref="INotifyPropertyChanged"/> is
/// used rather than adding a new framework dependency for this one view, per this item's own
/// explicit instruction). Takes <see cref="IHistoryStore"/> via constructor injection — the one
/// thing that makes this class testable against a fake store per §3.15's philosophy — and NEVER
/// talks to <c>SessionController</c>/<c>PipelineHost</c> directly (Phase 3 item 6's architecture
/// decision: History only ever talks to <see cref="IHistoryStore"/>, so it works identically
/// whether or not a live dictation session happens to be running this session).
///
/// <para>
/// <b>Live refresh:</b> subscribes to <see cref="IHistoryStore.Changed"/> (raised by a real store
/// after a successful append/purge/wipe, see that event's own doc comment) and re-queries on any
/// such signal — this is how an already-open History view picks up a dictation that completes
/// while it's on screen, without this ViewModel ever knowing that append came from
/// <c>SessionController.DictationCompleted</c> specifically. <see cref="IHistoryStore.Changed"/>
/// can fire on any thread (e.g. <c>SessionController</c>'s own worker thread, if the composition
/// root subscribed <see cref="IHistoryStore.AppendAsync"/> to <c>DictationCompleted</c>
/// fire-and-forget) — <see cref="OnStoreChanged"/> marshals through the UI thread (via the
/// injectable <c>uiThreadPost</c> delegate, production default <see cref="Dispatcher.UIThread"/>)
/// before touching <see cref="Entries"/>, the same threading discipline items 2/5 already
/// established as a hard requirement in this project.
/// </para>
///
/// <para>
/// <b>Search debounce:</b> a simple restart-on-every-keystroke timer (§3.10's own explicit
/// allowance — no need to reuse <c>ConfigService</c>'s file-watcher-debounce pattern). The actual
/// scheduling mechanism is injectable (<c>debounceScheduler</c>, production default a
/// <see cref="DispatcherTimer"/>) specifically so a unit test can invoke the debounced action
/// immediately/synchronously instead of needing a real timer tick — the debounce LOGIC itself
/// (restart the window on every call, only run the action once it elapses) lives in this
/// injectable delegate's real implementation, while <see cref="RefreshAsync"/> — what actually
/// runs once the debounce elapses — is itself directly callable/awaitable by a test with no
/// debounce involved at all.
/// </para>
/// </summary>
public sealed class HistoryViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>How long the search box waits after the last keystroke before re-querying.</summary>
    public const int DebounceMilliseconds = 400;

    /// <summary>Page size for both the initial load and every search re-query (§3.10 doesn't ask
    /// for paging UI yet — a single page is enough for a personal history of a few thousand
    /// rows, consistent with <see cref="SqliteHistoryStore"/>'s own class doc comment).</summary>
    public const int PageSize = 200;

    private readonly IHistoryStore _store;
    private readonly Action<Action> _postToUiThread;
    private readonly Action<Action> _scheduleDebounced;
    private readonly DispatcherTimer? _debounceTimer;
    private Action? _pendingDebouncedAction;

    private string _searchText = string.Empty;
    private HistoryEntry? _selectedEntry;
    private IReadOnlyList<DiffSegment> _rawSegments = [];
    private IReadOnlyList<DiffSegment> _finalSegments = [];
    private int _refreshGeneration;

    /// <summary>Newest-first entries currently shown (post most-recent search/refresh).</summary>
    public ObservableCollection<HistoryEntry> Entries { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The last <see cref="RefreshAsync"/> task kicked off, exposed so a test can await
    /// "the refresh that just happened" after driving <see cref="SearchText"/>/<see cref="OnStoreChanged"/>
    /// without needing to guess timing. Production callers never need this.</summary>
    public Task LastRefreshTask { get; private set; } = Task.CompletedTask;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
                return;

            _searchText = value;
            OnPropertyChanged();
            _scheduleDebounced(() => LastRefreshTask = RefreshAsync());
        }
    }

    public HistoryEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (ReferenceEquals(_selectedEntry, value))
                return;

            _selectedEntry = value;
            OnPropertyChanged();

            if (value is null)
            {
                RawSegments = [];
                FinalSegments = [];
            }
            else
            {
                RawSegments = BuildHighlightedSegments(value.RawText, value.RulesFired.Select(r => r.From));
                FinalSegments = BuildHighlightedSegments(value.FinalText, value.RulesFired.Select(r => r.To));
            }
        }
    }

    /// <summary><see cref="SelectedEntry"/>'s <c>RawText</c>, split into highlighted/plain
    /// segments per rule <c>From</c> span (see <see cref="DiffSegment"/>'s doc comment).</summary>
    public IReadOnlyList<DiffSegment> RawSegments
    {
        get => _rawSegments;
        private set
        {
            _rawSegments = value;
            OnPropertyChanged();
        }
    }

    /// <summary><see cref="SelectedEntry"/>'s <c>FinalText</c>, split into highlighted/plain
    /// segments per rule <c>To</c> span (see <see cref="DiffSegment"/>'s doc comment).</summary>
    public IReadOnlyList<DiffSegment> FinalSegments
    {
        get => _finalSegments;
        private set
        {
            _finalSegments = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Real, production constructor: uses a genuine <see cref="DispatcherTimer"/> for
    /// debounce and posts through <see cref="Dispatcher.UIThread"/> for live-refresh marshaling —
    /// both require a running Avalonia application, which is why the test-only constructor below
    /// exists.</summary>
    public HistoryViewModel(IHistoryStore store)
        : this(store, uiThreadPost: null, debounceScheduler: null)
    {
    }

    /// <summary>
    /// Test-facing constructor (internal, per §3.15's "ViewModel logic is unit-tested directly"
    /// philosophy): <paramref name="uiThreadPost"/>/<paramref name="debounceScheduler"/> let
    /// <c>Soneto.App.Tests</c> replace the two Avalonia-`Dispatcher`-dependent mechanisms (which
    /// throw outside a running Avalonia application) with synchronous stand-ins, so
    /// <see cref="HistoryViewModel"/>'s actual query/refresh/diff logic can be exercised in a
    /// plain xunit test with no Avalonia headless harness at all. <c>null</c> for either
    /// parameter falls back to the real, production mechanism.
    /// </summary>
    internal HistoryViewModel(
        IHistoryStore store, Action<Action>? uiThreadPost, Action<Action>? debounceScheduler)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _postToUiThread = uiThreadPost ?? (action => Dispatcher.UIThread.Post(action));

        if (debounceScheduler is not null)
        {
            _scheduleDebounced = debounceScheduler;
        }
        else
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var action = _pendingDebouncedAction;
                _pendingDebouncedAction = null;
                action?.Invoke();
            };
            _debounceTimer = timer;
            _scheduleDebounced = action =>
            {
                _pendingDebouncedAction = action;
                timer.Stop();
                timer.Start();
            };
        }

        _store.Changed += OnStoreChanged;

        // Load recent history immediately on construction (§3.10's own explicit requirement).
        LastRefreshTask = RefreshAsync();
    }

    /// <summary>
    /// Re-queries <see cref="IHistoryStore.SearchAsync"/> with the current <see cref="SearchText"/>
    /// and replaces <see cref="Entries"/> with the result, newest-first. Public (not just reached
    /// via the debounce timer/<see cref="IHistoryStore.Changed"/>) specifically so a test can call
    /// it directly, bypassing the debounce mechanism entirely, per §3.10's own "expose a method
    /// the timer calls, so a test can invoke that method directly" allowance.
    ///
    /// <para>
    /// <b>Post-review fix — stale-result race guard.</b> Two independent triggers can each start
    /// their own <see cref="RefreshAsync"/> call concurrently: a debounced search-text change and
    /// an <see cref="IHistoryStore.Changed"/>-driven live refresh (or two overlapping debounce
    /// windows, if a slow query for an earlier search text is still in flight when a later,
    /// faster one completes). With no ordering guard, whichever call happened to finish LAST would
    /// win and silently overwrite <see cref="Entries"/>, even if it started earlier and its result
    /// is now stale relative to a newer request. A monotonically increasing generation counter,
    /// captured before the <c>await</c> and re-checked after, ensures only the most-recently-STARTED
    /// call's results are ever actually applied — an older call whose await resolves after a newer
    /// one has already started is a no-op instead of a stale overwrite.
    /// </para>
    /// </summary>
    public async Task RefreshAsync()
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var results = await _store.SearchAsync(SearchText, PageSize, offset: 0);

        if (generation != Volatile.Read(ref _refreshGeneration))
            return; // superseded by a newer refresh started after this one; discard.

        Entries.Clear();
        foreach (var entry in results)
            Entries.Add(entry);
    }

    /// <summary>
    /// <see cref="IHistoryStore.Changed"/>'s handler — may run on any thread (see that event's own
    /// doc comment), so this marshals through <see cref="_postToUiThread"/> BEFORE touching
    /// <see cref="Entries"/> (an Avalonia-bound <see cref="ObservableCollection{T}"/>), the same
    /// threading discipline items 2/5 already established as a hard requirement in this project.
    /// </summary>
    private void OnStoreChanged(object? sender, EventArgs e)
    {
        _postToUiThread(() => LastRefreshTask = RefreshAsync());
    }

    /// <summary>
    /// Splits <paramref name="text"/> into <see cref="DiffSegment"/>s, marking the leftmost-first
    /// occurrences of the spans in <paramref name="spans"/> as highlighted. Pure, stateless, and
    /// independently unit-testable — see this class's own doc comment for why this uses
    /// <see cref="Soneto.Core.Abstractions.AppliedRule"/>'s structured spans directly rather than a
    /// general-purpose text-diff algorithm.
    ///
    /// <para>
    /// <b>Post-review fix — bounded by actual rule-fire COUNT, not "every occurrence of this
    /// text."</b> <see cref="Soneto.Core.Abstractions.AppliedRule"/> carries only <c>From</c>/<c>To</c>
    /// strings, no match position/offset — so this method cannot know exactly WHICH occurrence of a
    /// given span a rule actually touched if that same text happens to also appear elsewhere in
    /// <paramref name="text"/> for unrelated reasons (e.g. <c>RawText</c> = "the web methods team
    /// uses web methods daily" with only ONE rule firing on "web methods" — the original
    /// implementation highlighted BOTH occurrences, which is a false positive against text a rule
    /// never actually touched). This version counts how many times each distinct span string
    /// appears in <paramref name="spans"/> (i.e. how many rules actually fired with that exact
    /// text) and highlights at most that many LEFTMOST occurrences of it — a bounded heuristic,
    /// not exact position tracking (a genuinely correct fix would require extending
    /// <c>AppliedRule</c> with a match offset, a larger change touching every Phase 2 processor
    /// that constructs one, judged disproportionate for this narrow, visual-only, no-crash/no-data-
    /// corruption edge case). Left as a documented, deliberate limitation, not silently unfixed.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<DiffSegment> BuildHighlightedSegments(string text, IEnumerable<string> spans)
    {
        var remainingCounts = spans
            .Where(s => !string.IsNullOrEmpty(s))
            .GroupBy(s => s, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        if (string.IsNullOrEmpty(text) || remainingCounts.Count == 0)
            return [new DiffSegment(text, false)];

        var segments = new List<DiffSegment>();
        var pos = 0;
        while (pos < text.Length)
        {
            var bestIndex = -1;
            var bestLength = 0;
            string? bestSpan = null;
            foreach (var (span, remaining) in remainingCounts)
            {
                if (remaining <= 0)
                    continue;

                var idx = text.IndexOf(span, pos, StringComparison.Ordinal);
                if (idx < 0)
                    continue;

                if (bestIndex == -1 || idx < bestIndex || (idx == bestIndex && span.Length > bestLength))
                {
                    bestIndex = idx;
                    bestLength = span.Length;
                    bestSpan = span;
                }
            }

            if (bestIndex == -1)
            {
                segments.Add(new DiffSegment(text[pos..], false));
                break;
            }

            if (bestIndex > pos)
                segments.Add(new DiffSegment(text[pos..bestIndex], false));

            segments.Add(new DiffSegment(text.Substring(bestIndex, bestLength), true));
            remainingCounts[bestSpan!]--;
            pos = bestIndex + bestLength;
        }

        return segments;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Unsubscribes from <see cref="IHistoryStore.Changed"/> and stops the debounce
    /// timer, if one was created. Does NOT dispose <see cref="IHistoryStore"/> itself — this
    /// ViewModel does not own the store's lifetime (the composition root does).</summary>
    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _debounceTimer?.Stop();
    }
}
