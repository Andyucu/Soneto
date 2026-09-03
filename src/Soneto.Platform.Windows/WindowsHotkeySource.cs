using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SharpHook;
using SharpHook.Data;
using SharpHook.Simulation;
using Soneto.Core.Abstractions;

namespace Soneto.Platform.Windows;

/// <summary>
/// Windows implementation of <see cref="IHotkeySource"/> using SharpHook's
/// <see cref="SimpleGlobalHook"/>, exactly as validated by <c>spikes/s3-hotkey-win/</c>
/// (see that spike's README for the numbers backing every design choice below).
///
/// <para>
/// <b>Callback-thread discipline (the single most important thing about this class).</b>
/// Plan §1.4's threading model is non-negotiable: "Hook callback thread: sets a flag,
/// posts to a <c>Channel&lt;SessionCommand&gt;</c>, returns. Never does work." This
/// project has hit exactly this class of bug four times already (S3's own hook callback,
/// S4's clipboard atomicity, item 4b's audio callback, item 4c's WarmIdle timer — see
/// <c>Docs/PROJECT-MEMORY.md</c>) — so here is exactly what happens on each thread:
/// </para>
/// <list type="bullet">
/// <item><description><b>SharpHook's native hook callback thread</b> (<see cref="OnKeyPressed"/>/
/// <see cref="OnKeyReleased"/>): reads <see cref="HookEventArgs.EventTime"/> (a value already
/// computed by the OS/uiohook, not something we compute), does one <see cref="KeyCode"/>
/// comparison, one cheap field read (<c>_suppress</c>), sets <c>SuppressEvent</c> if
/// applicable, writes a small readonly struct into an unbounded <see cref="Channel{T}"/>
/// via <c>TryWrite</c> (never blocks — this is exactly the "posts to a
/// <c>Channel&lt;T&gt;</c>, returns" pattern plan §1.4 specifies), and returns. This
/// mirrors <c>SelfTest.cs</c>'s callback pattern from the spike ("timestamp read + field
/// write + semaphore release, zero I/O") — <c>Console.WriteLine</c>-in-callback, as used by
/// the spike's <c>ListenMode</c>/<c>BlockDemo</c> for human observability, is explicitly
/// NOT carried into this class.</description></item>
/// <item><description><b>A dedicated consumer <see cref="Task"/>/thread</b> (<see cref="ConsumeAsync"/>):
/// drains the channel and is the ONLY place the public <see cref="Pressed"/>/
/// <see cref="Released"/> .NET events are raised. Arbitrary, possibly slow subscriber code
/// therefore never runs on the real-time hook thread.</description></item>
/// <item><description><b>A <see cref="System.Threading.Timer"/>-driven heartbeat</b>
/// (<see cref="OnHeartbeatTick"/>), also never the hook thread: it only ever reads a
/// cheaply-updated <c>long</c> timestamp field via <see cref="Volatile"/>, and — only once
/// genuinely idle for 60s+ — synthesizes a harmless probe key event and waits briefly to see
/// whether the hook itself observes it, per plan §1.12's "heartbeat: no events for 60s + a
/// test-event probe" row.</description></item>
/// </list>
///
/// <para>
/// <b>Suppression.</b> Per plan §1.8's "suppress both or neither — never one" rule, both
/// DOWN and UP of the trigger key are suppressed together whenever
/// <see cref="HotkeyBinding.Suppress"/> is true; SharpHook's per-event
/// <c>SuppressEvent</c> flag is set independently on both the <see cref="KeyPressed"/> and
/// <see cref="KeyReleased"/> handlers, driven by the same immutable <c>_suppress</c> field.
/// </para>
///
/// <para>
/// <b>Self-injected keyboard events are never treated as a trigger press (post-review
/// fix).</b> <c>WH_KEYBOARD_LL</c> (the low-level hook uiohook/SharpHook installs on
/// Windows) observes ALL keyboard events system-wide, including synthetic ones this same
/// process sends via <c>SendInput</c> -- both <see cref="WindowsTextInjector"/>'s paste-chord
/// modifiers (<c>VK_LCONTROL</c>/<c>VK_LSHIFT</c>) and this class's own heartbeat probe.
/// Windows tags such events with <c>LLKHF_INJECTED</c>; SharpHook surfaces that as
/// <see cref="HookEventArgs.IsEventSimulated"/> (backed by
/// <see cref="SharpHook.Data.EventMask.SimulatedEvent"/> on <see cref="HookEventArgs.RawEvent"/>'s
/// mask -- confirmed present in the installed SharpHook 8.0.0 package's XML docs). Both
/// <see cref="OnKeyPressed"/> and <see cref="OnKeyReleased"/> ignore a trigger-key-coded event
/// entirely (no channel write, no suppression, no heartbeat-timestamp update) whenever
/// <c>IsEventSimulated</c> is true -- otherwise, if the user configures <c>LeftControl</c> or
/// <c>LeftShift</c> as the trigger (both are explicitly supported aliases in
/// <see cref="HotkeyKeyMapper"/>, same table as the default <c>RightControl</c>), every single
/// paste's synthetic Ctrl/Shift-down would deterministically match <c>_triggerKeyCode</c>,
/// get suppressed before reaching the target app (breaking the paste), and post a phantom
/// Down/Up pair into the channel with no real user action behind it. This check is
/// deliberately scoped to the trigger-key branch only: the heartbeat's own probe-key branch
/// (<see cref="ProbeKeyCode"/>) intentionally does NOT check <c>IsEventSimulated</c> --
/// the whole point of that branch is to observe the heartbeat's own synthetic probe, so
/// gating it the same way would break heartbeat liveness detection entirely.
/// </para>
///
/// <para>
/// <b>Known Windows hook limitation, carried forward from S3, not fixed here.</b> S3 found
/// that a briefly-blocked hook callback does not fully unhook — it silently drops the one
/// key-up event that arrived mid-block, while staying alive afterward. This class's own
/// callback discipline (above) makes that scenario extremely unlikely to originate from
/// this class's own code, but it can still happen due to other, unrelated hooks/AV/EDR
/// software on the same machine blocking the shared low-level hook chain. Per plan §1.4,
/// the actual defense for "key stuck down" is <c>SessionController</c>'s
/// <c>maxDurationMs</c> force-finalize timer on the DOWN event (item 9), not anything in
/// this class — this class only detects and reports total hook death via the heartbeat,
/// which is a different (narrower) failure mode than the orphan-key-up one.
/// </para>
///
/// <para>
/// <b>Concurrency contract:</b> <see cref="StartAsync"/>, <see cref="RestartAsync"/>, and
/// <see cref="DisposeAsync"/> are single-caller/strictly-sequential — nothing in this class
/// synchronizes overlapping calls to these three methods against each other (fields like
/// <c>_hook</c>, <c>_channel</c>, <c>_consumerTask</c>, <c>_heartbeatTimer</c> are read/written
/// without a lock). This mirrors <c>PortAudioCapture.StartAsync</c>/<c>StopAsync</c>'s
/// documented contract (item 4b) — a caller (eventually <c>SessionController</c>, item 9)
/// must not call two of these concurrently on the same instance.
/// </para>
///
/// <para>
/// <b>Standing honest gap: the heartbeat probe's real-world side effect is documented, not
/// verified.</b> Once genuinely idle for <see cref="HeartbeatIdleThreshold"/>, the heartbeat
/// (<see cref="OnHeartbeatTick"/>) injects a REAL, system-wide synthetic <see cref="ProbeKeyCode"/>
/// keystroke via SharpHook's <see cref="EventSimulator"/> (backed by a real Windows
/// <c>SendInput</c> call) roughly every <see cref="HeartbeatCheckInterval"/> thereafter —
/// which, for a dictation app used only occasionally alongside other work, is the common
/// case, not a rare edge case. This lands on whatever window currently has keyboard focus,
/// exactly like physical input. F24 is a reasonable inert choice (present in SharpHook's
/// <see cref="KeyCode"/> enum, not on standard keyboards, unlikely to be anyone's configured
/// trigger), but "vanishingly unlikely to be bound to anything" is an assumption about the
/// target machine's actual configuration (macro tools, remapped extended function keys, RDP
/// sessions forwarding extended keys to a remote host) that has not been verified on any real
/// machine — this has not been fixed, only honestly flagged, matching this project's
/// established "standing honest gap" convention from items 4b/4c. A follow-up worth
/// considering (not built here, to avoid complicating the generation-token fix below): simple
/// exponential backoff on the heartbeat interval after successive successful probes, so a
/// long-idle-but-healthy daemon doesn't keep injecting synthetic input indefinitely at a fixed
/// cadence.
/// </para>
/// </summary>
public sealed class WindowsHotkeySource : IHotkeySource
{
    private static readonly TimeSpan HeartbeatCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HeartbeatIdleThreshold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProbeWaitWindow = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Harmless probe key for the heartbeat's synthetic test event. F24 is present in
    /// SharpHook's <see cref="KeyCode"/> enum (extended-keyboard function key) and is
    /// vanishingly unlikely to be a real user's configured trigger or to be pressed by
    /// accident, so a probe event can never be confused with real user input. Never
    /// suppressed (see <see cref="OnKeyPressed"/>) since it isn't part of the real
    /// hold-to-talk input path.
    /// </summary>
    internal static readonly KeyCode ProbeKeyCode = KeyCode.VcF24;

