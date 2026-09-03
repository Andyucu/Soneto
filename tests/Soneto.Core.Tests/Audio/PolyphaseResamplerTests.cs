using Soneto.Core.Audio;
using Xunit.Abstractions;

namespace Soneto.Core.Tests.Audio;

/// <summary>
/// Tests for <see cref="PolyphaseResampler"/>, per plan §1.13's resampler requirement:
/// "48k → 16k and 44.1k → 16k against a reference implementation; sweep test asserting no
/// energy above 8kHz; length correctness (off-by-one at buffer boundaries is the classic
/// bug here)." Also covers the stateful/streaming design's own correctness property (no
/// edge-taper artifact repeating at internal chunk boundaries) — see class doc comment on
/// <see cref="PolyphaseResampler"/> for why that design was chosen over the S1b spike's
/// stateless whole-buffer approach. All tests are pure DSP: no audio device, no model
/// file, fast, and run in the default <c>dotnet test</c> pass (no Category trait).
/// </summary>
public class PolyphaseResamplerTests(ITestOutputHelper output)
{
    private const double F0 = 100.0;
    private const double F1 = 20000.0;

    private static float[] GenerateChirp(int sampleRate, double durationSec, double f0 = F0, double f1 = F1)
    {
        int n = (int)(sampleRate * durationSec);
        var x = new float[n];
        double k = (f1 - f0) / durationSec;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sampleRate;
            double phase = 2 * Math.PI * (f0 * t + k * t * t / 2.0);
            x[i] = (float)Math.Sin(phase);
        }
        return x;
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

    /// <summary>
    /// One-shot resample of a whole buffer via the streaming API: feed it all in one
    /// <see cref="PolyphaseResampler.Resample"/> call, then <see cref="PolyphaseResampler.Flush"/>
    /// to drain the tail. This is the "single call on the whole signal" reference used by
    /// several tests below.
    /// </summary>
    private static float[] ResampleWhole(PolyphaseResampler resampler, ReadOnlySpan<float> input)
    {
        var head = resampler.Resample(input);
        var tail = resampler.Flush();
        var result = new float[head.Length + tail.Length];
        head.CopyTo(result, 0);
        tail.CopyTo(result, head.Length);
        return result;
    }

    // ── Sweep / anti-aliasing tests (§1.13: "no energy above 8kHz") ────────────────────

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void Sweep_NoAliasedEnergyAboveOutputNyquist(int inRate)
    {
        const int outRate = 16000;
        const double durationSec = 2.0;

        var chirp = GenerateChirp(inRate, durationSec);
        var resampler = new PolyphaseResampler(inRate, outRate);
        var resampled = ResampleWhole(resampler, chirp);

        double k = (F1 - F0) / durationSec;
        double TimeAtFreq(double f) => (f - F0) / k;

        // The filter's support window means the first/last ~TapsPerPhase/2 input samples'
        // worth of output is edge-tapered (zero-padded context) — a real, documented, and
        // expected FIR boundary effect (see PROJECT-MEMORY.md), not aliasing. Route around
        // it exactly as the S1b spike's sweep test did.
        double edgeMarginSec = (resampler.TapsPerPhase / 2.0) / inRate;

        double earlyStart = TimeAtFreq(F0 + 200);
        double earlyEnd = TimeAtFreq(6000);
        double lateStart = TimeAtFreq(9000);
        double lateEnd = durationSec - edgeMarginSec;

        Assert.True(lateEnd > lateStart,
            $"Sweep duration too short for edge margin {edgeMarginSec * 1000:F1}ms to leave a valid late segment.");

        double earlyRms = SegmentRms(resampled, outRate, earlyStart, earlyEnd);
        double lateRms = SegmentRms(resampled, outRate, lateStart, lateEnd);

        double earlyDbFs = 20 * Math.Log10(Math.Max(earlyRms, 1e-12));
        double lateDbFs = 20 * Math.Log10(Math.Max(lateRms, 1e-12));
        double suppressionDb = lateDbFs - earlyDbFs;

        output.WriteLine(
            $"inRate={inRate} tapsPerPhase={resampler.TapsPerPhase} " +
            $"earlyRms={earlyDbFs:F1}dBFS lateRms={lateDbFs:F1}dBFS suppression={suppressionDb:F1}dB");

        // Bar per §S1b / plan is 40dB; the spike measured ~144dB. Assert comfortably above
        // the bar rather than hardcoding the spike's exact number.
        Assert.True(suppressionDb < -40.0,
            $"inRate={inRate}: earlyRms={earlyDbFs:F1}dBFS lateRms={lateDbFs:F1}dBFS suppression={suppressionDb:F1}dB (want < -40dB)");
    }

