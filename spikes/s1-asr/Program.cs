using System;
using System.Diagnostics;
using System.IO;
using SherpaOnnx;

namespace s1_asr;

/// <summary>
/// S1 spike: measure sherpa-onnx / Parakeet v3 int8 in-process ASR latency and
/// correctness. Throwaway code per Docs/soneto-implementation-plan-phase0-1.md
/// — no error-handling investment beyond "fail loudly with a clear message".
///
/// Usage: s1-asr <wav> [--repeat N] [--threads N] [--model-dir DIR]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
        {
            Console.Error.WriteLine("Usage: s1-asr <wav> [--repeat N] [--threads N] [--model-dir DIR]");
            return 1;
        }

        string wavPath = args[0];
        int repeat = 5;
        int threads = 8;
        string modelDir = FindDefaultModelDir();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repeat":
                    repeat = int.Parse(args[++i]);
                    break;
                case "--threads":
                    threads = int.Parse(args[++i]);
                    break;
                case "--model-dir":
                    modelDir = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 1;
            }
        }

        if (!File.Exists(wavPath))
        {
            Console.Error.WriteLine($"WAV file not found: {wavPath}");
            return 1;
        }

        string encoder = Path.Combine(modelDir, "encoder.int8.onnx");
        string decoder = Path.Combine(modelDir, "decoder.int8.onnx");
        string joiner = Path.Combine(modelDir, "joiner.int8.onnx");
        string tokens = Path.Combine(modelDir, "tokens.txt");

        foreach (var f in new[] { encoder, decoder, joiner, tokens })
        {
            if (!File.Exists(f))
            {
                Console.Error.WriteLine($"Model file not found: {f}");
                Console.Error.WriteLine($"Expected model dir: {modelDir}");
                Console.Error.WriteLine("See spikes/s1-asr/README.md for how to fetch the model.");
                return 1;
            }
        }

        var wav = WavReader.Read(wavPath);
        double audioDurationSec = wav.Samples.Length / (double)wav.SampleRate;

        // --- Build the recognizer once; time the cold load. ---
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        var loadSw = Stopwatch.StartNew();
        using var recognizer = new OfflineRecognizer(config);
        loadSw.Stop();
        double modelLoadMs = loadSw.Elapsed.TotalMilliseconds;

        // --- CSV output. ---
        Console.WriteLine("iteration,modelLoadMs,decodeMs,audioDurationSec,rtf,text");

        // Untimed warm-up decode (iteration 0): onnxruntime can lazily allocate
        // arenas/thread pools on the very first Run() call, so its cost isn't
        // guaranteed comparable to later iterations. Run it once, outside the
        // timed loop, but still report it (labeled iteration=0) so it stays
        // visible rather than being silently dropped.
        {
            var warmupSw = Stopwatch.StartNew();
            using var warmupStream = recognizer.CreateStream();
            warmupStream.AcceptWaveform(wav.SampleRate, wav.Samples);
            recognizer.Decode(warmupStream);
            string warmupText = warmupStream.Result.Text;
            warmupSw.Stop();

            double warmupMs = warmupSw.Elapsed.TotalMilliseconds;
            double warmupRtf = (warmupMs / 1000.0) / audioDurationSec;
            string warmupCsvText = "\"" + warmupText.Replace("\"", "\"\"") + "\"";

            Console.WriteLine(
                $"0,{modelLoadMs:F1},{warmupMs:F1},{audioDurationSec:F2},{warmupRtf:F4},{warmupCsvText}");
        }

        for (int iter = 1; iter <= repeat; iter++)
        {
            var decodeSw = Stopwatch.StartNew();
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(wav.SampleRate, wav.Samples);
            recognizer.Decode(stream);
            string text = stream.Result.Text;
            decodeSw.Stop();

            double decodeMs = decodeSw.Elapsed.TotalMilliseconds;
            double rtf = (decodeMs / 1000.0) / audioDurationSec;

            // Escape any embedded quotes/commas for CSV safety.
            string csvText = "\"" + text.Replace("\"", "\"\"") + "\"";

            Console.WriteLine(
                $"{iter},{modelLoadMs:F1},{decodeMs:F1},{audioDurationSec:F2},{rtf:F4},{csvText}");
        }

        return 0;
    }

    private static string FindDefaultModelDir()
    {
        const string modelFolderName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";

        // Walk up from the current directory looking for a `models/` sibling —
        // works whether invoked from repo root, spikes/s1-asr/, or bin/Debug/....
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "models", modelFolderName);
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        // Fall back to a path relative to the repo root, for a clear error message.
        return Path.Combine("models", modelFolderName);
    }
}
