using System;

namespace s1b_audio;

/// <summary>
/// Windowed-sinc polyphase resampler, per
/// Docs/soneto-implementation-plan-phase0-1.md §1.5 "Stream configuration and
/// resampling" (windowed-sinc, anti-aliasing lowpass cutoff at 7.8 kHz).
///
/// <para><b>Deviation from the plan's literal "32-tap" figure, documented
/// here and in spikes/s1b-audio/README.md:</b> a 32-tap windowed-sinc filter
/// cannot physically achieve the transition band the plan implies — a 7.8 kHz
/// cutoff feeding an 8 kHz output Nyquist is only a 200 Hz transition width,
/// and at 48 kHz input that requires on the order of ~1300 taps (Blackman
/// rule of thumb, N ≈ 5.5·Fs/BW) to get meaningful stopband attenuation, not
/// 32. A first implementation using literally 32 taps was measured against
/// the sweep test below and failed outright (near-zero attenuation at the
/// 7.8-8 kHz edge — see git history / session notes). The tap count here is
/// therefore computed from the actual required transition width instead of
/// hardcoded, while keeping the same design family (windowed-sinc, Blackman
/// window, polyphase table) the plan asks for.</para>
///
/// <para>Structure: a fixed-resolution polyphase table (<see cref="PhaseCount"/>
/// sub-sample phases) is precomputed once, sized so each phase's own filter
/// length matches the tap count actually required for the target
/// attenuation — this keeps the per-output-sample cost roughly constant
/// regardless of how "awkward" the exact input/output rate ratio is (e.g.
/// 44100:16000, which reduces to 441:160 and would otherwise demand a
/// filter with a polyphase branch count in the hundreds if built from the
/// exact rational reduction). Output samples that fall between two table
/// phases are linearly interpolated between the two nearest phase filters.</para>
/// </summary>
public sealed class PolyphaseResampler
{
    /// <summary>Sub-sample phase resolution of the polyphase table (fixed, independent of the input:output ratio).</summary>
    private const int PhaseCount = 32;

    /// <summary>Target stopband attenuation used to size the filter, in dB.</summary>
    private const double TargetAttenuationDb = 60.0;

    private readonly int _inRate;
    private readonly int _outRate;
    private readonly int _tapsPerPhase; // support width, in input samples
    private readonly float[][] _phases; // [PhaseCount][_tapsPerPhase]
    private readonly int _center;

    public PolyphaseResampler(int inRate, int outRate)
    {
        if (inRate <= 0 || outRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(inRate), "Sample rates must be positive.");

        _inRate = inRate;
        _outRate = outRate;

        double outNyquist = outRate / 2.0;
        double cutoffHz = Math.Min(7800.0, outNyquist - 200.0);
        double transitionWidthHz = outNyquist - cutoffHz;
        if (transitionWidthHz <= 0) transitionWidthHz = outNyquist * 0.05;
        if (cutoffHz <= 0) cutoffHz = outNyquist * 0.9;

        // Also respect the input Nyquist if it's the tighter constraint
        // (e.g. upsampling from a very low rate).
        cutoffHz = Math.Min(cutoffHz, inRate / 2.0 - transitionWidthHz);

        // Blackman-window rule of thumb: N ~= 5.5 * Fs / transitionWidthHz for
        // the support of the filter expressed in units of the rate at which
        // its taps are naturally spaced (here: the input sample rate).
        _tapsPerPhase = (int)Math.Ceiling(5.5 * inRate / transitionWidthHz);
        _tapsPerPhase = Math.Clamp(_tapsPerPhase, 33, 20000);
        if (_tapsPerPhase % 2 == 0) _tapsPerPhase += 1;
        _center = _tapsPerPhase / 2;

        _phases = BuildPolyphaseTable(_tapsPerPhase, PhaseCount, cutoffHz, inRate);
    }

    /// <summary>Total support of the filter, in taps, at the (fixed) input-sample spacing. Exposed for logging/README purposes.</summary>
    public int TapsPerPhase => _tapsPerPhase;

    private static float[][] BuildPolyphaseTable(int tapsPerPhase, int phaseCount, double cutoffHz, int inRate)
    {
        // Full prototype filter, sampled at (inRate * phaseCount), i.e. one
        // sample per polyphase sub-position. Length chosen so each phase
        // gets exactly `tapsPerPhase` taps.
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

            // Normalize each phase to unity DC gain independently, so the
            // interpolated output has flat gain across all fractional
            // positions (each phase is itself a valid unity-gain lowpass).
            var f = new float[tapsPerPhase];
            double scale = sum != 0 ? 1.0 / sum : 1.0;
            for (int k = 0; k < tapsPerPhase; k++)
                f[k] = (float)(branch[k] * scale);
            phases[p] = f;
        }

        return phases;
    }

    /// <summary>
    /// Resample the full buffer (offline, non-streaming — fine for a spike
    /// and for the fixed-length capture buffer described in §1.5).
    /// </summary>
    public float[] Resample(ReadOnlySpan<float> input)
    {
        if (_inRate == _outRate)
            return input.ToArray();

        int inLen = input.Length;
        double ratio = (double)_inRate / _outRate;
        long outLen = (long)(inLen / ratio);

        var output = new float[outLen];

        for (long n = 0; n < outLen; n++)
        {
            double t = n * ratio; // fractional position in input-sample index space
            int i0 = (int)Math.Floor(t);
            double frac = t - i0;

            double posInPhaseSpace = frac * PhaseCount;
            int p0 = (int)Math.Floor(posInPhaseSpace);
            int p1 = Math.Min(p0 + 1, PhaseCount - 1);
            double wgt = posInPhaseSpace - p0;
            if (p0 >= PhaseCount) p0 = PhaseCount - 1;

            double v0 = Convolve(_phases[p0], input, i0);
            double v1 = Convolve(_phases[p1], input, i0);
            output[n] = (float)(v0 * (1 - wgt) + v1 * wgt);
        }

        return output;
    }

    private double Convolve(float[] phase, ReadOnlySpan<float> input, int i0)
    {
        double acc = 0.0;
        int start = i0 - _center;
        int inLen = input.Length;
        for (int k = 0; k < phase.Length; k++)
        {
            int idx = start + k;
            if ((uint)idx < (uint)inLen)
                acc += phase[k] * input[idx];
        }
        return acc;
    }
}