    private readonly ILogger<WindowsHotkeySource> _logger;

    private HotkeyBinding? _binding;
    private KeyCode _triggerKeyCode;
    private bool _suppress;

    private SimpleGlobalHook? _hook;
    private Task? _hookRunTask;

    private Channel<RawHotkeyEvent>? _channel;
    private CancellationTokenSource? _consumerCts;
    private Task? _consumerTask;

    private Timer? _heartbeatTimer;
    private long _heartbeatGeneration;
    private long _lastEventTicksUtc;
    private int _probeObserved;
    private int _heartbeatRunning;
    private int _faultRaised;

    /// <inheritdoc cref="IHotkeySource.Pressed"/>
    public event EventHandler<HotkeyEventArgs>? Pressed;

    /// <inheritdoc cref="IHotkeySource.Released"/>
    public event EventHandler<HotkeyEventArgs>? Released;

    public event EventHandler<HotkeyFaultEventArgs>? Faulted;

    public WindowsHotkeySource(ILogger<WindowsHotkeySource> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(HotkeyBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);

        _binding = binding;
        _triggerKeyCode = HotkeyKeyMapper.ToKeyCode(binding.Key);
        _suppress = binding.Suppress;

        _channel = Channel.CreateUnbounded<RawHotkeyEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        _hook = new SimpleGlobalHook(globalHookProvider: null);
        _hook.KeyPressed += OnKeyPressed;
        _hook.KeyReleased += OnKeyReleased;

        // Per spike S3's confirmed RunAsync gotcha: the returned Task represents the
        // hook's entire run lifetime and only completes on Stop()/Dispose(), regardless of
        // useBackgroundThread. Fire it without awaiting; only await it (wrapped) at
        // shutdown after calling Stop().
        _hookRunTask = _hook.RunAsync(GlobalHookType.Keyboard, useBackgroundThread: true);
        await Task.Delay(200, ct).ConfigureAwait(false);

        Volatile.Write(ref _lastEventTicksUtc, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _faultRaised, 0);

        _consumerCts = new CancellationTokenSource();
        _consumerTask = Task.Run(() => ConsumeAsync(_channel, _consumerCts.Token), CancellationToken.None);

        // Fix 1 (blocking): bump the generation token before creating the new timer and pass
        // it as the Timer's `state`, so a stale callback from a previous (stopped/restarted)
        // generation can identify itself as stale even though it's racing entirely outside
        // this method's call stack. See the class doc comment's "generation token" note and
        // OnHeartbeatTick's own generation check.
        var heartbeatGeneration = Interlocked.Increment(ref _heartbeatGeneration);
        _heartbeatTimer = new Timer(OnHeartbeatTick, heartbeatGeneration, HeartbeatCheckInterval, HeartbeatCheckInterval);

        _logger.LogInformation(
            "WindowsHotkeySource started: trigger={TriggerKey} ({KeyCode}) suppress={Suppress}",
            binding.Key, _triggerKeyCode, binding.Suppress);
    }

