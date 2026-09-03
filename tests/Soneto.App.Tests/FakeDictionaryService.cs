using System.Text.Json;
using Soneto.Core.Dictionary;

namespace Soneto.App.Tests;

/// <summary>
/// An in-memory-plus-a-real-temp-file <see cref="IDictionaryService"/> test double, mirroring
/// <see cref="FakeHistoryStore"/>'s established pattern. <see cref="DictionaryEditorViewModel"/>'s
/// entire write path (per its own doc comment) writes real JSON directly to
/// <see cref="IDictionaryService.DictionaryPath"/> -- so, exactly like this project's existing
/// <c>ConfigService</c>/<c>DictionaryService</c> tests use real temp files rather than mocking
/// file I/O, <see cref="DictionaryPath"/> here points at a genuine (test-owned, throwaway) temp
/// file so the ViewModel's writes are real and inspectable, while everything else about "what the
/// real <see cref="DictionaryService"/> would have done next" (validate/reload/raise
/// <see cref="DictionaryChanged"/>) is under the test's explicit control via the
/// <c>Simulate*</c> methods below, rather than re-implementing that validation logic here.
/// </summary>
public sealed class FakeDictionaryService : IDictionaryService, IDisposable
{
    public FakeDictionaryService(DictionaryConfig? initial = null)
    {
        DictionaryPath = Path.Combine(Path.GetTempPath(), $"soneto-dictionary-test-{Guid.NewGuid():N}.json");
        Current = initial ?? DictionaryConfig.Empty;
    }

    public string DictionaryPath { get; }

    public DictionaryConfig Current { get; private set; }

    private event EventHandler<DictionaryChangedEventArgs>? _dictionaryChanged;
    private TaskCompletionSource? _subscribedTcs;

    public event EventHandler<DictionaryChangedEventArgs>? DictionaryChanged
    {
        add { _dictionaryChanged += value; _subscribedTcs?.TrySetResult(); }
        remove => _dictionaryChanged -= value;
    }

    /// <summary>
    /// Completes the moment <see cref="DictionaryEditorViewModel"/>'s
    /// <c>WaitForNextDictionaryChangedOrTimeoutAsync</c> subscribes to
    /// <see cref="DictionaryChanged"/>, so a test can fire <see cref="SimulateSuccessfulReload"/>/
    /// <see cref="SimulateEntryRejectedReload"/> with a guarantee it will be observed, instead of
    /// racing a wall-clock delay under load.
    /// </summary>
    public Task WaitForNextSubscriptionAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _subscribedTcs = tcs;
        return tcs.Task;
    }

    /// <summary>Every raw JSON string written to <see cref="DictionaryPath"/>, most recent last
    /// -- read directly off disk each time a <c>Simulate*</c> method is called, so a test can
    /// assert on exactly what the ViewModel wrote.</summary>
    public List<string> WrittenJsonSnapshots { get; } = [];

    /// <summary>
    /// Simulates the real <see cref="DictionaryService"/> successfully reloading after a write:
    /// reads whatever the ViewModel just wrote to <see cref="DictionaryPath"/>, deserializes it
    /// via the exact same <see cref="DictionaryDocument"/>/<see cref="DictionaryJsonOptions"/>
    /// shape the real service uses, applies it verbatim as the new <see cref="Current"/> (no
    /// re-validation -- a test that wants to simulate a REJECTION calls
    /// <see cref="SimulateEntryRejectedReload"/>/<see cref="SimulateWholeFileRejected"/>
    /// instead), and raises <see cref="DictionaryChanged"/>.
    /// </summary>
    public void SimulateSuccessfulReload()
    {
        var json = ReadFileWithRetry();
        WrittenJsonSnapshots.Add(json);
        var document = JsonSerializer.Deserialize<DictionaryDocument>(json, DictionaryJsonOptions.Create())!;
        Current = new DictionaryConfig(document.Entries, [], document.SchemaVersion);
        _dictionaryChanged?.Invoke(this, new DictionaryChangedEventArgs { Config = Current });
    }

    /// <summary>
    /// Simulates the real <see cref="DictionaryService"/> rejecting ONE entry (an unparseable
    /// regex, an empty From/Phrase) while the rest of the reload still lands -- per
    /// <see cref="DictionaryConfig.RejectedEntries"/>'s own contract, the rejected entry is
    /// simply absent from <see cref="Current"/>.<c>Entries</c> and shows up in
    /// <see cref="Current"/>.<c>RejectedEntries</c> instead.
    /// </summary>
    public void SimulateEntryRejectedReload(string rejectedId, string reason)
    {
        WrittenJsonSnapshots.Add(ReadFileWithRetry());
        var rejected = Current.RejectedEntries.Append(new RejectedDictionaryEntry(0, rejectedId, reason)).ToList();
        Current = new DictionaryConfig(Current.Entries, rejected, Current.SchemaVersion);
        _dictionaryChanged?.Invoke(this, new DictionaryChangedEventArgs { Config = Current });
    }

    /// <summary>
    /// Simulates the real <see cref="DictionaryService"/>'s whole-file duplicate-Id rejection --
    /// per that behavior's own doc comment, <see cref="DictionaryChanged"/> is NEVER raised for
    /// this case and <see cref="Current"/> is left completely untouched (the previous good
    /// config is retained). Deliberately a no-op method, kept only for tests to call explicitly
    /// (rather than simply not calling anything) so the intent at each call site is clear.
    /// </summary>
    public void SimulateWholeFileRejected()
    {
        WrittenJsonSnapshots.Add(ReadFileWithRetry());
        // Intentionally: no Current mutation, no DictionaryChanged raise.
    }

    /// <summary>
    /// A test's poll loop only confirms <see cref="File.Exists"/> before calling a
    /// <c>Simulate*</c> method, but <c>File.WriteAllTextAsync</c>'s underlying file handle can
    /// still be mid-close for a brief window after the file becomes visible to
    /// <see cref="File.Exists"/> on Windows -- a real, observed sharing-violation race, not
    /// hypothetical. A short bounded retry closes it without weakening what's actually being
    /// tested (the ViewModel's own write content).
    /// </summary>
    private string ReadFileWithRetry()
    {
        IOException? last = null;
        for (var i = 0; i < 50; i++)
        {
            try
            {
                return File.ReadAllText(DictionaryPath);
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(10);
            }
        }

        throw last!;
    }

    public Task<bool> LoadAsync(CancellationToken ct = default) => Task.FromResult(true);

    public void StartWatching()
    {
    }

    public void StopWatching()
    {
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(DictionaryPath))
                File.Delete(DictionaryPath);
        }
        catch (IOException)
        {
            // Best-effort test cleanup only.
        }
    }
}
