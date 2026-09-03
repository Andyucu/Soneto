using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Soneto.Core.Abstractions;

namespace Soneto.Platform.Linux;

/// <summary>
/// Linux implementation of <see cref="IHotkeySource"/>: multi-keyboard evdev capture per
/// plan §1.9, multiplexed with <c>epoll</c> on a single reader thread, with an
/// <c>inotify</c> watch on <c>/dev/input</c> for hotplug re-enumeration.
///
/// <para>
/// <b>Same callback-thread discipline as <c>WindowsHotkeySource</c>, adapted to evdev.</b>
/// The raw reader thread (<see cref="ReaderLoop"/>) does only the epoll/inotify/read
/// syscalls plus a cheap key-code comparison, then posts a small struct into an unbounded
/// <see cref="Channel{T}"/> and returns to the epoll wait -- exactly the
/// "hook callback thread never does work, post and return" rule from plan §1.4, generalized
/// from "the hook callback thread" to "the raw syscall-reading thread" since evdev has no
/// hook callback at all. <see cref="Pressed"/>/<see cref="Released"/> are raised only from
/// <see cref="ConsumeAsync"/>, a separate consumer task, mirroring
/// <c>WindowsHotkeySource</c>'s <c>ConsumeAsync</c> exactly.
/// </para>
///
/// <para>
/// <b>Suppression is a documented no-op in this pass -- do not treat this as resolved.</b>
/// Per plan S5's explicit warning, evdev reading does NOT suppress at the compositor level:
/// the trigger key reaches the focused app regardless of anything this class does, and
/// <c>EVIOCGRAB</c> (the only real suppression mechanism) grabs the WHOLE device, not just
/// the trigger key, which the plan calls invasive and says must be tested against real
/// hardware before being built, since a botched grab can break the keyboard. This class
/// therefore accepts <see cref="HotkeyBinding.Suppress"/> but never grabs anything, and logs
/// a one-time startup warning that the trigger key WILL leak through to whatever app has
/// focus. This is an explicitly OPEN question pending spike S5
/// (<c>Docs/soneto-implementation-plan-phase0-1.md</c>, "S5 -- Fedora KDE Wayland input"),
/// not a permanent design decision -- do not read the absence of <c>EVIOCGRAB</c> here as
/// "leaking is fine," only as "nobody has run S5 yet to find out."
/// </para>
///
/// <para>
/// <b>What is and is not verified.</b> The channel/consumer-thread plumbing, the key-code
/// comparison logic, and the alias table (<see cref="EvdevKeyMapper"/>) are ordinary,
/// unit-testable C#. The actual <c>open</c>/<c>ioctl</c>/<c>epoll_wait</c>/<c>read</c>/
/// <c>inotify</c> syscalls this class's <see cref="StartAsync"/>/<see cref="ReaderLoop"/>
/// make have never been executed against a real Linux kernel from any agent session working
/// on this item -- see <see cref="EvdevInterop"/>'s doc comment for the specifics of what's
/// unverified there (struct packing, in particular). Multi-keyboard hotplug (plugging in a
/// second keyboard mid-session) is this item's own explicitly stated "done when" criterion
/// and is NOT satisfied by anything in this codebase alone -- it requires a human with real
/// hardware.
/// </para>
///
/// <para>
/// <b>Concurrency contract:</b> same as <c>WindowsHotkeySource</c> -- <see cref="StartAsync"/>,
/// <see cref="RestartAsync"/>, and <see cref="DisposeAsync"/> are single-caller/strictly
/// sequential; no internal locking coordinates overlapping calls to these three methods.
/// </para>
/// </summary>
public sealed class LinuxHotkeySource : IHotkeySource
{
    private readonly ILogger _logger;

    private HotkeyBinding? _binding;
    private ushort _triggerCode;
    private bool _suppress;
    private bool _suppressionWarningLogged;

    private List<KeyboardDeviceInfo> _devices = new();
    private int _epollFd = -1;
    private int _inotifyFd = -1;
    private int _inotifyWatchFd = -1;

    private Channel<RawHotkeyEvent>? _channel;
    private CancellationTokenSource? _readerCts;
    private Thread? _readerThread;
    private CancellationTokenSource? _consumerCts;
    private Task? _consumerTask;

    private int _faultRaised;

    public event EventHandler<HotkeyEventArgs>? Pressed;
    public event EventHandler<HotkeyEventArgs>? Released;
    public event EventHandler<HotkeyFaultEventArgs>? Faulted;

