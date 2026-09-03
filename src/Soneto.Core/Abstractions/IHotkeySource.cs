namespace Soneto.Core.Abstractions;

/// <summary>
/// Global hold-to-talk key capture. Implementations are platform-specific (Windows: a
/// low-level keyboard hook; Linux: evdev) but must honor the hook-callback-thread rule
/// from plan §1.4: the callback thread never does work — it sets a flag, posts a
/// command, and returns.
///
/// <para>
/// <b>Concurrency contract:</b> <see cref="StartAsync"/>, <see cref="RestartAsync"/>, and
/// <see cref="IAsyncDisposable.DisposeAsync"/> are single-caller/strictly-sequential, the
/// same contract <c>PortAudioCapture.StartAsync</c>/<c>StopAsync</c> document (item 4b) —
/// implementations are not required to synchronize overlapping calls to these methods
/// against each other.
/// </para>
/// </summary>
public interface IHotkeySource : IAsyncDisposable
{
    /// <summary>
    /// Raised on an internal consumer thread (never the platform hook/callback thread —
    /// that thread is always kept free per the class doc comment above). Handlers must not
    /// block synchronously for any meaningful duration: this consumer thread is also what
    /// drains the internal event channel, so a slow handler can let that channel's queue
    /// (unbounded on at least the Windows implementation) grow indefinitely.
    /// </summary>
    event EventHandler<HotkeyEventArgs>? Pressed;

    /// <summary>Same non-blocking-handler contract as <see cref="Pressed"/>.</summary>
    event EventHandler<HotkeyEventArgs>? Released;

    /// Raised when the underlying hook/device dies and needs recovery (plan §1.4's
    /// "hook faulted" transition, handled by SessionController's watchdog).
    event EventHandler<HotkeyFaultEventArgs>? Faulted;

    Task StartAsync(HotkeyBinding binding, CancellationToken ct);

    Task RestartAsync(CancellationToken ct);
}

public sealed record HotkeyBinding(string Key, bool Suppress);

/// <summary>A single press or release event on the trigger key.</summary>
public sealed record HotkeyEventArgs(DateTimeOffset Timestamp);

/// <summary>Raised when the hotkey source detects it can no longer reliably capture events.</summary>
public sealed record HotkeyFaultEventArgs(string Reason, Exception? Exception);
