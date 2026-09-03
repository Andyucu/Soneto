namespace Soneto.Core.Wav;

/// <summary>
/// Minimal RIFF/WAVE writer, the counterpart to <see cref="WavReader"/> — needed by item 4b's
/// <c>--record</c> CLI demo so a captured utterance can be saved and played back / inspected.
/// Writes 16-bit PCM mono (broadly playable by any OS media player, unlike float32 WAV, which
/// several common players still mishandle) at an arbitrary sample rate.
/// </summary>
public static class WavWriter
{
    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        using var fs = File.Create(path);
        Write(fs, samples, sampleRate);
    }

    public static void Write(Stream stream, ReadOnlySpan<float> samples, int sampleRate)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        const short channels = 1;
        const short bitsPerSample = 16;
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int dataSize = samples.Length * (bitsPerSample / 8);

        using var bw = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataSize); // RIFF chunk size
        bw.Write("WAVE"u8.ToArray());

        bw.Write("fmt "u8.ToArray());
        bw.Write(16); // fmt chunk size (PCM)
        bw.Write((short)1); // audio format: 1 = PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);

        bw.Write("data"u8.ToArray());
        bw.Write(dataSize);

        foreach (var sample in samples)
        {
            float clamped = Math.Clamp(sample, -1f, 1f);
            short pcm = (short)Math.Round(clamped * short.MaxValue);
            bw.Write(pcm);
        }
    }
}
