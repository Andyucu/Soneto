namespace Soneto.Core.Audio;

/// <summary>
/// Fixed-capacity circular buffer of 16 kHz mono float samples that continuously records the
/// most recent <c>preRollMs</c> of audio while a <c>WarmIdle</c>/<c>AlwaysOn</c> stream is
/// open but no utterance is currently being captured — plan §1.5's "Full [pre-roll], from
/// the 2nd utterance of a burst onward."
///
/// <para><b>Not thread-safe by itself.</b> <see cref="PortAudioCapture"/> is the only
/// intended owner of an instance, and guards every access to it with the same
/// <c>_sync</c> lock that class already uses between its non-real-time consumer thread and
/// non-real-time callers (<c>BeginCapture</c>/<c>EndCapture</c>/<c>AbortCapture</c>) — see
/// that class's doc comment. This is deliberately just another field guarded by the
/// existing lock, not a new contention point, and definitely never touched from the
/// PortAudio real-time callback thread.</para>
/// </summary>
public sealed class PreRollRingBuffer
{
    private readonly float[] _buffer;
    private int _writePos;
    private int _count;

    public PreRollRingBuffer(int capacitySamples)
    {
        if (capacitySamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacitySamples), "Capacity must be positive.");
        _buffer = new float[capacitySamples];
    }

    /// <summary>Fixed capacity, in samples, this instance was constructed with.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Samples currently held (always &lt;= <see cref="Capacity"/>).</summary>
    public int Count => _count;

    /// <summary>
    /// Appends new samples, overwriting the oldest ones once <see cref="Capacity"/> is
    /// exceeded (a real ring: no resize, no allocation beyond the fixed backing array). If
    /// <paramref name="samples"/> alone is longer than <see cref="Capacity"/>, only its
    /// newest <see cref="Capacity"/> samples are kept.
    /// </summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return;

        if (samples.Length >= _buffer.Length)
        {
            samples[^_buffer.Length..].CopyTo(_buffer);
            _writePos = 0;
            _count = _buffer.Length;
            return;
        }

        int firstLen = Math.Min(samples.Length, _buffer.Length - _writePos);
        samples[..firstLen].CopyTo(_buffer.AsSpan(_writePos));
        if (firstLen < samples.Length)
            samples[firstLen..].CopyTo(_buffer.AsSpan(0));

        _writePos = (_writePos + samples.Length) % _buffer.Length;
        _count = Math.Min(_count + samples.Length, _buffer.Length);
    }

    /// <summary>
    /// Returns the most recent <c>min(maxSamples, Count)</c> samples, oldest-first — ready to
    /// be prepended in front of a freshly started capture's own samples via
    /// <c>List&lt;float&gt;.AddRange</c>. Returns an empty array if nothing is buffered yet or
    /// <paramref name="maxSamples"/> is zero/negative.
    /// </summary>
    public float[] Snapshot(int maxSamples)
    {
        int take = Math.Min(Math.Max(maxSamples, 0), _count);
        if (take == 0) return [];

        var result = new float[take];

        // The newest sample lives just behind _writePos; the oldest sample we want to keep
        // starts `take` samples before that, wrapping around the backing array as needed.
        int start = ((_writePos - take) % _buffer.Length + _buffer.Length) % _buffer.Length;
        int firstLen = Math.Min(take, _buffer.Length - start);
        _buffer.AsSpan(start, firstLen).CopyTo(result);
        if (firstLen < take)
            _buffer.AsSpan(0, take - firstLen).CopyTo(result.AsSpan(firstLen));

        return result;
    }

    /// <summary>Discards all buffered samples without releasing the backing array.</summary>
    public void Clear()
    {
        _writePos = 0;
        _count = 0;
    }
}
