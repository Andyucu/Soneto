using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PortAudioSharp;
using Soneto.Core.Abstractions;
using PaStream = PortAudioSharp.Stream;

namespace Soneto.Core.Audio;

/// <summary>
/// <see cref="IAudioCapture"/> implementation over PortAudio, plan §1.5's <c>OnDemand</c>
/// capture mode only (item 4b's scope): the stream is opened by <see cref="StartAsync"/> and
/// closed by <see cref="StopAsync"/> — no idle-keep-open logic (that's item 4c's
/// <c>WarmIdle</c>), no ready cue (item 4c), no VAD (item 5).
///
/// <para><b>Stream configuration, per §1.5's "Correct sequence":</b> queries the resolved
/// device's <c>defaultSampleRate</c>, probes <c>Pa_IsFormatSupported</c> for 16 kHz mono
/// float32 via <see cref="PortAudioExtras"/> (the two hand-added P/Invoke declarations
/// ported from <c>spikes/s1b-audio</c>, since PortAudioSharp2 doesn't expose them), and picks
/// the path via <see cref="CaptureFormatSelector"/> — direct 16 kHz if supported, otherwise
/// the device's native rate with an in-process <see cref="PolyphaseResampler"/>. Both the
/// chosen path and the actual negotiated rate are logged at Information level, per §1.5:
/// "Every audio bug you will ever have starts with not knowing this."</para>
///
/// <para><b>Callback-thread discipline (redesigned, see <c>Docs/PROJECT-MEMORY.md</c> item 4b
/// for why the original design was wrong):</b> §1.4's threading model states the PortAudio
/// callback thread "writes into the ring buffer only. Lock-free single-producer/single-
/// consumer." <see cref="OnCallback"/> now does exactly that and nothing else: one bulk
/// native-pointer-to-managed-array copy (<see cref="Marshal.Copy(nint,float[],int,int)"/>)
/// into a pre-sized reusable scratch array, one bulk copy of that scratch array into a
/// pre-allocated <see cref="SpscFloatRingBuffer"/> (<see cref="_ringBuffer"/>), and a
/// lock-free signal (<see cref="ManualResetEventSlim.Set"/>) to wake the consumer. No lock,
/// no resampling, no RMS/level computation, no per-sample work of any kind — if the ring
/// buffer is full (the consumer has fallen behind), the newest chunk is dropped and counted
/// rather than the callback blocking or contending a lock. All of the actual work the old
/// design did inside the callback — resampling, RMS→dBFS for <see cref="LevelChanged"/>,
/// appending into the growable capture buffer, and the one lock this class still takes
/// (<see cref="_sync"/>) — now happens on <see cref="ConsumerLoop"/>, a dedicated
/// non-real-time background thread that drains the ring buffer. <see cref="EndCapture"/> and
/// <see cref="AbortCapture"/> also only ever touch <see cref="_sync"/> from a non-real-time
/// caller thread, so the lock is never contended by the audio callback.</para>
///
/// <para><b>Device resolution</b> is re-done fresh on every <see cref="StartAsync"/> call via
/// <see cref="CaptureDeviceResolver"/>, per §1.5's "Device changes": a configured device that
/// no longer exists falls back to the system default with a logged warning, rather than
/// failing the dictation.</para>
///
/// <para><b>Deferred (not this item):</b> §1.12's error-matrix rows "Audio device removed
/// mid-record → abort session, fall back to default device" and the Bluetooth
/// time-to-first-sample warning are only partly addressed here — the Bluetooth
/// >400ms-warning check is implemented (see <see cref="NoteFirstNonZeroBuffer"/>), but
/// device-removal detection/fallback mid-capture is not: PortAudio surfaces that as a stream
/// error/abort from the callback rather than a clean event, and reacting to it correctly
/// (tearing down and reopening on the default device mid-utterance) needs the session-level
/// orchestration item 9 (<c>SessionController</c>) builds, or at minimum item 4c's WarmIdle
/// work. Left as a known gap rather than a silent omission.</para>
///
/// <para><b>Concurrency contract:</b> <see cref="StartAsync"/>, <see cref="StopAsync"/>, and
/// <see cref="IsRunning"/> are NOT synchronized against each other and are intended for a
/// single caller (the future <c>SessionController</c>, item 9) driving this instance's
/// lifecycle strictly sequentially — start, then stop, never two starts or two stops
/// in flight concurrently. <see cref="BeginCapture"/>/<see cref="EndCapture"/>/
/// <see cref="AbortCapture"/> are safe to call from that same single caller thread while a
/// capture is open; they synchronize correctly against the internal consumer thread via
/// <see cref="_sync"/>.</para>
///
/// <para><b>PortAudio init/terminate:</b> this class independently calls
/// <see cref="PortAudio.Initialize()"/>/<see cref="PortAudio.Terminate()"/>, as does
/// <see cref="AudioCuePlayer"/> -- safe because PortAudio's native init/terminate is
/// documented as reference-counted/safe with balanced calls across multiple independent
/// owners in the same process.</para>
/// </summary>
public sealed class PortAudioCapture : IAudioCapture, IReadySignal
{
    private const int FramesPerBuffer = 512; // native-rate frames per callback, per §1.5 "Buffer"
    private const int TargetRate = CaptureFormatSelector.TargetSampleRate;

