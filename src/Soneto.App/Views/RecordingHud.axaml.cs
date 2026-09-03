using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Soneto.Core;
using Soneto.Core.Abstractions;

namespace Soneto.App.Views;

/// <summary>
/// Phase 3 item 5 (§3.9): the Recording HUD — a small, always-on-top, borderless,
/// non-activating window that appears the moment a real <see cref="SessionController"/>
/// transitions to <see cref="SessionState.Recording"/> and disappears on the transition
/// back to <see cref="SessionState.Idle"/>/<see cref="SessionState.Faulted"/>. Shows a live
/// level meter (from <see cref="IAudioCapture.LevelChanged"/>) and an elapsed-time counter.
/// No language chip — §3.1's own explicit scope decision (no classifier exists yet).
///
/// <para>
/// <b>Positioning — fixed bottom-right corner of the primary screen, documented choice:</b>
/// simpler and fully deterministic than tracking the cursor, with no dependency on a
/// cursor-position API; a fixed corner is also less likely to overlap the exact spot the
/// user is typing/looking at than a cursor-anchored HUD would be.
/// </para>
///
/// <para>
/// <b>Threading — both event sources fire on non-UI threads:</b>
/// <see cref="SessionController.StateChanged"/> fires on <c>SessionController</c>'s own
/// worker thread; <see cref="IAudioCapture.LevelChanged"/> fires on the audio capture
/// callback thread. Every handler below does the absolute minimum off-thread work (pure
/// data extraction only) and marshals every actual UI mutation through
/// <see cref="Dispatcher.UIThread"/>.Post — this project's own history flags "an event
/// handler mutating a UI control from the wrong thread" as a recurring first-pass mistake
/// (see <c>Docs/PROJECT-MEMORY.md</c>), so this is treated as a hard requirement here, not
/// an afterthought.
/// </para>
///
/// <para>
/// <b>No focus stealing — a hard requirement (§3.9):</b> <see cref="Window.ShowActivated"/>
/// is set <c>False</c> in the XAML (confirmed via reflection to be a real
/// <see cref="bool"/> property on <c>Avalonia.Controls.Window</c> in the actually-pinned
/// Avalonia 12.0.4 package, not assumed from a different release), so calling
/// <see cref="Window.Show()"/> never activates or focuses this window — whatever app the
/// user is dictating into keeps input focus throughout.
/// </para>
/// </summary>
public partial class RecordingHud : Window
{
    private readonly DispatcherTimer _elapsedTimer;
    private DateTimeOffset _recordingStartedAtUtc;
    private bool _attached;

    public RecordingHud()
    {
        InitializeComponent();

        // Elapsed-time counter: a plain once-per-second DispatcherTimer, deliberately NOT
        // tied to the ~20Hz level-meter updates (§3.9's own distinction between the two).
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => UpdateElapsedLabel();
    }

    /// <summary>
    /// Wires this HUD to a real, already-started <see cref="SessionController"/>/
    /// <see cref="IAudioCapture"/> pair — called once by <c>PipelineHost</c> after real
    /// pipeline startup succeeds (sub-task A). <paramref name="audioCapture"/> is nullable
    /// only so this method's own signature stays honest about
    /// <c>BuildAndStartSessionControllerAsync</c>'s contract (it is never actually null when
    /// <paramref name="controller"/> is non-null in practice, but nothing here assumes that).
    /// If <paramref name="audioCapture"/> is null, the HUD still shows/hides correctly on
    /// state transitions — it simply never receives level updates (the meter stays empty).
    ///
    /// <para>
    /// <b>One-shot by contract, and now defensively enforced</b> (post-review should-fix):
    /// today's only call site (<c>PipelineHost.Started</c>) is a one-shot event, so this can
    /// never actually be called twice in the current call graph — but a future change (e.g. a
    /// "restart pipeline" feature) that made <c>Started</c> fire more than once would have
    /// silently double-subscribed both events with no crash, just duplicate/compounding
    /// show-hide-level updates. Cheap insurance: a second call is a clear no-op instead.
    /// </para>
    /// </summary>
    public void AttachSession(SessionController controller, IAudioCapture? audioCapture)
    {
        if (_attached)
            return;
        _attached = true;

        controller.StateChanged += OnStateChanged;
        if (audioCapture != null)
            audioCapture.LevelChanged += OnLevelChanged;
    }

