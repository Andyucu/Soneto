using Soneto.App.ViewModels;
using Soneto.Core.Configuration;

namespace Soneto.App.Tests;

/// <summary>
/// Unit tests for <see cref="SettingsViewModel"/> against <see cref="FakeConfigService"/> --
/// same philosophy as <c>DictionaryEditorViewModelTests</c> (§3.15): a plain xunit test, the
/// internal test-facing constructor replacing <c>Dispatcher.UIThread</c> marshaling with a
/// synchronous stand-in and using a short settle timeout instead of the real ~1.5s default, no
/// headless Avalonia harness needed.
/// </summary>
public sealed class SettingsViewModelTests
{
    private static readonly TimeSpan ShortSettleTimeout = TimeSpan.FromMilliseconds(150);

    private static SettingsViewModel CreateViewModel(
        FakeConfigService service, FakeHistoryStore? historyStore = null, string? debugAudioDir = null) =>
        new(service, historyStore ?? new FakeHistoryStore(), uiThreadPost: action => action(),
            settleTimeout: ShortSettleTimeout, debugAudioDir: debugAudioDir, logger: null);

    /// <summary>Polls until the ViewModel's write has actually landed on disk -- same reasoning
    /// as <c>DictionaryEditorViewModelTests.WaitForFileWriteAsync</c> (a real, observed Windows
    /// sharing-violation race against a bare <see cref="File.Exists"/> check).</summary>
    private static async Task WaitForFileWriteAsync(string path)
    {
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
                    // Still mid-write/mid-close; keep polling.
                }
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Expected {path} to have been written and readable by now.");
    }

    [Fact]
    public void Construction_ReadsCurrentConfigValuesIntoExposedProperties()
    {
        var config = new SonetoConfig
        {
            Hotkey = new HotkeyConfig { Key = "LeftShift", Suppress = false },
            Asr = new AsrConfig { ModelDir = @"C:\models\parakeet", NumThreads = 6, DecodingMethod = "greedy_search" },
            Audio = new AudioConfig
            {
                DeviceId = "mic-1",
                CaptureMode = CaptureMode.WarmIdle,
                Vad = new VadConfig { Threshold = 0.6, MinSilenceMs = 400, MinSpeechMs = 300 },
            },
            Injection = new InjectionConfig
            {
                Method = InjectionMethod.UnicodeSynth,
                PasteChord = "ctrl+shift+v",
                ClipboardPolicy = ClipboardPolicy.Never,
            },
            LanguageProfile = new LanguageProfileConfig { SecondaryTriggerKey = "LeftAlt" },
            DataPrivacy = new DataPrivacyConfig
            {
                DebugAudioRetentionEnabled = true,
                DebugAudioRetentionMaxClips = 5,
                HistoryAutoDeleteAfterDays = 30,
            },
        };
        using var service = new FakeConfigService(config);

        var vm = CreateViewModel(service);

        Assert.Equal("LeftShift", vm.HotkeyKey);
        Assert.False(vm.HotkeySuppress);
        Assert.Equal(@"C:\models\parakeet", vm.AsrModelDir);
        Assert.Equal("6", vm.AsrNumThreadsText);
        Assert.Equal("greedy_search", vm.AsrDecodingMethod);
        Assert.Equal("WarmIdle", vm.AudioCaptureMode);
        Assert.Equal("mic-1", vm.AudioDeviceId);
        Assert.Equal("0.6", vm.VadThresholdText);
        Assert.Equal("400", vm.VadMinSilenceMsText);
        Assert.Equal("300", vm.VadMinSpeechMsText);
        Assert.Equal("UnicodeSynth", vm.InjectionMethod);
        Assert.Equal("ctrl+shift+v", vm.InjectionPasteChord);
        Assert.Equal("Never", vm.InjectionClipboardPolicy);
        Assert.Equal("LeftAlt", vm.LanguageProfileSecondaryTriggerKey);
        Assert.True(vm.DebugAudioRetentionEnabled);
        Assert.Equal("5", vm.DebugAudioRetentionMaxClipsText);
        Assert.Equal("30", vm.HistoryAutoDeleteAfterDaysText);
    }

    [Fact]
    public async Task ChangingHotkey_WritesCorrectlyShapedConfig_AndReportsSuccess()
    {
        using var service = new FakeConfigService();
        var vm = CreateViewModel(service);

        vm.HotkeyKey = "RightAlt";
        vm.HotkeySuppress = false;

        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();
        await subscribed;
        service.SimulateSuccessfulReload();
        await saveTask;

        Assert.False(vm.StatusIsError);
        Assert.Equal("RightAlt", service.Current.Hotkey.Key);
        Assert.False(service.Current.Hotkey.Suppress);
    }

    [Fact]
    public async Task IsSaving_StaysTrueForTheEntireWriteThenSettleWindow_AndFalseOnlyAfter()
    {
        // Post-review regression test: SettingsView.axaml now disables the WHOLE form (not
        // just the Save button) while IsSaving is true, specifically to prevent a concurrent
        // edit to a different field from being silently clobbered when RefreshFromCurrent runs
        // after the settle wait (see SettingsViewModel's own doc comment for the full race this
        // is meant to close). That UI-level fix only actually closes the race if IsSaving is
        // true for the ENTIRE window from "write started" through "RefreshFromCurrent has run
        // and settled" -- this test proves that property directly, independent of any XAML/
        // Avalonia rendering.
        using var service = new FakeConfigService();
        var vm = CreateViewModel(service);
        Assert.False(vm.IsSaving);

        vm.HotkeyKey = "RightAlt";
        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();

        // IsSaving must already be true the moment the write has landed on disk -- i.e. for
        // the whole settle-wait window a concurrent edit could otherwise race against.
        await WaitForFileWriteAsync(service.ConfigPath);
        Assert.True(vm.IsSaving);

        await subscribed;
        service.SimulateSuccessfulReload();
        await saveTask;

        Assert.False(vm.IsSaving);
    }

    [Fact]
    public async Task ChangingAsrFields_WritesCorrectlyShapedConfig()
    {
        using var service = new FakeConfigService();
        var vm = CreateViewModel(service);

        vm.AsrModelDir = @"D:\custom-model";
        vm.AsrNumThreadsText = "8";
        vm.AsrDecodingMethod = "modified_beam_search";

        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();
        await subscribed;
        service.SimulateSuccessfulReload();
        await saveTask;

        Assert.False(vm.StatusIsError);
        Assert.Equal(@"D:\custom-model", service.Current.Asr.ModelDir);
        Assert.Equal(8, service.Current.Asr.NumThreads);
        Assert.Equal("modified_beam_search", service.Current.Asr.DecodingMethod);
    }

    [Fact]
    public async Task ChangingAudioFields_WritesCorrectlyShapedConfig()
    {
        using var service = new FakeConfigService();
        var vm = CreateViewModel(service);

        vm.AudioCaptureMode = "AlwaysOn";
        vm.AudioDeviceId = "mic-42";
        vm.VadThresholdText = "0.75";
        vm.VadMinSilenceMsText = "500";
        vm.VadMinSpeechMsText = "200";

        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();
        await subscribed;
        service.SimulateSuccessfulReload();
        await saveTask;

        Assert.False(vm.StatusIsError);
        Assert.Equal(CaptureMode.AlwaysOn, service.Current.Audio.CaptureMode);
        Assert.Equal("mic-42", service.Current.Audio.DeviceId);
        Assert.Equal(0.75, service.Current.Audio.Vad.Threshold);
        Assert.Equal(500, service.Current.Audio.Vad.MinSilenceMs);
        Assert.Equal(200, service.Current.Audio.Vad.MinSpeechMs);
    }

    [Fact]
    public async Task ChangingInjectionFields_WritesCorrectlyShapedConfig()
    {
        using var service = new FakeConfigService();
        var vm = CreateViewModel(service);

        vm.InjectionMethod = "UnicodeSynth";
        vm.InjectionPasteChord = "ctrl+alt+v";
        vm.InjectionClipboardPolicy = "BestEffort";

        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();
        await subscribed;
        service.SimulateSuccessfulReload();
        await saveTask;

        Assert.False(vm.StatusIsError);
        Assert.Equal(InjectionMethod.UnicodeSynth, service.Current.Injection.Method);
        Assert.Equal("ctrl+alt+v", service.Current.Injection.PasteChord);
        Assert.Equal(ClipboardPolicy.BestEffort, service.Current.Injection.ClipboardPolicy);
    }

    [Fact]
    public async Task ChangingLanguageProfileSecondaryTriggerKey_WritesToNewConfigSection()
    {
        using var service = new FakeConfigService();
        var vm = CreateViewModel(service);

        vm.LanguageProfileSecondaryTriggerKey = "LeftShift";

        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();
        await subscribed;
        service.SimulateSuccessfulReload();
        await saveTask;

        Assert.False(vm.StatusIsError);
        Assert.Equal("LeftShift", service.Current.LanguageProfile.SecondaryTriggerKey);
    }

    [Fact]
    public async Task Save_PreservesFieldsNotExposedByThisPage()
    {
        var config = new SonetoConfig
        {
            Injection = new InjectionConfig
            {
                PreDelayMs = 999,
                ClipboardRestoreDelayMs = 888,
                SanitizeModifiers = false,
                TargetLostPolicy = "abort",
            },
            Logging = new LoggingConfig { Level = "Debug", RetainDays = 42 },
        };
        using var service = new FakeConfigService(config);
        var vm = CreateViewModel(service);

        vm.InjectionPasteChord = "ctrl+v";

        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();
        await subscribed;
        service.SimulateSuccessfulReload();
        await saveTask;

        Assert.False(vm.StatusIsError);
        Assert.Equal(999, service.Current.Injection.PreDelayMs);
        Assert.Equal(888, service.Current.Injection.ClipboardRestoreDelayMs);
        Assert.False(service.Current.Injection.SanitizeModifiers);
        Assert.Equal("abort", service.Current.Injection.TargetLostPolicy);
        Assert.Equal("Debug", service.Current.Logging.Level);
        Assert.Equal(42, service.Current.Logging.RetainDays);
    }

    [Fact]
    public async Task Save_WithInvalidNumericField_DoesNotWrite_AndReportsError()
    {
        using var service = new FakeConfigService();
        var vm = CreateViewModel(service);

        vm.AsrNumThreadsText = "not-a-number";

        await vm.SaveAsync();

        Assert.True(vm.StatusIsError);
        Assert.False(File.Exists(service.ConfigPath));
    }

    [Fact]
    public void ConfigChanged_TriggersRefresh_ThroughInjectedUiThreadPost()
    {
        using var service = new FakeConfigService();
        var posted = 0;
        var vm = new SettingsViewModel(
            service, new FakeHistoryStore(), uiThreadPost: action => { posted++; action(); },
            settleTimeout: ShortSettleTimeout, debugAudioDir: null, logger: null);

        // Simulate an external hand-edit to config.json landing (the real ConfigService would
        // raise this after its own file-watcher debounce settles).
        var newConfig = new SonetoConfig { Hotkey = new HotkeyConfig { Key = "F13", Suppress = true } };
        service.SimulateExternalChange(newConfig);

        Assert.True(posted > 0);
        Assert.Equal("F13", vm.HotkeyKey);
    }

    // --- Data & privacy (item 10, §3.14) ---

    [Fact]
    public async Task ChangingDataPrivacyFields_WritesCorrectlyShapedConfig()
    {
        using var service = new FakeConfigService();
        var vm = CreateViewModel(service);

        vm.DebugAudioRetentionEnabled = true;
        vm.DebugAudioRetentionMaxClipsText = "5";
        vm.HistoryAutoDeleteAfterDaysText = "30";

        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();
        await subscribed;
        service.SimulateSuccessfulReload();
        await saveTask;

        Assert.False(vm.StatusIsError);
        Assert.True(service.Current.DataPrivacy.DebugAudioRetentionEnabled);
        Assert.Equal(5, service.Current.DataPrivacy.DebugAudioRetentionMaxClips);
        Assert.Equal(30, service.Current.DataPrivacy.HistoryAutoDeleteAfterDays);
    }

    [Fact]
    public async Task BlankHistoryAutoDeleteAfterDays_WritesNull_MeaningNeverAutoDelete()
    {
        var config = new SonetoConfig
        {
            DataPrivacy = new DataPrivacyConfig { HistoryAutoDeleteAfterDays = 30 },
        };
        using var service = new FakeConfigService(config);
        var vm = CreateViewModel(service);
        Assert.Equal("30", vm.HistoryAutoDeleteAfterDaysText);

        vm.HistoryAutoDeleteAfterDaysText = "";

        var subscribed = service.WaitForNextSubscriptionAsync();
        var saveTask = vm.SaveAsync();
        await subscribed;
        service.SimulateSuccessfulReload();
        await saveTask;

        Assert.False(vm.StatusIsError);
        Assert.Null(service.Current.DataPrivacy.HistoryAutoDeleteAfterDays);
    }

    [Fact]
    public async Task Save_WithInvalidHistoryAutoDeleteDays_DoesNotWrite_AndReportsError()
    {
        using var service = new FakeConfigService();
        var vm = CreateViewModel(service);

        vm.HistoryAutoDeleteAfterDaysText = "not-a-number";

        await vm.SaveAsync();

        Assert.True(vm.StatusIsError);
        Assert.False(File.Exists(service.ConfigPath));
    }

    [Fact]
    public async Task Save_WithInvalidDebugAudioMaxClips_DoesNotWrite_AndReportsError()
    {
        using var service = new FakeConfigService();
        var vm = CreateViewModel(service);

        vm.DebugAudioRetentionMaxClipsText = "-1";

        await vm.SaveAsync();

        Assert.True(vm.StatusIsError);
        Assert.False(File.Exists(service.ConfigPath));
    }

    [Fact]
    public async Task PanicWipeAsync_EmptiesTheHistoryStore_AndReportsStatus()
    {
        using var service = new FakeConfigService();
        var historyStore = new FakeHistoryStore();
        historyStore.Seed(
            new Soneto.Core.History.HistoryEntry(
                1, DateTimeOffset.UtcNow, "raw", "final", [], TimeSpan.Zero, TimeSpan.Zero, true));
        var vm = CreateViewModel(service, historyStore);

        await vm.PanicWipeAsync();

        var remaining = await historyStore.SearchAsync(null, 100, 0);
        Assert.Empty(remaining);
        Assert.False(vm.IsWiping);
        Assert.NotNull(vm.WipeStatusMessage);
    }

    [Fact]
    public async Task PanicWipeAsync_DeletesEveryDebugAudioClip()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"soneto-debug-audio-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir, "1.wav"), [0]);
            File.WriteAllBytes(Path.Combine(tempDir, "2.wav"), [0]);

            using var service = new FakeConfigService();
            var vm = CreateViewModel(service, debugAudioDir: tempDir);

            await vm.PanicWipeAsync();

            Assert.Empty(Directory.GetFiles(tempDir, "*.wav"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task IsWiping_IsTrueWhileTheHistoryStoreWipeIsInFlight_AndFalseAfter()
    {
        using var service = new FakeConfigService();
        var historyStore = new FakeHistoryStore();
        historyStore.AddPanicWipeGate();
        var vm = CreateViewModel(service, historyStore);
        Assert.False(vm.IsWiping);

        var wipeTask = vm.PanicWipeAsync();
        // Give the async call a chance to reach IsWiping=true before it blocks on the gate.
        await Task.Delay(20);
        Assert.True(vm.IsWiping);

        historyStore.ReleasePanicWipeGate();
        await wipeTask;

        Assert.False(vm.IsWiping);
    }
}
