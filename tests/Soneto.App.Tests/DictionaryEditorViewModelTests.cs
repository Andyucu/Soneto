using Soneto.App.ViewModels;
using Soneto.Core.Dictionary;

namespace Soneto.App.Tests;

/// <summary>
/// Unit tests for <see cref="DictionaryEditorViewModel"/> against <see cref="FakeDictionaryService"/>
/// -- same philosophy as <c>HistoryViewModelTests</c> (§3.15): a plain xunit test, the internal
/// test-facing constructor replacing <c>Dispatcher.UIThread</c> marshaling with a synchronous
/// stand-in and using a short settle timeout instead of the real ~1.5s default, no headless
/// Avalonia harness needed.
/// </summary>
public sealed class DictionaryEditorViewModelTests
{
    private static readonly TimeSpan ShortSettleTimeout = TimeSpan.FromMilliseconds(150);

    private static DictionaryEditorViewModel CreateViewModel(FakeDictionaryService service) =>
        new(service, uiThreadPost: action => action(), settleTimeout: ShortSettleTimeout);

    /// <summary>Polls until the ViewModel's write has actually landed on disk, then lets the
    /// caller drive the fake's simulated outcome -- avoids a fixed sleep/timing guess for a real,
    /// if fast, async file write.</summary>
    private static async Task WaitForFileWriteAsync(string path)
    {
        // Waits not just for File.Exists (true while the write handle may still be mid-close on
        // Windows -- a real, observed sharing-violation race) but for the file to actually be
        // openable for a shared read, so callers (including a test's own direct File.ReadAllText)
        // never race the ViewModel's own File.WriteAllTextAsync call.
        for (var i = 0; i < 200; i++)
        {
            if (File.Exists(path))
            {
                try
                {
                    using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still mid-write/mid-close (or a transient AV-scan file lock, which on
                    // Windows can surface as either exception type); keep polling.
                }
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Expected {path} to have been written and readable by now.");
    }

    /// <summary>
    /// Post-review hardening: a bare <see cref="File.ReadAllText(string)"/> call made
    /// IMMEDIATELY after <see cref="WaitForFileWriteAsync"/> confirms the file is openable still
    /// has a real (if narrow) TOCTOU gap on Windows -- observed for real in one CI-adjacent
    /// full-solution test run (passed in isolation and on 3 subsequent full-solution re-runs,
    /// never reproduced again, consistent with a transient AV-scan/file-handle-release timing
    /// window rather than a logic bug) -- a raw <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/>
    /// surfaced from this exact read a moment after the wait loop above had already confirmed
    /// the file was readable. Wrapping the actual read in the SAME retry discipline
    /// <see cref="WaitForFileWriteAsync"/> already uses for its own probe read closes this gap
    /// for good, rather than leaving an unexplained one-off flake undiagnosed.
    /// </summary>
    private static async Task<string> ReadFileWithRetryAsync(string path)
    {
        for (var i = 0; i < 200; i++)
        {
            try
            {
                return await File.ReadAllTextAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Post-review widening: DictionaryService.LoadAsync itself treats these two
                // exception types as the same class of transient/environmental failure (a real
                // AV-scan file lock on Windows can surface as either) -- catching only
                // IOException left a residual gap where the exact same flake could resurface
                // under the other exception type. Mirrors WaitForFileWriteAsync's own widened
                // catch above.
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Expected {path} to still be readable by now.");
        return string.Empty; // unreachable -- Assert.Fail always throws.
    }

    [Fact]
    public void Construction_ReadsCurrentEntriesAndRejectedEntries()
    {
        var term = new VocabularyTerm { Id = "v1", Term = "webMethods" };
        var rejected = new RejectedDictionaryEntry(1, "bad-1", "Pattern did not compile");
        using var service = new FakeDictionaryService(new DictionaryConfig([term], [rejected], 1));

        var vm = CreateViewModel(service);

        Assert.Single(vm.Entries);
        Assert.Equal("v1", vm.Entries[0].Id);
        Assert.Single(vm.RejectedEntries);
        Assert.Equal("Pattern did not compile", vm.RejectedEntries[0].Reason);
    }

    [Fact]
    public async Task AddingValidVocabularyTerm_WritesCorrectlyShapedEntry_AndReportsSuccess()
    {
        using var service = new FakeDictionaryService();
        var vm = CreateViewModel(service);

        vm.BeginAddNew(DictionaryEntryKind.VocabularyTerm);
        vm.EditingTerm = "webMethods";

        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();
        await subscribed;
        service.SimulateSuccessfulReload();
        await saveTask;

        Assert.False(vm.StatusIsError);
        Assert.Single(service.Current.Entries);
        var written = Assert.IsType<VocabularyTerm>(service.Current.Entries[0]);
        Assert.Equal("webMethods", written.Term);
        Assert.True(written.Enabled);
        Assert.False(vm.IsEditing);
        Assert.Single(vm.Entries);
    }

    [Fact]
    public async Task AddingValidCorrectionPair_WritesCorrectlyShapedEntry()
    {
        using var service = new FakeDictionaryService();
        var vm = CreateViewModel(service);

        vm.BeginAddNew(DictionaryEntryKind.CorrectionPair);
        vm.EditingFrom = "web methods";
        vm.EditingTo = "webMethods";

        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();
        await subscribed;
        service.SimulateSuccessfulReload();
        await saveTask;

        var written = Assert.IsType<CorrectionPair>(Assert.Single(service.Current.Entries));
        Assert.Equal("web methods", written.From);
        Assert.Equal("webMethods", written.To);
    }

    [Fact]
    public async Task AddingValidRegexRule_WritesCorrectlyShapedEntry()
    {
        using var service = new FakeDictionaryService();
        var vm = CreateViewModel(service);

        vm.BeginAddNew(DictionaryEntryKind.RegexRule);
        vm.EditingPattern = @"\bIS (\d+)\b";
        vm.EditingReplacement = "IS $1";

        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();
        await subscribed;
        service.SimulateSuccessfulReload();
        await saveTask;

        var written = Assert.IsType<RegexRule>(Assert.Single(service.Current.Entries));
        Assert.Equal(@"\bIS (\d+)\b", written.Pattern);
        Assert.Equal("IS $1", written.Replacement);
    }

    [Fact]
    public async Task AddingValidSpokenCommand_WritesCorrectlyShapedEntry()
    {
        using var service = new FakeDictionaryService();
        var vm = CreateViewModel(service);

        vm.BeginAddNew(DictionaryEntryKind.SpokenCommand);
        vm.EditingPhrase = "new paragraph";
        vm.EditingEmits = "\n\n";

        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();
        await subscribed;
        service.SimulateSuccessfulReload();
        await saveTask;

        var written = Assert.IsType<SpokenCommand>(Assert.Single(service.Current.Entries));
        Assert.Equal("new paragraph", written.Phrase);
        Assert.Equal("\n\n", written.Emits);
    }

    [Fact]
    public async Task AddingValidPerAppOverride_WritesCorrectlyShapedEntry()
    {
        using var service = new FakeDictionaryService();
        var vm = CreateViewModel(service);

        vm.BeginAddNew(DictionaryEntryKind.PerAppOverride);
        vm.EditingProcessName = "wt.exe";
        vm.EditingAutoCapitalize = false;

        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();
        await subscribed;
        service.SimulateSuccessfulReload();
        await saveTask;

        var written = Assert.IsType<PerAppOverride>(Assert.Single(service.Current.Entries));
        Assert.Equal("wt.exe", written.ProcessName);
        Assert.False(written.AutoCapitalize);
    }

    [Fact]
    public async Task AddingEntry_ThatServiceRejects_SurfacesTheRejectionReason()
    {
        using var service = new FakeDictionaryService();
        var vm = CreateViewModel(service);

        vm.BeginAddNew(DictionaryEntryKind.CorrectionPair);
        vm.EditingFrom = string.Empty; // deliberately invalid -- empty From
        vm.EditingTo = "webMethods";

        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();
        await WaitForFileWriteAsync(service.DictionaryPath);

        // The real DictionaryService would reject this for an empty From (§2.7); simulate that
        // exact outcome. We don't know the generated Guid id ahead of time, so read it back off
        // the file the ViewModel actually wrote.
        var writtenJson = await ReadFileWithRetryAsync(service.DictionaryPath);
        var writtenId = ExtractLastEntryId(writtenJson);
        await subscribed;
        service.SimulateEntryRejectedReload(writtenId, "From is empty or whitespace-only");
        await saveTask;

        Assert.True(vm.StatusIsError);
        Assert.Contains("From is empty or whitespace-only", vm.StatusMessage);
        Assert.Empty(service.Current.Entries);
    }

    [Fact]
    public async Task AddingEntry_WhenWholeFileIsRejected_SurfacesAWholeFileRejectionMessage()
    {
        using var service = new FakeDictionaryService();
        var vm = CreateViewModel(service);

        vm.BeginAddNew(DictionaryEntryKind.VocabularyTerm);
        vm.EditingTerm = "duplicate";

        var saveTask = vm.SaveAsync();
        await WaitForFileWriteAsync(service.DictionaryPath);
        // Real DictionaryService's whole-file duplicate-Id rejection never raises
        // DictionaryChanged at all -- simulate that by doing nothing until the ViewModel's own
        // short settle timeout elapses.
        service.SimulateWholeFileRejected();
        await saveTask;

        Assert.True(vm.StatusIsError);
        Assert.Contains("rejected as a whole", vm.StatusMessage);
    }

    [Fact]
    public async Task TogglingEnabled_WritesEntryWithFlippedEnabledState()
    {
        var existing = new VocabularyTerm { Id = "v1", Term = "webMethods", Enabled = true };
        using var service = new FakeDictionaryService(new DictionaryConfig([existing], [], 1));
        var vm = CreateViewModel(service);

        var row = Assert.Single(vm.Entries);
        var subscribed = service.WaitForNextSubscriptionAsync();
        var toggleTask = vm.ToggleEnabledAsync(row);
        await subscribed;
        service.SimulateSuccessfulReload();
        await toggleTask;

        var written = Assert.IsType<VocabularyTerm>(Assert.Single(service.Current.Entries));
        Assert.False(written.Enabled);
        Assert.False(vm.StatusIsError);
    }

    [Fact]
    public async Task Deleting_RemovesEntryFromWrittenFile()
    {
        var existing = new VocabularyTerm { Id = "v1", Term = "webMethods" };
        using var service = new FakeDictionaryService(new DictionaryConfig([existing], [], 1));
        var vm = CreateViewModel(service);

        var row = Assert.Single(vm.Entries);
        var subscribed = service.WaitForNextSubscriptionAsync();
        var deleteTask = vm.DeleteAsync(row);
        await subscribed;
        service.SimulateSuccessfulReload();
        await deleteTask;

        Assert.Empty(service.Current.Entries);
        Assert.False(vm.StatusIsError);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void DictionaryChanged_TriggersARefresh_MarshaledThroughInjectedUiThreadPost()
    {
        using var service = new FakeDictionaryService();
        var posted = 0;
        var vm = new DictionaryEditorViewModel(
            service, uiThreadPost: action => { posted++; action(); }, settleTimeout: ShortSettleTimeout);

        Assert.Empty(vm.Entries);

        // Simulate an external hand-edit landing (a completely unrelated reload), not a
        // ViewModel-initiated write -- exercises the permanent, whole-lifetime subscription,
        // not the write-then-verify settle-waiter.
        File.WriteAllText(service.DictionaryPath, /*lang=json*/ """
            { "schemaVersion": 1, "entries": [ { "type": "vocabularyTerm", "id": "hand-edited", "term": "Claude" } ] }
            """);
        service.SimulateSuccessfulReload();

        Assert.True(posted > 0);
        Assert.Single(vm.Entries);
        Assert.Equal("hand-edited", vm.Entries[0].Id);
    }

    [Fact]
    public void TypeFilter_ShowsOnlyMatchingEntries()
    {
        var vocab = new VocabularyTerm { Id = "v1", Term = "webMethods" };
        var correction = new CorrectionPair { Id = "c1", From = "a", To = "b" };
        using var service = new FakeDictionaryService(new DictionaryConfig([vocab, correction], [], 1));
        var vm = CreateViewModel(service);

        Assert.Equal(2, vm.Entries.Count);

        vm.TypeFilter = DictionaryEntryKind.CorrectionPair;

        Assert.Single(vm.Entries);
        Assert.Equal("c1", vm.Entries[0].Id);
    }

    private static string ExtractLastEntryId(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var entries = doc.RootElement.GetProperty("entries");
        var last = entries.EnumerateArray().Last();
        return last.GetProperty("id").GetString()!;
    }
}
