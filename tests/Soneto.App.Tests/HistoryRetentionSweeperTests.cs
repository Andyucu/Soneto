using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Configuration;
using Soneto.Core.History;

namespace Soneto.App.Tests;

/// <summary>
/// Unit tests for <see cref="HistoryRetentionSweeper"/> (Phase 3 item 10, §3.14) against
/// <see cref="FakeHistoryStore"/>/<see cref="FakeConfigService"/> -- same philosophy as every
/// other ViewModel-adjacent test in this project: exercise <c>RunSweepAsync</c> directly rather
/// than waiting on a real 24-hour <see cref="Timer"/>, per the "expose a method the timer calls,
/// so a test can invoke it directly" pattern this project already established
/// (<c>HistoryViewModel.RefreshAsync</c>).
/// </summary>
public sealed class HistoryRetentionSweeperTests
{
    [Fact]
    public async Task RunSweepAsync_WithNoRetentionWindowConfigured_DoesNotPurgeAnything()
    {
        var historyStore = new FakeHistoryStore();
        historyStore.Seed(new HistoryEntry(
            1, DateTimeOffset.UtcNow.AddDays(-1000), "raw", "final", [], TimeSpan.Zero, TimeSpan.Zero, true));
        using var configService = new FakeConfigService(); // DataPrivacy.HistoryAutoDeleteAfterDays defaults to null
        var sweeper = new HistoryRetentionSweeper(historyStore, configService, NullLogger.Instance, TimeSpan.FromDays(1));

        await sweeper.RunSweepAsync();

        var remaining = await historyStore.SearchAsync(null, 100, 0);
        Assert.Single(remaining);
    }

    [Fact]
    public async Task RunSweepAsync_WithRetentionWindowConfigured_PurgesOnlyOlderEntries()
    {
        var historyStore = new FakeHistoryStore();
        historyStore.Seed(
            new HistoryEntry(1, DateTimeOffset.UtcNow.AddDays(-100), "old", "old", [], TimeSpan.Zero, TimeSpan.Zero, true),
            new HistoryEntry(2, DateTimeOffset.UtcNow.AddDays(-1), "recent", "recent", [], TimeSpan.Zero, TimeSpan.Zero, true));
        using var configService = new FakeConfigService(
            new SonetoConfig { DataPrivacy = new DataPrivacyConfig { HistoryAutoDeleteAfterDays = 30 } });
        var sweeper = new HistoryRetentionSweeper(historyStore, configService, NullLogger.Instance, TimeSpan.FromDays(1));

        await sweeper.RunSweepAsync();

        var remaining = await historyStore.SearchAsync(null, 100, 0);
        Assert.Single(remaining);
        Assert.Equal("recent", remaining[0].FinalText);
    }

    [Fact]
    public async Task RunSweepAsync_WithZeroOrNegativeDays_TreatsAsDisabled()
    {
        var historyStore = new FakeHistoryStore();
        historyStore.Seed(new HistoryEntry(
            1, DateTimeOffset.UtcNow.AddDays(-1000), "raw", "final", [], TimeSpan.Zero, TimeSpan.Zero, true));
        using var configService = new FakeConfigService(
            new SonetoConfig { DataPrivacy = new DataPrivacyConfig { HistoryAutoDeleteAfterDays = 0 } });
        var sweeper = new HistoryRetentionSweeper(historyStore, configService, NullLogger.Instance, TimeSpan.FromDays(1));

        await sweeper.RunSweepAsync();

        var remaining = await historyStore.SearchAsync(null, 100, 0);
        Assert.Single(remaining);
    }

    [Fact]
    public async Task RunSweepAsync_ReadsTheRetentionWindowFreshEachCall_NotCapturedAtConstruction()
    {
        // Proves the "no restart required" design decision: a Settings-page edit to
        // DataPrivacyConfig.HistoryAutoDeleteAfterDays between two sweeps changes the SECOND
        // sweep's behavior, without reconstructing HistoryRetentionSweeper.
        var historyStore = new FakeHistoryStore();
        historyStore.Seed(new HistoryEntry(
            1, DateTimeOffset.UtcNow.AddDays(-100), "raw", "final", [], TimeSpan.Zero, TimeSpan.Zero, true));
        using var configService = new FakeConfigService(); // starts disabled (null)
        var sweeper = new HistoryRetentionSweeper(historyStore, configService, NullLogger.Instance, TimeSpan.FromDays(1));

        await sweeper.RunSweepAsync();
        var afterFirstSweep = await historyStore.SearchAsync(null, 100, 0);
        Assert.Single(afterFirstSweep); // still disabled -- nothing purged

        configService.SimulateExternalChange(
            new SonetoConfig { DataPrivacy = new DataPrivacyConfig { HistoryAutoDeleteAfterDays = 30 } });
        await sweeper.RunSweepAsync();

        var afterSecondSweep = await historyStore.SearchAsync(null, 100, 0);
        Assert.Empty(afterSecondSweep);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndSafeBeforeStart()
    {
        var historyStore = new FakeHistoryStore();
        using var configService = new FakeConfigService();
        var sweeper = new HistoryRetentionSweeper(historyStore, configService, NullLogger.Instance, TimeSpan.FromDays(1));

        sweeper.Dispose();
        sweeper.Dispose(); // must not throw
    }

    [Fact]
    public void StartThenDispose_DoesNotThrow()
    {
        var historyStore = new FakeHistoryStore();
        using var configService = new FakeConfigService();
        var sweeper = new HistoryRetentionSweeper(historyStore, configService, NullLogger.Instance, TimeSpan.FromMinutes(30));

        sweeper.Start();
        sweeper.Dispose();
    }
}
