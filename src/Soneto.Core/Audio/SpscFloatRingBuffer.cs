namespace Soneto.Core.Audio;

/// <summary>
/// Fixed-capacity, pre-allocated, lock-free single-producer/single-consumer circular buffer
/// of <see cref="float"/> samples — the mechanism that lets <c>PortAudioCapture</c>'s
/// real-time audio callback satisfy <c>Docs/soneto-implementation-plan-phase0-1.md</c>
/// §1.4's threading model ("Audio callback thread (PortAudio): writes into the ring buffer
/// only. Lock-free single-producer/single-consumer.") literally, rather than the
/// lock+resample-in-callback design that violated it.
///
/// <para><b>Contract:</b> exactly one thread may ever call <see cref="TryWrite"/> (the
/// producer — PortAudio's real-time callback), and exactly one (possibly different) thread
/// may ever call <see cref="DrainInto"/> (the consumer — a non-real-time background thread).
/// Calling either method concurrently from more than one thread each is undefined behaviour;
/// this class does no locking and relies entirely on the SPSC contract plus
/// <see cref="Volatile"/> reads/writes of the two index fields for cross-thread visibility.
/// <see cref="IsEmpty"/> and <see cref="DroppedSamples"/> may be read from any thread
/// (approximate/best-effort snapshots, which is all a liveness/diagnostic check needs).</para>
///
/// <para><b>Never blocks:</b> <see cref="TryWrite"/> never waits for room — if the buffer is
/// full (the consumer isn't keeping up), the entire incoming chunk is dropped and
/// <see cref="DroppedSamples"/> is incremented, rather than overwriting unread data or
/// blocking the real-time producer. This matches item 4b's redesign requirement that "a
/// real-time producer must never block."</para>
/// </summary>
public sealed class SpscFloatRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacity;

    // Monotonically increasing total sample counts (not indices modulo capacity) — the
    // producer only ever advances _writeIndex, the consumer only ever advances _readIndex.
    // Publishing/observing the other side's index through Volatile gives the happens-before
    // relationship this SPSC design needs without a lock.
    private long _writeIndex;
    private long _readIndex;
    private long _droppedSamples;

    public SpscFloatRingBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Ring buffer capacity must be positive.");

        _capacity = capacity;
        _buffer = new float[capacity];
    }

    /// <summary>Fixed capacity, in samples, this instance was constructed with.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Total number of samples ever dropped because <see cref="TryWrite"/> was called while
    /// there wasn't enough free room. Zero on a healthy stream where the consumer keeps up.
    /// </summary>
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    /// <summary>Best-effort snapshot of whether the consumer has drained everything the producer has published so far.</summary>
    public bool IsEmpty => Volatile.Read(ref _writeIndex) == Volatile.Read(ref _readIndex);

    /// <summary>
    /// Producer-only. Bulk-copies <paramref name="data"/> into the ring buffer (one or two
    /// <see cref="Span{T}.CopyTo"/> calls, to handle wraparound — no per-sample work) and
    /// atomically publishes the new write position. If there isn't enough free room for the
    /// whole chunk, the entire chunk is dropped (not partially written) and
    /// <see cref="DroppedSamples"/> is incremented by <c>data.Length</c> — the producer never
    /// blocks and never overwrites data the consumer hasn't read yet.
    /// </summary>
    /// <returns><c>true</c> if the chunk was written, <c>false</c> if it was dropped.</returns>
    public bool TryWrite(ReadOnlySpan<float> data)
    {
        if (data.Length == 0) return true;

        long writeIdx = _writeIndex; // producer-owned; safe to read non-volatile here
        long readIdx = Volatile.Read(ref _readIndex); // published by the consumer
        long used = writeIdx - readIdx;
        long free = _capacity - used;

        if (data.Length > free)
        {
            Interlocked.Add(ref _droppedSamples, data.Length);
            return false;
        }

        int start = (int)(writeIdx % _capacity);
        int firstLen = Math.Min(data.Length, _capacity - start);
        data[..firstLen].CopyTo(_buffer.AsSpan(start));
        if (firstLen < data.Length)
            data[firstLen..].CopyTo(_buffer.AsSpan(0));

        // Publish last: any consumer that observes the new write index via Volatile.Read is
        // guaranteed (by the runtime's Volatile semantics) to also see the sample writes above.
        Volatile.Write(ref _writeIndex, writeIdx + data.Length);
        return true;
    }

    /// <summary>
    /// Consumer-only. Hands every currently-available sample to <paramref name="consume"/> as
    /// one or two contiguous <see cref="ReadOnlySpan{T}"/> segments (two only when the
    /// available data wraps past the end of the backing array), then advances the read
    /// position. Returns the number of samples drained (0 if the buffer was empty).
    /// </summary>
    public int DrainInto(FloatSpanAction consume)
    {
        ArgumentNullException.ThrowIfNull(consume);

        long readIdx = _readIndex; // consumer-owned; safe to read non-volatile here
        long writeIdx = Volatile.Read(ref _writeIndex); // published by the producer
        long avail = writeIdx - readIdx;
        if (avail <= 0) return 0;

        int start = (int)(readIdx % _capacity);
        int firstLen = (int)Math.Min(avail, _capacity - start);
        consume(_buffer.AsSpan(start, firstLen));

        long remaining = avail - firstLen;
        if (remaining > 0)
            consume(_buffer.AsSpan(0, (int)remaining));

        // Publish last, same rationale as TryWrite: the producer must not treat this space as
        // free until the consumer callback above has actually finished reading it.
        Volatile.Write(ref _readIndex, writeIdx);
        return (int)avail;
    }
}

/// <summary>
/// Callback used by <see cref="SpscFloatRingBuffer.DrainInto"/> — a plain delegate (not
/// <c>Action&lt;ReadOnlySpan&lt;float&gt;&gt;</c>) because <see cref="ReadOnlySpan{T}"/> is a
/// ref struct and can't be used as a generic type argument.
/// </summary>
public delegate void FloatSpanAction(ReadOnlySpan<float> data);
