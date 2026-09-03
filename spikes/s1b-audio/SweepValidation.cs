using System;
using System.Linq;

namespace s1b_audio;

public sealed record SweepResult(
    double EarlySegmentRmsDbFs,
    double LateSegmentRmsDbFs,
    double SuppressionDb,
    double PeakNearNyquistDb,
    bool Pass,
    string Details);

/// <summary>
/// Implements the S1b sweep test from the plan:
///
///   "generate a 48 kHz sweep from 100 Hz to 20 kHz, resample to 16 kHz, FFT
///   the result, confirm no aliased energy above 8 kHz."
///
/// <para><b>Why a plain single full-signal FFT isn't sufficient, and what
/// this actually measures instead:</b> after correct resampling to 16 kHz,
/// a real signal's DFT physically cannot show content "above 8 kHz" (that
/// *is* the new Nyquist) — aliased energy from the original 8-20 kHz sweep
/// content doesn't disappear, it folds back down and shows up *within* the
/// 0-8 kHz band, indistinguishable in a plain magnitude spectrum from
/// legitimate low-frequency chirp content. A single whole-signal FFT alone
/// can't tell "clean chirp energy near 3 kHz" apart from "8-20 kHz content
/// that aliased down to 3 kHz because the anti-aliasing lowpass didn't do
/// its job".</para>
///
/// <para>The decisive test used here is time-segmented instead: a linear
/// chirp's instantaneous frequency at time t is known exactly
/// (<c>f0 + k*t</c>). Split the resampled output into an "early" segment
/// where the true input frequency is safely in-band (100 Hz - 6 kHz) and a
/// "late" segment where the true input frequency is safely above the 16 kHz
/// output's Nyquist with margin (9 kHz - 20 kHz, well past the 7.8 kHz
/// anti-alias cutoff). If the anti-aliasing lowpass is doing its job, the
/// resampled output should be near-silent during the late segment — there's
/// nothing left there to decimate. If the lowpass is missing or too weak
/// (the classic "naive sample-dropping" bug §1.5 warns about), the late
/// segment's 8-20 kHz energy aliases straight back into the output band and
/// shows up as RMS energy comparable to the early segment. Pass criterion:
/// late-segment RMS at least 40 dB below early-segment RMS.</para>
///
/// <para>A full-signal FFT is still computed and reported for documentation
/// (visualizing the passband/stopband shape), but the pass/fail gate is the
/// segmented RMS suppression test above, which is the one that actually
/// distinguishes "filtered" from "aliased".</para>
/// </summary>
public static class SweepValidation
{
    private const double F0 = 100.0;
    private const double F1 = 20000.0;

