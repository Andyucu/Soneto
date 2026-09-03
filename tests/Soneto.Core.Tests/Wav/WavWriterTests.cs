using Soneto.Core.Wav;

namespace Soneto.Core.Tests.Wav;

/// <summary>
/// Real unit tests for <see cref="WavWriter"/> — round-trips through <see cref="WavReader"/>,
/// no audio device or model file required.
/// </summary>
public class WavWriterTests
{
    [Fact]
    public void Round_trips_samples_and_sample_rate_through_WavReader()
    {
        float[] samples = [0f, 0.5f, -0.5f, 1f, -1f, 0.25f];
        using var ms = new MemoryStream();

        WavWriter.Write(ms, samples, sampleRate: 16000);
        ms.Position = 0;

        var read = WavReader.Read(ms, "in-memory");

        Assert.Equal(16000, read.SampleRate);
        Assert.Equal(samples.Length, read.Samples.Length);
        for (int i = 0; i < samples.Length; i++)
            Assert.Equal(samples[i], read.Samples[i], precision: 3);
    }

    [Fact]
    public void Clamps_out_of_range_samples_instead_of_overflowing()
    {
        float[] samples = [2f, -2f];
        using var ms = new MemoryStream();

        WavWriter.Write(ms, samples, sampleRate: 16000);
        ms.Position = 0;

        var read = WavReader.Read(ms, "in-memory");

        Assert.Equal(1f, read.Samples[0], precision: 2);
        Assert.Equal(-1f, read.Samples[1], precision: 2);
    }

    [Fact]
    public void Empty_sample_buffer_produces_a_valid_zero_length_wav()
    {
        using var ms = new MemoryStream();

        WavWriter.Write(ms, ReadOnlySpan<float>.Empty, sampleRate: 16000);
        ms.Position = 0;

        var read = WavReader.Read(ms, "in-memory");

        Assert.Equal(16000, read.SampleRate);
        Assert.Empty(read.Samples);
    }

    [Fact]
    public void Throws_for_non_positive_sample_rate()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => WavWriter.Write(ms, [0f], sampleRate: 0));
    }
}