    private void OnStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        // Fires on SessionController's own worker thread — no UI touches before the Post.
        if (e.To == SessionState.Recording)
        {
            Dispatcher.UIThread.Post(ShowAtBottomRightOfPrimaryScreen);
        }
        else if (e.To is SessionState.Idle or SessionState.Faulted)
        {
            Dispatcher.UIThread.Post(HideHud);
        }
    }

    private void OnLevelChanged(object? sender, AudioLevelEventArgs e)
    {
        // Fires on the audio capture callback thread — the pure conversion happens here
        // (cheap, no UI touch), only the resulting ratio crosses to the UI thread.
        double ratio = DbfsToFillRatio(e.Dbfs);
        Dispatcher.UIThread.Post(() => UpdateLevelFill(ratio));
    }

    // ---- UI-thread-only methods below this point --------------------------------------

    private void ShowAtBottomRightOfPrimaryScreen()
    {
        _recordingStartedAtUtc = DateTimeOffset.UtcNow;
        UpdateElapsedLabel();
        UpdateLevelFill(0.0);
        PositionAtBottomRightOfPrimaryScreen();

        // Show() (not ShowDialog/Activate) — with ShowActivated="False" already set on the
        // Window in XAML, this never steals focus from whatever app is currently focused.
        Show();
        _elapsedTimer.Start();
    }

    private void HideHud()
    {
        _elapsedTimer.Stop();
        Hide();
    }

    private void UpdateElapsedLabel()
    {
        var elapsed = DateTimeOffset.UtcNow - _recordingStartedAtUtc;
        int totalSeconds = Math.Max(0, (int)elapsed.TotalSeconds);
        ElapsedLabel.Text = $"{totalSeconds / 60}:{totalSeconds % 60:D2}";
    }

    private void UpdateLevelFill(double ratio)
    {
        double trackWidth = this.FindResource("SizeHudMeterWidth") is double w ? w : 0.0;
        LevelFill.Width = trackWidth * ratio;
    }

    private void PositionAtBottomRightOfPrimaryScreen()
    {
        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null)
            return; // No screen info available — leave whatever position Avalonia defaulted to.

        double margin = this.FindResource("SpaceXl") is double m ? m : 24.0;
        var workingArea = screen.WorkingArea;
        int x = workingArea.X + workingArea.Width - (int)Width - (int)margin;
        int y = workingArea.Y + workingArea.Height - (int)Height - (int)margin;
        Position = new PixelPoint(x, y);
    }

    /// <summary>
    /// Maps a dBFS level reading onto a 0-1 fill ratio for the level meter, clamping
    /// sensibly at both ends. dBFS is 0 at full-scale (loudest possible sample) and
    /// increasingly negative as the signal gets quieter; typical speech sits roughly in
    /// the -60..0 range (per <see cref="AudioLevelEventArgs"/>'s own doc comment), so that
    /// range is treated as the meter's full scale. Silence/no-signal readings
    /// (<see cref="double.NegativeInfinity"/> or <see cref="double.NaN"/>, which a true
    /// zero-amplitude buffer can produce) map to an empty (0) meter rather than throwing or
    /// producing a garbage width.
    ///
    /// <para>
    /// Kept as a small, well-commented <c>internal static</c> method per this item's own
    /// documented, deliberate testing choice: a real unit test project for
    /// <c>Soneto.App</c> is disproportionate to stand up for this one function alone (item
    /// 6/History's ViewModel is where that real test infrastructure belongs, per §3.15) —
    /// this function is "correct by inspection," which is an accepted bar for this one
    /// small piece this item, not an oversight.
    /// </para>
    /// </summary>
    internal static double DbfsToFillRatio(double dbfs)
    {
        const double minDbfs = -60.0;
        const double maxDbfs = 0.0;

        if (double.IsNaN(dbfs) || double.IsNegativeInfinity(dbfs))
            return 0.0;

        if (dbfs <= minDbfs)
            return 0.0;
        if (dbfs >= maxDbfs)
            return 1.0;

        return (dbfs - minDbfs) / (maxDbfs - minDbfs);
    }
}