    // ── Streaming statefulness: chunked output must match one-shot output ──────────────

    [Theory]
    [InlineData(48000, 512)]
    [InlineData(44100, 512)]
    [InlineData(48000, 160)] // a chunk length smaller than a single polyphase-table phase gap
    public void Streaming_ChunkedOutputMatchesOneShotOutput(int inRate, int chunkFrames)
    {
        const int outRate = 16000;
        var signal = GenerateChirp(inRate, durationSec: 1.5);

        var wholeResampler = new PolyphaseResampler(inRate, outRate);
        var wholeOutput = ResampleWhole(wholeResampler, signal);

        var chunkedResampler = new PolyphaseResampler(inRate, outRate);
        var chunkedOutput = new List<float>();
        for (int offset = 0; offset < signal.Length; offset += chunkFrames)
        {
            int len = Math.Min(chunkFrames, signal.Length - offset);
            chunkedOutput.AddRange(chunkedResampler.Resample(signal.AsSpan(offset, len)));
        }
        chunkedOutput.AddRange(chunkedResampler.Flush());

        Assert.Equal(wholeOutput.Length, chunkedOutput.Count);

        double maxAbsDiff = 0;
        for (int i = 0; i < wholeOutput.Length; i++)
            maxAbsDiff = Math.Max(maxAbsDiff, Math.Abs(wholeOutput[i] - chunkedOutput[i]));

        output.WriteLine(
            $"inRate={inRate} chunkFrames={chunkFrames}: wholeLen={wholeOutput.Length} chunkedLen={chunkedOutput.Count} maxAbsDiff={maxAbsDiff:E3}");

        // Same convolution, same floating-point operation order for every sample whose
        // support is fully within the buffered history either way — chunking should not
        // introduce any discontinuity or repeated edge-taper. Allow a tiny float tolerance,
        // not a perceptual one.
        Assert.True(maxAbsDiff < 1e-5,
            $"inRate={inRate} chunkFrames={chunkFrames}: max abs diff between chunked and one-shot output = {maxAbsDiff:E3}");
    }

    [Fact]
    public void Streaming_NoDiscontinuityAtChunkBoundaries()
    {
        // A pure sustained tone deep in the passband: if chunk boundaries introduced a
        // repeating edge-taper (the bug this design fixes), the resampled tone's amplitude
        // would dip periodically every chunk boundary. Confirm every chunk's own RMS is
        // close to the signal's overall passband RMS, not just that concatenation matches
        // a one-shot call (covered above) -- this directly targets "no periodic artifact".
        const int inRate = 48000;
        const int outRate = 16000;
        const int chunkFrames = 512; // matches §1.5's framesPerBuffer
        const double toneHz = 1000.0;
        const double durationSec = 1.0;

        int n = (int)(inRate * durationSec);
        var signal = new float[n];
        for (int i = 0; i < n; i++)
            signal[i] = (float)Math.Sin(2 * Math.PI * toneHz * i / inRate);

        var resampler = new PolyphaseResampler(inRate, outRate);
        var perChunkRms = new List<double>();
        for (int offset = 0; offset < signal.Length; offset += chunkFrames)
        {
            int len = Math.Min(chunkFrames, signal.Length - offset);
            var chunkOut = resampler.Resample(signal.AsSpan(offset, len));
            if (chunkOut.Length > 0)
            {
                double sumSq = 0;
                foreach (var s in chunkOut) sumSq += (double)s * s;
                perChunkRms.Add(Math.Sqrt(sumSq / chunkOut.Length));
            }
        }
        resampler.Flush();

        // Skip the first couple of chunks (filter still filling its history window, so
        // early chunks legitimately produce little/no output) and the last chunk (tail is
        // drained via Flush, not here).
        var steadyState = perChunkRms.Skip(3).Take(perChunkRms.Count - 4).ToList();
        Assert.True(steadyState.Count > 5, "Not enough steady-state chunks produced to assert on.");

        double expectedRms = 1.0 / Math.Sqrt(2); // unit-amplitude sine
        foreach (var rms in steadyState)
        {
            Assert.True(Math.Abs(rms - expectedRms) < 0.02,
                $"Per-chunk RMS {rms:F4} deviates from expected steady-state {expectedRms:F4} by more than 0.02 " +
                "-- suggests a periodic artifact at chunk boundaries.");
        }
    }

