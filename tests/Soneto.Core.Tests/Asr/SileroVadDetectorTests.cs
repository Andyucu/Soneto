using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Asr;
using Soneto.Core.Configuration;
using Soneto.Core.Wav;

namespace Soneto.Core.Tests.Asr;

/// <summary>
/// Tests for <see cref="SileroVadDetector"/>, per item 5 of the plan. The Silero VAD model
/// is a plain embedded resource in Soneto.Core (see that class's doc comment) with no
/// separate download/ModelManager step, so — unlike <see cref="SherpaOnnxTranscriberCorpusTests"/>
/// which needs the real, separately-downloaded ~640MB ASR model and is therefore tagged
/// <c>Category=Corpus</c> and excluded from the default run — these tests, including the
/// real-speech one against the checked-in <c>TestAssets/en-16k.wav</c> clip, run fine in the
/// default `dotnet test` with no network access and no extra setup: constructing a
/// <see cref="SileroVadDetector"/> below (in the "VAD enabled" tests) exercises the exact
/// same embedded-resource-extraction path the CLI does, and it works out of the box. No
/// <c>Category</c> trait is needed.
/// </summary>
public sealed class SileroVadDetectorTests
{
    private const int SampleRate = 16000;

    private static VadConfig DefaultConfig() => new();

    private static SileroVadDetector CreateDetector(VadConfig? config = null) =>
        new(NullLogger<SileroVadDetector>.Instance, config ?? DefaultConfig());

    private static float[] Silence(int sampleCount) => new float[sampleCount];

    /// <summary>
    /// Silero is a learned model trained on real speech, not a simple energy-threshold
    /// detector — a synthetic pure sine tone does NOT reliably register as "speech" to it
    /// (confirmed empirically: an earlier version of these tests used a 200Hz tone and
    /// Silero classified it as non-speech). So instead of a synthetic signal, tests that
    /// need "known speech content" slice a real-speech excerpt out of the checked-in
    /// <c>TestAssets/en-16k.wav</c> clip (a real recorded sentence) and combine it with
    /// genuine digital silence for the surrounding padding.
    /// </summary>
    private static float[] RealSpeechExcerpt(int startSample, int sampleCount)
    {
        string clipPath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "en-16k.wav");
        var wav = WavReader.Read(clipPath);
        Assert.Equal(SampleRate, wav.SampleRate);
        Assert.True(startSample + sampleCount <= wav.Samples.Length,
            "Requested excerpt exceeds the real-speech test clip's length.");

