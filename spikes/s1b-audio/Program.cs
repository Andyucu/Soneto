using System;
using System.Globalization;
using System.IO;
using System.Linq;
using PortAudioSharp;

namespace s1b_audio;

internal static class Program
{
    private static int Main(string[] args)
    {
        int repeats = 20;
        int trialTimeoutMs = 3000;
        bool runDevices = true;
        bool runSweep = true;
        string? csvPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repeat":
                    repeats = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--trial-timeout-ms":
                    trialTimeoutMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--devices-only":
                    runSweep = false;
                    break;
                case "--sweep-only":
                    runDevices = false;
                    break;
                case "--csv":
                    csvPath = args[++i];
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintUsage();
                    return 1;
            }
        }

        int exitCode = 0;

        if (runSweep)
        {
            exitCode = Math.Max(exitCode, RunSweepValidation());
        }

        if (runDevices)
        {
            exitCode = Math.Max(exitCode, RunDeviceLatency(repeats, trialTimeoutMs, csvPath));
        }

        return exitCode;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            s1b-audio [--repeat N] [--trial-timeout-ms MS] [--devices-only] [--sweep-only] [--csv path.csv]

              --repeat N            Number of open/start/first-sample trials per input device (default 20).
              --trial-timeout-ms MS Max time to wait for the first non-zero buffer per trial (default 3000).
              --devices-only        Skip the resampler sweep validation, only run the device latency harness.
              --sweep-only          Skip the device latency harness, only run the resampler sweep validation.
              --csv path.csv        Also write per-trial device latency rows to this CSV file.
            """);
    }

    private static int RunSweepValidation()
    {
        Console.Error.WriteLine("=== Resampler sweep validation (100Hz-20kHz chirp, 48kHz -> 16kHz) ===");
        var result48to16 = SweepValidation.Validate(48000, 16000);
        Console.Error.WriteLine($"48000 -> 16000: {(result48to16.Pass ? "PASS" : "FAIL")}  {result48to16.Details}");

        Console.Error.WriteLine();
        Console.Error.WriteLine("=== Resampler sweep validation (100Hz-20kHz chirp, 44100Hz -> 16kHz, awkward ratio 147:160) ===");
        var result441to16 = SweepValidation.Validate(44100, 16000);
        Console.Error.WriteLine($"44100 -> 16000: {(result441to16.Pass ? "PASS" : "FAIL")}  {result441to16.Details}");

        Console.Error.WriteLine();
        Console.Error.WriteLine("=== Resampler output-length correctness (§1.13: off-by-one at buffer boundaries) ===");
        var (lengthsPass, lengthsDetails) = SweepValidation.ValidateOutputLengths();
        Console.Error.WriteLine($"{(lengthsPass ? "PASS" : "FAIL")}  {lengthsDetails}");

        Console.WriteLine("resample,inRate,outRate,pass,earlySegmentRmsDbFs,lateSegmentRmsDbFs,suppressionDb");
        Console.WriteLine($"sweep,48000,16000,{result48to16.Pass},{result48to16.EarlySegmentRmsDbFs:F2},{result48to16.LateSegmentRmsDbFs:F2},{result48to16.SuppressionDb:F2}");
        Console.WriteLine($"sweep,44100,16000,{result441to16.Pass},{result441to16.EarlySegmentRmsDbFs:F2},{result441to16.LateSegmentRmsDbFs:F2},{result441to16.SuppressionDb:F2}");
        Console.WriteLine($"lengths,,,{lengthsPass},,,");

        return (result48to16.Pass && result441to16.Pass && lengthsPass) ? 0 : 2;
    }

    private static int RunDeviceLatency(int repeats, int trialTimeoutMs, string? csvPath)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("=== Device enumeration + open/start/first-sample latency ===");
        Console.Error.WriteLine($"PortAudio version: {PortAudio.VersionInfo.versionText}");

        try
        {
            PortAudio.Initialize();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: PortAudio.Initialize() failed: {ex.Message}");
            Console.Error.WriteLine("No audio hardware/driver testable in this environment. Device latency harness cannot run.");
            return 3;
        }

        try
        {
            int deviceCount = PortAudio.DeviceCount;
            Console.Error.WriteLine($"Device count: {deviceCount}");

            if (deviceCount <= 0)
            {
                Console.Error.WriteLine("No audio devices reported by PortAudio in this environment.");
                Console.Error.WriteLine("Device latency harness cannot produce numbers here; the enumeration path itself ran without error.");
                return 3;
            }

            int defaultInput = PortAudio.DefaultInputDevice;
            Console.Error.WriteLine($"Default input device index: {(defaultInput == PortAudio.NoDevice ? "NONE" : defaultInput.ToString())}");

            var reports = DeviceLatencyHarness.RunAll(repeats, TimeSpan.FromMilliseconds(trialTimeoutMs));

            if (reports.Count == 0)
            {
                Console.Error.WriteLine("No input-capable devices found (all enumerated devices were output-only).");
                return 3;
            }

            Console.WriteLine();
            Console.WriteLine("device_index,device_name,host_api,default_sample_rate,supports_16k_mono_f32,is_default_input,openMs_p50,openMs_p95,startMs_p50,startMs_p95,firstSampleMs_p50,firstSampleMs_p95,failed_trials,total_trials");

            StreamWriter? csv = csvPath != null ? new StreamWriter(csvPath) : null;
            csv?.WriteLine("device_index,device_name,trial,openMs,startMs,firstSampleMs,error");

            foreach (var r in reports)
            {
                var opens = r.Trials.Select(t => t.OpenMs);
                var starts = r.Trials.Select(t => t.StartMs);
                var firsts = r.Trials.Select(t => t.TimeToFirstNonZeroMs);
                var (openP50, openP95) = DeviceLatencyHarness.Percentiles(opens);
                var (startP50, startP95) = DeviceLatencyHarness.Percentiles(starts);
                var (firstP50, firstP95) = DeviceLatencyHarness.Percentiles(firsts);
                int failed = r.Trials.Count(t => t.Error != null);

                bool isDefault = r.Index == defaultInput;

                Console.WriteLine($"{r.Index},\"{r.Name}\",{r.HostApiName},{r.DefaultSampleRate},{r.Supports16kMonoFloat32},{isDefault}," +
                                   $"{Fmt(openP50)},{Fmt(openP95)},{Fmt(startP50)},{Fmt(startP95)},{Fmt(firstP50)},{Fmt(firstP95)},{failed},{r.Trials.Count}");

                if (csv != null)
                {
                    for (int i = 0; i < r.Trials.Count; i++)
                    {
                        var t = r.Trials[i];
                        csv.WriteLine($"{r.Index},\"{r.Name}\",{i},{Fmt(t.OpenMs)},{Fmt(t.StartMs)},{Fmt(t.TimeToFirstNonZeroMs)},\"{t.Error}\"");
                    }
                }
            }

            csv?.Dispose();
            if (csvPath != null)
                Console.Error.WriteLine($"Per-trial CSV written to {csvPath}");

            return 0;
        }
        finally
        {
            PortAudio.Terminate();
        }
    }

    private static string Fmt(double v) => double.IsNaN(v) ? "" : v.ToString("F1", CultureInfo.InvariantCulture);
}