    /// <summary>
    /// Single, clean restart attempt: stop and dispose the current hook/consumer/timer,
    /// then start fresh with the same binding. Per this work item's explicit scope, the
    /// 5x-with-exponential-backoff retry loop from plan §1.4's "hook faulted" row is
    /// <c>SessionController</c>'s job (item 9), not built here — a caller that wants
    /// retries must call this repeatedly itself.
    /// </summary>
    public async Task RestartAsync(CancellationToken ct)
    {
        if (_binding is null)
            throw new InvalidOperationException("StartAsync must be called before RestartAsync.");

        _logger.LogWarning("WindowsHotkeySource restarting (single attempt; retry/backoff is SessionController's job)");

        var binding = _binding;
        await StopInternalAsync().ConfigureAwait(false);
        await StartAsync(binding, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopInternalAsync().ConfigureAwait(false);
    }

    private async Task StopInternalAsync()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        // Fix 1 (blocking): bump unconditionally, even if there was no timer to dispose --
        // Timer.Dispose() (the parameterless overload) does NOT wait for an in-flight callback
        // to finish, so a heartbeat tick can already be mid-flight (sleeping through its probe
        // wait window) on a ThreadPool thread independent of this call. Bumping here
        // invalidates that in-flight callback's captured generation regardless of whether a
        // subsequent StartAsync creates a new timer afterward.
        Interlocked.Increment(ref _heartbeatGeneration);

        if (_hook is { } hook)
        {
            hook.KeyPressed -= OnKeyPressed;
            hook.KeyReleased -= OnKeyReleased;
            try
            {
                hook.Stop();
            }
            catch (Exception ex)
            {
                // Stop() can throw (observed: SharpHook.HookException "Failed stopping the
                // global hook") if the underlying native hook is already gone or in a bad
                // state -- disposal must still complete cleanly regardless, per this
                // project's "every disposal path must be clean" convention (see
                // Docs/PROJECT-MEMORY.md's recurring-concurrency-pattern note).
                _logger.LogWarning(ex, "Hook.Stop() threw during shutdown; continuing disposal anyway");
            }
            if (_hookRunTask is { } runTask)
            {
                try { await runTask.ConfigureAwait(false); }
                catch { /* expected: Stop() causes the run task to complete/cancel */ }
            }
            try
            {
                hook.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hook.Dispose() threw during shutdown; continuing disposal anyway");
            }
            _hook = null;
        }
        _hookRunTask = null;

        _channel?.Writer.TryComplete();
        if (_consumerTask is { } consumerTask)
        {
            try { await consumerTask.ConfigureAwait(false); }
            catch { /* consumer loop's own OperationCanceledException, expected */ }
        }
        _consumerTask = null;
        _consumerCts?.Cancel();
        _consumerCts?.Dispose();
        _consumerCts = null;
        _channel = null;
    }

