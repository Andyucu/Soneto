namespace Soneto.Platform.Windows;

/// <summary>
/// Generic "N attempts, fixed delay between" retry loop -- plan §1.8's "not optional" retry
/// requirement for <c>SetClipboardData</c> (step 5): "Clipboard managers (Ditto, Windows
/// clipboard history, Flow Launcher, Copilot) hold the clipboard and will collide with you."
/// Pulled out as its own pure, OS-call-free helper specifically so the retry *decision*
/// logic (how many attempts, when to give up, when to sleep between attempts) can be
/// unit-tested with a fake <paramref name="attempt"/> function, without touching a real
/// clipboard, window, or any other OS/hardware resource.
/// </summary>
public static class RetryHelper
{
    /// <summary>
    /// Calls <paramref name="attempt"/> up to <paramref name="attempts"/> times, sleeping
    /// <paramref name="delay"/> between (but not after) failed attempts, returning true on
    /// the first success. <paramref name="onAttemptFailed"/> is invoked (with the 1-based
    /// attempt number) after each failed attempt, purely for logging -- it does not affect
    /// control flow.
    /// </summary>
    public static bool TryWithRetry(Func<bool> attempt, int attempts, TimeSpan delay, Action<int>? onAttemptFailed = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempts < 1)
            throw new ArgumentOutOfRangeException(nameof(attempts), attempts, "attempts must be at least 1.");

        for (int i = 1; i <= attempts; i++)
        {
            if (attempt())
                return true;

            onAttemptFailed?.Invoke(i);

            if (i < attempts && delay > TimeSpan.Zero)
                Thread.Sleep(delay);
        }
        return false;
    }

    /// <summary>
    /// Post-review fix: the async, <see cref="CancellationToken"/>-aware twin of
    /// <see cref="TryWithRetry"/>, for callers that run on an async, otherwise
    /// non-blocking pipeline (e.g. <c>WindowsTextInjector.InjectAsync</c>). Unlike
    /// <see cref="TryWithRetry"/>'s <c>Thread.Sleep</c> -- which blocks a thread-pool thread
    /// with no way to be interrupted -- this awaits <see cref="Task.Delay(TimeSpan,
    /// CancellationToken)"/> between attempts, so a cancelled <paramref name="ct"/> is
    /// observed promptly instead of being ignored for up to a full <paramref name="delay"/>
    /// window. <see cref="TryWithRetry"/> itself is kept as-is (not deleted, not changed to
    /// take a token) specifically so its own unit tests
    /// (<c>RetryHelperTests</c>) keep exercising the pure, allocation-free, OS-call-free
    /// synchronous retry-decision logic this class's doc comment calls out, without an
    /// async/cancellation dimension muddying what those tests are checking.
    /// </summary>
    public static async Task<bool> TryWithRetryAsync(
        Func<bool> attempt, int attempts, TimeSpan delay, CancellationToken ct, Action<int>? onAttemptFailed = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempts < 1)
            throw new ArgumentOutOfRangeException(nameof(attempts), attempts, "attempts must be at least 1.");

        for (int i = 1; i <= attempts; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt())
                return true;

            onAttemptFailed?.Invoke(i);

            if (i < attempts && delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        return false;
    }
}
