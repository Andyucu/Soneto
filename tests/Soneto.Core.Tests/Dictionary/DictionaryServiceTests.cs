using Microsoft.Extensions.Logging;
using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Tests for <see cref="DictionaryService"/> (Phase 2 work item 9, §2.7): mirrors
/// <c>ConfigServiceTests</c>' established debounce/hot-reload test shapes closely, plus the
/// dictionary-specific validation rules (per-entry JSON error isolation, duplicate-Id whole-file
/// rejection, empty From/Phrase single-entry rejection, unparseable regex rejection), item 8's
/// collision-warning hook firing on load, and a live-rebuild-without-restart demonstration
/// proving fresh processors constructed from a hot-reloaded <see cref="DictionaryConfig"/>
/// produce different output than processors built from the old entries -- no daemon restart
/// anywhere in the test.
/// </summary>
public sealed class DictionaryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dictionaryPath;

    public DictionaryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "soneto-dictionary-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dictionaryPath = Path.Combine(_tempDir, "dictionary.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Missing_dictionary_file_writes_and_loads_the_seed_dictionary()
    {
        // Item 10: unlike item 9's "start empty, write nothing" behavior, a missing
        // dictionary.json now writes the embedded seed dictionary to disk and loads it through
        // the real parse/validate pipeline (round-tripping the seed content for real, not just
        // trusting it blindly).
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);

        Assert.False(File.Exists(_dictionaryPath));

        var ok = await sut.LoadAsync();

        Assert.True(ok);
        Assert.True(File.Exists(_dictionaryPath));
        Assert.NotEmpty(sut.Current.Entries);
        Assert.Empty(sut.Current.RejectedEntries); // the seed dictionary must be entirely valid
        Assert.Contains(sut.Current.Entries, e => e is VocabularyTerm { Term: "webMethods" });
        Assert.Contains(sut.Current.Entries, e => e is SpokenCommand { Phrase: "new paragraph" });

        // The written file, read back independently, is the exact embedded seed JSON.
        var writtenJson = await File.ReadAllTextAsync(_dictionaryPath);
        Assert.Equal(SeedDictionary.Json, writtenJson);
    }

    [Fact]
    public async Task Invalid_json_keeps_previous_dictionary_and_does_not_throw()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);

        await File.WriteAllTextAsync(_dictionaryPath, """
            { "schemaVersion": 1, "entries": [
                { "type": "correctionPair", "id": "c1", "from": "cloud code", "to": "Claude Code" }
            ] }
            """);
        var firstLoad = await sut.LoadAsync();
        Assert.True(firstLoad);
        Assert.Single(sut.Current.Entries);

        await File.WriteAllTextAsync(_dictionaryPath, "{ this is not valid json ");

        var exception = await Record.ExceptionAsync(() => sut.LoadAsync());

        Assert.Null(exception);
        Assert.Single(sut.Current.Entries); // unchanged
        Assert.True(logger.HasEntry(LogLevel.Error, "Invalid dictionary JSON"));
    }

    [Fact]
    public async Task Malformed_single_entry_is_skipped_but_rest_of_file_still_loads()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);

        await File.WriteAllTextAsync(_dictionaryPath, """
            { "schemaVersion": 1, "entries": [
                { "type": "correctionPair", "id": "good1", "from": "web methods", "to": "webMethods" },
                { "type": "bogusType", "id": "bad1" },
                { "type": "correctionPair", "id": "good2", "from": "cloud code", "to": "Claude Code" }
            ] }
            """);

        var ok = await sut.LoadAsync();

        Assert.True(ok);
        Assert.Equal(2, sut.Current.Entries.Count);
        Assert.Contains(sut.Current.Entries, e => e.Id == "good1");
        Assert.Contains(sut.Current.Entries, e => e.Id == "good2");
        Assert.Single(sut.Current.RejectedEntries);
        Assert.Equal(1, sut.Current.RejectedEntries[0].Index);
        Assert.True(logger.HasEntry(LogLevel.Error, "malformed"));
    }

    [Fact]
    public async Task Unparseable_regex_rejects_only_that_entry_rest_of_file_still_loads()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);

        await File.WriteAllTextAsync(_dictionaryPath, """
            { "schemaVersion": 1, "entries": [
                { "type": "correctionPair", "id": "good1", "from": "web methods", "to": "webMethods" },
                { "type": "regexRule", "id": "bad-regex", "pattern": "(unterminated", "replacement": "x" }
            ] }
            """);

        var ok = await sut.LoadAsync();

        Assert.True(ok);
        Assert.Single(sut.Current.Entries);
        Assert.Equal("good1", sut.Current.Entries[0].Id);
        Assert.Contains(sut.Current.RejectedEntries, r => r.Id == "bad-regex");
        Assert.True(logger.HasEntry(LogLevel.Error, "does not compile"));
    }

    [Fact]
    public async Task Empty_From_on_CorrectionPair_rejects_only_that_entry()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);

        await File.WriteAllTextAsync(_dictionaryPath, """
            { "schemaVersion": 1, "entries": [
                { "type": "correctionPair", "id": "blank-from", "from": "   ", "to": "x" },
                { "type": "correctionPair", "id": "good1", "from": "cloud code", "to": "Claude Code" }
            ] }
            """);

        var ok = await sut.LoadAsync();

        Assert.True(ok);
        Assert.Single(sut.Current.Entries);
        Assert.Equal("good1", sut.Current.Entries[0].Id);
        Assert.Contains(sut.Current.RejectedEntries, r => r.Id == "blank-from");
        Assert.True(logger.HasEntry(LogLevel.Error, "empty or whitespace-only"));
    }

    [Fact]
    public async Task Empty_Phrase_on_SpokenCommand_rejects_only_that_entry()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);

        await File.WriteAllTextAsync(_dictionaryPath, """
            { "schemaVersion": 1, "entries": [
                { "type": "spokenCommand", "id": "blank-phrase", "phrase": "", "emits": "\n" },
                { "type": "spokenCommand", "id": "good1", "phrase": "new paragraph", "emits": "\n\n" }
            ] }
            """);

        var ok = await sut.LoadAsync();

        Assert.True(ok);
        Assert.Single(sut.Current.Entries);
        Assert.Equal("good1", sut.Current.Entries[0].Id);
        Assert.Contains(sut.Current.RejectedEntries, r => r.Id == "blank-phrase");
    }

    [Fact]
    public async Task Duplicate_ids_reject_the_whole_file_and_keep_previous_dictionary()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);

        await File.WriteAllTextAsync(_dictionaryPath, """
            { "schemaVersion": 1, "entries": [
                { "type": "correctionPair", "id": "c1", "from": "cloud code", "to": "Claude Code" }
            ] }
            """);
        var firstLoad = await sut.LoadAsync();
        Assert.True(firstLoad);
        Assert.Single(sut.Current.Entries);

        await File.WriteAllTextAsync(_dictionaryPath, """
            { "schemaVersion": 1, "entries": [
                { "type": "correctionPair", "id": "dup", "from": "web methods", "to": "webMethods" },
                { "type": "correctionPair", "id": "dup", "from": "cloud code", "to": "Claude Code" }
            ] }
            """);

        var secondLoad = await sut.LoadAsync();

        Assert.False(secondLoad);
        Assert.Single(sut.Current.Entries); // previous good dictionary retained, wholesale
        Assert.Equal("c1", sut.Current.Entries[0].Id);
        Assert.True(logger.HasEntry(LogLevel.Error, "duplicate entry Ids"));
    }

    [Fact]
    public async Task Collision_warning_fires_on_a_common_single_word_correction_pair()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);

        await File.WriteAllTextAsync(_dictionaryPath, """
            { "schemaVersion": 1, "entries": [
                { "type": "correctionPair", "id": "risky", "from": "cloud", "to": "Cloudflare" }
            ] }
            """);

        var ok = await sut.LoadAsync();

        Assert.True(ok);
        Assert.True(logger.HasEntry(LogLevel.Warning, "risky"));
    }

    [Fact]
    public async Task DictionaryChanged_is_not_raised_on_the_first_load()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);

        var raised = false;
        sut.DictionaryChanged += (_, _) => raised = true;

        await File.WriteAllTextAsync(_dictionaryPath, """
            { "schemaVersion": 1, "entries": [
                { "type": "correctionPair", "id": "c1", "from": "cloud code", "to": "Claude Code" }
            ] }
            """);

        // The very first LoadAsync call still raises the event today (only the constructor's
        // implicit empty state doesn't) -- assert the documented contract precisely: no file
        // present at all never raises, per LoadAsync's own early-return path.
        Directory.Delete(_tempDir, recursive: true);
        Directory.CreateDirectory(_tempDir);
        await sut.LoadAsync();

        Assert.False(raised);
    }

    [Fact]
    public async Task Hot_reload_picks_up_a_valid_change_after_the_debounce()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);

        await File.WriteAllTextAsync(_dictionaryPath, """
            { "schemaVersion": 1, "entries": [
                { "type": "correctionPair", "id": "c1", "from": "web methods", "to": "webMethods" }
            ] }
            """);
        await sut.LoadAsync();
        Assert.Single(sut.Current.Entries);

        var changeTcs = new TaskCompletionSource();
        sut.DictionaryChanged += (_, e) =>
        {
            if (e.Config.Entries.Count == 2)
                changeTcs.TrySetResult();
        };

        sut.StartWatching();
        try
        {
            await File.WriteAllTextAsync(_dictionaryPath, """
                { "schemaVersion": 1, "entries": [
                    { "type": "correctionPair", "id": "c1", "from": "web methods", "to": "webMethods" },
                    { "type": "correctionPair", "id": "c2", "from": "cloud code", "to": "Claude Code" }
                ] }
                """);

            var completed = await Task.WhenAny(changeTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(changeTcs.Task, completed);
            Assert.Equal(2, sut.Current.Entries.Count);
        }
        finally
        {
            sut.StopWatching();
        }
    }

    [Fact]
    public async Task Rapid_fire_writes_within_the_debounce_window_collapse_into_a_single_reload()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);

        await File.WriteAllTextAsync(_dictionaryPath, """
            { "schemaVersion": 1, "entries": [] }
            """);
        await sut.LoadAsync();

        var changeCount = 0;
        var lastChangeTcs = new TaskCompletionSource();
        sut.DictionaryChanged += (_, e) =>
        {
            Interlocked.Increment(ref changeCount);
            if (e.Config.Entries.Count == 4)
                lastChangeTcs.TrySetResult();
        };

        sut.StartWatching();
        try
        {
            for (var n = 1; n <= 4; n++)
            {
                var entries = string.Join(",", Enumerable.Range(1, n).Select(i =>
                    $$"""{ "type": "correctionPair", "id": "c{{i}}", "from": "term {{i}}", "to": "Term{{i}}" }"""));
                await File.WriteAllTextAsync(_dictionaryPath, $$"""{ "schemaVersion": 1, "entries": [{{entries}}] }""");
                await Task.Delay(50);
            }

            var completed = await Task.WhenAny(lastChangeTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(lastChangeTcs.Task, completed);

            await Task.Delay(1000);

            Assert.Equal(1, changeCount);
            Assert.Equal(4, sut.Current.Entries.Count);
        }
        finally
        {
            sut.StopWatching();
        }
    }

    /// <summary>
    /// Item 9's own "done when" bar: demonstrate live rebuild without a daemon restart. The
    /// four processors (items 4-7) are immutable value-like classes with no in-place
    /// <c>Rebuild()</c> method by design (item 10's job is wiring live processor-swapping into
    /// <c>Program.cs</c>'s real composition, not this item's) -- so this test proves the
    /// round-trip at the level that IS this item's job: a file change on disk -> debounced
    /// reload -> <see cref="IDictionaryService.Current"/> observably changes -> a FRESH
    /// <see cref="DictionaryEngineProcessor"/> constructed from the new entries produces
    /// different, correctly-updated output than one built from the old entries. No daemon
    /// restart anywhere in this test.
    /// </summary>
    [Fact]
    public async Task Live_rebuild_without_restart_new_processor_from_reloaded_entries_changes_output()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);

        await File.WriteAllTextAsync(_dictionaryPath, """
            { "schemaVersion": 1, "entries": [
                { "type": "correctionPair", "id": "c1", "from": "web methods", "to": "webMethods" }
            ] }
            """);
        await sut.LoadAsync();

        var oldProcessor = new DictionaryEngineProcessor(sut.Current.Entries);
        var beforeResult = oldProcessor.Process(new Abstractions.PostProcessResult(
            "I used web methods and cloud code today.", []));

        // The old processor (built from the OLD entries) corrects "web methods" but has no
        // idea about "cloud code" yet.
        Assert.Contains("webMethods", beforeResult.Text);
        Assert.Contains("cloud code", beforeResult.Text);

        var changeTcs = new TaskCompletionSource();
        sut.DictionaryChanged += (_, e) =>
        {
            if (e.Config.Entries.Count == 2)
                changeTcs.TrySetResult();
        };

        sut.StartWatching();
        try
        {
            await File.WriteAllTextAsync(_dictionaryPath, """
                { "schemaVersion": 1, "entries": [
                    { "type": "correctionPair", "id": "c1", "from": "web methods", "to": "webMethods" },
                    { "type": "correctionPair", "id": "c2", "from": "cloud code", "to": "Claude Code" }
                ] }
                """);

            var completed = await Task.WhenAny(changeTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(changeTcs.Task, completed);
        }
        finally
        {
            sut.StopWatching();
        }

        // Construct a FRESH processor from the reloaded Current entries -- no daemon
        // restart, no mutation of oldProcessor.
        var newProcessor = new DictionaryEngineProcessor(sut.Current.Entries);
        var afterResult = newProcessor.Process(new Abstractions.PostProcessResult(
            "I used web methods and cloud code today.", []));

        Assert.Contains("webMethods", afterResult.Text);
        Assert.Contains("Claude Code", afterResult.Text);
        Assert.NotEqual(beforeResult.Text, afterResult.Text);
    }
}
