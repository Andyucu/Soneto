namespace Soneto.Core.Wav;

/// <summary>
/// Minimal RIFF/WAVE reader. Ported from <c>spikes/s1-asr/WavReader.cs</c>, including the
/// two review-fixed bugs from that spike (WAVE_FORMAT_EXTENSIBLE support, RIFF chunk
/// padding) — both hard-won, so the logic is kept intact rather than re-derived.
///
/// Supports PCM8/PCM16/PCM32 and IEEE Float32, mono or stereo (stereo is downmixed to
/// mono by averaging channels), including WAVE_FORMAT_EXTENSIBLE wrappers around either.
/// Not hardened against arbitrary malformed input beyond throwing a clear
/// <see cref="InvalidDataException"/> / <see cref="NotSupportedException"/>.
/// </summary>
public static class WavReader
{
    public sealed record WavData(float[] Samples, int SampleRate)
    {
        public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);
    }

    public static WavData Read(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs, path);
    }

    public static WavData Read(Stream stream, string? sourceNameForErrors = null)
    {
        string source = sourceNameForErrors ?? "<stream>";
        using var br = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        if (new string(br.ReadChars(4)) != "RIFF")
            throw new InvalidDataException($"{source} is not a RIFF file.");
        br.ReadInt32(); // chunk size, unused
        if (new string(br.ReadChars(4)) != "WAVE")
            throw new InvalidDataException($"{source} is not a WAVE file.");

        int sampleRate = 0;
        short bitsPerSample = 0;
        short channels = 0;
        short audioFormat = 0;
        float[]? samples = null;

        while (stream.Position < stream.Length)
        {
            char[] idChars = br.ReadChars(4);
            if (idChars.Length < 4) break;
            string chunkId = new(idChars);
            int chunkSize = br.ReadInt32();
            // RIFF chunks are word-aligned: if chunkSize is odd, there's one pad byte
            // after the chunk data that isn't counted in chunkSize.
            long chunkEnd = stream.Position + chunkSize + (chunkSize % 2);

            if (chunkId == "fmt ")
            {
                audioFormat = br.ReadInt16();
                channels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32(); // byte rate
                br.ReadInt16(); // block align
                bitsPerSample = br.ReadInt16();

                if (audioFormat == unchecked((short)0xFFFE)) // WAVE_FORMAT_EXTENSIBLE
                {
                    short cbSize = br.ReadInt16();
                    if (cbSize >= 22)
                    {
                        br.ReadInt16(); // valid bits per sample
                        br.ReadInt32(); // channel mask
                        // SubFormat GUID: first two bytes carry the format tag
                        // (1 = PCM, 3 = IEEE float), same encoding as audioFormat.
                        short subFormat = br.ReadInt16();
                        audioFormat = subFormat;
                    }
                    else
                    {
                        throw new NotSupportedException(
                            $"{source}: WAVE_FORMAT_EXTENSIBLE fmt chunk too short to contain a "
                            + $"SubFormat GUID (cbSize={cbSize}).");
                    }
                }
            }
            else if (chunkId == "data")
            {
                if (channels <= 0 || bitsPerSample <= 0)
                    throw new InvalidDataException($"{source}: data chunk encountered before a valid fmt chunk.");

                int bytesPerSample = bitsPerSample / 8;
                int totalFrames = chunkSize / (bytesPerSample * channels);
                samples = new float[totalFrames];

                for (int i = 0; i < totalFrames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        float v = audioFormat switch
                        {
                            3 => br.ReadSingle(), // IEEE float
                            1 when bitsPerSample == 16 => br.ReadInt16() / 32768f,
                            1 when bitsPerSample == 8 => (br.ReadByte() - 128) / 128f,
                            1 when bitsPerSample == 32 => br.ReadInt32() / 2147483648f,
                            _ => throw new NotSupportedException(
                                $"{source}: unsupported WAV format (audioFormat={audioFormat}, "
                                + $"bitsPerSample={bitsPerSample}).")
                        };
                        sum += v;
                    }
                    samples[i] = sum / channels;
                }
            }

            // Skip any remaining bytes in the chunk (e.g. padding, unknown chunks).
            if (stream.Position != chunkEnd && chunkEnd <= stream.Length)
                stream.Seek(chunkEnd, SeekOrigin.Begin);
        }

        if (samples == null)
            throw new InvalidDataException($"{source}: no data chunk found.");
        if (sampleRate == 0)
            throw new InvalidDataException($"{source}: no fmt chunk found.");

        return new WavData(samples, sampleRate);
    }
}
