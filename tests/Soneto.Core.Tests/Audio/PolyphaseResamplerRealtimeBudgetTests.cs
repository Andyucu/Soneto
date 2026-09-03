using System.Diagnostics;
using Soneto.Core.Audio;
using Xunit.Abstractions;

namespace Soneto.Core.Tests.Audio;

/// <summary>
/// Independent-verification-only test (not part of the implementer's original suite):
/// measures actual wall-clock time for <see cref="PolyphaseResampler.Resample(ReadOnlySpan{float}, List{float})"/>
/// on a single 512-frame native-rate buffer — the exact call shape
/// <c>PortAudioCapture.OnCallback</c> makes once per PortAudio callback, per §1.5's
/// "Buffer: framesPerBuffer = 512 at the native rate."
///
/// This exists because no test in the implementer's suite measured this: the resample path
/// is entirely untimed, and on this dev machine it is never even exercised by
/// <c>PortAudioCaptureHardwareTests</c> or the <c>--record</c> CLI demo, because the default
/// device here (MME "Microphone (streamplify Mic.)") supports 16 kHz mono float32 directly
/// and takes <see cref="CapturePathKind.Direct16k"/>, skipping the resampler entirely.
///
/// Budget: at 48 kHz native input, a 512-frame callback period is 512/48000 = 10.667ms.
/// If a single call to <c>Resample</c> takes a meaningful fraction of that budget -- and
/// especially if it can occasionally spike close to or past it (GC, cache misses, first-call
/// JIT) -- that is a real risk of audio glitches/dropouts in production on hardware that
/// actually takes the resample path, even though this exact risk is invisible on this
/// machine's own default device.
/// </summary>
public class PolyphaseResamplerRealtimeBudgetTests(ITestOutputHelper output)
{
    [Fact]
    public void Resample_512frame_48k_buffer_reports_actual_wall_clock_cost_against_the_10_667ms_budget()
    {
        const int inRate = 48000;
        const int chunkFrames = 512;
        const double budgetMs = chunkFrames / (double)inRate * 1000.0; // 10.667ms

        var resampler = new PolyphaseResampler(inRate, 16000);
        var input = new float[chunkFrames];
        var rng = new Random(42);
        for (int i = 0; i < input.Length; i++)
            input[i] = (float)(rng.NextDouble() * 2 - 1);

        var outputSamples = new List<float>(256);

        // Warm-up: let the JIT tier up and the filter table settle into cache before timing,
        // since the real callback runs this thousands of times per utterance -- the
        // steady-state cost is what matters for a sustained real-time budget, not the
        // one-off first-call cost (which is a separate, also-real concern noted below).
        for (int i = 0; i < 50; i++)
        {
            outputSamples.Clear();
            resampler.Resample(input, outputSamples);
        }

        const int iterations = 2000;
        var samples = new double[iterations];
        var sw = new Stopwatch();
        for (int i = 0; i < iterations; i++)
        {
            outputSamples.Clear();
            sw.Restart();
            resampler.Resample(input, outputSamples);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        double p50 = samples[iterations / 2];
        double p95 = samples[(int)(iterations * 0.95)];
        double max = samples[^1];

        WriteResult(output, budgetMs, p50, p95, max, resampler.TapsPerPhase);

        // Not a hard pass/fail gate on p50/p95 alone -- see the report for the actual verdict,
        // which also weighs lock contention and worst-case scheduling, not just raw
        // convolution throughput. But an outright budget violation on p50 would mean the
        // design cannot possibly keep up even in the best case, so that much is asserted.
        Assert.True(p50 < budgetMs,
            $"p50 resample cost ({p50:F4}ms) already exceeds the {budgetMs:F3}ms callback budget " +
            "on its own -- the design cannot keep up with 48kHz native capture even in the best case.");
    }

    private static void WriteResult(ITestOutputHelper output, double budgetMs, double p50, double p95, double max, int taps)
    {
        output.WriteLine($"48kHz->16kHz, 512-frame buffer, tapsPerPhase={taps}");
        output.WriteLine($"Callback budget: {budgetMs:F3}ms");
        output.WriteLine($"Resample() wall-clock: p50={p50:F4}ms  p95={p95:F4}ms  max={max:F4}ms");
        output.WriteLine($"p50 as % of budget: {p50 / budgetMs * 100:F1}%   max as % of budget: {max / budgetMs * 100:F1}%");
    }

    /// <summary>
    /// Item 4c review should-fix 3: keeps a permanent measurement of the TWO-resampler scenario
    /// <see cref="PortAudioCapture"/>'s <c>ConsumerLoop</c> actually runs when
    /// <c>preRollCapacityMs</c> &gt; 0 on a device that takes the
    /// <see cref="CapturePathKind.ResampleFromNative"/> path -- <c>ProcessNativeSegment</c> feeds
    /// the SAME drained native-rate segment through <em>both</em> <c>_preRollResampler</c> (the
    /// always-running pre-roll feed) and <c>_resampler</c> (the per-utterance capture, when one is
    /// in progress) back-to-back, on the same consumer thread, against the same 10.667ms
    /// real-time budget as the single-resampler test above. An ad-hoc (not previously kept)
    /// measurement found this leaves real but not huge margin (p50=6.53ms/61%, max=7.27ms/68% of
    /// budget on the dev machine that produced that number) -- and, per the same untested-path gap
    /// item 4b flagged, this exact scenario is never exercised by any hardware test here, since the
    /// default device takes <see cref="CapturePathKind.Direct16k"/> and skips resampling entirely.
    /// This test exists so that risk is measured and tracked on every run rather than resting on
    /// that one-off number, and so a future regression in the resampler or the consumer loop that
    /// erodes this margin gets caught.
    /// </summary>
    [Fact]
    public void TwoResamplers_BackToBack_512frame_48k_buffer_reports_combined_wall_clock_cost_against_the_10_667ms_budget()
    {
        const int inRate = 48000;
        const int chunkFrames = 512;
        const double budgetMs = chunkFrames / (double)inRate * 1000.0; // 10.667ms

        // Two independent instances, exactly as PortAudioCapture keeps `_preRollResampler` and
        // `_resampler` fully separate (never shared/reset together) -- see its class doc's
        // "Item 4c pre-roll support" section.
        var preRollResampler = new PolyphaseResampler(inRate, 16000);
        var utteranceResampler = new PolyphaseResampler(inRate, 16000);
        var input = new float[chunkFrames];
        var rng = new Random(42);
        for (int i = 0; i < input.Length; i++)
            input[i] = (float)(rng.NextDouble() * 2 - 1);

        var preRollOutput = new List<float>(256);
        var utteranceOutput = new List<float>(256);

        // Warm-up both instances, same rationale as the single-resampler test above.
        for (int i = 0; i < 50; i++)
        {
            preRollOutput.Clear();
            preRollResampler.Resample(input, preRollOutput);
            utteranceOutput.Clear();
            utteranceResampler.Resample(input, utteranceOutput);
        }

        const int iterations = 2000;
        var samples = new double[iterations];
        var sw = new Stopwatch();
        for (int i = 0; i < iterations; i++)
        {
            preRollOutput.Clear();
            utteranceOutput.Clear();
            sw.Restart();
            // Same order ProcessNativeSegment runs them in: pre-roll feed first, then the
            // per-utterance capture resample, both against the same drained native segment.
            preRollResampler.Resample(input, preRollOutput);
            utteranceResampler.Resample(input, utteranceOutput);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        double p50 = samples[iterations / 2];
        double p95 = samples[(int)(iterations * 0.95)];
        double max = samples[^1];

        output.WriteLine($"48kHz->16kHz, 512-frame buffer, TWO resamplers back-to-back, tapsPerPhase={preRollResampler.TapsPerPhase}");
        output.WriteLine($"Callback budget: {budgetMs:F3}ms");
        output.WriteLine($"Combined Resample() wall-clock: p50={p50:F4}ms  p95={p95:F4}ms  max={max:F4}ms");
        output.WriteLine($"p50 as % of budget: {p50 / budgetMs * 100:F1}%   max as % of budget: {max / budgetMs * 100:F1}%");

        // Same reasoning as the single-resampler test: a p50 budget violation would mean the
        // two-resampler (pre-roll + in-progress utterance) scenario can't keep up even in the
        // best case. Not a hard gate on p95/max -- those are reported for tracking, not asserted,
        // since worst-case scheduling/GC jitter is a separate concern from steady-state throughput.
        Assert.True(p50 < budgetMs,
            $"Combined two-resampler p50 cost ({p50:F4}ms) already exceeds the {budgetMs:F3}ms callback " +
            "budget on its own -- WarmIdle/AlwaysOn's pre-roll + per-utterance resampling cannot keep up " +
            "with 48kHz native capture even in the best case.");
    }
}
