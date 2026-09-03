using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Soneto.Core.Configuration;

/// <summary>
/// File-backed <see cref="IConfigService"/> implementation. Platform-agnostic (only uses
/// <see cref="System.IO.FileSystemWatcher"/>, <see cref="System.Text.Json"/> and BCL file
/// I/O), so it legitimately lives in Soneto.Core per item 1's hard rule.
/// </summary>
public sealed class ConfigService : IConfigService, IDisposable
{
    private const int DebounceMs = 500;

    private readonly ILogger<ConfigService> _logger;
    private readonly object _gate = new();

    private SonetoConfig _current = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private bool _disposed;

    public ConfigService(ILogger<ConfigService> logger, string configPath)
    {
        _logger = logger;
        ConfigPath = configPath;
    }

    public string ConfigPath { get; }

    public SonetoConfig Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;

    public async Task<bool> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ConfigPath))
        {
            _logger.LogInformation(
                "Config file not found at {ConfigPath}; writing defaults", ConfigPath);

            var defaults = new SonetoConfig();
            try
            {
                await WriteAsync(defaults, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // LoadAsync's contract is "never throws" (see IConfigService), so a
                // permission-denied config dir/AV lock etc. must never propagate — fall
                // back to in-memory defaults and let the daemon keep starting.
                _logger.LogError(ex,
                    "Failed to write default config to {ConfigPath}; using in-memory defaults only",
                    ConfigPath);
            }

            SetCurrent(defaults, raiseEvent: false);
            return true;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(ConfigPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex,
                "Failed to read config file at {ConfigPath}; keeping previous config", ConfigPath);
            return false;
        }

        SonetoConfig? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SonetoConfig>(json, BuildOptions());
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Invalid config JSON at {ConfigPath}; keeping previous config", ConfigPath);
            return false;
        }

        if (parsed is null)
        {
            _logger.LogError(
                "Config file at {ConfigPath} parsed to null; keeping previous config", ConfigPath);
            return false;
        }

        SetCurrent(parsed, raiseEvent: true);
        _logger.LogInformation("Config loaded from {ConfigPath}", ConfigPath);
        return true;
    }

    public void StartWatching()
    {
        if (_watcher is not null)
            return;

        var dir = Path.GetDirectoryName(ConfigPath);
        var file = Path.GetFileName(ConfigPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
        {
            _logger.LogWarning(
                "Cannot watch config path {ConfigPath}: could not resolve directory/file name",
                ConfigPath);
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);

            var watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
            _logger.LogInformation("Watching {ConfigPath} for changes", ConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Hot-reload is a nice-to-have, not a startup requirement — never let a
            // watcher construction failure (permission-denied dir, etc.) take the
            // daemon down. Config loading itself is unaffected.
            _logger.LogError(ex,
                "Failed to start watching {ConfigPath} for changes; hot-reload disabled",
                ConfigPath);
        }
    }

    public void StopWatching()
    {
        if (_watcher is null)
            return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileEvent;
        _watcher.Created -= OnFileEvent;
        _watcher.Renamed -= OnFileEvent;
        _watcher.Error -= OnWatcherError;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // The watcher's internal buffer can overflow under rapid writes/directory churn,
        // at which point it silently stops delivering change notifications. There's no
        // in-process state corruption to recover from — just warn loudly so this doesn't
        // fail silently, per should-fix 8 of the item 2 review.
        _logger.LogWarning(e.GetException(),
            "Config file watcher for {ConfigPath} reported an error; hot-reload may stop working until the daemon restarts",
            ConfigPath);
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => _ = OnDebounceElapsedAsync(), null, DebounceMs, Timeout.Infinite);
        }
    }

    private async Task OnDebounceElapsedAsync()
    {
        try
        {
            var reloaded = await LoadAsync();
            if (reloaded)
                _logger.LogInformation("Config hot-reloaded from {ConfigPath}", ConfigPath);
        }
        catch (Exception ex)
        {
            // Defensive: LoadAsync itself never throws, but this runs on a bare
            // Timer callback thread with no other error boundary above it.
            _logger.LogError(ex, "Unexpected error during config hot-reload");
        }
    }

    private void SetCurrent(SonetoConfig config, bool raiseEvent)
    {
        lock (_gate)
            _current = config;

        if (raiseEvent)
            ConfigChanged?.Invoke(this, new ConfigChangedEventArgs { Config = config });
    }

    private async Task WriteAsync(SonetoConfig config, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, BuildOptions());
        await File.WriteAllTextAsync(ConfigPath, json, ct);
    }

    /// <summary>
    /// Phase 3 item 8: extracted into the shared, public <see cref="ConfigJsonOptions"/> so
    /// <c>SettingsViewModel</c> (a second writer of <c>config.json</c>) can reuse the exact same
    /// options this service reads with, rather than re-deriving them — see that class's own doc
    /// comment. This method now just supplies the same logger-backed fallback callback the
    /// former inline implementation always used.
    /// </summary>
    private JsonSerializerOptions BuildOptions() => ConfigJsonOptions.Create(raw =>
        _logger.LogWarning(
            "Unknown audio.captureMode value '{Value}'; falling back to OnDemand", raw));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        // Outside the lock: StopWatching() unsubscribes/disposes the FileSystemWatcher,
        // which is independent of _gate. The race this used to have — an in-flight
        // OnFileEvent winning against Dispose(), creating a fresh timer no one captures
        // after Dispose() already read/disposed the old one — is now closed because
        // _disposed is flipped to true under the SAME lock that guards the timer field:
        // any OnFileEvent call that acquires _gate after this point sees _disposed and
        // returns before touching the timer at all.
        StopWatching();
    }
}
