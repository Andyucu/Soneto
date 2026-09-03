using System.Text.Json;
using Soneto.Core.Configuration;

namespace Soneto.App.Tests;

/// <summary>
/// An in-memory-plus-a-real-temp-file <see cref="IConfigService"/> test double, mirroring
/// <see cref="FakeDictionaryService"/>'s established pattern exactly (Phase 3 item 8).
/// <see cref="SettingsViewModel"/>'s entire write path (per its own doc comment) writes real
/// JSON directly to <see cref="IConfigService.ConfigPath"/> -- so, exactly like
/// <see cref="FakeDictionaryService"/>, <see cref="ConfigPath"/> here points at a genuine
/// (test-owned, throwaway) temp file so the ViewModel's writes are real and inspectable, while
/// "what the real <see cref="ConfigService"/> would have done next" (reload, raise
/// <see cref="ConfigChanged"/>) is under the test's explicit control via
/// <see cref="SimulateSuccessfulReload"/>, rather than re-implementing that logic here.
/// </summary>
public sealed class FakeConfigService : IConfigService, IDisposable
{
    public FakeConfigService(SonetoConfig? initial = null)
    {
        ConfigPath = Path.Combine(Path.GetTempPath(), $"soneto-config-test-{Guid.NewGuid():N}.json");
        Current = initial ?? new SonetoConfig();
    }

    public string ConfigPath { get; }

    public SonetoConfig Current { get; private set; }

    private event EventHandler<ConfigChangedEventArgs>? _configChanged;
    private TaskCompletionSource? _subscribedTcs;

    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged
    {
        add { _configChanged += value; _subscribedTcs?.TrySetResult(); }
        remove => _configChanged -= value;
    }

    /// <summary>
    /// Completes the moment <see cref="SettingsViewModel"/>'s
    /// <c>WaitForNextConfigChangedOrTimeoutAsync</c> subscribes to <see cref="ConfigChanged"/>,
    /// so a test can fire <see cref="SimulateSuccessfulReload"/>/<see cref="SimulateExternalChange"/>
    /// with a guarantee it will be observed, instead of racing a wall-clock delay under load.
    /// </summary>
    public Task WaitForNextSubscriptionAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _subscribedTcs = tcs;
        return tcs.Task;
    }

    /// <summary>Every raw JSON string written to <see cref="ConfigPath"/>, most recent last --
    /// read directly off disk each time <see cref="SimulateSuccessfulReload"/> is called, so a
    /// test can assert on exactly what the ViewModel wrote.</summary>
    public List<string> WrittenJsonSnapshots { get; } = [];

    /// <summary>
    /// Simulates the real <see cref="ConfigService"/> successfully reloading after a write:
    /// reads whatever the ViewModel just wrote to <see cref="ConfigPath"/>, deserializes it via
    /// the exact same <see cref="ConfigJsonOptions"/> shape the real service uses, applies it
    /// verbatim as the new <see cref="Current"/>, and raises <see cref="ConfigChanged"/>.
    /// </summary>
    public void SimulateSuccessfulReload()
    {
        var json = ReadFileWithRetry();
        WrittenJsonSnapshots.Add(json);
        Current = JsonSerializer.Deserialize<SonetoConfig>(json, ConfigJsonOptions.Create())!;
        _configChanged?.Invoke(this, new ConfigChangedEventArgs { Config = Current });
    }

    /// <summary>
    /// Simulates an EXTERNAL change landing (e.g. a hand-edit to <c>config.json</c> picked up by
    /// the real service's file watcher) without going through a file write at all -- sets
    /// <see cref="Current"/> directly and raises <see cref="ConfigChanged"/>. Distinct from
    /// <see cref="SimulateSuccessfulReload"/>, which specifically reads back whatever a
    /// ViewModel's OWN write just produced.
    /// </summary>
    public void SimulateExternalChange(SonetoConfig config)
    {
        Current = config;
        _configChanged?.Invoke(this, new ConfigChangedEventArgs { Config = config });
    }

    /// <summary>
    /// Simulates the real <see cref="ConfigService.LoadAsync"/>'s "invalid JSON/parses to
    /// null -> keep previous config, never raise ConfigChanged" behavior -- deliberately a
    /// no-op method (like <see cref="FakeDictionaryService.SimulateWholeFileRejected"/>), kept
    /// only so a test's intent is explicit at the call site.
    /// </summary>
    public void SimulateReloadFailed()
    {
        WrittenJsonSnapshots.Add(ReadFileWithRetry());
        // Intentionally: no Current mutation, no ConfigChanged raise.
    }

    /// <summary>Same real, observed Windows sharing-violation race
    /// <see cref="FakeDictionaryService"/>'s own identical helper guards against -- a short
    /// bounded retry closes it without weakening what's actually being tested.</summary>
    private string ReadFileWithRetry()
    {
        IOException? last = null;
        for (var i = 0; i < 50; i++)
        {
            try
            {
                return File.ReadAllText(ConfigPath);
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
            if (File.Exists(ConfigPath))
                File.Delete(ConfigPath);
        }
        catch (IOException)
        {
            // Best-effort test cleanup only.
        }
    }
}
