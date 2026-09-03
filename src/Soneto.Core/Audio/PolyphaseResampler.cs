using System.Runtime.InteropServices;

namespace Soneto.Core.Audio;

/// <summary>
/// Stateful, streaming windowed-sinc polyphase resampler, per
/// <c>Docs/soneto-implementation-plan-phase0-1.md</c> §1.5 "Stream configuration and
/// resampling" (windowed-sinc, Blackman window, anti-aliasing lowpass cutoff at 7.8 kHz),
/// with the tap-count correction documented in <c>Docs/PROJECT-MEMORY.md</c> and
/// <c>spikes/s1b-audio/README.md</c>: a literal "32-tap" filter cannot physically achieve
/// the required 7.8 kHz → 8 kHz transition (its transition band would be ~8.25 kHz wide,
/// wider than the whole output Nyquist band). The tap count here is derived from the
/// actual required transition width at construction time (the same Blackman rule of
/// thumb, N ≈ 5.5·Fs/ΔF, the spike used) rather than hardcoded — ~1300 taps/phase at
/// 48 kHz input, ~1200 at 44.1 kHz.
///
/// <para><b>Why this is stateful, unlike the S1b spike's <c>Resample(wholeSignal)</c>
/// design:</b> the real pipeline (§1.5's "Buffer" paragraph) calls the resampler once per
/// ~512-frame native-rate audio callback buffer, appending each buffer's 16 kHz output
/// into a growing list for the whole utterance. A stateless, whole-buffer resampler that
/// zero-pads its filter's support window at every call boundary — which is exactly what
/// the spike's <c>Convolve()</c> did — would repeat the spike's documented ~14ms
/// edge-taper artifact at <i>every</i> buffer boundary within a single utterance (every
/// ~10-11ms of audio at 48 kHz), not just once. That would be a real, audible, repeating
/// quality problem baked into every dictation, not a one-time edge case.</para>
///
/// <para>Instead, this class maintains a small internal history buffer (the filter's
/// support window, ~<see cref="TapsPerPhase"/> samples) across calls to
/// <see cref="Resample"/>, and only emits an output sample once every input sample its
/// convolution touches has actually arrived. Concatenating the outputs of any sequence of
/// <see cref="Resample"/> calls therefore produces output bit-identical to a single
/// one-shot call on the concatenation of all those chunks — no artifact at internal chunk
/// boundaries. The only place zero-padding (and the resulting ~14ms edge-taper) can occur
/// is the true start and true end of the whole stream — exactly once per utterance, via
/// <see cref="Flush"/> — which is the same, unavoidable edge behaviour any finite-length
/// FIR filter has, just no longer repeated internally.</para>
///
/// <para><b>Thread-safety:</b> this class is stateful and NOT thread-safe. All of a given
/// instance's <see cref="Resample"/>/<see cref="Flush"/>/<see cref="Reset"/> calls must
/// come from a single thread (the audio capture callback thread, per the eventual §1.5/item
/// 4b wiring) — there is no internal locking, so sharing one instance across threads would
/// corrupt its streaming state.</para>
/// </summary>
public sealed class PolyphaseResampler
{
    /// <summary>Sub-sample phase resolution of the polyphase table (fixed, independent of the input:output ratio).</summary>
    private const int PhaseCount = 32;

    private readonly int _inRate;
    private readonly int _outRate;
    private readonly double _ratio; // inRate / outRate
    private readonly bool _passthrough;
    private readonly int _tapsPerPhase; // support width, in input samples
    private readonly int _center;
    private readonly float[][] _phases; // [PhaseCount][_tapsPerPhase]

    // Streaming state.
    private readonly List<float> _buffer = [];
    private long _bufferStartIndex; // global input index of _buffer[0]
    private long _totalInputReceived; // total input samples ever appended
    private long _nextOutputIndex; // next output sample index (global, across the whole stream) to produce

    /// <summary>
    /// Constructs a resampler for one input sample rate. Construct once per native device
    /// rate and reuse across an utterance's buffers via repeated <see cref="Resample"/>
    /// calls; call <see cref="Reset"/> (or just use <see cref="Flush"/>, which resets
    /// automatically) between utterances.
    /// </summary>
    public PolyphaseResampler(int inputRate, int outputRate = 16000)
    {
        if (inputRate <= 0 || outputRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputRate), "Sample rates must be positive.");