    // ============================================================================
    // Hook callback thread. NOTHING below this line in these two methods may block,
    // allocate unboundedly, log, or call into subscriber code. See the class doc comment.
    // ============================================================================

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        var code = e.Data.KeyCode;
        // Post-review fix: a self-injected event (LLKHF_INJECTED, surfaced by SharpHook as
        // IsEventSimulated) coded as the trigger key is never a real user press -- ignore it
        // entirely rather than matching, suppressing, or posting it. See the class doc
        // comment's "self-injected keyboard events" note for why this specifically matters
        // for LeftControl/LeftShift trigger bindings colliding with WindowsTextInjector's own
        // synthetic paste-chord modifiers.
        if (code == _triggerKeyCode && e.IsEventSimulated)
            return;

        if (code == _triggerKeyCode)
        {
            Volatile.Write(ref _lastEventTicksUtc, e.EventTime.UtcDateTime.Ticks);
            if (_suppress) e.SuppressEvent = true;
            _channel?.Writer.TryWrite(new RawHotkeyEvent(HotkeyEdge.Down, e.EventTime));
        }
        else if (code == ProbeKeyCode)
        {
            Volatile.Write(ref _lastEventTicksUtc, e.EventTime.UtcDateTime.Ticks);
            Interlocked.Exchange(ref _probeObserved, 1);
            // Never suppressed: the probe key isn't part of the real hold-to-talk path.
        }
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        var code = e.Data.KeyCode;
        // See OnKeyPressed's matching comment: ignore self-injected events coded as the
        // trigger key entirely, symmetrically on the UP side too (never suppress/post only
        // one half of a phantom pair).
        if (code == _triggerKeyCode && e.IsEventSimulated)
            return;

