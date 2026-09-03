namespace Soneto.Core.Configuration;

/// <summary>
/// Loads, validates, persists and hot-reloads <see cref="SonetoConfig"/>. The future
/// SessionController (item 9) is expected to subscribe to <see cref="ConfigChanged"/> to
/// react to hot-reloaded config; no consumer is built yet, this just makes the surface
/// area sensible for that later use.
/// </summary>
public interface IConfigService
{
    /// The resolved absolute path this service reads from / writes to.
    string ConfigPath { get; }

    /// The most recently successfully loaded config. Never null after construction —
    /// starts as `new SonetoConfig()` (all defaults) until the first successful load.
    SonetoConfig Current { get; }

    /// Raised after a config file is (re)loaded successfully and <see cref="Current"/>
    /// changes. Not raised for the very first load performed by the constructor's
    /// implicit defaults, nor when a load fails and the previous config is retained.
    event EventHandler<ConfigChangedEventArgs>? ConfigChanged;

    /// Loads (or, on first run, writes-then-loads) the config file. Never throws:
    /// invalid JSON or an unreadable file logs an error and leaves <see cref="Current"/>
    /// unchanged.
    Task<bool> LoadAsync(CancellationToken ct = default);

    /// Starts watching <see cref="ConfigPath"/> for changes, debounced 500ms, calling
    /// <see cref="LoadAsync"/> on settle. Safe to call multiple times (idempotent).
    void StartWatching();

    /// Stops the file watcher, if running.
    void StopWatching();
}

public sealed class ConfigChangedEventArgs : EventArgs
{
    public required SonetoConfig Config { get; init; }
}