        _inRate = inputRate;
        _outRate = outputRate;
        _ratio = (double)inputRate / outputRate;
        _passthrough = inputRate == outputRate;

        if (_passthrough)
        {
            _tapsPerPhase = 0;
            _center = 0;
            _phases = [];
            return;
        }

        double outNyquist = outputRate / 2.0;
        double cutoffHz = Math.Min(7800.0, outNyquist - 200.0);
        double transitionWidthHz = outNyquist - cutoffHz;
        if (transitionWidthHz <= 0) transitionWidthHz = outNyquist * 0.05;
        if (cutoffHz <= 0) cutoffHz = outNyquist * 0.9;

        // Also respect the input Nyquist if it's the tighter constraint (e.g. upsampling
        // from a very low rate).
        cutoffHz = Math.Min(cutoffHz, inputRate / 2.0 - transitionWidthHz);

        // Recompute the transition width from the (possibly clamped) cutoff before deriving
        // the tap count: when the input-Nyquist clamp above actually engages (upsampling
        // from a low input rate), cutoffHz can be pulled down well below its pre-clamp
        // value. The real available transition band always runs from cutoffHz up to
        // whichever Nyquist is tighter (input or output) — using the stale, pre-clamp
        // transitionWidthHz (measured against outNyquist alone) here would tell the
        // Blackman-rule formula it has more room than it actually does whenever the input
        // Nyquist is the binding constraint, under-provisioning _tapsPerPhase for the real,
        // narrower transition band that was just carved out.
        double tighterNyquist = Math.Min(outNyquist, inputRate / 2.0);
        transitionWidthHz = tighterNyquist - cutoffHz;
        if (transitionWidthHz <= 0) transitionWidthHz = tighterNyquist * 0.05;

        // Blackman-window rule of thumb: N ~= 5.5 * Fs / transitionWidthHz for the support
        // of the filter expressed in units of the rate at which its taps are naturally
        // spaced (here: the input sample rate). Derived, not hardcoded — see class doc.
        _tapsPerPhase = (int)Math.Ceiling(5.5 * inputRate / transitionWidthHz);
        _tapsPerPhase = Math.Clamp(_tapsPerPhase, 33, 20000);
        if (_tapsPerPhase % 2 == 0) _tapsPerPhase += 1;
        _center = _tapsPerPhase / 2;