    // How many seconds of native-rate audio the lock-free ring buffer can absorb if the
    // consumer thread is briefly delayed (GC, scheduler, resample-cost jitter measured by
    // PolyphaseResamplerRealtimeBudgetTests). Generous on purpose: this only needs to survive
    // scheduling jitter between callback bursts, not hold a whole utterance — the consumer
    // drains continuously and this is a fixed circular buffer, not something sized to
    // maxDurationMs.
    private const double RingBufferSeconds = 2.0;

    // Consumer thread wakes at least this often even if the producer's signal is missed/
    // coalesced (ManualResetEventSlim.Set() is idempotent, so a burst of callbacks between
    // two consumer wakeups only requires one drain pass, not one per callback) — a cheap
    // safety net, not the primary wake mechanism.
    private const int DrainPollMs = 5;

    // Restored per-item-4b-review requirement: "first non-zero buffer," not just "first
    // buffer at all," is the metric that matters — S1b's spike found a real device
    // (WASAPI/WDM-KS on this machine) that opens/starts successfully but delivers all-zero
    // buffers forever. A buffer is "silent" if every sample's magnitude is below this noise
    // floor.
    private const float NonZeroThreshold = 1e-4f;

    // Plan §1.12's error-matrix row: "Bluetooth mic profile switch delay (time-to-first-
    // sample > 400ms) -> log warning suggesting WarmIdle."
    private const double BluetoothWarnThresholdMs = 400.0;

    // Plan §1.5/interface doc: LevelChanged is documented as firing at ~20 Hz, not once per
    // native callback (which is 512/nativeRate, e.g. ~94 Hz at 48 kHz or ~31 Hz at 16 kHz).
    private const int LevelRaiseHz = 20;

    private readonly ILogger<PortAudioCapture> _logger;
    private readonly int _maxDurationMs;
    private readonly int _preRollCapacityMs;

    // Guards the fields the consumer thread and the calling (StartAsync/BeginCapture/
    // EndCapture/AbortCapture) thread both touch: the capture buffer, the resampler's
    // streaming state, and the capturing flag. Never touched by the PortAudio callback
    // thread (OnCallback) — see class doc's "Callback-thread discipline."
    private readonly object _sync = new();
    private readonly List<float> _capturedSamples = [];
    private PolyphaseResampler? _resampler;
    private bool _capturing;
    private long _maxSamplesAt16k;
    private bool _capacityWarningLogged;

    // Item 4c pre-roll support (plan §1.5's WarmIdle/AlwaysOn "full pre-roll from the 2nd
    // utterance onward"). Deliberately a SEPARATE PolyphaseResampler instance from
    // `_resampler`, kept continuously running across BeginCapture/EndCapture boundaries
    // (its own state is never Reset/Flushed by an utterance's lifecycle) so the ring buffer
    // sees seamless audio while idle -- `_resampler` above keeps its existing per-utterance
    // Reset()-at-BeginCapture/Flush()-at-EndCapture behaviour completely unchanged, so
    // OnDemand's (preRollCapacityMs=0, both preroll fields stay null) behaviour is provably
    // identical to pre-item-4c. Both live under the same `_sync` lock as everything else
    // here -- appended to from ConsumerLoop, snapshotted from BeginCapture's caller thread,
    // never touched by OnCallback.
    private PreRollRingBuffer? _preRollBuffer;
    private PolyphaseResampler? _preRollResampler;
    private readonly List<float> _preRollResampleScratch = [];

    // Guards the first-buffer timing handshake only. Read/written from both the consumer
    // thread and whichever thread calls WaitForFirstSampleAsync/TimeToFirstSampleMs.
    private readonly object _timingSync = new();
    private Stopwatch? _openStopwatch;
    private double? _timeToFirstBufferMs; // fast diagnostic: first buffer at all, may be silent
    private double? _timeToFirstSampleMs; // primary metric: first buffer with real signal in it
    private TaskCompletionSource<double>? _firstBufferTcs;
    private bool _firstBufferReceivedNoted;
    private bool _firstNonZeroNoted;