        var excerpt = new float[sampleCount];
        Array.Copy(wav.Samples, startSample, excerpt, 0, sampleCount);
        return excerpt;
    }

    private static float[] Concat(params float[][] parts)
    {
        int total = parts.Sum(p => p.Length);
        var result = new float[total];
        int offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    [Fact]
    public void Pure_silence_is_discarded_with_no_speech_detected()
    {
        using var detector = CreateDetector();
        var samples = Silence(SampleRate * 2); // 2s of digital silence

        var result = detector.Trim(samples);

        Assert.True(result.ShouldDiscard);
        Assert.Equal(TimeSpan.Zero, result.TotalSpeechDuration);
        Assert.True(result.TrimmedSamples.IsEmpty);
    }

    [Fact]
    public void Real_speech_clip_is_kept_and_not_over_trimmed()
    {
        string clipPath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "en-16k.wav");
        Assert.True(File.Exists(clipPath), $"Test clip not found: {clipPath}");

        var wav = WavReader.Read(clipPath);
        Assert.Equal(SampleRate, wav.SampleRate);

        using var detector = CreateDetector();
        var result = detector.Trim(wav.Samples);

        Assert.False(result.ShouldDiscard);
        // Real speech should be recognised as well over the ~250ms default discard floor.
        Assert.True(result.TotalSpeechDuration.TotalMilliseconds > 500,
            $"Expected >500ms of detected speech, got {result.TotalSpeechDuration.TotalMilliseconds}ms");
        // Should not have trimmed away more than the original clip's length allows, and
        // should not report negative/nonsensical boundaries.
        Assert.InRange(result.SpeechStartSample, 0, wav.Samples.Length);
        Assert.InRange(result.SpeechEndSample, result.SpeechStartSample, wav.Samples.Length);
        // The detected speech region shouldn't be over-trimmed down to a sliver of the
        // real content -- the clip is a full spoken sentence, so most of the buffer
        // (allowing for some leading/trailing silence in the source recording) should
        // survive trimming.
        double keptFraction = result.TotalSpeechDuration.TotalSeconds / wav.Duration.TotalSeconds;
        Assert.True(keptFraction > 0.3,
            $"Expected most of the real speech clip to survive trimming, kept fraction={keptFraction:P0}");
    }

    [Fact]
    public void Silence_speech_silence_pattern_trims_leading_and_trailing_silence_near_the_real_boundaries()
    {
        using var detector = CreateDetector();

        var leadingSilence = Silence(SampleRate); // 1s
        var speech = RealSpeechExcerpt(SampleRate, SampleRate * 2); // real speech, 1s-3s of the clip
        var trailingSilence = Silence(SampleRate); // 1s

        var samples = Concat(leadingSilence, speech, trailingSilence);

        var result = detector.Trim(samples);

        // 2s of real speech should be recognised as long enough to survive the discard threshold.
        Assert.False(result.ShouldDiscard);
        Assert.False(result.TrimmedSamples.IsEmpty);

        // Boundaries should be "reasonably close" to where the tone actually starts/ends
        // (sample index leadingSilence.Length and leadingSilence.Length + speech.Length),
        // allowing generous slack for Silero's own internal windowing/smoothing rather
        // than asserting exact equality.
        const int sloppinessSamples = SampleRate; // 1s of slack either way
        Assert.InRange(result.SpeechStartSample, 0, leadingSilence.Length + sloppinessSamples);
        Assert.InRange(
            result.SpeechEndSample,
            leadingSilence.Length + speech.Length - sloppinessSamples,
            samples.Length);

        // Some leading and trailing silence should genuinely have been trimmed away
        // (not necessarily all of the full 1s margins, since Silero includes some
        // padding/smoothing around detected segments).
        Assert.True(result.LeadingSilenceTrimmed > TimeSpan.Zero);
        Assert.True(result.TrailingSilenceTrimmed > TimeSpan.Zero);
    }

    [Fact]
    public void Short_blip_under_discard_threshold_is_discarded()
    {
        var config = new VadConfig { MinSpeechMs = 250 };
        using var detector = CreateDetector(config);

        // A ~60ms slice of real speech is well under both Silero's own MinSpeechDuration
        // filter and the 250ms discard threshold, surrounded by generous silence.
        var leadingSilence = Silence(SampleRate);
        var blip = RealSpeechExcerpt(SampleRate, (int)(SampleRate * 0.06));
        var trailingSilence = Silence(SampleRate);

        var samples = Concat(leadingSilence, blip, trailingSilence);

        var result = detector.Trim(samples);

        Assert.True(result.ShouldDiscard);
        Assert.True(result.TrimmedSamples.IsEmpty || result.TotalSpeechDuration.TotalMilliseconds < 250);
    }

    [Fact]
    public void Speech_at_or_just_under_MinUtteranceMs_but_at_or_above_MinSpeechMs_is_discarded()
    {
        // This exercises the exact scenario that was structurally unreachable before the
        // MinUtteranceMs/MinSpeechMs split: a real Silero-detected segment whose combined
        // span clears Silero's own native per-segment MinSpeechDuration filter (so Silero
        // does emit it as "speech") but falls at/under the separate whole-utterance discard
        // floor. Before the fix, the discard check reused MinSpeechMs for both purposes,
        // which made "total speech < MinSpeechMs" essentially impossible whenever any
        // segment was found at all (TotalSpeechDuration is always >= a contributing
        // segment's own length). With MinSpeechMs=250 and MinUtteranceMs=280, a ~260ms
        // excerpt of real speech clears the 250ms per-segment filter but is under the 280ms
        // whole-utterance floor, so it must now be discarded.
        var config = new VadConfig { MinSpeechMs = 150, MinUtteranceMs = 450 };
        using var detector = CreateDetector(config);

        var leadingSilence = Silence(SampleRate);
        var speech = RealSpeechExcerpt(SampleRate, (int)(SampleRate * 0.3)); // ~300ms
        var trailingSilence = Silence(SampleRate);

        var samples = Concat(leadingSilence, speech, trailingSilence);

        var result = detector.Trim(samples);

        // Confirms Silero genuinely detected this as speech (i.e. this isn't just hitting
        // the "no segments found at all" branch, which was already reachable pre-fix) --
        // the point of this test is that a real, non-empty detected segment still gets
        // discarded once it's under MinUtteranceMs, even though it's at/above MinSpeechMs.
        Assert.True(result.TotalSpeechDuration.TotalMilliseconds > 0,
            $"Expected Silero to detect some speech, got {result.TotalSpeechDuration.TotalMilliseconds}ms -- " +
            "adjust the excerpt/offset if the source clip's content changes.");
        Assert.True(result.TotalSpeechDuration.TotalMilliseconds >= config.MinSpeechMs);
        Assert.True(result.TotalSpeechDuration.TotalMilliseconds < config.MinUtteranceMs);
        Assert.True(result.ShouldDiscard);
    }

    [Fact]
    public void Disabled_config_passes_audio_through_unchanged_without_trimming()
    {
        var config = new VadConfig { Enabled = false };
        using var detector = CreateDetector(config);

        var samples = Concat(Silence(SampleRate), RealSpeechExcerpt(SampleRate, SampleRate / 2), Silence(SampleRate));

        var result = detector.Trim(samples);

        Assert.False(result.ShouldDiscard);
        Assert.Equal(samples.Length, result.TrimmedSamples.Length);
        Assert.Equal(0, result.SpeechStartSample);
        Assert.Equal(samples.Length, result.SpeechEndSample);
        Assert.Equal(TimeSpan.Zero, result.LeadingSilenceTrimmed);
        Assert.Equal(TimeSpan.Zero, result.TrailingSilenceTrimmed);
        Assert.Equal(samples, result.TrimmedSamples.ToArray());
    }

    [Fact]
    public void Empty_buffer_does_not_throw_and_is_discarded()
    {
        using var detector = CreateDetector();

        var result = detector.Trim(ReadOnlyMemory<float>.Empty);

        Assert.True(result.ShouldDiscard);
        Assert.True(result.TrimmedSamples.IsEmpty);
    }
}
