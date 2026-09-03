namespace Soneto.Core.Dictionary;

/// <summary>
/// Loads, validates, persists and hot-reloads <c>dictionary.json</c>, mirroring
/// <see cref="Soneto.Core.Configuration.IConfigService"/>'s shape/contract closely (Phase 2
/// plan §2.7's own recommendation: a sibling service with the identical debounce/hot-reload
/// contract, not a generalized dual-file <c>ConfigService</c>). <c>Program.cs</c>'s
/// <c>BuildPostProcessors</c> (item 10, NOT this item) is expected to subscribe to
/// <see cref="DictionaryChanged"/> and construct fresh <c>DictionaryEngineProcessor</c>/
/// <c>RegexRuleProcessor</c>/<c>SpokenCommandsExtensionProcessor</c> instances from the new
/// <see cref="Current"/> entries -- see this item's tests for a live demonstration of that
/// round-trip without any daemon restart.
/// </summary>
public interface IDictionaryService
{
    /// The resolved absolute path this service reads from / writes to.
    string DictionaryPath { get; }

    /// The most recently successfully validated dictionary. Never null after construction --
    /// starts as <see cref="DictionaryConfig.Empty"/> until the first successful load.
    DictionaryConfig Current { get; }

    /// Raised after a dictionary file is (re)loaded successfully and <see cref="Current"/>
    /// changes. NOT raised for a load that fails outright (invalid JSON, duplicate Ids) --
    /// the previous <see cref="Current"/> is retained and no event fires.
    event EventHandler<DictionaryChangedEventArgs>? DictionaryChanged;

    /// Loads `dictionary.json` if present; if it is missing, writes the embedded seed dictionary
    /// (<see cref="SeedDictionary"/>, item 10) to <see cref="DictionaryPath"/> as its default
    /// content -- mirroring <c>IConfigService.LoadAsync</c>'s "write defaults on first run"
    /// contract -- and loads THAT. Never throws: invalid JSON, a structurally ambiguous file
    /// (duplicate Ids), an unreadable file, or a failure writing the first-run seed dictionary
    /// logs an error and leaves <see cref="Current"/> unchanged (or, for the first-run write
    /// failure specifically, falls back to the in-memory seed dictionary so the daemon still
    /// starts with something usable). Individual malformed/invalid entries are skipped
    /// (logged) without failing the whole load, per §2.7's per-entry validation rules.
    Task<bool> LoadAsync(CancellationToken ct = default);

    /// Starts watching <see cref="DictionaryPath"/> for changes, debounced 500ms (same window
    /// as <c>ConfigService</c>), calling <see cref="LoadAsync"/> on settle. Safe to call
    /// multiple times (idempotent).
    void StartWatching();

    /// Stops the file watcher, if running.
    void StopWatching();
}

public sealed class DictionaryChangedEventArgs : EventArgs
{
    public required DictionaryConfig Config { get; init; }
}