    // The lock-free producer/consumer plumbing. All non-null only while a stream is open.
    private SpscFloatRingBuffer? _ringBuffer;
    private ManualResetEventSlim? _drainSignal;
    private Thread? _consumerThread;
    private volatile bool _consumerRunning;
    private long _callbackErrorCount;

    // Consumer-thread-owned only (no lock needed: single writer, and LevelChanged handlers
    // are documented as running on this thread so nothing else touches these fields).
    private double _levelSumSquares;
    private int _levelSampleCount;
    private int _levelRaiseThresholdSamples = 1;

    private bool _paInitialized;
    private volatile PaStream? _stream;
    private float[]? _callbackScratch;

    /// <param name="logger"></param>
    /// <param name="maxDurationMs"></param>
    /// <param name="preRollCapacityMs">
    /// Item 4c: how much rolling pre-capture audio (in ms, 16 kHz domain) to keep buffered
    /// while the stream is open but no utterance is being captured — sized from
    /// <c>AudioConfig.PreRollMs</c> by the caller. 0 (the default) disables the pre-roll
    /// buffer entirely, which is exactly item 4b's behaviour (no allocation, no extra
    /// per-segment work) — the correct default for <c>OnDemand</c>, whose stream isn't even
    /// running between utterances.
    /// </param>
    public PortAudioCapture(ILogger<PortAudioCapture> logger, int maxDurationMs = 120_000, int preRollCapacityMs = 0)
    {
        _logger = logger;
        _maxDurationMs = maxDurationMs;
        _preRollCapacityMs = preRollCapacityMs;
    }

    /// <summary>
    /// Reflects whether a stream is currently open. <see cref="_stream"/> is <c>volatile</c>
    /// so a caller on a different thread than whichever thread is driving <see cref="StartAsync"/>/
    /// <see cref="StopAsync"/> (e.g. a health check or diagnostics poll) sees an up-to-date value
    /// rather than a stale cached read — this doesn't relax the documented single-driving-thread
    /// contract for StartAsync/StopAsync themselves, it only makes IsRunning safe to poll from
    /// anywhere.
    /// </summary>
    public bool IsRunning => _stream != null;

    /// <summary>
    /// Raised at ~<see cref="LevelRaiseHz"/> Hz while running (throttled from the raw
    /// per-native-callback rate — see <see cref="AccumulateLevel"/>). Handlers run on the
    /// internal consumer background thread (<see cref="ConsumerLoop"/>), NOT the PortAudio
    /// real-time callback thread and NOT necessarily the thread that called
    /// <see cref="StartAsync"/> — a handler that blocks delays draining the ring buffer and
    /// can eventually cause dropped audio, so handlers must return quickly.
    /// </summary>
    public event EventHandler<AudioLevelEventArgs>? LevelChanged;

    /// <summary>
    /// Which path <see cref="StartAsync"/> chose for the currently (or most recently) open
    /// stream — exposed beyond the bare <see cref="IAudioCapture"/> contract because item
    /// 4b's <c>--record</c> CLI demo needs to report it. Null before the first
    /// <see cref="StartAsync"/> call.
    /// </summary>
    public CapturePathKind? CapturePath { get; private set; }

    /// <summary>The actual rate negotiated with <c>Pa_OpenStream</c> (not necessarily 16 kHz — see <see cref="CapturePath"/>).</summary>
    public int? NegotiatedSampleRate { get; private set; }

    /// <summary>
    /// Time from stream-start to the first callback buffer that actually contains real
    /// signal (any sample magnitude above a small noise floor), in milliseconds — the same
    /// "time to first non-zero buffer" metric S1b's spike measured, restored here after a
    /// prior simplification regressed it to "first buffer at all" (see
    /// <c>Docs/PROJECT-MEMORY.md</c> item 4b). Null until such a buffer has arrived; stays
    /// null forever for a device that opens/starts but only ever delivers silence — exactly
    /// the known-bad WASAPI/WDM-KS configuration S1b found, which this metric is meant to
    /// expose rather than hide. See also <see cref="TimeToFirstBufferMs"/>.
    /// </summary>
    public double? TimeToFirstSampleMs
    {
        get { lock (_timingSync) { return _timeToFirstSampleMs; } }
    }

    /// <summary>
    /// Time from stream-start to the first callback buffer arriving at all, regardless of
    /// its content (silent or not) — a faster, weaker diagnostic than
    /// <see cref="TimeToFirstSampleMs"/>, useful for distinguishing "device never started"
    /// (this also stays null) from "device started but is silent" (this is set,
    /// <see cref="TimeToFirstSampleMs"/> is not).
    /// </summary>
    public double? TimeToFirstBufferMs
    {
        get { lock (_timingSync) { return _timeToFirstBufferMs; } }
    }

