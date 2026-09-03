using Soneto.Core.Wav;

namespace Soneto.Core.Tests;

/// <summary>
/// Real unit tests for <see cref="WavReader"/>, per plan §1.13 ("real unit tests, not
/// spike-level 'trust it works'"). Covers PCM16, Float32, WAVE_FORMAT_EXTENSIBLE, RIFF
/// chunk padding, and sample/duration calculation — all against synthetic in-memory WAV
/// bytes, no audio device or model file required.
/// </summary>
public class WavReaderTests
{
    private static byte[] BuildPcm16Wav(short[] samples, int sampleRate = 16000, short channels = 1)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        short bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        int dataSize = samples.Length * sizeof(short);

        WriteRiffHeader(bw, dataSize, fmtChunkSize: 16);
        WriteFmtChunk(bw, formatTag: 1, channels, sampleRate, byteRate, blockAlign, bitsPerSample);
        WriteAscii(bw, "data");
        bw.Write(dataSize);
        foreach (var s in samples) bw.Write(s);

        return ms.ToArray();
    }

    private static byte[] BuildFloat32Wav(float[] samples, int sampleRate = 16000, short channels = 1)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        short bitsPerSample = 32;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        int dataSize = samples.Length * sizeof(float);

        WriteRiffHeader(bw, dataSize, fmtChunkSize: 16);
        WriteFmtChunk(bw, formatTag: 3, channels, sampleRate, byteRate, blockAlign, bitsPerSample);
        WriteAscii(bw, "data");
        bw.Write(dataSize);
        foreach (var s in samples) bw.Write(s);

        return ms.ToArray();
    }

    private static byte[] BuildExtensiblePcm16Wav(short[] samples, int sampleRate = 16000, short channels = 1)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        short bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        int dataSize = samples.Length * sizeof(short);
        const int fmtChunkSize = 40; // 16 base + 2 cbSize + 22 extension

        WriteRiffHeader(bw, dataSize, fmtChunkSize);
        WriteAscii(bw, "fmt ");
        bw.Write(fmtChunkSize);
        bw.Write(unchecked((short)0xFFFE)); // WAVE_FORMAT_EXTENSIBLE
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);
        bw.Write((short)22); // cbSize
        bw.Write(bitsPerSample); // valid bits per sample
        bw.Write(0); // channel mask
        // SubFormat GUID: leading two bytes = format tag (1 = PCM), rest is padding.
        bw.Write((short)1);
        bw.Write(new byte[14]);

        WriteAscii(bw, "data");
        bw.Write(dataSize);
        foreach (var s in samples) bw.Write(s);

        return ms.ToArray();
    }

    /// Builds a PCM16 WAV with an odd-sized "LIST" chunk (as Audacity/OBS often write)
    /// placed before "data", to exercise RIFF chunk-padding handling.
    private static byte[] BuildPcm16WavWithOddListChunk(short[] samples, int sampleRate = 16000)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        short channels = 1, bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        int dataSize = samples.Length * sizeof(short);

        // Odd-sized LIST chunk payload (5 bytes) — requires 1 pad byte per RIFF spec.
        byte[] listPayload = { (byte)'I', (byte)'N', (byte)'F', (byte)'O', 0x41 };

        int riffSize = 4
            + (8 + 16)                                    // fmt chunk
            + (8 + listPayload.Length + 1)                // LIST chunk + pad byte
            + (8 + dataSize);                              // data chunk
        WriteAscii(bw, "RIFF");
        bw.Write(riffSize);
        WriteAscii(bw, "WAVE");

        WriteFmtChunk(bw, formatTag: 1, channels, sampleRate, byteRate, blockAlign, bitsPerSample);

        WriteAscii(bw, "LIST");
        bw.Write(listPayload.Length);
        bw.Write(listPayload);
        bw.Write((byte)0); // pad byte for odd chunk size

        WriteAscii(bw, "data");
        bw.Write(dataSize);
        foreach (var s in samples) bw.Write(s);

        return ms.ToArray();
    }

    private static void WriteRiffHeader(BinaryWriter bw, int dataSize, int fmtChunkSize)
    {
        WriteAscii(bw, "RIFF");
        bw.Write(4 + (8 + fmtChunkSize) + (8 + dataSize));
        WriteAscii(bw, "WAVE");
    }

    private static void WriteFmtChunk(
        BinaryWriter bw, short formatTag, short channels, int sampleRate, int byteRate,
        short blockAlign, short bitsPerSample)
    {
        WriteAscii(bw, "fmt ");
        bw.Write(16);
        bw.Write(formatTag);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);
    }

    private static void WriteAscii(BinaryWriter bw, string s) => bw.Write(System.Text.Encoding.ASCII.GetBytes(s));

    [Fact]
    public void Reads_pcm16_mono_samples_correctly()
    {
        short[] raw = { 0, short.MaxValue, short.MinValue, -16384, 16384 };
        var bytes = BuildPcm16Wav(raw, sampleRate: 16000);

        var result = WavReader.Read(new MemoryStream(bytes), "test.wav");

        Assert.Equal(16000, result.SampleRate);
        Assert.Equal(raw.Length, result.Samples.Length);
        Assert.Equal(0f, result.Samples[0], 3);
        Assert.Equal(1f, result.Samples[1], 3);
        Assert.Equal(-1f, result.Samples[2], 3);
        Assert.Equal(-0.5f, result.Samples[3], 3);
        Assert.Equal(0.5f, result.Samples[4], 3);
    }

    [Fact]
    public void Reads_float32_mono_samples_correctly()
    {
        float[] raw = { 0f, 0.5f, -0.5f, 1f, -1f };
        var bytes = BuildFloat32Wav(raw, sampleRate: 22050);

        var result = WavReader.Read(new MemoryStream(bytes), "test.wav");

        Assert.Equal(22050, result.SampleRate);
        Assert.Equal(raw, result.Samples);
    }

    [Fact]
    public void Downmixes_stereo_to_mono_by_averaging_channels()
    {
        // Interleaved L/R: (0, 32767), (-32768, 0)
        short[] interleaved = { 0, short.MaxValue, short.MinValue, 0 };
        var bytes = BuildPcm16Wav(interleaved, sampleRate: 16000, channels: 2);

        var result = WavReader.Read(new MemoryStream(bytes), "test.wav");

        Assert.Equal(2, result.Samples.Length);
        Assert.Equal((0f + 1f) / 2f, result.Samples[0], 3);
        Assert.Equal((-1f + 0f) / 2f, result.Samples[1], 3);
    }

    [Fact]
    public void Supports_wave_format_extensible_pcm16()
    {
        short[] raw = { 0, short.MaxValue / 2, short.MinValue / 2 };
        var bytes = BuildExtensiblePcm16Wav(raw, sampleRate: 48000);

        var result = WavReader.Read(new MemoryStream(bytes), "test.wav");

        Assert.Equal(48000, result.SampleRate);
        Assert.Equal(raw.Length, result.Samples.Length);
        Assert.Equal(0f, result.Samples[0], 3);
    }

    [Fact]
    public void Handles_odd_sized_riff_chunk_padding_before_data()
    {
        short[] raw = { 100, 200, 300, -400 };
        var bytes = BuildPcm16WavWithOddListChunk(raw, sampleRate: 16000);

        var result = WavReader.Read(new MemoryStream(bytes), "test.wav");

        Assert.Equal(16000, result.SampleRate);
        Assert.Equal(raw.Length, result.Samples.Length);
        Assert.Equal(100 / 32768f, result.Samples[0], 5);
        Assert.Equal(-400 / 32768f, result.Samples[3], 5);
    }

    [Fact]
    public void Computes_duration_from_sample_count_and_rate()
    {
        short[] raw = new short[16000]; // 1 second at 16kHz
        var bytes = BuildPcm16Wav(raw, sampleRate: 16000);

        var result = WavReader.Read(new MemoryStream(bytes), "test.wav");

        Assert.Equal(TimeSpan.FromSeconds(1), result.Duration);
    }

    [Fact]
    public void Throws_on_non_riff_file()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes("NOTAWAVFILEXXXXXXXXXXXXXXXXXX");

        Assert.Throws<InvalidDataException>(() => WavReader.Read(new MemoryStream(bytes), "bad.wav"));
    }

    [Fact]
    public void Throws_when_no_data_chunk_present()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        WriteRiffHeader(bw, dataSize: 0, fmtChunkSize: 16);
        WriteFmtChunk(bw, formatTag: 1, channels: 1, sampleRate: 16000, byteRate: 32000, blockAlign: 2, bitsPerSample: 16);

        Assert.Throws<InvalidDataException>(() => WavReader.Read(new MemoryStream(ms.ToArray()), "no-data.wav"));
    }
}
