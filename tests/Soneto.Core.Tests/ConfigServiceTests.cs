using Microsoft.Extensions.Logging;
using Soneto.Core.Configuration;

namespace Soneto.Core.Tests;

/// <summary>
/// Captures log calls so tests can assert on specific warnings/errors without any real
/// logging sink. No audio device, no model file — pure file-IO + JSON, per plan §1.13.
/// </summary>
internal sealed class TestLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }

    public bool HasEntry(LogLevel level, string containing) =>
        Entries.Any(e => e.Level == level && e.Message.Contains(containing, StringComparison.OrdinalIgnoreCase));
}

public sealed class ConfigServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public ConfigServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "soneto-config-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Missing_config_file_writes_defaults_and_loads_successfully()
    {
        var logger = new TestLogger<ConfigService>();
        var sut = new ConfigService(logger, _configPath);

        Assert.False(File.Exists(_configPath));

        var ok = await sut.LoadAsync();

        Assert.True(ok);
        Assert.True(File.Exists(_configPath));
        Assert.Equal(CaptureMode.OnDemand, sut.Current.Audio.CaptureMode);
        Assert.Equal(4, sut.Current.Asr.NumThreads);
        Assert.Equal("RightControl", sut.Current.Hotkey.Key);
    }

    [Fact]
    public async Task Invalid_json_keeps_previous_config_and_does_not_throw()
    {
        var logger = new TestLogger<ConfigService>();
        var sut = new ConfigService(logger, _configPath);

        // First establish a known-good "previous" config.
        await File.WriteAllTextAsync(_configPath, """{ "asr": { "numThreads": 6 } }""");
        var firstLoad = await sut.LoadAsync();
        Assert.True(firstLoad);
        Assert.Equal(6, sut.Current.Asr.NumThreads);

        // Now corrupt it.
        await File.WriteAllTextAsync(_configPath, "{ this is not valid json ");

        var exception = await Record.ExceptionAsync(() => sut.LoadAsync());

        Assert.Null(exception);
        Assert.Equal(6, sut.Current.Asr.NumThreads); // unchanged
        Assert.True(logger.HasEntry(LogLevel.Error, "Invalid config JSON"));
    }

    [Fact]
    public async Task Unknown_capture_mode_falls_back_to_OnDemand_with_a_warning()
    {
        var logger = new TestLogger<ConfigService>();
        var sut = new ConfigService(logger, _configPath);

        await File.WriteAllTextAsync(
            _configPath, """{ "audio": { "captureMode": "TotallyBogusMode" } }""");

        var ok = await sut.LoadAsync();

        Assert.True(ok);
        Assert.Equal(CaptureMode.OnDemand, sut.Current.Audio.CaptureMode);
        Assert.True(logger.HasEntry(LogLevel.Warning, "TotallyBogusMode"));
    }

    [Fact]
    public async Task Hot_reload_picks_up_a_valid_change_after_the_debounce()
    {
        var logger = new TestLogger<ConfigService>();
        var sut = new ConfigService(logger, _configPath);

        await sut.LoadAsync(); // writes defaults (numThreads=4)
        Assert.Equal(4, sut.Current.Asr.NumThreads);

        var changeTcs = new TaskCompletionSource();
        sut.ConfigChanged += (_, e) =>
        {
            if (e.Config.Asr.NumThreads == 12)
                changeTcs.TrySetResult();
        };

        sut.StartWatching();
        try
        {
            await File.WriteAllTextAsync(_configPath, """{ "asr": { "numThreads": 12 } }""");

            var completed = await Task.WhenAny(changeTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(changeTcs.Task, completed);
            Assert.Equal(12, sut.Current.Asr.NumThreads);
        }
        finally
        {
            sut.StopWatching();
        }
    }

    [Fact]
    public async Task Rapid_fire_writes_within_the_debounce_window_collapse_into_a_single_reload()
    {
        var logger = new TestLogger<ConfigService>();
        var sut = new ConfigService(logger, _configPath);

        await sut.LoadAsync(); // writes defaults (numThreads=4)
        Assert.Equal(4, sut.Current.Asr.NumThreads);

        var changeCount = 0;
        var lastChangeTcs = new TaskCompletionSource();
        sut.ConfigChanged += (_, e) =>
        {
            Interlocked.Increment(ref changeCount);
            if (e.Config.Asr.NumThreads == 15)
                lastChangeTcs.TrySetResult();
        };

        sut.StartWatching();
        try
        {
            // Four writes, each well within the 500ms debounce window of the previous
            // one. A non-debounced (or incorrectly-debounced) implementation would fire
            // ConfigChanged multiple times here instead of collapsing to one.
            for (var numThreads = 12; numThreads <= 15; numThreads++)
            {
                await File.WriteAllTextAsync(
                    _configPath, $$"""{ "asr": { "numThreads": {{numThreads}} } }""");
                await Task.Delay(50);
            }

            var completed = await Task.WhenAny(lastChangeTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(lastChangeTcs.Task, completed);

            // Give any spurious extra reloads a chance to land before asserting the count.
            await Task.Delay(1000);

            Assert.Equal(1, changeCount);
            Assert.Equal(15, sut.Current.Asr.NumThreads);
        }
        finally
        {
            sut.StopWatching();
        }
    }

    [Fact]
    public async Task PerApp_overrides_including_the_new_method_field_round_trip_through_config_json()
    {
        // Phase 4 item 2 (§4.4): PerAppOverride.Method is the field this item added. It is only
        // useful if it actually survives a real config.json read -- the resolver's own unit
        // tests construct the table in memory and would not catch a serialization/casing gap.
        var logger = new TestLogger<ConfigService>();
        var sut = new ConfigService(logger, _configPath);

        await File.WriteAllTextAsync(_configPath, """
            {
              "injection": {
                "perApp": {
                  "wt.exe": { "method": "unicodeSynth" },
                  "WindowsTerminal.exe": { "pasteChord": "ctrl+shift+v" },
                  "Teams.exe": { "clipboardRestoreDelayMs": 300 }
                }
              }
            }
            """);

        await sut.LoadAsync();

        var perApp = sut.Current.Injection.PerApp;
        Assert.Equal(InjectionMethod.UnicodeSynth, perApp["wt.exe"].Method);
        Assert.Null(perApp["wt.exe"].PasteChord);
        Assert.Equal("ctrl+shift+v", perApp["WindowsTerminal.exe"].PasteChord);
        Assert.Null(perApp["WindowsTerminal.exe"].Method);
        Assert.Equal(300, perApp["Teams.exe"].ClipboardRestoreDelayMs);
    }

    [Fact]
    public async Task Written_default_config_json_contains_the_shipped_per_app_example_entries()
    {
        // The defaults ConfigService writes on first run are the discoverability surface for
        // per-app overrides (the Settings page deliberately does not expose Injection.PerApp).
        var logger = new TestLogger<ConfigService>();
        var sut = new ConfigService(logger, _configPath);

        await sut.LoadAsync();

        var written = await File.ReadAllTextAsync(_configPath);
        Assert.Contains("perApp", written);
        Assert.Contains("WindowsTerminal.exe", written);
        // NOTE (pre-existing, NOT introduced by Phase 4 item 2): System.Text.Json's default
        // encoder escapes '+', so every paste chord in config.json -- including the top-level
        // injection.pasteChord, since Phase 1 -- is written as "ctrl+shift+v". It
        // round-trips correctly, so this is a config.json readability wart rather than a
        // behaviour bug; assert on the re-read value, not on the raw substring.
        var reread = new ConfigService(new TestLogger<ConfigService>(), _configPath);
        await reread.LoadAsync();
        Assert.Equal("ctrl+shift+v", reread.Current.Injection.PerApp["WindowsTerminal.exe"].PasteChord);
        Assert.Equal(300, reread.Current.Injection.PerApp["Teams.exe"].ClipboardRestoreDelayMs);
    }
}