        _phases = BuildPolyphaseTable(_tapsPerPhase, PhaseCount, cutoffHz, inputRate);
    }

    /// <summary>Total support of the filter, in taps at the input-sample spacing. Exposed for logging/tests.</summary>
    public int TapsPerPhase => _tapsPerPhase;

    /// <summary>Input sample rate this instance was constructed for.</summary>
    public int InputRate => _inRate;

    /// <summary>Output sample rate this instance was constructed for.</summary>
    public int OutputRate => _outRate;

    private static float[][] BuildPolyphaseTable(int tapsPerPhase, int phaseCount, double cutoffHz, int inRate)
    {
        // Full prototype filter, sampled at (inRate * phaseCount), i.e. one sample per
        // polyphase sub-position. Length chosen so each phase gets exactly `tapsPerPhase`
        // taps.
        int fullLen = tapsPerPhase * phaseCount;
        double fOpRate = inRate * (double)phaseCount;
        double fc = cutoffHz / fOpRate; // normalized cutoff, cycles/sample at the operating rate
        int fullCenter = (fullLen - 1) / 2;

        var proto = new double[fullLen];
        for (int n = 0; n < fullLen; n++)
        {
            int k = n - fullCenter;
            double sinc = k == 0 ? 2.0 * fc : Math.Sin(2.0 * Math.PI * fc * k) / (Math.PI * k);
            double w = 0.42 - 0.5 * Math.Cos(2.0 * Math.PI * n / (fullLen - 1))
                             + 0.08 * Math.Cos(4.0 * Math.PI * n / (fullLen - 1));
            proto[n] = sinc * w;
        }

        // Decompose into `phaseCount` phases: phase[p][k] = proto[p + k*phaseCount].
        var phases = new float[phaseCount][];
        for (int p = 0; p < phaseCount; p++)
        {
            var branch = new double[tapsPerPhase];
            double sum = 0;
            for (int k = 0; k < tapsPerPhase; k++)
            {
                int idx = p + k * phaseCount;
                double v = idx < fullLen ? proto[idx] : 0.0;
                branch[k] = v;
                sum += v;
            }

            // Normalize each phase to unity DC gain independently, so the interpolated
            // output has flat gain across all fractional positions (each phase is itself a
            // valid unity-gain lowpass).
            var f = new float[tapsPerPhase];
            double scale = sum != 0 ? 1.0 / sum : 1.0;
            for (int k = 0; k < tapsPerPhase; k++)
                f[k] = (float)(branch[k] * scale);
            phases[p] = f;
        }

        return phases;
    }

    /// <summary>
    /// Resamples one chunk of a streaming utterance. Maintains internal filter-history
    /// state across calls: only emits an output sample once every input sample its
    /// convolution window touches has arrived, so no zero-padding (and therefore no
    /// edge-taper artifact) occurs at chunk boundaries — only at the true start/end of the
    /// whole stream, the latter via <see cref="Flush"/>. Safe to call with any chunk
    /// length, including lengths that don't evenly divide the resampling ratio.
    ///
    /// <para>Allocates and returns a fresh array every call. For the real-time capture path
    /// (called once per ~10ms native audio buffer, potentially thousands of times over a
    /// long dictation per §1.13's allocation-churn concern), prefer
    /// <see cref="Resample(ReadOnlySpan{float}, List{float})"/>, which appends into a
    /// caller-owned, reusable buffer instead.</para>
    /// </summary>
    public float[] Resample(ReadOnlySpan<float> inputChunk)
    {
        if (_passthrough)
            return inputChunk.ToArray();

        // Pre-size to the expected output count (rounded up, plus one for rounding slop) so
        // the list doesn't repeatedly regrow/copy internally while filling.
        int expectedOutputCount = (int)Math.Ceiling(inputChunk.Length / _ratio) + 1;
        var outputs = new List<float>(Math.Max(expectedOutputCount, 4));
        ResampleCore(inputChunk, outputs);
        return outputs.ToArray();
    }

    /// <summary>
    /// Same streaming resample as <see cref="Resample(ReadOnlySpan{float})"/>, but appends
    /// output samples to the caller-supplied <paramref name="output"/> list instead of
    /// allocating and returning a new array on every call. Intended for the real-time
    /// capture path: call this once per native audio buffer with a single reused
    /// <see cref="List{T}"/> (cleared by the caller between utterances) to avoid the
    /// allocation churn a long dictation would otherwise incur via the array-returning
    /// overload, per §1.13.
    /// </summary>
    public void Resample(ReadOnlySpan<float> inputChunk, List<float> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (_passthrough)
        {
            int start = output.Count;
            CollectionsMarshal.SetCount(output, start + inputChunk.Length);
            inputChunk.CopyTo(CollectionsMarshal.AsSpan(output)[start..]);
            return;
        }

        ResampleCore(inputChunk, output);
    }

    private void ResampleCore(ReadOnlySpan<float> inputChunk, List<float> outputs)
    {
        int bufOldCount = _buffer.Count;
        CollectionsMarshal.SetCount(_buffer, bufOldCount + inputChunk.Length);
        inputChunk.CopyTo(CollectionsMarshal.AsSpan(_buffer)[bufOldCount..]);
        _totalInputReceived += inputChunk.Length;

        while (true)
        {
            double t = _nextOutputIndex * _ratio;
            int i0 = (int)Math.Floor(t);
            long rightmostNeeded = (long)i0 - _center + _tapsPerPhase - 1;

            // Not enough future data yet to compute this output without zero-padding —
            // wait for the next chunk. (This is the causal streaming design: we defer
            // rather than zero-pad, so chunk boundaries never taper.)
            if (rightmostNeeded > _totalInputReceived - 1)
                break;

            double frac = t - i0;
            double posInPhaseSpace = frac * PhaseCount;
            int p0 = (int)Math.Floor(posInPhaseSpace);
            int p1 = Math.Min(p0 + 1, PhaseCount - 1);
            double wgt = posInPhaseSpace - p0;
            if (p0 >= PhaseCount) p0 = PhaseCount - 1;

            long start = (long)i0 - _center;
            double v0 = ConvolveGlobal(_phases[p0], start);
            double v1 = ConvolveGlobal(_phases[p1], start);
            outputs.Add((float)(v0 * (1 - wgt) + v1 * wgt));

            _nextOutputIndex++;
        }

        TrimBuffer();
    }

    /// <summary>
    /// Drains any remaining buffered input at the end of an utterance, zero-padding the
    /// filter's support window past the true end of the stream (the one place, along with
    /// the true start, where the ~14ms edge-taper documented in
    /// <c>Docs/PROJECT-MEMORY.md</c> is expected and unavoidable). Resets internal
    /// streaming state afterward so the instance is ready to be reused for the next
    /// utterance without rebuilding the (expensive-ish, one-time) filter table.
    /// </summary>
    public float[] Flush()
    {
        if (_passthrough)
            return [];

        long outLen = (long)(_totalInputReceived / _ratio);
        var outputs = new List<float>();

        while (_nextOutputIndex < outLen)
        {
            double t = _nextOutputIndex * _ratio;
            int i0 = (int)Math.Floor(t);
            double frac = t - i0;
            double posInPhaseSpace = frac * PhaseCount;
            int p0 = (int)Math.Floor(posInPhaseSpace);
            int p1 = Math.Min(p0 + 1, PhaseCount - 1);
            double wgt = posInPhaseSpace - p0;
            if (p0 >= PhaseCount) p0 = PhaseCount - 1;

            long start = (long)i0 - _center;
            double v0 = ConvolveGlobal(_phases[p0], start);
            double v1 = ConvolveGlobal(_phases[p1], start);
            outputs.Add((float)(v0 * (1 - wgt) + v1 * wgt));

            _nextOutputIndex++;
        }

        Reset();
        return outputs.ToArray();
    }

    /// <summary>
    /// Discards all internal streaming state (history buffer, output/input position
    /// counters) without producing any final output — use when a capture is aborted
    /// rather than finalized. The precomputed filter table is kept, so the instance can be
    /// reused immediately for the next utterance. <see cref="Flush"/> calls this
    /// internally after draining, so it does not need to be called after a normal
    /// end-of-utterance <see cref="Flush"/>.
    /// </summary>
    public void Reset()
    {
        _buffer.Clear();
        _bufferStartIndex = 0;
        _totalInputReceived = 0;
        _nextOutputIndex = 0;
    }

    /// <summary>
    /// Convolves the given phase filter against the internal history buffer, starting at
    /// global input index <paramref name="globalStart"/>. Any referenced index outside the
    /// currently buffered range (before the true start of the stream, or — during
    /// <see cref="Flush"/> — after the true end) is treated as zero, matching the
    /// zero-padding behaviour of a finite-length FIR filter at a real signal boundary.
    /// </summary>
    private double ConvolveGlobal(float[] phase, long globalStart)
    {
        double acc = 0.0;
        for (int k = 0; k < phase.Length; k++)
        {
            long g = globalStart + k;
            long idx = g - _bufferStartIndex;
            if (idx >= 0 && idx < _buffer.Count)
                acc += phase[k] * _buffer[(int)idx];
        }
        return acc;
    }

    /// <summary>
    /// Drops history no longer needed by any future output — bounds the internal buffer to
    /// roughly the filter's support width plus one chunk, regardless of total utterance
    /// length.
    /// </summary>
    private void TrimBuffer()
    {
        long i0Next = (long)Math.Floor(_nextOutputIndex * _ratio);
        long neededStart = Math.Max(0, i0Next - _center);
        long removable = neededStart - _bufferStartIndex;
        if (removable <= 0) return;

        int removeCount = (int)Math.Min(removable, _buffer.Count);
        if (removeCount <= 0) return;

        _buffer.RemoveRange(0, removeCount);
        _bufferStartIndex += removeCount;
    }
}
