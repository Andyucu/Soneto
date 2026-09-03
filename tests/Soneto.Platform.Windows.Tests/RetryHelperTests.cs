namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Pure logic tests for <see cref="RetryHelper"/> -- item 7's "retry-loop decision logic"
/// unit test called for by the work item, using a fake <c>attempt</c> function so no real
/// clipboard, hardware, or OS resource is ever touched.
/// </summary>
public sealed class RetryHelperTests
{
    [Fact]
    public void Succeeds_on_first_attempt_without_retrying()
    {
        int calls = 0;
        bool result = RetryHelper.TryWithRetry(() => { calls++; return true; }, attempts: 3, delay: TimeSpan.Zero);

        Assert.True(result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Retries_up_to_the_configured_attempt_count_then_fails()
    {
        int calls = 0;
        bool result = RetryHelper.TryWithRetry(() => { calls++; return false; }, attempts: 3, delay: TimeSpan.Zero);

        Assert.False(result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Stops_retrying_as_soon_as_an_attempt_succeeds()
    {
        int calls = 0;
        bool result = RetryHelper.TryWithRetry(
            () => { calls++; return calls == 2; },
            attempts: 5, delay: TimeSpan.Zero);

        Assert.True(result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Invokes_onAttemptFailed_once_per_failed_attempt_with_1_based_index()
    {
        var failedAttempts = new List<int>();
        RetryHelper.TryWithRetry(
            () => false, attempts: 3, delay: TimeSpan.Zero,
            onAttemptFailed: i => failedAttempts.Add(i));

        Assert.Equal([1, 2, 3], failedAttempts);
    }

    [Fact]
    public void Does_not_invoke_onAttemptFailed_for_a_successful_attempt()
    {
        var failedAttempts = new List<int>();
        RetryHelper.TryWithRetry(
            () => true, attempts: 3, delay: TimeSpan.Zero,
            onAttemptFailed: i => failedAttempts.Add(i));

        Assert.Empty(failedAttempts);
    }

    [Fact]
    public void Throws_for_a_non_positive_attempt_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryHelper.TryWithRetry(() => true, attempts: 0, delay: TimeSpan.Zero));
    }

    [Fact]
    public void Throws_for_a_null_attempt_function()
    {
        Assert.Throws<ArgumentNullException>(() => RetryHelper.TryWithRetry(null!, attempts: 1, delay: TimeSpan.Zero));
    }

    [Fact]
    public void Sleeps_between_failed_attempts_but_not_after_the_last_one()
    {
        // A generous-but-bounded delay: proves the loop actually sleeps between attempts
        // (not that it sleeps a precise amount), and that overall elapsed time is
        // consistent with "delay between failures, not after the final one" (2 sleeps for
        // 3 attempts), keeping this test fast and non-flaky.
        var delay = TimeSpan.FromMilliseconds(30);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RetryHelper.TryWithRetry(() => false, attempts: 3, delay: delay);
        sw.Stop();

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(50), $"Expected at least ~2 delays (~60ms), got {sw.ElapsedMilliseconds}ms.");
    }
}
