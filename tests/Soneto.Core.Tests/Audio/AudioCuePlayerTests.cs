using Soneto.Core.Audio;

namespace Soneto.Core.Tests.Audio;

/// <summary>
/// Tests for <see cref="AudioCuePlayer.GenerateTone"/> — the pure, hardware-free waveform
/// generation logic behind plan §1.5's readiness/failure cues. Does NOT exercise the actual
/// PortAudio output stream playback in <see cref="AudioCuePlayer.PlayReady"/>/<see
/// cref="AudioCuePlayer.PlayFailure"/>, which needs a real output device; that seam
/// (<c>GenerateTone</c>) exists specifically so this can be tested without hardware. Frequency
/// is verified via zero-crossing counting, the same independent-of-FFT technique
/// <c>PolyphaseResamplerTests</c> uses for the S1b/item-4 resampler. Explicitly left undone by
/// item 4c's implementer.
/// </summary>
public class AudioCuePlayerTests
{
    private const int SampleRate = 16000;

    private static double MeasureFrequencyHzByZeroCrossing(float[] samples, int sampleRate)
    {
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
            if (Math.Sign(samples[i]) != Math.Sign(samples[i - 1]) && samples[i - 1] != 0)
                crossings++;

        double durationSec = samples.Length / (double)sampleRate;
        return crossings / 2.0 / durationSec;
    }

    [Fact]
    public void GenerateTone_ReadyFrequency_HasCorrectDurationAndFrequency()
    {
        // Plan §1.5: "a short, quiet sine blip (~40ms, 880Hz)."
        const double freqHz = 880.0;
        const double durationMs = 40.0;

        var samples = AudioCuePlayer.GenerateTone(freqHz, durationMs, SampleRate);

        int expectedSamples = (int)(SampleRate * durationMs / 1000.0);
        Assert.Equal(expectedSamples, samples.Length);

        double measuredHz = MeasureFrequencyHzByZeroCrossing(samples, SampleRate);
        Assert.True(Math.Abs(measuredHz - freqHz) < 40.0,
            $"expected ~{freqHz}Hz, measured ~{measuredHz:F1}Hz via zero-crossings");
    }

    [Fact]
    public void GenerateTone_FailureFrequency_HasCorrectDurationAndFrequency()
    {
        // Implementer's choice for the failure tone: 220Hz -- distinct and lower than the
        // 880Hz ready tone, consistent with plan §1.5's "distinct, lower failure tone"
        // (the plan does not pin an exact Hz value, so this only confirms internal
        // consistency with the implementer's own constant, not a plan-mandated number).
        const double freqHz = 220.0;
        const double durationMs = 40.0;

        var samples = AudioCuePlayer.GenerateTone(freqHz, durationMs, SampleRate);

        int expectedSamples = (int)(SampleRate * durationMs / 1000.0);
        Assert.Equal(expectedSamples, samples.Length);

        double measuredHz = MeasureFrequencyHzByZeroCrossing(samples, SampleRate);
        Assert.True(Math.Abs(measuredHz - freqHz) < 20.0,
            $"expected ~{freqHz}Hz, measured ~{measuredHz:F1}Hz via zero-crossings");
    }

    [Fact]
    public void GenerateTone_FailureFrequency_IsLowerThanReadyFrequency()
    {
        // Direct assertion on the "distinct, lower" requirement itself, independent of the
        // exact numbers used above.
        var ready = AudioCuePlayer.GenerateTone(880.0, 40.0, SampleRate);
        var failure = AudioCuePlayer.GenerateTone(220.0, 40.0, SampleRate);

        double readyHz = MeasureFrequencyHzByZeroCrossing(ready, SampleRate);
        double failureHz = MeasureFrequencyHzByZeroCrossing(failure, SampleRate);

        Assert.True(failureHz < readyHz, $"failure tone ({failureHz:F1}Hz) must be lower than ready tone ({readyHz:F1}Hz)");
    }

    [Fact]
    public void GenerateTone_AmplitudeStaysWithinRequestedBound()
    {
        const double amplitude = 0.2; // "quiet", per plan §1.5 / AudioCuePlayer's Amplitude constant
        var samples = AudioCuePlayer.GenerateTone(880.0, 40.0, SampleRate, amplitude);

        foreach (var s in samples)
            Assert.True(Math.Abs(s) <= amplitude + 1e-6, $"sample {s} exceeds requested amplitude {amplitude}");
    }

    [Fact]
    public void GenerateTone_FadesInAndOutToAvoidClickAtEdges()
    {
        var samples = AudioCuePlayer.GenerateTone(880.0, 40.0, SampleRate, amplitude: 0.2);

        // The very first and last samples should be near-zero (fade envelope), not jumping
        // straight to full amplitude -- that discontinuity is exactly what produces an
        // audible click.
        Assert.True(Math.Abs(samples[0]) < 0.05, $"first sample {samples[0]} not faded in");
        Assert.True(Math.Abs(samples[^1]) < 0.05, $"last sample {samples[^1]} not faded out");
    }

    [Fact]
    public void GenerateTone_ZeroDuration_ReturnsEmptyArray()
    {
        var samples = AudioCuePlayer.GenerateTone(880.0, durationMs: 0.0, SampleRate);
        Assert.Empty(samples);
    }
}