    /// <summary>
    /// Waits for the first NON-ZERO audio buffer to arrive after <see cref="StartAsync"/>
    /// (see <see cref="TimeToFirstSampleMs"/>), returning the time-to-first-sample in
    /// milliseconds. Throws <see cref="TimeoutException"/> if none arrives within
    /// <paramref name="timeout"/> — including the case where buffers are arriving but are
    /// all silent, which is a real, actionable failure this method is meant to surface, not
    /// mask. <paramref name="ct"/> is honoured for the whole wait, not just checked at entry —
    /// cancelling it during the wait raises <see cref="OperationCanceledException"/>
    /// immediately rather than only taking effect once <paramref name="timeout"/> elapses.
    /// </summary>
    public async Task<double> WaitForFirstSampleAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        Task<double> task;
        lock (_timingSync)
        {
            if (_timeToFirstSampleMs is { } ms)
                return ms;
            _firstBufferTcs ??= new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            task = _firstBufferTcs.Task;
        }

        var completed = await Task.WhenAny(task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (completed != task)
            throw new TimeoutException(
                $"No audio buffer arrived within {timeout.TotalMilliseconds:F0}ms of starting the stream.");
        return await task.ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="IReadySignal"/> implementation for item 4c's ready-cue wiring
    /// (<see cref="CaptureModeController"/>): a thin wrapper over the existing
    /// <see cref="WaitForFirstSampleAsync"/> so the cue player reuses item 4b's "first
    /// non-zero buffer" metric exactly, rather than a separate/weaker "stream opened" signal.
    /// <paramref name="ct"/> is threaded all the way through to the underlying
    /// <see cref="Task.Delay(TimeSpan,CancellationToken)"/> race, so cancelling after entry
    /// takes effect immediately rather than only at entry.
    /// </summary>
    Task IReadySignal.WaitForReadyAsync(TimeSpan timeout, CancellationToken ct)
        => WaitForFirstSampleAsync(timeout, ct);

    public Task StartAsync(AudioDeviceId? device, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_stream != null)
            throw new InvalidOperationException("Capture is already running; call StopAsync first.");

        EnsurePortAudioInitialized();

        int resolvedIndex;
        DeviceInfo info;
        try
        {
            int defaultInput = PortAudio.DefaultInputDevice;
            resolvedIndex = CaptureDeviceResolver.Resolve(device, DeviceExists, defaultInput, _logger);
            if (resolvedIndex == PortAudio.NoDevice)
                throw new InvalidOperationException("No input audio device is available on this system.");
            info = PortAudio.GetDeviceInfo(resolvedIndex);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to resolve/query the audio input device.", ex);
        }

        var (hostApiName, _) = PortAudioExtras.GetHostApiName(info.hostApi);
        bool supports16k = SafeIsFormatSupported(resolvedIndex, 1, SampleFormat.Float32, TargetRate);
        var plan = CaptureFormatSelector.Select(supports16k, info.defaultSampleRate);

        _logger.LogInformation(
            "Resolved audio device {DeviceIndex} \"{DeviceName}\" (hostApi={HostApi}, defaultRate={DefaultRate}Hz, "
            + "supports16kDirect={Supports16k}) -> path={Path}, openRate={OpenRate}Hz",
            resolvedIndex, info.name, hostApiName, info.defaultSampleRate, supports16k, plan.Kind, plan.OpenRate);

        lock (_sync)
        {
            _capturedSamples.Clear();
            _capturing = false;
            _capacityWarningLogged = false;
            _maxSamplesAt16k = (long)TargetRate * _maxDurationMs / 1000;

            // Pre-size the final 16kHz output buffer up front so a long capture doesn't
            // trigger List<float>'s internal regrow-and-memcpy mid-recording — the growable
            // buffer that most needed pre-sizing, since it lives for the whole utterance.
            int reserveCapacity = (int)Math.Min(_maxSamplesAt16k, int.MaxValue);
            if (_capturedSamples.Capacity < reserveCapacity)
                _capturedSamples.Capacity = reserveCapacity;

            _resampler = plan.Kind == CapturePathKind.ResampleFromNative
                ? new PolyphaseResampler(plan.OpenRate, TargetRate)
                : null;

            int preRollCapacitySamples = _preRollCapacityMs > 0
                ? Math.Max(1, TargetRate * _preRollCapacityMs / 1000)
                : 0;
            _preRollBuffer = preRollCapacitySamples > 0 ? new PreRollRingBuffer(preRollCapacitySamples) : null;
            _preRollResampler = (_preRollBuffer != null && plan.Kind == CapturePathKind.ResampleFromNative)
                ? new PolyphaseResampler(plan.OpenRate, TargetRate)
                : null;
            _preRollResampleScratch.Clear();
        }

        lock (_timingSync)
        {
            _timeToFirstBufferMs = null;
            _timeToFirstSampleMs = null;
            _firstBufferTcs = null;
            _firstBufferReceivedNoted = false;
            _firstNonZeroNoted = false;
        }

        _callbackScratch = new float[FramesPerBuffer];
        _callbackErrorCount = 0;
        _levelSumSquares = 0;
        _levelSampleCount = 0;
        _levelRaiseThresholdSamples = Math.Max(1, plan.OpenRate / LevelRaiseHz);

        int ringCapacity = Math.Max(FramesPerBuffer * 8, (int)(plan.OpenRate * RingBufferSeconds));
        _ringBuffer = new SpscFloatRingBuffer(ringCapacity);
        _drainSignal = new ManualResetEventSlim(false);
        _consumerRunning = true;
        _consumerThread = new Thread(ConsumerLoop)
        {
            IsBackground = true,
            Name = "Soneto.AudioConsumer",
        };
        _consumerThread.Start();

        var streamParams = new StreamParameters
        {
            device = resolvedIndex,
            channelCount = 1,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        var openSw = Stopwatch.StartNew();
        try
        {
            _stream = new PaStream(inParams: streamParams, outParams: null, sampleRate: plan.OpenRate,
                framesPerBuffer: FramesPerBuffer, streamFlags: StreamFlags.ClipOff, callback: OnCallback, userData: 0);
            _stream.Start();
        }
        catch (Exception ex)
        {
            // Plan §1.12: "Audio stream fails to open" -> log device + host API, abort.
            _logger.LogError(ex,
                "Pa_OpenStream/Start failed for device {DeviceIndex} \"{DeviceName}\" (hostApi={HostApi})",
                resolvedIndex, info.name, hostApiName);
            try { _stream?.Dispose(); } catch { /* best effort */ }
            _stream = null;

            // Nothing will ever call OnCallback for this attempt; shut the consumer thread
            // back down instead of leaking it.
            StopConsumerThread();
            _ringBuffer = null;

            throw new InvalidOperationException(
                $"Failed to open the audio input stream on device '{info.name}' (hostApi={hostApiName}).", ex);
        }
        openSw.Stop();

        // Time-to-first-sample is measured from here (stream start), matching S1b's spike
        // method, not from Pa_OpenStream's own (much smaller) elapsed time above.
        lock (_timingSync)
        {
            _openStopwatch = Stopwatch.StartNew();
        }

        CapturePath = plan.Kind;
        NegotiatedSampleRate = plan.OpenRate;

        _logger.LogInformation(
            "Audio stream opened in {OpenMs:F1}ms and started (device {DeviceIndex}, negotiated rate={Rate}Hz, path={Path})",
            openSw.Elapsed.TotalMilliseconds, resolvedIndex, plan.OpenRate, plan.Kind);

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        var stream = _stream;
        _stream = null;

        if (stream != null)
        {
            try
            {
                stream.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping the audio stream (closing it anyway)");
            }
            finally
            {
                // Dispose(), not just Close(): Close() only closes the native stream handle
                // — the pinned GCHandles PortAudioSharp2 keeps for the callback/userData are
                // only freed by Dispose()/the finalizer (per spikes/s1b-audio's validated
                // finding). Always runs, per §1.12's "IAudioCapture closes in finally, always."
                stream.Dispose();
            }
        }

        // Now that OnCallback can no longer fire (the native stream is stopped/disposed),
        // it's safe to tell the consumer thread to drain whatever's left and exit.
        StopConsumerThread();

        long dropped = _ringBuffer?.DroppedSamples ?? 0;
        long errors = Interlocked.Read(ref _callbackErrorCount);
        if (dropped > 0)
            _logger.LogWarning(
                "Audio ring buffer dropped {DroppedSamples} native-rate samples during this stream "
                + "(the consumer thread couldn't keep up with the real-time callback)", dropped);
        if (errors > 0)
            _logger.LogWarning(
                "The audio callback (OnCallback) hit {ErrorCount} exception(s) during this stream "
                + "(swallowed at the time, since a managed exception can't cross PortAudio's native "
                + "trampoline) — investigate if this is non-zero", errors);

        _ringBuffer = null;

        // Item 4c: the pre-roll buffer/resampler only live for the lifetime of one open
        // stream (same as the ring buffer above) -- a fresh BeginCapture after a later
        // StartAsync gets a fresh, empty pre-roll buffer, never stale audio from a previous
        // stream.
        _preRollBuffer = null;
        _preRollResampler = null;

        lock (_timingSync)
        {
            _openStopwatch = null;
        }

        return Task.CompletedTask;
    }

    public void BeginCapture(TimeSpan preRoll)
    {
        lock (_sync)
        {
            _capturedSamples.Clear();

            if (preRoll > TimeSpan.Zero && _preRollBuffer != null)
            {
                int wantSamples = (int)Math.Round(preRoll.TotalSeconds * TargetRate);
                var snapshot = _preRollBuffer.Snapshot(wantSamples);
                if (snapshot.Length > 0)
                {
                    _capturedSamples.AddRange(snapshot);
                    _logger.LogDebug(
                        "BeginCapture: prepended {SnapshotSamples} pre-roll samples ({SnapshotMs:F0}ms) "
                        + "from the rolling buffer", snapshot.Length, snapshot.Length * 1000.0 / TargetRate);
                }
            }
            else if (preRoll > TimeSpan.Zero)
            {
                _logger.LogDebug(
                    "BeginCapture preRoll={PreRollMs}ms requested but no pre-roll buffer is active "
                    + "(preRollCapacityMs=0, or this is OnDemand mode where the stream isn't running "
                    + "between utterances) -- nothing to prepend, per plan §1.5",
                    preRoll.TotalMilliseconds);
            }

            _resampler?.Reset();
            _capacityWarningLogged = false;
            _capturing = true;
        }
    }

    public ReadOnlyMemory<float> EndCapture()
    {
        // Drain any samples still sitting in the ring buffer before flipping _capturing off,
        // so the tail of the utterance isn't lost to a lagging consumer thread. The consumer
        // wakes at least every DrainPollMs, so this should resolve in low single-digit
        // milliseconds; bounded so a stalled consumer can't hang the caller indefinitely.
        WaitForRingBufferDrained(TimeSpan.FromMilliseconds(100));

        lock (_sync)
        {
            _capturing = false;

            if (_resampler != null)
            {
                var tail = _resampler.Flush();
                if (tail.Length > 0 && _capturedSamples.Count < _maxSamplesAt16k)
                {
                    int room = (int)Math.Min(tail.Length, _maxSamplesAt16k - _capturedSamples.Count);
                    _capturedSamples.AddRange(tail.AsSpan(0, room).ToArray());
                }
            }

            var result = _capturedSamples.ToArray();
            _capturedSamples.Clear();
            return result;
        }
    }

    public void AbortCapture()
    {
        lock (_sync)
        {
            _capturing = false;
            _capturedSamples.Clear();
            _resampler?.Reset();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Always close the stream on the way out, per §1.12's error-matrix row for
        // "Audio stream left open on an error path" — regardless of how we got here.
        await StopAsync().ConfigureAwait(false);

        if (_paInitialized)
        {
            try
            {
                PortAudio.Terminate();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PortAudio.Terminate() failed during dispose");
            }
            _paInitialized = false;
        }
    }

    private void EnsurePortAudioInitialized()
    {
        if (_paInitialized) return;
        PortAudio.Initialize();
        _paInitialized = true;
    }

    private static bool DeviceExists(int index)
    {
        if (index < 0 || index >= PortAudio.DeviceCount)
            return false;
        try
        {
            return PortAudio.GetDeviceInfo(index).maxInputChannels > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeIsFormatSupported(int device, int channels, SampleFormat format, double rate)
    {
        try
        {
            return PortAudioExtras.IsInputFormatSupported(device, channels, format, rate);
        }
        catch
        {
            return false;
        }
    }

    private void StopConsumerThread()
    {
        _consumerRunning = false;
        _drainSignal?.Set();

        var consumerThread = _consumerThread;
        _consumerThread = null;
        if (consumerThread != null && !consumerThread.Join(TimeSpan.FromSeconds(2)))
        {
            _logger.LogWarning("Audio consumer thread did not exit within 2s of being asked to stop");
        }

        _drainSignal?.Dispose();
        _drainSignal = null;
    }

    private void WaitForRingBufferDrained(TimeSpan timeout)
    {
        var ring = _ringBuffer;
        if (ring is null) return;

        var sw = Stopwatch.StartNew();
        while (!ring.IsEmpty && sw.Elapsed < timeout)
        {
            _drainSignal?.Set();
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// The PortAudio real-time callback. Per §1.4's threading model ("writes into the ring
    /// buffer only. Lock-free single-producer/single-consumer") this does the minimum
    /// possible: one bulk copy from the native buffer into a pre-sized scratch array, one
    /// bulk copy from scratch into <see cref="_ringBuffer"/>, and a lock-free wake signal.
    /// No lock, no resampling, no RMS, no logging, no exceptions allowed to escape
    /// (PortAudio's native trampoline can't usefully handle a managed exception) — a caught
    /// exception here only increments a lock-free counter, checked/logged from
    /// <see cref="StopAsync"/>.
    /// </summary>
    private StreamCallbackResult OnCallback(
        IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        try
        {
            if (input == IntPtr.Zero || frameCount == 0)
                return StreamCallbackResult.Continue;

            var scratch = _callbackScratch;
            var ring = _ringBuffer;
            var signal = _drainSignal;
            if (scratch is null || ring is null)
                return StreamCallbackResult.Continue;

            int n = (int)Math.Min(frameCount, (uint)scratch.Length);
            if (n < frameCount)
            {
                // PortAudio is expected to always call back with exactly FramesPerBuffer
                // frames (per Pa_OpenStream); this should not happen in practice, but some
                // host APIs can hand back more under device reconfiguration. Rather than
                // silently truncating with no diagnostic trail (unlike the ring-buffer-full
                // drop path below, which IS counted), count it via the same RT-thread-safe
                // error counter checked/logged from StopAsync.
                Interlocked.Increment(ref _callbackErrorCount);
            }
            Marshal.Copy(input, scratch, 0, n);

            // Never blocks: if the consumer has fallen behind and the ring buffer is full,
            // this chunk is dropped and counted rather than the callback waiting.
            ring.TryWrite(scratch.AsSpan(0, n));

            signal?.Set();
        }
        catch
        {
            // Never let an exception escape the native callback trampoline. Interlocked, not
            // logged here (no I/O on the real-time thread) — surfaced from StopAsync instead.
            Interlocked.Increment(ref _callbackErrorCount);
        }

        return StreamCallbackResult.Continue;
    }

    /// <summary>
    /// Dedicated non-real-time background thread that drains <see cref="_ringBuffer"/>: this
    /// is where the resampling, RMS→dBFS level computation, and the one remaining lock
    /// (<see cref="_sync"/>) all live now, per the item 4b redesign — none of this work is
    /// time-critical the way the PortAudio callback is, so blocking here briefly (e.g. on
    /// <see cref="_sync"/> contention with <see cref="EndCapture"/>) is safe.
    /// </summary>
    private void ConsumerLoop()
    {
        var ring = _ringBuffer!;
        var signal = _drainSignal!;

        while (true)
        {
            signal.Wait(DrainPollMs);
            signal.Reset();

            while (ring.DrainInto(ProcessNativeSegment) > 0)
            {
                // Keep draining bursts fully before going back to waiting, so a signal
                // coalesced across several callbacks doesn't leave data sitting around.
            }

            if (!_consumerRunning && ring.IsEmpty)
                break;
        }
    }

    /// <summary>
    /// Processes one contiguous segment of native-rate audio drained from the ring buffer:
    /// first-buffer timing, level metering, and (if a capture is in progress) resampling +
    /// appending into <see cref="_capturedSamples"/>. Always runs on <see cref="ConsumerLoop"/>'s thread.
    /// </summary>
    private void ProcessNativeSegment(ReadOnlySpan<float> native)
    {
        NoteFirstBufferReceived();
        NoteFirstNonZeroBuffer(native);
        AccumulateLevel(native);

        lock (_sync)
        {
            // Item 4c: feed the pre-roll ring buffer continuously, independent of whether a
            // capture is in progress -- this is what lets WarmIdle prepend real pre-capture
            // audio starting from the 2nd utterance of a burst. Uses its own resampler
            // instance (`_preRollResampler`), never `_resampler`, so this has zero effect on
            // `_resampler`'s existing per-utterance Reset()/Flush() lifecycle below. A no-op
            // (single null check, no allocation) whenever `_preRollBuffer` is null --
            // OnDemand's default preRollCapacityMs=0 keeps this path exactly as cheap as
            // before item 4c.
            if (_preRollBuffer != null)
            {
                if (_preRollResampler != null)
                {
                    _preRollResampleScratch.Clear();
                    _preRollResampler.Resample(native, _preRollResampleScratch);
                    _preRollBuffer.Append(CollectionsMarshal.AsSpan(_preRollResampleScratch));
                }
                else
                {
                    _preRollBuffer.Append(native); // Direct16k path: native IS already 16 kHz.
                }
            }

            if (!_capturing || _capturedSamples.Count >= _maxSamplesAt16k)
                return;

            if (_resampler != null)
            {
                _resampler.Resample(native, _capturedSamples);
            }
            else
            {
                int start = _capturedSamples.Count;
                CollectionsMarshal.SetCount(_capturedSamples, start + native.Length);
                native.CopyTo(CollectionsMarshal.AsSpan(_capturedSamples)[start..]);
            }

            // The resampler can emit slightly more/fewer samples than a naive ratio
            // calculation per call; clamp here the same way EndCapture's tail-append does,
            // so the steady-state path can't overshoot maxDurationMs by up to a buffer's
            // worth of samples the way it used to.
            if (_capturedSamples.Count > _maxSamplesAt16k)
            {
                int excess = (int)(_capturedSamples.Count - _maxSamplesAt16k);
                _capturedSamples.RemoveRange((int)_maxSamplesAt16k, excess);
            }

            if (_capturedSamples.Count >= _maxSamplesAt16k && !_capacityWarningLogged)
            {
                _capacityWarningLogged = true;
                _logger.LogWarning(
                    "Capture buffer reached the maxDurationMs cap ({MaxDurationMs}ms); further audio is being dropped",
                    _maxDurationMs);
            }
        }
    }

    /// <summary>Fast diagnostic: notes when the first buffer of any kind (including all-silent) arrives.</summary>
    private void NoteFirstBufferReceived()
    {
        if (Volatile.Read(ref _firstBufferReceivedNoted)) return;

        lock (_timingSync)
        {
            if (_firstBufferReceivedNoted) return;
            _timeToFirstBufferMs = _openStopwatch?.Elapsed.TotalMilliseconds ?? 0;
            _firstBufferReceivedNoted = true;
        }
    }

    /// <summary>
    /// Primary metric: notes when the first buffer containing real signal (any sample above
    /// <see cref="NonZeroThreshold"/>) arrives — restored per item 4b's review findings (see
    /// <see cref="TimeToFirstSampleMs"/>'s doc comment). Also implements §1.12's Bluetooth
    /// time-to-first-sample warning once this fires.
    ///
    /// <para>Note: until the first non-zero buffer arrives, this scans every drained segment
    /// for a non-zero sample — for the exact known-bad device configuration this metric
    /// exists to detect (a device that opens/starts but delivers silence forever), that scan
    /// runs for the entire lifetime of the stream. That's acceptable here because this now
    /// runs on the non-real-time consumer thread, not the audio callback — the same
    /// perpetual-scan cost on the PortAudio callback thread would have been a real
    /// steady-state real-time-budget risk, which is exactly why this was moved here rather
    /// than done in <see cref="OnCallback"/>.</para>
    /// </summary>
    private void NoteFirstNonZeroBuffer(ReadOnlySpan<float> native)
    {
        if (Volatile.Read(ref _firstNonZeroNoted)) return;

        bool hasSignal = false;
        foreach (var s in native)
        {
            if (MathF.Abs(s) > NonZeroThreshold)
            {
                hasSignal = true;
                break;
            }
        }
        if (!hasSignal) return;

        double ms;
        lock (_timingSync)
        {
            if (_firstNonZeroNoted) return;
            ms = _openStopwatch?.Elapsed.TotalMilliseconds ?? 0;
            _timeToFirstSampleMs = ms;
            _firstNonZeroNoted = true;
        }

        if (ms > BluetoothWarnThresholdMs)
        {
            _logger.LogWarning(
                "Time-to-first-sample was {Ms:F1}ms (> {ThresholdMs:F0}ms) -- matches plan §1.12's "
                + "\"Bluetooth mic profile switch delay\" error-matrix row; consider WarmIdle capture "
                + "mode for this device instead of OnDemand.",
                ms, BluetoothWarnThresholdMs);
        }

        _firstBufferTcs?.TrySetResult(ms);
    }

    /// <summary>
    /// Accumulates RMS across native buffers and raises <see cref="LevelChanged"/> at roughly
    /// <see cref="LevelRaiseHz"/> Hz (throttled from the raw per-callback rate), rather than
    /// once per ~10ms native buffer as the pre-redesign implementation did. Consumer-thread-
    /// owned state only; no lock needed.
    /// </summary>
    private void AccumulateLevel(ReadOnlySpan<float> native)
    {
        var handler = LevelChanged;
        if (handler is null)
        {
            // No subscriber: skip the RMS accumulation entirely and reset, so a
            // late-subscribing handler doesn't immediately get a stale/oversized window.
            _levelSumSquares = 0;
            _levelSampleCount = 0;
            return;
        }

        foreach (var s in native)
            _levelSumSquares += (double)s * s;
        _levelSampleCount += native.Length;

        if (_levelSampleCount < _levelRaiseThresholdSamples)
            return;

        double rms = Math.Sqrt(_levelSumSquares / _levelSampleCount);
        double dbfs = rms > 0 ? 20.0 * Math.Log10(rms) : -120.0;
        _levelSumSquares = 0;
        _levelSampleCount = 0;

        handler(this, new AudioLevelEventArgs(dbfs, DateTimeOffset.UtcNow));
    }
}
