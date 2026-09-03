using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soneto.Core.Configuration;
using Soneto.Core.History;

namespace Soneto.App;

/// <summary>
/// Phase 3 item 10 (§3.14): the "auto-delete history after N days" background sweep, calling
/// <see cref="IHistoryStore.PurgeOlderThanAsync"/> on a timer -- "daily is enough" per this
/// item's own spec, no <see cref="System.IO.FileSystemWatcher"/>-grade responsiveness needed.
///
/// <para>
/// <b>Lives in <c>Soneto.App</c>, constructed/started from the composition root, mirroring item
/// 6's <see cref="IHistoryStore"/> decoupling</b> -- <see cref="SqliteHistoryStore"/> is
/// constructed eagerly in <c>App.axaml.cs</c>, independent of whether <c>PipelineHost</c>'s real
/// dictation pipeline ever comes up (see <c>HistoryPaths</c>'s own doc comment). The retention
/// sweep has the exact same independence requirement -- old history rows must be purged on
/// schedule even in a session where the live pipeline never starts -- so this class is started
/// alongside the history store itself, not inside <c>PipelineHost</c>.
/// </para>
///
/// <para>
/// <b>Reads the retention window fresh from <see cref="IConfigService.Current"/> on every tick,
/// not baked in at construction</b> -- so a Settings-page edit to
/// <see cref="DataPrivacyConfig.HistoryAutoDeleteAfterDays"/> takes effect on the very next
/// sweep, with no restart required (unlike this project's several other "no live rebuild into a
/// running SessionController" gaps -- this setting affects only a plain background loop with no
/// analogous rebuild cost). <c>null</c>/non-positive means "disabled" -- the sweep still runs on
/// schedule but is a no-op each time.
/// </para>
///
/// <para>
/// <b>Timer lifecycle -- lock-guarded, mirroring <c>CaptureModeController</c>'s established
/// <c>_gate</c>/<c>ScheduleIdleClose</c>/<c>CancelIdleCloseTimer</c> pattern</b> (this project's
/// own hardened precedent for a background <see cref="Timer"/> rather than a naive
/// <c>Timer.Dispose()</c>: <see cref="Dispose"/> disposes under the same lock
/// <see cref="Start"/> constructs under, so a callback that's already been dequeued off the
/// thread pool and is racing <see cref="Dispose"/> either runs to completion (harmless -- a
/// sweep that fires once more during shutdown is not a correctness problem, unlike
/// <c>CaptureModeController</c>'s stream-reuse race, since there is no "new utterance" this
/// class could clobber) or never starts at all (whichever way the race resolves). No stale-
/// generation guard is needed here (unlike <c>CaptureModeController</c>'s idle-close timer, which
/// specifically guards against tearing down a stream a NEWER utterance has already started
/// reusing) -- there is no analogous "reused resource" a stale sweep tick could ever misfire
/// against; a redundant sweep is simply a second, harmless no-op-if-nothing-old-exists call to
/// <see cref="IHistoryStore.PurgeOlderThanAsync"/>.
/// </para>
/// </summary>
public sealed class HistoryRetentionSweeper : IDisposable
{
    /// <summary>Real production interval -- "daily is enough" per this item's own spec.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromDays(1);

    private readonly IHistoryStore _historyStore;
    private readonly IConfigService _configService;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;

    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;

    /// <param name="interval">
    /// Injectable purely so a test can drive a short interval instead of
    /// <see cref="DefaultInterval"/>'s real 24 hours -- mirrors every other injectable-timing
    /// constructor parameter this project's ViewModels already establish (e.g.
    /// <c>SettingsViewModel</c>'s <c>settleTimeout</c>).
    /// </param>
    public HistoryRetentionSweeper(
        IHistoryStore historyStore, IConfigService configService, ILogger logger, TimeSpan? interval = null)
    {
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>
    /// Starts the recurring sweep -- an immediate first tick (so a long-stale history doesn't
    /// wait a full day for its first purge) plus one every <see cref="_interval"/> thereafter.
    /// Safe to call exactly once, from the composition root.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _timer = new Timer(OnTimerFired, null, TimeSpan.Zero, _interval);
        }
    }

    private void OnTimerFired(object? state) => _ = RunSweepAsync();

    /// <summary>
    /// Runs one sweep: reads the current retention window and, if enabled (a positive day
    /// count), calls <see cref="IHistoryStore.PurgeOlderThanAsync"/>. Internal (not private) so
    /// a test can invoke it directly instead of waiting on a real timer -- the same "expose a
    /// method the timer calls, so a test can invoke it directly" pattern
    /// <c>HistoryViewModel.RefreshAsync</c> already established. Never throws --
    /// <see cref="IHistoryStore.PurgeOlderThanAsync"/> already never throws by its own contract,
    /// but this is wrapped defensively anyway, matching every other fire-and-forget call site in
    /// this project.
    /// </summary>
    internal async Task RunSweepAsync()
    {
        try
        {
            var days = _configService.Current.DataPrivacy.HistoryAutoDeleteAfterDays;
            if (days is not > 0)
                return;

            var deleted = await _historyStore.PurgeOlderThanAsync(TimeSpan.FromDays(days.Value)).ConfigureAwait(false);
            if (deleted > 0)
                _logger.LogInformation("History retention sweep purged {Count} entries older than {Days} days.", deleted, days.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "History retention sweep failed unexpectedly.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
