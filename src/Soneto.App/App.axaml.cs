using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Soneto.App.Views;
using Soneto.Composition;
using Soneto.Core;
using Soneto.Core.Audio;
using Soneto.Core.Configuration;
using Soneto.Core.Dictionary;
using Soneto.Core.History;

namespace Soneto.App;

public partial class App : Application
{
    // "Pause dictation" is an inert UI toggle for now — see SetupTrayIcon's comment
    // for why (nothing in Soneto.App runs a real dictation session yet).
    private bool _isPaused;

    // Item 5 (§3.9's sub-task A): a minimal logging setup, since Soneto.App has none yet —
    // a bare console provider, deliberately NOT a full Serilog setup like Soneto.Daemon's
    // (disproportionate for this item, per the work item's own instruction). Kept for the
    // whole app's lifetime so PipelineHost's background startup task can keep logging after
    // this method returns.
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder =>
        builder.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        }).SetMinimumLevel(LogLevel.Information));

    // Set true immediately before a REAL shutdown (the tray menu's "Quit" item) so
    // MainWindow.Closing's close-to-tray handler knows to let this one close through
    // instead of intercepting it. Every other close (the window's own X button) must
    // NOT have this set.
    private bool _isQuitting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Item 6 (§3.10) architecture decision, documented here at its one construction
            // site: the real SqliteHistoryStore is constructed EAGERLY, right here, BEFORE
            // MainWindow/PipelineHost even exist — NOT gated behind PipelineHost.Started's
            // success. History persistence (browsing/searching PAST sessions) must work even
            // in a session where the live dictation pipeline never comes up (e.g. a missing
            // ASR model) — it has no dependency on whether a live SessionController exists
            // this run. See HistoryPaths.cs's own doc comment for the full writeup.
            var historyStore = new SqliteHistoryStore(
                _loggerFactory.CreateLogger<SqliteHistoryStore>(), HistoryPaths.Resolve());

            // Item 7 (§3.11) architecture decision, documented here at its one construction
            // site — mirrors item 6's IHistoryStore decoupling above, but for a subtly
            // different reason. SqliteHistoryStore's real work is lazy (deferred to first
            // call), so constructing it eagerly above is genuinely free/synchronous.
            // DictionaryService.LoadAsync() is NOT lazy — it does real, if fast, work (a file
            // read, full JSON validation, possibly writing the embedded seed dictionary on a
            // first run) that the Dictionary editor needs to have ALREADY COMPLETED by the
            // time it's constructed: a hand-edited dictionary.json that already exists on disk
            // must show its real entries immediately when the editor opens, not start empty
            // and eventually catch up once some later reload happens to land.
            //
            // Contrast with PipelineHost below, which stays deliberately fire-and-forget: the
            // ASR model's cold load alone is ~1.7-2s and the tray/window must appear
            // immediately regardless of whether a model is even present. dictionary.json's
            // read+validate is nothing like that (a small local JSON file, no model I/O) — a
            // brief synchronous wait for the dictionary specifically, before MainWindow is
            // even constructed, is a deliberate, bounded, documented exception to "never block
            // the UI thread on startup work", not a precedent for blocking on anything else.
            //
            // Mechanism chosen: a documented `.GetAwaiter().GetResult()` right here, rather
            // than making Program.cs's Main async and awaiting before
            // StartWithClassicDesktopLifetime (the other reasonable option).
            //
            // REAL BUG, hit and fixed during this item's own live verification (not
            // hypothetical): DaemonComposition.LoadAndStartWatchingDictionaryAsync and
            // DictionaryService.LoadAsync underneath it never touch Dispatcher.UIThread or any
            // other Avalonia API directly, but that alone does NOT make a bare
            // `.GetAwaiter().GetResult()` safe here. By the time OnFrameworkInitializationCompleted
            // runs, Avalonia has ALREADY installed its own SynchronizationContext on this (the
            // UI) thread -- so every plain `await` inside LoadAsync (e.g.
            // `await File.ReadAllTextAsync(...)`) captures THAT context by default and tries to
            // resume its continuation back on this exact thread once the I/O completes. Blocking
            // this same thread on `.GetResult()` while waiting for that continuation is a
            // textbook UI-thread deadlock -- confirmed for real: the app hung indefinitely (no
            // window, no tray, flat ~98MB memory, no log output) until this was fixed.
            //
            // Fixed by running the whole awaited call on a thread-pool thread via Task.Run
            // BEFORE blocking on it: Task.Run's delegate starts with
            // SynchronizationContext.Current == null (thread-pool threads never have one), so
            // every `await` inside captures no context at all and its continuations simply
            // resume on whatever thread-pool thread happens to be free -- nothing ever needs to
            // post back to this (blocked) UI thread, so there is nothing to deadlock on. This
            // still genuinely blocks OnFrameworkInitializationCompleted until the load
            // completes (Task.Run(...).GetAwaiter().GetResult() only returns once the inner task
            // does), which is exactly the synchronous-wait behavior this architecture decision
            // calls for.
            var dictionaryService = DaemonComposition.CreateDictionaryService(
                _loggerFactory, DictionaryPaths.Resolve());
            var dictionaryLogger = _loggerFactory.CreateLogger("DictionaryEditorStartup");
            Task.Run(() => DaemonComposition.LoadAndStartWatchingDictionaryAsync(dictionaryService, dictionaryLogger))
                .GetAwaiter().GetResult();

            // Item 8 (§3.12) architecture decision, documented here at its one construction
            // site — mirrors item 7's IDictionaryService decoupling immediately above (which
            // itself mirrored item 6's IHistoryStore decoupling), for the exact same reason:
            // the Settings page needs a synchronously-loaded IConfigService available before the
            // UI is even shown, so a hand-edited config.json shows its real values immediately
            // rather than starting from `new SonetoConfig()` defaults and catching up later.
            // config.json's read+validate is the same kind of fast, non-model-loading work
            // dictionary.json's is (see the big comment above this one for the full "why a
            // brief synchronous wait here is a deliberate, bounded exception, not a precedent"
            // reasoning, and the REAL deadlock story it documents) — reusing that exact,
            // already-proven-safe Task.Run(...).GetAwaiter().GetResult() pattern here rather
            // than rediscovering the bug a third time.
            //
            // PipelineHost.StartInBackground (below) now takes this already-loaded
            // IConfigService as a parameter instead of constructing/loading/watching its own —
            // see that method's own doc comment. Soneto.Daemon is completely unaffected: it
            // never calls PipelineHost and continues to construct/load/watch its own
            // IConfigService via DaemonComposition directly, exactly as before.
            var configService = DaemonComposition.CreateConfigService(_loggerFactory, ConfigPaths.Resolve());
            var configLogger = _loggerFactory.CreateLogger("SettingsStartup");
            Task.Run(() => DaemonComposition.LoadAndStartWatchingConfigAsync(configService, configLogger))
                .GetAwaiter().GetResult();

            // Item 10 (§3.14): the "auto-delete history after N days" background sweep. Started
            // here, alongside historyStore/configService above (both already eagerly
            // constructed/loaded by this point), mirroring history persistence's own item-6
            // decoupling from PipelineHost's success/failure — the sweep must run on schedule
            // even in a session where the live dictation pipeline never comes up. See
            // HistoryRetentionSweeper's own doc comment for the full design (lock-guarded Timer
            // lifecycle mirroring CaptureModeController's established pattern; reads the
            // retention window fresh from configService.Current on every tick, so a Settings-page
            // edit takes effect on the next sweep with no restart needed).
            var historyRetentionSweeper = new HistoryRetentionSweeper(
                historyStore, configService, _loggerFactory.CreateLogger<HistoryRetentionSweeper>());
            historyRetentionSweeper.Start();

            // Item 4 (§3.8): the real nav-rail shell replaces item 3's scratch
            // KitchenSinkView content — see MainWindow.axaml's own comment.
            var mainWindow = new MainWindow(historyStore, dictionaryService, configService, _loggerFactory);
            desktop.MainWindow = mainWindow;

            SetupTrayIcon(desktop, mainWindow);

            // Close-to-tray (§3.8): closing the window via its X button minimizes to
            // tray instead of exiting the process, the standard tray-app convention.
            //
            // HONEST GAP, still not silently skipped, now updated by item 8: §3.8 also says
            // this should be "configurable in Settings... don't hardcode it as the only
            // behavior", since some users will want X to actually quit. IConfigService IS now
            // wired into Soneto.App (this item), but the orchestrating session's own item-8
            // scope (§3.12 — Hotkey/ASR/Audio/Injection/language-profile-hint sections) does
            // not list a close-to-tray toggle among the fields to build, and SonetoConfig has no
            // schema field for it today. Rather than invent a new, unscoped config field/UI
            // control to close this specific gap unasked, this remains the correct, current,
            // FIXED default behavior for now — a genuinely close-to-tray-configurable setting is
            // left as a still-open, named gap for whichever future item's scope actually calls
            // for it.
            mainWindow.Closing += (_, e) =>
            {
                if (_isQuitting)
                {
                    return;
                }

                e.Cancel = true;
                mainWindow.Hide();
            };

            // Item 5 (§3.9): the Recording HUD, created now (empty/hidden) so it's ready
            // to attach to the real pipeline the moment PipelineHost.Started fires — see
            // PipelineHost.cs's own doc comment for the full ordering-decision rationale
            // (this is the FIRST real pipeline wiring in Soneto.App; item 6/History
            // reuses the same running SessionController, it does not re-wire anything).
            var recordingHud = new RecordingHud();
            PipelineHost.Started += (_, e) =>
            {
                // PipelineHost.Started can fire on any thread — marshal to the UI thread
                // before touching recordingHud, which is itself an Avalonia control.
                Dispatcher.UIThread.Post(() => recordingHud.AttachSession(e.Controller, e.AudioCapture));

                // Item 6 (§3.10): if/when a live pipeline ever comes up this session, feed its
                // completed dictations into the SAME historyStore constructed above — no new
                // event/plumbing beyond what item 5 already built (this is the one existing
                // hook point, PipelineHost.Started, that item 5's own doc comment already
                // earmarked for item 6 to reuse). Fire-and-forget, per AppendAsync's own
                // explicit "must never throw, safe to call fire-and-forget, must never be
                // awaited inline before text is already injected" contract — never awaited
                // here, and never allowed to block whatever thread raised DictationCompleted.
                e.Controller.DictationCompleted += (_, dictation) =>
                {
                    _ = AppendHistoryEntryAndMaybeSaveDebugAudioSafelyAsync(historyStore, configService, dictation);
                };
            };

            // Fire-and-forget, non-blocking (§3.9 sub-task A's explicit requirement): the
            // tray/window above are already shown by this point regardless of whether real
            // pipeline startup (ASR model load alone is ~1.7-2s) ever succeeds. The already-
            // loaded configService (item 8) and dictionaryService (item 7) are handed in so the
            // real pipeline composition, if/when the model loads, is still built from real
            // config/dictionary state exactly as before this refactor — see
            // PipelineHost.StartInBackground's own doc comment.
            PipelineHost.StartInBackground(_loggerFactory, configService, dictionaryService);

            // Best-effort cleanup on real shutdown only (Exit fires once, when the process is
            // actually about to end — never on close-to-tray, which the Closing handler above
            // already intercepts before it ever reaches this point). Not awaited (Exit's
            // handler is synchronous) — a missed final flush here is not worse than the
            // process simply being killed, which SqliteHistoryStore's own "never throws"
            // contract already tolerates.
            desktop.Exit += (_, _) =>
            {
                historyRetentionSweeper.Dispose();
                _ = historyStore.DisposeAsync().AsTask();
                (dictionaryService as IDisposable)?.Dispose();
                (configService as IDisposable)?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Post-review fix: the `DictationCompleted` handler above discards the task
    /// <see cref="IHistoryStore.AppendAsync"/> returns (correctly — it must never be awaited
    /// inline on <c>SessionController</c>'s worker thread), but a discarded task's exception is
    /// otherwise unobserved. <see cref="IHistoryStore.AppendAsync"/>'s own contract already
    /// catches every realistic environmental failure (bad path, locked file, etc.) and returns
    /// <c>-1</c> instead of throwing — the only way this could still fault is caller misuse
    /// (calling a member after <see cref="IHistoryStore.DisposeAsync"/>, e.g. a
    /// <c>DictationCompleted</c> firing during/after app shutdown teardown), which is a narrow
    /// window but worth catching and logging rather than leaving as a silent unobserved-task
    /// exception.
    ///
    /// <para>
    /// Item 10 (§3.14): also, opt-in only, saves a correlated debug audio WAV clip. **Design
    /// decision (the correlation-timing question this item's own instructions asked to be
    /// resolved explicitly, not assumed): the WAV write happens strictly AFTER
    /// <see cref="IHistoryStore.AppendAsync"/> returns its real, database-assigned
    /// <see cref="HistoryEntry.Id"/>** — never speculatively before the row exists — since that
    /// Id is the ONLY correlation key (see <see cref="Soneto.Core.Audio.DebugAudioStore"/>'s own
    /// doc comment for why a separate client-generated key was considered and rejected as
    /// unnecessary complexity here). The retention flag/max-clip-count are both read fresh from
    /// <paramref name="configService"/>.Current at call time (not captured once at startup), so a
    /// Settings-page toggle takes effect on the very next dictation with no restart needed — this
    /// one setting has no "no live rebuild into a running SessionController" cost the way
    /// ASR/hotkey/injection settings do, since it only gates whether this handler writes a file.
    /// </para>
    /// </summary>
    private async Task AppendHistoryEntryAndMaybeSaveDebugAudioSafelyAsync(
        IHistoryStore historyStore, IConfigService configService, DictationCompletedEventArgs dictation)
    {
        long id;
        try
        {
            id = await historyStore.AppendAsync(new HistoryEntry(
                Id: 0,
                Timestamp: DateTimeOffset.UtcNow,
                RawText: dictation.RawText,
                FinalText: dictation.FinalText,
                RulesFired: dictation.RulesFired,
                RecordingDuration: dictation.RecordingDuration,
                ProcessingLatency: dictation.ProcessingLatency,
                WasInjected: dictation.WasInjected));
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<App>().LogError(ex,
                "Failed to append a completed dictation to history (likely a DictationCompleted " +
                "event firing during/after app shutdown teardown); this history entry is lost.");
            return;
        }

        if (id < 0)
            return; // AppendAsync already logged the failure; no row exists to correlate audio with.

        var privacy = configService.Current.DataPrivacy;
        if (!privacy.DebugAudioRetentionEnabled)
            return;

        await DebugAudioStore.SaveClipAsync(
            DebugAudioPaths.Resolve(), id, dictation.AudioSamples, CaptureFormatSelector.TargetSampleRate,
            privacy.DebugAudioRetentionMaxClips, _loggerFactory.CreateLogger<App>());
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow mainWindow)
    {
        // Avalonia 12.0.4's tray-icon API (confirmed directly against the actually-
        // pinned package version via reflection, not assumed from a different
        // Avalonia release — this shape has moved across versions): TrayIcon is an
        // Avalonia.Controls.TrayIcon instance, attached to the Application via the
        // static TrayIcon.SetIcons(Application, TrayIcons) attached-property-style
        // method; the icon itself is a WindowIcon; the right-click menu is a
        // NativeMenu of NativeMenuItem/NativeMenuItemSeparator; left-click is the
        // TrayIcon.Clicked event.
        var trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Soneto.App/Assets/tray-icon.ico"))),
            ToolTipText = "Soneto",
        };

        var openItem = new NativeMenuItem("Open Soneto");
        openItem.Click += (_, _) => mainWindow.ShowAndActivate();

        // "Pause dictation" — per this item's own scoped-down instruction: a
        // correctly-behaving, inert UI toggle (flips a boolean, updates its own
        // checked state/label), NOT wired to any real session, since
        // SessionController/the real daemon composition isn't wired into Soneto.App
        // yet (that's item 10/11's job). This deliberately has NO real effect on
        // dictation yet.
        var pauseItem = new NativeMenuItem("Pause dictation")
        {
            ToggleType = MenuItemToggleType.CheckBox,
        };
        pauseItem.Click += (_, _) =>
        {
            _isPaused = !_isPaused;
            pauseItem.IsChecked = _isPaused;
        };

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) =>
        {
            _isQuitting = true;
            desktop.Shutdown();
        };

        trayIcon.Menu = new NativeMenu
        {
            openItem,
            new NativeMenuItemSeparator(),
            pauseItem,
            new NativeMenuItemSeparator(),
            quitItem,
        };

        // Left-click restores/focuses the main window.
        trayIcon.Clicked += (_, _) => mainWindow.ShowAndActivate();

        TrayIcon.SetIcons(this, new TrayIcons { trayIcon });
    }
}
