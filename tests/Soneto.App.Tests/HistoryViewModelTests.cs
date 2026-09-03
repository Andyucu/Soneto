using Soneto.App.ViewModels;
using Soneto.Core.Abstractions;
using Soneto.Core.History;

namespace Soneto.App.Tests;

/// <summary>
/// Unit tests for <see cref="HistoryViewModel"/> against <see cref="FakeHistoryStore"/> -- the
/// first real <c>Soneto.App</c> ViewModel-level unit test infrastructure in this project, per
/// §3.15's own philosophy ("a HistoryViewModel's search-debounce/paging logic... [is a] plain C#
/// object that take[s] fakes/mocks for IHistoryStore... and can be tested exactly like
/// Soneto.Core.Tests' existing style"). Every test uses the internal test-facing constructor with
/// a synchronous <c>debounceScheduler</c>/<c>uiThreadPost</c> so none of this needs a running
/// Avalonia application/headless harness -- see that constructor's own doc comment.
/// </summary>
public class HistoryViewModelTests
{
    private static HistoryEntry MakeEntry(
        long id, string rawText, string finalText, IReadOnlyList<AppliedRule>? rulesFired = null,
        DateTimeOffset? timestamp = null) =>
        new(id, timestamp ?? DateTimeOffset.UtcNow, rawText, finalText, rulesFired ?? [],
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200), WasInjected: true);

    private static HistoryViewModel CreateViewModel(FakeHistoryStore store) =>
        new(store, uiThreadPost: action => action(), debounceScheduler: action => action());

    [Fact]
    public void Construction_LoadsRecentEntries()
    {
        var store = new FakeHistoryStore();
        store.Seed(
            MakeEntry(1, "raw one", "final one"),
            MakeEntry(2, "raw two", "final two"));

        var vm = CreateViewModel(store);

        Assert.Equal(2, vm.Entries.Count);
        Assert.Contains(vm.Entries, e => e.FinalText == "final one");
        Assert.Contains(vm.Entries, e => e.FinalText == "final two");
    }

    [Fact]
    public void Construction_QueriesWithBlankQueryAndDefaultPaging()
    {
        var store = new FakeHistoryStore();

        _ = CreateViewModel(store);

        var call = Assert.Single(store.SearchCalls);
        Assert.True(string.IsNullOrEmpty(call.Query));
        Assert.Equal(HistoryViewModel.PageSize, call.Limit);
        Assert.Equal(0, call.Offset);
    }

    [Fact]
    public void SearchTextChange_AfterDebounceElapses_RequeriesWithNewQuery()
    {
        var store = new FakeHistoryStore();
        store.Seed(
            MakeEntry(1, "raw apple", "final apple"),
            MakeEntry(2, "raw banana", "final banana"));

        var vm = CreateViewModel(store);
        store.SearchCalls.Clear();

        vm.SearchText = "banana";

        var call = Assert.Single(store.SearchCalls);
        Assert.Equal("banana", call.Query);
        Assert.Equal(HistoryViewModel.PageSize, call.Limit);
        Assert.Equal(0, call.Offset);

        var entry = Assert.Single(vm.Entries);
        Assert.Equal("final banana", entry.FinalText);
    }

    [Fact]
    public void SettingSameSearchText_DoesNotRequery()
    {
        var store = new FakeHistoryStore();
        var vm = CreateViewModel(store);
        store.SearchCalls.Clear();

        vm.SearchText = string.Empty; // already the current value

        Assert.Empty(store.SearchCalls);
    }

    [Fact]
    public async Task RefreshAsync_CanBeCalledDirectlyBypassingDebounce()
    {
        var store = new FakeHistoryStore();
        store.Seed(MakeEntry(1, "raw", "final"));
        var vm = CreateViewModel(store);

        await vm.RefreshAsync();

        Assert.Single(vm.Entries);
    }

    [Fact]
    public void StoreChanged_TriggersReQuery_AndUpdatesEntries()
    {
        var store = new FakeHistoryStore();
        var vm = CreateViewModel(store);
        Assert.Empty(vm.Entries);

        // Simulate a live append happening elsewhere (e.g. a real DictationCompleted-driven
        // AppendAsync on SessionController's own worker thread) without going through
        // AppendAsync itself -- just the Changed signal, per IHistoryStore.Changed's own
        // contract ("a UI observer... go re-query me").
        store.Seed(MakeEntry(1, "raw", "final"));
        store.RaiseChanged();

        Assert.Single(vm.Entries);
    }

    [Fact]
    public void SelectingEntry_WithNoRulesFired_ProducesOneUnhighlightedSegmentPerSide()
    {
        var store = new FakeHistoryStore();
        var entry = MakeEntry(1, "hello world", "hello world");
        store.Seed(entry);
        var vm = CreateViewModel(store);

        vm.SelectedEntry = vm.Entries[0];

        var raw = Assert.Single(vm.RawSegments);
        Assert.Equal("hello world", raw.Text);
        Assert.False(raw.IsHighlighted);

        var final = Assert.Single(vm.FinalSegments);
        Assert.Equal("hello world", final.Text);
        Assert.False(final.IsHighlighted);
    }

    [Fact]
    public void SelectingEntry_WithAppliedRule_HighlightsExactFromAndToSpans()
    {
        var store = new FakeHistoryStore();
        var rule = new AppliedRule(Processor: "DictionaryEngine", Rule: "r1", From: "teh", To: "the");
        var entry = MakeEntry(1, "i saw teh cat", "i saw the cat", [rule]);
        store.Seed(entry);
        var vm = CreateViewModel(store);

        vm.SelectedEntry = vm.Entries[0];

        Assert.Equal(3, vm.RawSegments.Count);
        Assert.Equal("i saw ", vm.RawSegments[0].Text);
        Assert.False(vm.RawSegments[0].IsHighlighted);
        Assert.Equal("teh", vm.RawSegments[1].Text);
        Assert.True(vm.RawSegments[1].IsHighlighted);
        Assert.Equal(" cat", vm.RawSegments[2].Text);
        Assert.False(vm.RawSegments[2].IsHighlighted);

        Assert.Equal(3, vm.FinalSegments.Count);
        Assert.Equal("i saw ", vm.FinalSegments[0].Text);
        Assert.False(vm.FinalSegments[0].IsHighlighted);
        Assert.Equal("the", vm.FinalSegments[1].Text);
        Assert.True(vm.FinalSegments[1].IsHighlighted);
        Assert.Equal(" cat", vm.FinalSegments[2].Text);
        Assert.False(vm.FinalSegments[2].IsHighlighted);
    }

    [Fact]
    public void SelectingEntry_WithMultipleRules_HighlightsEachSpanIndependently()
    {
        var store = new FakeHistoryStore();
        var rules = new[]
        {
            new AppliedRule("DictionaryEngine", "r1", "teh", "the"),
            new AppliedRule("DictionaryEngine", "r2", "kat", "cat"),
        };
        var entry = MakeEntry(1, "i saw teh kat", "i saw the cat", rules);
        store.Seed(entry);
        var vm = CreateViewModel(store);

        vm.SelectedEntry = vm.Entries[0];

        Assert.Equal(
            ["i saw ", "the", " ", "cat"],
            vm.FinalSegments.Select(s => s.Text));
        Assert.Equal(
            [false, true, false, true],
            vm.FinalSegments.Select(s => s.IsHighlighted));
    }

    [Fact]
    public void DeselectingEntry_ClearsDiffSegments()
    {
        var store = new FakeHistoryStore();
        var rule = new AppliedRule("DictionaryEngine", "r1", "teh", "the");
        store.Seed(MakeEntry(1, "teh", "the", [rule]));
        var vm = CreateViewModel(store);
        vm.SelectedEntry = vm.Entries[0];
        Assert.NotEmpty(vm.RawSegments);

        vm.SelectedEntry = null;

        Assert.Empty(vm.RawSegments);
        Assert.Empty(vm.FinalSegments);
    }

    [Fact]
    public async Task StaleSlowerRefresh_DoesNotClobberNewerFasterRefresh()
    {
        // Post-review regression test: an earlier search ("a", deliberately gated/slow) whose
        // RefreshAsync resolves AFTER a later, faster search ("ab") must NOT overwrite the newer
        // results -- exactly the debounce/async race code review found unguarded. Constructed as
        // two separate debounce windows firing independently (the realistic trigger: a slow query
        // still in flight when a faster, later one both starts and completes), not a single
        // rapid-keystroke burst (which the debounce timer alone already collapses).
        var store = new FakeHistoryStore();
        store.Seed(
            MakeEntry(1, "raw apple", "final apple"),
            MakeEntry(2, "raw abacus", "final abacus"));
        store.AddGate("a"); // "a"'s SearchAsync call will block until explicitly released below.
        var vm = CreateViewModel(store);

        vm.SearchText = "a";
        var slowRefresh = vm.LastRefreshTask; // still pending -- gated.

        vm.SearchText = "ab"; // a second, independent debounce window; no gate, resolves immediately.
        await vm.LastRefreshTask;

        var afterFastRefresh = Assert.Single(vm.Entries);
        Assert.Equal("final abacus", afterFastRefresh.FinalText);

        // Now let the STALE, earlier "a" query finally resolve.
        store.ReleaseGate("a");
        await slowRefresh;

        // The stale "a" results (which would include "apple" too) must NOT have clobbered the
        // already-applied, newer "ab" results.
        var afterStaleRefresh = Assert.Single(vm.Entries);
        Assert.Equal("final abacus", afterStaleRefresh.FinalText);
    }

    [Fact]
    public void SelectingEntry_WithSpanAppearingTwiceButOnlyOneRuleFired_HighlightsOnlyOneOccurrence()
    {
        // Post-review regression test: a rule firing ONCE for "web methods" must not cause BOTH
        // occurrences of that literal text to be highlighted just because the same text happens
        // to also appear elsewhere in the transcript for unrelated reasons.
        var store = new FakeHistoryStore();
        var rule = new AppliedRule("DictionaryEngine", "r1", "web methods", "webMethods");
        var entry = MakeEntry(
            1,
            "the web methods team uses web methods daily",
            "the webMethods team uses web methods daily",
            [rule]);
        store.Seed(entry);
        var vm = CreateViewModel(store);

        vm.SelectedEntry = vm.Entries[0];

        // Exactly one highlighted segment on each side, not two.
        Assert.Equal(1, vm.RawSegments.Count(s => s.IsHighlighted));
        Assert.Equal(1, vm.FinalSegments.Count(s => s.IsHighlighted));

        // And it's the LEFTMOST occurrence that got highlighted, per the documented heuristic.
        var firstHighlighted = vm.FinalSegments.First(s => s.IsHighlighted);
        Assert.Equal("webMethods", firstHighlighted.Text);
    }

    [Fact]
    public void Dispose_UnsubscribesFromStoreChanged()
    {
        var store = new FakeHistoryStore();
        var vm = CreateViewModel(store);

        vm.Dispose();
        store.Seed(MakeEntry(1, "raw", "final"));
        store.RaiseChanged();

        Assert.Empty(vm.Entries);
    }
}