    public LinuxHotkeySource(ILogger<LinuxHotkeySource> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(HotkeyBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);

        _binding = binding;
        _triggerCode = EvdevKeyMapper.ToKeyCode(binding.Key);
        _suppress = binding.Suppress;

        if (_suppress && !_suppressionWarningLogged)
        {
            _logger.LogWarning(
                "Hotkey suppression was requested (binding.Suppress=true) but is NOT implemented on Linux "
                + "in this build -- EVIOCGRAB-based grabbing is deliberately deferred pending spike S5 "
                + "(Docs/soneto-implementation-plan-phase0-1.md), which has not run yet. The trigger key "
                + "'{TriggerKey}' WILL leak through to whatever application currently has focus.",
                binding.Key);
            _suppressionWarningLogged = true;
        }

        // Post-review fix (issue 2): everything from EnumerateKeyboards() through
        // inotify_init1() opens real fds into local/instance state before the reader thread
        // or consumer task exist -- if anything in this sequence throws partway through, the
        // fds already opened must be closed here rather than relying on any caller to call
        // DisposeAsync() on a StartAsync that never actually completed (SessionController's
        // Faulted-transition path does NOT call DisposeAsync on a failed StartAsync).
        try
        {
            var enumerator = new KeyboardDeviceEnumerator(_logger);
            _devices = enumerator.EnumerateKeyboards();
            if (_devices.Count == 0)
            {
                throw new InvalidOperationException(
                    "No keyboard-like /dev/input/event* device was found or openable. Check 'input' group "
                    + "membership (see scripts/setup-linux.sh) and that at least one keyboard is attached.");
            }

            _epollFd = EvdevInterop.epoll_create1(0);
            if (_epollFd < 0)
                throw new InvalidOperationException("epoll_create1 failed.");

            foreach (var dev in _devices)
                AddToEpoll(dev.Fd);

            _inotifyFd = EvdevInterop.inotify_init1(EvdevInterop.IN_NONBLOCK);
            if (_inotifyFd >= 0)
            {
                _inotifyWatchFd = EvdevInterop.inotify_add_watch(
                    _inotifyFd, "/dev/input", EvdevInterop.IN_CREATE | EvdevInterop.IN_DELETE);
                if (_inotifyWatchFd >= 0)
                    AddToEpoll(_inotifyFd);
                else
                    _logger.LogWarning("inotify_add_watch(/dev/input) failed -- hotplug re-enumeration will not fire.");
            }
            else
            {
                _logger.LogWarning("inotify_init1 failed -- hotplug re-enumeration will not fire.");
            }
        }
        catch
        {
            foreach (var dev in _devices)
            {
                try { EvdevInterop.close(dev.Fd); } catch { /* best-effort cleanup */ }
            }
            _devices = new List<KeyboardDeviceInfo>();

            if (_inotifyFd >= 0) { try { EvdevInterop.close(_inotifyFd); } catch { } }
            _inotifyFd = -1;
            _inotifyWatchFd = -1;

            if (_epollFd >= 0) { try { EvdevInterop.close(_epollFd); } catch { } }
            _epollFd = -1;

            throw;
        }

        _channel = Channel.CreateUnbounded<RawHotkeyEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        Interlocked.Exchange(ref _faultRaised, 0);

        // Post-review fix (issue 1): the reader thread's delegate captures these as
        // immutable locals, NOT read as live `this._epollFd`/`_inotifyFd`/`_triggerCode`/
        // `_channel` fields inside ReaderLoop's own loop body. This is what makes a stuck
        // (unjoined) reader thread from a PREVIOUS generation provably unable to observe or
        // interfere with a subsequently restarted generation's fds/channel, regardless of
        // whether StopInternalAsync's Thread.Join succeeds -- see that method's own doc
        // comment for the other half of this fix.
        int epollFdForReader = _epollFd;
        int inotifyFdForReader = _inotifyFd;
        ushort triggerCodeForReader = _triggerCode;
        var channelForReader = _channel;

        _readerCts = new CancellationTokenSource();
        var readerToken = _readerCts.Token;
        _readerThread = new Thread(() => ReaderLoop(readerToken, epollFdForReader, inotifyFdForReader, triggerCodeForReader, channelForReader))
        {
            IsBackground = true,
            Name = "LinuxHotkeySource-reader",
        };
        _readerThread.Start();

        _consumerCts = new CancellationTokenSource();
        _consumerTask = Task.Run(() => ConsumeAsync(_channel, _consumerCts.Token), CancellationToken.None);

        _logger.LogInformation(
            "LinuxHotkeySource started: trigger={TriggerKey} ({KeyCode}) suppress={Suppress} devices={DeviceCount}",
            binding.Key, _triggerCode, binding.Suppress, _devices.Count);

        return Task.CompletedTask;
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        if (_binding is null)
            throw new InvalidOperationException("StartAsync must be called before RestartAsync.");

        _logger.LogWarning("LinuxHotkeySource restarting (single attempt; retry/backoff is SessionController's job)");

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
        _readerCts?.Cancel();

        // Post-review fix (issue 1): Join's return value is now checked. If the reader
        // thread does NOT stop within the bound (a stalled log sink, a bad GC pause), it is
        // still alive and -- per ReaderLoop's closure-captured locals (see StartAsync) --
        // will keep looping against ITS OWN captured epoll/inotify fd numbers forever,
        // never touching whatever new fds/channel a subsequent StartAsync creates. That
        // closure capture is what makes it SAFE to leave those specific fd numbers alone
        // here rather than closing them out from under a thread that might still be
        // epoll_wait-ing/read-ing on them (closing a live fd number that another thread is
        // still using invites the classic fd-reuse race: a later, unrelated open() could
        // be handed that same now-closed number while the stuck thread still thinks it
        // owns it). A leaked fd is a strictly smaller failure than that race.
        bool readerJoinedCleanly = true;
        if (_readerThread is { } rt)
        {
            // Reader thread blocks in epoll_wait with a bounded timeout (see ReaderLoop),
            // so Join with a generous bound rather than forever -- mirrors this project's
            // "every disposal path must be clean, never hang" convention.
            readerJoinedCleanly = rt.Join(TimeSpan.FromSeconds(2));
            if (!readerJoinedCleanly)
            {
                _logger.LogCritical(
                    "LinuxHotkeySource: the evdev reader thread did not stop within the 2s shutdown "
                    + "timeout. Deliberately leaking this generation's device/epoll/inotify fds rather "
                    + "than closing them out from under a thread that may still be using them -- see "
                    + "this method's own doc comment for why. This generation's fd numbers are NOT "
                    + "reused by anything this instance does afterward.");
            }
        }
        _readerThread = null;
        _readerCts?.Dispose();
        _readerCts = null;

        if (readerJoinedCleanly)
        {
            foreach (var dev in _devices)
            {
                try { EvdevInterop.close(dev.Fd); } catch { /* best-effort cleanup */ }
            }

            if (_inotifyFd >= 0) { try { EvdevInterop.close(_inotifyFd); } catch { } }
            if (_epollFd >= 0) { try { EvdevInterop.close(_epollFd); } catch { } }
        }
        _devices = new List<KeyboardDeviceInfo>();
        _inotifyFd = -1;
        _inotifyWatchFd = -1;
        _epollFd = -1;

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

    private void AddToEpoll(int fd)
    {
        var ev = new EvdevInterop.epoll_event { events = EvdevInterop.EPOLLIN, data = (ulong)(uint)fd };
        if (EvdevInterop.epoll_ctl(_epollFd, EvdevInterop.EPOLL_CTL_ADD, fd, ref ev) < 0)
            _logger.LogWarning("epoll_ctl(ADD) failed for fd={Fd}", fd);
    }

    // ============================================================================
    // Raw reader thread: epoll_wait + read() only. Never raises Pressed/Released, never
    // logs at more than warning-on-fault level, never blocks on anything but epoll_wait's
    // own bounded timeout. Posts to the channel and returns, per the class doc comment.
    // ============================================================================

    // Post-review fix (issue 1): takes its fds/trigger-code/channel as parameters, captured
    // as immutable locals by StartAsync's thread-creation closure, rather than reading the
    // live `_epollFd`/`_inotifyFd`/`_triggerCode`/`_channel` instance fields. See
    // StartAsync's and StopInternalAsync's doc comments for why this specifically matters:
    // a stuck (unjoined-within-timeout) reader thread from a previous generation can now
    // never observe or interfere with whatever a subsequent StartAsync/RestartAsync writes
    // into those instance fields.
    private void ReaderLoop(CancellationToken ct, int epollFd, int inotifyFd, ushort triggerCode, Channel<RawHotkeyEvent> channel)
    {
        var events = new EvdevInterop.epoll_event[16];
        var readBuf = new byte[EvdevInterop.InputEventSize];
        var inotifyBuf = new byte[4096];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = EvdevInterop.epoll_wait(epollFd, events, events.Length, 500);
                if (n < 0)
                {
                    RaiseFaultOnce("epoll_wait returned an error.", null);
                    return;
                }

                for (int i = 0; i < n; i++)
                {
                    int fd = (int)events[i].data;
                    if ((events[i].events & (EvdevInterop.EPOLLERR | EvdevInterop.EPOLLHUP)) != 0)
                    {
                        RaiseFaultOnce($"A device fd (or the inotify fd) reported EPOLLERR/EPOLLHUP: fd={fd}.", null);
                        continue;
                    }

                    if (fd == inotifyFd)
                    {
                        // Hotplug event: drain, then let the caller decide to re-enumerate.
                        // Actual re-enumeration is a StartAsync-level operation (rebuilds
                        // epoll/fds); this reader thread only detects and reports it, since
                        // rebuilding fd sets from inside the epoll_wait loop that owns them
                        // would be its own source of races. Reported via a Faulted-shaped
                        // signal so SessionController's existing restart-with-backoff path
                        // (item 9/10) handles it uniformly with any other hotkey-source fault.
                        //
                        // Post-review fix (issue 4): only treated as fault-worthy if at
                        // least one changed name actually looks like an evdev node
                        // ("event*") -- routine unrelated /dev/input churn (e.g. some other
                        // USB device's non-keyboard sub-node) would otherwise trigger a full
                        // restart-storm for no reason.
                        int bytesRead = (int)EvdevInterop.read(inotifyFd, inotifyBuf, (nuint)inotifyBuf.Length);
                        bool relevant = bytesRead > 0
                            && EvdevInterop.ParseInotifyEventNames(inotifyBuf, bytesRead)
                                .Any(name => name.StartsWith("event", StringComparison.Ordinal));
                        if (relevant)
                        {
                            RaiseFaultOnce(
                                "/dev/input changed (evdev device plugged/unplugged); re-enumeration required.", null);
                        }
                        continue;
                    }

                    var readResult = EvdevInterop.read(fd, readBuf, (nuint)readBuf.Length);
                    if (readResult != EvdevInterop.InputEventSize)
                        continue; // partial/empty read; not fatal on its own.

                    var (type, code, value) = EvdevInterop.ParseInputEvent(readBuf);
                    if (type != EvdevConstants.EV_KEY || code != triggerCode)
                        continue;

                    // evdev EV_KEY value: 0 = up, 1 = down, 2 = autorepeat (ignored here --
                    // only the first down and the eventual up matter for hold-to-talk).
                    if (value == 1)
                        channel.Writer.TryWrite(new RawHotkeyEvent(HotkeyEdge.Down, DateTimeOffset.UtcNow));
                    else if (value == 0)
                        channel.Writer.TryWrite(new RawHotkeyEvent(HotkeyEdge.Up, DateTimeOffset.UtcNow));
                }
            }
        }
        catch (Exception ex)
        {
            RaiseFaultOnce("Unhandled exception in the evdev reader loop.", ex);
        }
    }

    private void RaiseFaultOnce(string reason, Exception? ex)
    {
        if (Interlocked.CompareExchange(ref _faultRaised, 1, 0) != 0)
            return;
        _logger.LogError(ex, "LinuxHotkeySource fault: {Reason}", reason);
        try
        {
            // Post-review fix (issue 3): every other subscriber-invocation site in this
            // class (and in WindowsHotkeySource) guards the event-raise in its own
            // try/catch so a misbehaving subscriber can't take down the process. This is
            // the one site that can be reached directly from ReaderLoop's raw background
            // Thread (not a Task with an observed/logged exception) -- an unhandled
            // exception here would terminate the whole process, violating plan §1.12's
            // "daemon never exits on a recoverable error" principle.
            Faulted?.Invoke(this, new HotkeyFaultEventArgs(reason, ex));
        }
        catch (Exception subscriberEx)
        {
            _logger.LogError(subscriberEx, "Unhandled exception from a Faulted subscriber");
        }
    }

    // ============================================================================
    // Consumer thread: the ONLY place Pressed/Released are raised. Same pattern as
    // WindowsHotkeySource.ConsumeAsync.
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
            // Expected on shutdown.
        }
    }

    private enum HotkeyEdge { Down, Up }

    private readonly record struct RawHotkeyEvent(HotkeyEdge Edge, DateTimeOffset Timestamp);
}