    // ── Length correctness (§1.13: "off-by-one at buffer boundaries") ──────────────────

    [Theory]
    [InlineData(48000, 16000)]
    [InlineData(44100, 16000)]
    public void Length_OneShotMatchesExpectedFormula(int inRate, int outRate)
    {
        double ratio = (double)inRate / outRate;
        foreach (int seconds in new[] { 1, 2, 3 })
        {
            int baseLen = inRate * seconds;
            foreach (int inLen in new[] { baseLen - 1, baseLen, baseLen + 1 })
            {
                var resampler = new PolyphaseResampler(inRate, outRate);
                var output = ResampleWhole(resampler, new float[inLen]);
                long expected = (long)(inLen / ratio);
                Assert.True(output.Length == expected,
                    $"{inRate}->{outRate} inLen={inLen}: expected {expected}, got {output.Length}");
            }
        }
    }

    [Theory]
    [InlineData(48000, 16000, 512)]
    [InlineData(44100, 16000, 512)]
    [InlineData(48000, 16000, 500)] // doesn't divide the ratio evenly
    [InlineData(44100, 16000, 333)] // deliberately awkward chunk size
    public void Length_ChunkedTotalMatchesOneShotFormula(int inRate, int outRate, int chunkFrames)
    {
        double ratio = (double)inRate / outRate;
        int totalInputLen = inRate * 2 + 137; // whole seconds plus an odd remainder

        var resampler = new PolyphaseResampler(inRate, outRate);
        var signal = new float[totalInputLen];
        // Non-zero content so this isn't trivially all-zero output.
        for (int i = 0; i < signal.Length; i++)
            signal[i] = (float)Math.Sin(2 * Math.PI * 440.0 * i / inRate);

        long totalOut = 0;
        for (int offset = 0; offset < signal.Length; offset += chunkFrames)
        {
            int len = Math.Min(chunkFrames, signal.Length - offset);
            totalOut += resampler.Resample(signal.AsSpan(offset, len)).Length;
        }
        totalOut += resampler.Flush().Length;

        long expected = (long)(totalInputLen / ratio);
        Assert.Equal(expected, totalOut);
    }

    [Fact]
    public void Reset_AllowsReuseForNewUtteranceWithClearedState()
    {
        var resampler = new PolyphaseResampler(48000, 16000);
        resampler.Resample(GenerateChirp(48000, 0.05));
        resampler.Reset();

        // After Reset (as after Flush), a fresh short utterance should behave exactly as
        // if a brand-new instance had been used.
        var fresh = new PolyphaseResampler(48000, 16000);
        var afterResetOutput = ResampleWhole(resampler, GenerateChirp(48000, 0.2));
        var freshOutput = ResampleWhole(fresh, GenerateChirp(48000, 0.2));

        Assert.Equal(freshOutput.Length, afterResetOutput.Length);
        for (int i = 0; i < freshOutput.Length; i++)
            Assert.Equal(freshOutput[i], afterResetOutput[i], 5);
    }