        if (code == _triggerKeyCode)
        {
            Volatile.Write(ref _lastEventTicksUtc, e.EventTime.UtcDateTime.Ticks);
            if (_suppress) e.SuppressEvent = true;
            _channel?.Writer.TryWrite(new RawHotkeyEvent(HotkeyEdge.Up, e.EventTime));
        }
        else if (code == ProbeKeyCode)
        {
            Volatile.Write(ref _lastEventTicksUtc, e.EventTime.UtcDateTime.Ticks);
            Interlocked.Exchange(ref _probeObserved, 1);
        }
    }

    // ============================================================================
    // Consumer thread: the ONLY place Pressed/Released are raised. Runs off the hook
    // thread specifically so a slow subscriber can never stall hook dispatch.
    // ============================================================================

    private async Task ConsumeAsync(Channel<RawHotkeyEvent> channel, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var args = new HotkeyEventArgs(evt.Timestamp);
                    if (evt.Edge == HotkeyEdge.Down)
                        Pressed?.Invoke(this, args);
                    else
                        Released?.Invoke(this, args);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled exception from a Pressed/Released subscriber");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown (StopInternalAsync cancels _consumerCts).
        }
    }

    // ============================================================================
    // Heartbeat: a System.Threading.Timer callback (ThreadPool thread), never the hook
    // thread. Per plan §1.12: "heartbeat: no events for 60s + a test-event probe".
    //
    // Nit (deferred, not applied): this method blocks a ThreadPool thread for up to
    // ~770ms per idle probe via the two Thread.Sleep calls below. Infrequent enough not to
    // be a real starvation risk, but an async Task.Delay-based wait would be cleaner. Left
    // as a sync method (Thread.Sleep, not awaited) specifically to keep the two generation
    // re-checks below (fix 1) simple and easy to reason about as one straight-line
    // synchronous method with no interleaving `await` continuations to account for.
    // ============================================================================

    private void OnHeartbeatTick(object? state)
    {
        // Fix 1 (blocking): the generation this tick was scheduled with. Compared against
        // the live _heartbeatGeneration both before doing any probing work AND again after
        // the ~770ms probe-wait window below, since a restart (StopInternalAsync bumping the
        // generation, then StartAsync creating a brand-new hook/timer/generation) can happen
        // entirely on another thread while this tick is sleeping through that window --
        // exactly the race the blocking review finding described. A stale generation at
        // either checkpoint is a no-op: it must never touch _probeObserved/_faultRaised or
        // raise Faulted against whatever (possibly brand new, healthy) instance state is
        // current by the time this stale callback finally gets to run.
        var generation = (long)state!;

        if (Interlocked.CompareExchange(ref _heartbeatRunning, 1, 0) != 0)
            return; // a previous tick is still probing; skip this one rather than overlap.

        try
        {
            if (Volatile.Read(ref _heartbeatGeneration) != generation)
                return; // stale: superseded by a restart/stop before this tick could even start.

            var lastTicks = Volatile.Read(ref _lastEventTicksUtc);
            var idleFor = DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc);
            if (idleFor < HeartbeatIdleThreshold)
                return;

            Interlocked.Exchange(ref _probeObserved, 0);

            using (var simulator = EventSimulator.Create("Soneto.Platform.Windows"))
            {
                simulator.SimulateKeyPress(ProbeKeyCode);
                Thread.Sleep(20);
                simulator.SimulateKeyRelease(ProbeKeyCode);
            }

            Thread.Sleep(ProbeWaitWindow);

            if (Volatile.Read(ref _heartbeatGeneration) != generation)
                return; // stale: superseded by a restart/stop while sleeping through the probe wait.

            if (Volatile.Read(ref _probeObserved) == 1)
            {
                // Hook is alive: the probe was observed. Reset the idle baseline so we
                // don't re-probe again until another full idle threshold has elapsed.
                Volatile.Write(ref _lastEventTicksUtc, DateTime.UtcNow.Ticks);
                _logger.LogDebug("Hotkey heartbeat probe succeeded after {IdleSeconds:F0}s idle; hook is alive.", idleFor.TotalSeconds);
                return;
            }

            if (Interlocked.CompareExchange(ref _faultRaised, 1, 0) == 0)
            {
                var reason =
                    $"No hook events observed for {idleFor.TotalSeconds:F0}s and a synthetic probe " +
                    $"key-press/release was not observed by the hook within {ProbeWaitWindow.TotalMilliseconds:F0}ms.";
                _logger.LogError("Hotkey hook heartbeat failed: {Reason}", reason);
                Faulted?.Invoke(this, new HotkeyFaultEventArgs(reason, null));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hotkey heartbeat check threw unexpectedly");
        }
        finally
        {
            Interlocked.Exchange(ref _heartbeatRunning, 0);
        }
    }

    private enum HotkeyEdge { Down, Up }

    private readonly record struct RawHotkeyEvent(HotkeyEdge Edge, DateTimeOffset Timestamp);
}