    public static float[] GenerateChirp(int sampleRate, double durationSec, double f0, double f1)
    {
        int n = (int)(sampleRate * durationSec);
        var x = new float[n];
        double k = (f1 - f0) / durationSec; // linear chirp rate, Hz/s
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sampleRate;
            // Instantaneous phase of a linear chirp: 2*pi*(f0*t + k*t^2/2)
            double phase = 2 * Math.PI * (f0 * t + k * t * t / 2.0);
            x[i] = (float)Math.Sin(phase);
        }
        return x;
    }

    public static SweepResult Validate(int inRate, int outRate, double durationSec = 2.0)
    {
        var chirp = GenerateChirp(inRate, durationSec, F0, F1);
        var resampler = new PolyphaseResampler(inRate, outRate);
        var resampled = resampler.Resample(chirp);

        double k = (F1 - F0) / durationSec;
        // Time at which the true instantaneous frequency crosses a given Hz value.
        double TimeAtFreq(double f) => (f - F0) / k;

        // Edge-taper margin: Convolve() zero-pads once the filter's support
        // window (± TapsPerPhase/2 input samples) extends past the start/end
        // of the input buffer, so the first/last `edgeMarginSec` of any
        // whole-buffer Resample() output is filtered against truncated
        // (zero) context rather than real samples -- see README "known
        // limitation". Derived from the actual filter support instead of a
        // hardcoded constant so it stays correct if the tap-sizing rule
        // above changes; asserted (not just assumed) to be comfortably
        // inside this test's segment boundaries below.
        double edgeMarginSec = (resampler.TapsPerPhase / 2.0) / inRate;

        double earlyStart = TimeAtFreq(F0 + 200); // skip the very start (ramp-up transients)
        double earlyEnd = TimeAtFreq(6000);
        double lateStart = TimeAtFreq(9000); // comfortable margin above 7.8kHz cutoff / 8kHz Nyquist
        double lateEnd = durationSec - edgeMarginSec;

        if (lateEnd <= lateStart)
        {
            throw new InvalidOperationException(
                $"Sweep test duration ({durationSec}s) is too short for the filter's edge-taper " +
                $"margin ({edgeMarginSec * 1000:F1}ms) to leave a valid late segment " +
                $"[{lateStart:F3}-{lateEnd:F3}]s -- increase durationSec.");
        }

        double outRateD = outRate;
        double earlyRms = SegmentRms(resampled, outRateD, earlyStart, earlyEnd);
        double lateRms = SegmentRms(resampled, outRateD, lateStart, lateEnd);

        double earlyDbFs = 20 * Math.Log10(Math.Max(earlyRms, 1e-12));
        double lateDbFs = 20 * Math.Log10(Math.Max(lateRms, 1e-12));
        double suppressionDb = lateDbFs - earlyDbFs; // negative = good (late segment quieter)

        bool pass = suppressionDb < -40.0;

        // Documentation-only full-signal FFT: report the peak magnitude in
        // the 7.8-8kHz transition/stop edge relative to the peak magnitude
        // in the clean low-frequency band, just to show the passband shape.
        var mag = Fft.MagnitudeSpectrum(resampled, out int fftSize, out double binHz, outRate);
        double refPeak = PeakInRange(mag, binHz, 0, 3000);
        double peakNearNyquist = PeakInRange(mag, binHz, 7800, outRate / 2.0);
        double peakNearNyquistDb = 20 * Math.Log10(Math.Max(peakNearNyquist, 1e-12) / Math.Max(refPeak, 1e-12));

        string details =
            $"earlySegment[{earlyStart:F3}-{earlyEnd:F3}s]RMS={earlyDbFs:F1}dBFS, " +
            $"lateSegment[{lateStart:F3}-{lateEnd:F3}s]RMS={lateDbFs:F1}dBFS, " +
            $"suppression={suppressionDb:F1}dB (pass < -40dB), " +
            $"fyi-fullSpectrumPeakNear7.8-8kHz={peakNearNyquistDb:F1}dB rel. 0-3kHz peak " +
            $"(expected to be near 0dB -- the chirp legitimately passes through ~7-7.8kHz around " +
            $"t={TimeAtFreq(7800):F2}s before the lowpass rolls it off; this is NOT the pass gate, see suppression above), " +
            $"tapsPerPhase={resampler.TapsPerPhase}, fftSize={fftSize}, binHz={binHz:F2}";

        return new SweepResult(earlyDbFs, lateDbFs, suppressionDb, peakNearNyquistDb, pass, details);
    }

    /// <summary>
    /// Length-correctness check for §1.13's "length correctness (off-by-one
    /// at buffer boundaries is the classic bug here)" resampler test
    /// requirement. The sweep test above deliberately routes around the
    /// first/last ~14ms of every signal to isolate the aliasing question
    /// (see <c>edgeMarginSec</c> in <see cref="Validate"/>), so it exercises
    /// nothing about exact output length -- this does, for both the exact
    /// (3:1, 48000-&gt;16000) and awkward (147:160, 44100-&gt;16000) ratios,
    /// at and either side of whole-second input-length boundaries, which is
    /// where off-by-one bugs classically hide.
    /// </summary>
    public static (bool Pass, string Details) ValidateOutputLengths()
    {
        var cases = new (int inRate, int outRate)[] { (48000, 16000), (44100, 16000) };
        var details = new System.Text.StringBuilder();
        bool allPass = true;

        foreach (var (inRate, outRate) in cases)
        {
            var resampler = new PolyphaseResampler(inRate, outRate);
            double ratio = (double)inRate / outRate;

            foreach (int seconds in new[] { 1, 2, 3 })
            {
                int baseLen = inRate * seconds;
                foreach (int inLen in new[] { baseLen - 1, baseLen, baseLen + 1 })
                {
                    var output = resampler.Resample(new float[inLen]);
                    long expected = (long)(inLen / ratio);
                    bool ok = output.Length == expected;
                    allPass &= ok;
                    if (!ok)
                        details.Append($"[MISMATCH {inRate}->{outRate} inLen={inLen} expected={expected} actual={output.Length}] ");
                }
            }
        }

        details.Append(allPass
            ? "all inLen/expected-outLen pairs matched exactly for both ratios, incl. whole-second +-1 sample boundaries."
            : "one or more length mismatches -- see above.");

        return (allPass, details.ToString());
    }

    private static double SegmentRms(float[] signal, double sampleRate, double startSec, double endSec)
    {
        int start = Math.Max(0, (int)(startSec * sampleRate));
        int end = Math.Min(signal.Length, (int)(endSec * sampleRate));
        if (end <= start) return 0.0;

        double sumSq = 0;
        for (int i = start; i < end; i++)
            sumSq += (double)signal[i] * signal[i];

        return Math.Sqrt(sumSq / (end - start));
    }

    private static double PeakInRange(double[] mag, double binHz, double loHz, double hiHz)
    {
        double peak = 0;
        for (int i = 0; i < mag.Length; i++)
        {
            double freq = i * binHz;
            if (freq >= loHz && freq <= hiHz)
                peak = Math.Max(peak, mag[i]);
        }
        return peak;
    }
}