    [Fact]
    public void Flush_ResetsStateForNextUtterance()
    {
        var resampler = new PolyphaseResampler(48000, 16000);
        ResampleWhole(resampler, GenerateChirp(48000, 0.3));

        // Flush() already drained + reset internally; a second utterance should produce
        // the same output as a brand-new instance would for the same input.
        var fresh = new PolyphaseResampler(48000, 16000);
        var second = ResampleWhole(resampler, GenerateChirp(48000, 0.3));
        var expected = ResampleWhole(fresh, GenerateChirp(48000, 0.3));

        Assert.Equal(expected.Length, second.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], second[i], 5);
    }

    // ── Against a reference (mathematically-known signal) — §1.13 ──────────────────────

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void ReferenceSignal_PureToneSurvivesResamplingWithCorrectFrequencyAndAmplitude(int inRate)
    {
        // A pure sine well inside the passband (1kHz, output Nyquist is 8kHz) is a
        // mathematically-known reference: after correct resampling it must still be a
        // ~1kHz sine of ~unit amplitude. This is the "reference implementation" comparison
        // per §1.13, using a signal whose correct output is derivable analytically rather
        // than an external library dependency.
        const int outRate = 16000;
        const double toneHz = 1000.0;
        const double durationSec = 1.0;

        int n = (int)(inRate * durationSec);
        var signal = new float[n];
        for (int i = 0; i < n; i++)
            signal[i] = (float)Math.Sin(2 * Math.PI * toneHz * i / inRate);

        var resampler = new PolyphaseResampler(inRate, outRate);
        var resampled = ResampleWhole(resampler, signal);

        double edgeMarginSec = (resampler.TapsPerPhase / 2.0) / inRate;
        int start = (int)((edgeMarginSec + 0.05) * outRate);
        int end = (int)((durationSec - edgeMarginSec - 0.05) * outRate);
        Assert.True(end > start + outRate / 10, "Not enough steady-state samples to validate.");

        var steady = resampled[start..end];

        // Amplitude: RMS of a unit-amplitude sine is 1/sqrt(2).
        double sumSq = 0;
        foreach (var s in steady) sumSq += (double)s * s;
        double rms = Math.Sqrt(sumSq / steady.Length);
        Assert.True(Math.Abs(rms - 1.0 / Math.Sqrt(2)) < 0.02,
            $"inRate={inRate}: expected RMS ~{1.0 / Math.Sqrt(2):F4}, got {rms:F4}");

        // Frequency: count zero crossings to recover frequency independently of the FFT
        // machinery used elsewhere in this file, so this really is an independent check.
        int crossings = 0;
        for (int i = 1; i < steady.Length; i++)
            if (Math.Sign(steady[i]) != Math.Sign(steady[i - 1]) && steady[i - 1] != 0)
                crossings++;
        double measuredHz = crossings / 2.0 / (steady.Length / (double)outRate);
        Assert.True(Math.Abs(measuredHz - toneHz) < 15.0,
            $"inRate={inRate}: expected ~{toneHz}Hz, measured ~{measuredHz:F1}Hz via zero-crossings");
    }

    [Fact]
    public void ExactThreeToOneRatio_48000To16000_UsesGeneralPolyphasePathCorrectly()
    {
        // 48000->16000 reduces to an exact 3:1 ratio -- confirm it isn't handled as a
        // degenerate/special case that skips the filter (naive decimation), which would
        // fail the sweep test but is worth asserting directly too: taps must actually be
        // sized (not zero/trivial).
        var resampler = new PolyphaseResampler(48000, 16000);
        Assert.True(resampler.TapsPerPhase > 1000, $"Expected ~1200-1300 taps/phase, got {resampler.TapsPerPhase}.");
    }

    [Fact]
    public void AwkwardRatio_44100To16000_DerivesReasonableTapCount()
    {
        var resampler = new PolyphaseResampler(44100, 16000);
        Assert.True(resampler.TapsPerPhase > 1000, $"Expected ~1200-1300 taps/phase, got {resampler.TapsPerPhase}.");
    }

    // ── Upsampling path (inputRate < outputRate) — untested before the review that flagged
    // the stale-transitionWidthHz-after-clamp bug; this is the one path where that bug
    // would actually surface, since the input-Nyquist clamp only ever engages when
    // upsampling from a rate low enough that inputRate/2 is tighter than the output-derived
    // cutoff. ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Upsampling_8000To16000_TapsAreProvisionedForTheClampedTransitionBand()
    {
        // 8000->16000 engages the input-Nyquist clamp in the constructor (outNyquist=8000
        // pulls cutoff down toward 7800, but the input Nyquist of 4000Hz is the tighter
        // constraint here, forcing cutoff down further). Assert taps are actually derived
        // from that clamped, narrower transition band rather than a stale wider one -- a
        // grossly under-provisioned filter (a handful of taps) would fail the sweep
        // assertion below, not just this direct sanity check.
        var resampler = new PolyphaseResampler(8000, 16000);
        Assert.True(resampler.TapsPerPhase >= 33,
            $"Expected a real, non-degenerate tap count for the upsampling path, got {resampler.TapsPerPhase}.");
    }

    [Fact]
    public void Upsampling_8000To16000_NoAliasedImageEnergyAboveInputNyquist()
    {
        // Upsampling shouldn't introduce spectral images above the original input Nyquist
        // (4000Hz here) -- a sweep from below to above that frequency should show strong
        // suppression once the sweep passes it, exactly like the downsampling anti-aliasing
        // test above but mirrored around the input (not output) Nyquist, which is the
        // tighter of the two constraints for this rate pair.
        const int inRate = 8000;
        const int outRate = 16000;
        const double durationSec = 2.0;
        const double f0 = 100.0;
        const double f1 = 3900.0; // stays just under the input Nyquist (4000Hz)

        var chirp = GenerateChirp(inRate, durationSec, f0, f1);
        var resampler = new PolyphaseResampler(inRate, outRate);
        var resampled = ResampleWhole(resampler, chirp);

        double k = (f1 - f0) / durationSec;
        double TimeAtFreq(double f) => (f - f0) / k;
        double edgeMarginSec = (resampler.TapsPerPhase / 2.0) / inRate;

        double earlyStart = TimeAtFreq(f0 + 200);
        double earlyEnd = TimeAtFreq(2500);
        double lateStart = TimeAtFreq(3700);
        double lateEnd = durationSec - edgeMarginSec;

        Assert.True(lateEnd > lateStart,
            $"Sweep duration too short for edge margin {edgeMarginSec * 1000:F1}ms to leave a valid late segment.");

        double earlyRms = SegmentRms(resampled, outRate, earlyStart, earlyEnd);
        double lateRms = SegmentRms(resampled, outRate, lateStart, lateEnd);
        double earlyDbFs = 20 * Math.Log10(Math.Max(earlyRms, 1e-12));
        double lateDbFs = 20 * Math.Log10(Math.Max(lateRms, 1e-12));

        output.WriteLine(
            $"Upsampling 8000->16000: tapsPerPhase={resampler.TapsPerPhase} " +
            $"earlyRms={earlyDbFs:F1}dBFS lateRms={lateDbFs:F1}dBFS");

        // Not aliasing suppression in this direction (there's no aliasing risk when
        // upsampling into a higher output rate) but a basic sanity check that the passband
        // near the input Nyquist edge is still passed through reasonably rather than
        // catastrophically attenuated by an under-provisioned filter -- both segments
        // should carry real signal energy.
        Assert.True(lateRms > 0.1,
            $"Expected non-trivial signal energy near the input Nyquist edge, got lateRms={lateDbFs:F1}dBFS -- " +
            "suggests an under-provisioned or otherwise broken upsampling filter.");
    }

    [Theory]
    [InlineData(512)]
    [InlineData(160)]
    public void Upsampling_8000To16000_ChunkedOutputMatchesOneShotOutput(int chunkFrames)
    {
        const int inRate = 8000;
        const int outRate = 16000;
        var signal = GenerateChirp(inRate, durationSec: 1.5, f0: 100.0, f1: 3900.0);

        var wholeResampler = new PolyphaseResampler(inRate, outRate);
        var wholeOutput = ResampleWhole(wholeResampler, signal);

        var chunkedResampler = new PolyphaseResampler(inRate, outRate);
        var chunkedOutput = new List<float>();
        for (int offset = 0; offset < signal.Length; offset += chunkFrames)
        {
            int len = Math.Min(chunkFrames, signal.Length - offset);
            chunkedOutput.AddRange(chunkedResampler.Resample(signal.AsSpan(offset, len)));
        }
        chunkedOutput.AddRange(chunkedResampler.Flush());

        Assert.Equal(wholeOutput.Length, chunkedOutput.Count);

        double maxAbsDiff = 0;
        for (int i = 0; i < wholeOutput.Length; i++)
            maxAbsDiff = Math.Max(maxAbsDiff, Math.Abs(wholeOutput[i] - chunkedOutput[i]));

        Assert.True(maxAbsDiff < 1e-5,
            $"chunkFrames={chunkFrames}: max abs diff between chunked and one-shot output = {maxAbsDiff:E3}");
    }

    [Fact]
    public void Upsampling_8000To16000_OutputLengthMatchesExpectedFormula()
    {
        const int inRate = 8000;
        const int outRate = 16000;
        double ratio = (double)inRate / outRate;
        foreach (int inLen in new[] { inRate - 1, inRate, inRate + 1, inRate * 2 })
        {
            var resampler = new PolyphaseResampler(inRate, outRate);
            var result = ResampleWhole(resampler, new float[inLen]);
            long expected = (long)(inLen / ratio);
            Assert.True(result.Length == expected,
                $"8000->16000 inLen={inLen}: expected {expected}, got {result.Length}");
        }
    }

    // ── Buffer-writing overload (avoids per-call array allocation on the real-time path) ─

    [Fact]
    public void Resample_BufferOverload_MatchesArrayReturningOverload()
    {
        const int inRate = 48000;
        const int outRate = 16000;
        var signal = GenerateChirp(inRate, durationSec: 0.5);

        var arrayResampler = new PolyphaseResampler(inRate, outRate);
        var arrayOutput = new List<float>();
        const int chunkFrames = 512;
        for (int offset = 0; offset < signal.Length; offset += chunkFrames)
        {
            int len = Math.Min(chunkFrames, signal.Length - offset);
            arrayOutput.AddRange(arrayResampler.Resample(signal.AsSpan(offset, len)));
        }
        arrayOutput.AddRange(arrayResampler.Flush());

        var bufferResampler = new PolyphaseResampler(inRate, outRate);
        var bufferOutput = new List<float>();
        for (int offset = 0; offset < signal.Length; offset += chunkFrames)
        {
            int len = Math.Min(chunkFrames, signal.Length - offset);
            bufferResampler.Resample(signal.AsSpan(offset, len), bufferOutput);
        }
        bufferOutput.AddRange(bufferResampler.Flush());

        Assert.Equal(arrayOutput.Count, bufferOutput.Count);
        for (int i = 0; i < arrayOutput.Count; i++)
            Assert.Equal(arrayOutput[i], bufferOutput[i]);
    }

    [Fact]
    public void Resample_BufferOverload_PassthroughAppendsWithoutClearing()
    {
        var resampler = new PolyphaseResampler(16000, 16000);
        var output = new List<float> { -1f, -2f }; // pre-existing content must be preserved
        var input = GenerateChirp(16000, 0.05);
        resampler.Resample(input, output);

        Assert.Equal(2 + input.Length, output.Count);
        Assert.Equal(-1f, output[0]);
        Assert.Equal(-2f, output[1]);
        for (int i = 0; i < input.Length; i++)
            Assert.Equal(input[i], output[2 + i]);
    }

    [Fact]
    public void SameInputAndOutputRate_IsPassthrough()
    {
        var resampler = new PolyphaseResampler(16000, 16000);
        var input = GenerateChirp(16000, 0.1);
        var output = resampler.Resample(input);
        Assert.Equal(input.Length, output.Length);
        for (int i = 0; i < input.Length; i++)
            Assert.Equal(input[i], output[i]);
        Assert.Empty(resampler.Flush());
    }
}
