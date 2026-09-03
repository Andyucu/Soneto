using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace s1b_audio;

public sealed record LatencyTrial(double OpenMs, double StartMs, double TimeToFirstNonZeroMs, string? Error);

public sealed record DeviceReport(
    int Index,
    string Name,
    string HostApiName,
    double DefaultSampleRate,
    int MaxInputChannels,
    bool Supports16kMonoFloat32,
    List<LatencyTrial> Trials);

/// <summary>
/// Implements the S1b latency method: for each input device, open/start/
/// first-non-zero-buffer timings, repeated N times, per
/// Docs/soneto-implementation-plan-phase0-1.md "S1b — Audio stream open
/// latency and resampling".
/// </summary>
public static class DeviceLatencyHarness
{
    public static List<DeviceReport> RunAll(int repeats, TimeSpan perTrialTimeout)
    {
        var reports = new List<DeviceReport>();
        int count = PortAudio.DeviceCount;

        for (int i = 0; i < count; i++)
        {
            DeviceInfo info;
            try
            {
                info = PortAudio.GetDeviceInfo(i);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Device {i}: failed to query device info: {ex.Message}");
                continue;
            }

            if (info.maxInputChannels <= 0)
                continue; // output-only device, not relevant to capture

            var (hostApiName, _) = PortAudioExtras.GetHostApiName(info.hostApi);
            bool supports16k = SafeIsFormatSupported(i, 1, SampleFormat.Float32, 16000.0);

            Console.Error.WriteLine($"--- Device {i}: {info.name} ---");
            Console.Error.WriteLine($"    hostApi={hostApiName} (index {info.hostApi})");
            Console.Error.WriteLine($"    defaultSampleRate={info.defaultSampleRate}");
            Console.Error.WriteLine($"    maxInputChannels={info.maxInputChannels}");
            Console.Error.WriteLine($"    defaultLowInputLatency={info.defaultLowInputLatency:F4}s defaultHighInputLatency={info.defaultHighInputLatency:F4}s");
            Console.Error.WriteLine($"    supports 16kHz mono float32 directly: {supports16k}");

            var trials = new List<LatencyTrial>();
            for (int t = 0; t < repeats; t++)
            {
                var trial = RunOneTrial(i, info, perTrialTimeout);
                trials.Add(trial);
                if (trial.Error != null)
                    Console.Error.WriteLine($"    trial {t}: ERROR {trial.Error}");
                else
                    Console.Error.WriteLine($"    trial {t}: open={trial.OpenMs:F1}ms start={trial.StartMs:F1}ms firstSample={trial.TimeToFirstNonZeroMs:F1}ms");

                Thread.Sleep(150); // let the device settle between opens
            }

            reports.Add(new DeviceReport(i, info.name, hostApiName, info.defaultSampleRate, info.maxInputChannels, supports16k, trials));
        }

        return reports;
    }

    private static bool SafeIsFormatSupported(int device, int channels, SampleFormat format, double rate)
    {
        try
        {
            return PortAudioExtras.IsInputFormatSupported(device, channels, format, rate);
        }
        catch
        {
            return false;
        }
    }

    private static LatencyTrial RunOneTrial(int deviceIndex, DeviceInfo info, TimeSpan timeout)
    {
        // Per §1.5: open at the device's native rate/mono/float32 for this
        // latency measurement (don't request 16 kHz from the device itself).
        double sampleRate = info.defaultSampleRate;

        var firstNonZeroSignal = new ManualResetEventSlim(false);
        var firstBufferAnySignal = new ManualResetEventSlim(false);
        long t0Ticks = 0, tFirstNonZeroTicks = 0, tFirstBufferTicks = 0;
        object sync = new();

        PaStream.Callback callback = (IntPtr input, IntPtr output, uint frameCount,
            ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData) =>
        {
            long now = DateTime.UtcNow.Ticks;
            lock (sync)
            {
                if (tFirstBufferTicks == 0)
                {
                    tFirstBufferTicks = now;
                    firstBufferAnySignal.Set();
                }
            }

            if (tFirstNonZeroTicks == 0 && input != IntPtr.Zero && frameCount > 0)
            {
                var samples = new float[frameCount];
                Marshal.Copy(input, samples, 0, (int)frameCount);
                bool anyNonZero = false;
                for (int i = 0; i < samples.Length; i++)
                {
                    if (samples[i] != 0f) { anyNonZero = true; break; }
                }

                if (anyNonZero)
                {
                    lock (sync)
                    {
                        if (tFirstNonZeroTicks == 0)
                        {
                            tFirstNonZeroTicks = now;
                            firstNonZeroSignal.Set();
                        }
                    }
                }
            }

            return StreamCallbackResult.Continue;
        };

        var param = new StreamParameters
        {
            device = deviceIndex,
            channelCount = 1,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        PaStream? stream = null;
        try
        {
            long swOpenStart = DateTime.UtcNow.Ticks;
            stream = new PaStream(inParams: param, outParams: null, sampleRate: sampleRate,
                framesPerBuffer: 512, streamFlags: StreamFlags.ClipOff, callback: callback, userData: 0);
            long swOpenEnd = DateTime.UtcNow.Ticks;

            t0Ticks = swOpenStart; // "key-down" equivalent: the instant we decided to open the stream
            double openMs = TicksToMs(swOpenEnd - swOpenStart);

            long swStartStart = DateTime.UtcNow.Ticks;
            stream.Start();
            long swStartEnd = DateTime.UtcNow.Ticks;
            double startMs = TicksToMs(swStartEnd - swStartStart);

            bool got = firstNonZeroSignal.Wait(timeout);
            if (!got)
            {
                // Fall back to "first buffer at all" so a device that is
                // legitimately silent (muted mic, no signal) doesn't report
                // as a hard failure — but flag it clearly.
                bool gotAny = firstBufferAnySignal.IsSet;
                stream.Stop();
                stream.Dispose(); // not just Close(): Close() only closes the native handle,
                                  // the pinned GCHandles for userDataHandle/streamCallback are
                                  // freed by Dispose()/the finalizer only.
                return new LatencyTrial(openMs, startMs, double.NaN,
                    gotAny
                        ? $"no non-zero sample within {timeout.TotalMilliseconds}ms (buffers arrived but were silent/muted)"
                        : $"no callback at all within {timeout.TotalMilliseconds}ms");
            }

            double firstSampleMs = TicksToMs(tFirstNonZeroTicks - t0Ticks);

            stream.Stop();
            stream.Dispose(); // see Dispose() note above -- not just Close()

            return new LatencyTrial(openMs, startMs, firstSampleMs, null);
        }
        catch (Exception ex)
        {
            try { stream?.Dispose(); } catch { /* best effort */ }
            return new LatencyTrial(double.NaN, double.NaN, double.NaN, ex.Message);
        }
    }

    private static double TicksToMs(long ticks) => ticks / (double)TimeSpan.TicksPerMillisecond;

    public static (double P50, double P95) Percentiles(IEnumerable<double> values)
    {
        var sorted = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return (double.NaN, double.NaN);
        return (Percentile(sorted, 0.50), Percentile(sorted, 0.95));
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1) return sorted[0];
        double rank = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        double frac = rank - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }
}
