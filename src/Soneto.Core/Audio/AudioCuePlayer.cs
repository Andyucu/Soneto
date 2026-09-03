using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace Soneto.Core.Audio;

/// <summary>Testable seam for <see cref="AudioCuePlayer"/>, so <see cref="CaptureModeController"/>'s
/// state machine can be exercised against a fake in tests without touching real audio hardware.</summary>
public interface IAudioCuePlayer
{
    /// Ready cue: short, quiet, higher-pitched blip — plan §1.5, played once a capture stream
    /// is confirmed genuinely delivering real (non-silent) audio.
    void PlayReady();

    /// Failure cue: distinct, lower-pitched tone — played if a capture stream fails to open at
    /// all. "Silence is the worst possible feedback here" (plan §1.5).
    void PlayFailure();
}

/// <summary>
/// Plays the plan §1.5 readiness/failure cues — short sine blips — through a SEPARATE
/// PortAudio OUTPUT stream, deliberately never routed anywhere near
/// <see cref="PortAudioCapture"/>'s input path, so a cue can never be picked up by the
/// microphone/ASR pipeline. This class itself always plays whichever cue it's asked to,
/// unconditionally — the ready-vs-none gating is done entirely by the caller
/// (<see cref="CaptureModeController"/>), not here.
///
/// <para><b><c>ReadyCue.None</c> only suppresses the routine ready cue, never the failure
/// cue:</b> per plan §1.5, the failure cue's justification ("silence is the worst possible
/// feedback here, because you'll talk for ten seconds into nothing") is independent of the
/// <c>readyCue</c> config knob, which is about suppressing the "you're all set" success beep.
/// <see cref="CaptureModeController"/> plays the failure cue unconditionally of
/// <c>readyCueMode</c> (gated only on a non-null cue player) for exactly this reason — see its
/// class doc.</para>
///
/// <para>A cue failing to play (no output device, PortAudio error, etc.) is swallowed and
/// logged — it is feedback, not part of the capture pipeline, and must never fail/abort a
/// dictation.</para>
///
/// <para><b>PortAudio init/terminate:</b> this class independently calls
/// <see cref="PortAudio.Initialize()"/>/<see cref="PortAudio.Terminate()"/>, as does
/// <see cref="PortAudioCapture"/> — safe because PortAudio's native init/terminate is
/// documented as reference-counted/safe with balanced calls across multiple independent
/// owners in the same process.</para>
/// </summary>
public sealed class AudioCuePlayer : IAudioCuePlayer, IDisposable
{
    // Plan §1.5: "a short, quiet sine blip (~40ms, 880Hz) on stream-ready ... played on a
    // separate output stream."
    private const int SampleRate = 16000;
    private const double ReadyFreqHz = 880.0;
    private const double FailureFreqHz = 220.0; // distinct, LOWER pitch, per §1.5.
    private const double DurationMs = 40.0;
    private const double Amplitude = 0.2; // "quiet"
    private const int FramesPerBuffer = 256;

    private readonly ILogger<AudioCuePlayer> _logger;
    private bool _paInitializedHere;

    public AudioCuePlayer(ILogger<AudioCuePlayer> logger)
    {
        _logger = logger;
    }

    public void PlayReady() => PlayTone(ReadyFreqHz, "ready");

    public void PlayFailure() => PlayTone(FailureFreqHz, "failure");

    /// <summary>
    /// Generates the raw samples for a cue (a sine tone with a short linear fade-in/out to
    /// avoid a click at the edges). Exposed as a pure, static, hardware-free function
    /// specifically so tests can inspect frequency/duration/amplitude without opening a real
    /// output device — this is the seam item 4c's test plan calls "a WAV-equivalent output
    /// you can inspect."
    /// </summary>
    public static float[] GenerateTone(
        double freqHz, double durationMs = DurationMs, int sampleRate = SampleRate, double amplitude = Amplitude)
    {
        int n = (int)(sampleRate * durationMs / 1000.0);
        var samples = new float[n];
        int fadeSamples = Math.Max(1, Math.Min(n / 4, sampleRate * 3 / 1000)); // ~3ms fade

        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sampleRate;
            double env = i < fadeSamples ? i / (double)fadeSamples
                : i >= n - fadeSamples ? (n - i) / (double)fadeSamples
                : 1.0;
            samples[i] = (float)(amplitude * env * Math.Sin(2 * Math.PI * freqHz * t));
        }

        return samples;
    }

    private void PlayTone(double freqHz, string label)
    {
        try
        {
            EnsurePortAudioInitialized();
            var samples = GenerateTone(freqHz);
            PlaySamplesBlocking(samples, SampleRate);
            _logger.LogInformation(
                "Played {Label} cue ({FreqHz}Hz, {DurationMs}ms)", label, freqHz, DurationMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to play the {Label} cue (non-fatal)", label);
        }
    }

    private void PlaySamplesBlocking(float[] samples, int sampleRate)
    {
        int position = 0;
        using var doneSignal = new ManualResetEventSlim(false);

        StreamCallbackResult Callback(
            IntPtr input, IntPtr output, uint frameCount,
            ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
        {
            int remaining = Math.Max(samples.Length - position, 0);
            int toWrite = Math.Min((int)frameCount, remaining);
            if (toWrite > 0)
            {
                Marshal.Copy(samples, position, output, toWrite);
                position += toWrite;
            }

            int silenceFrames = (int)frameCount - toWrite;
            if (silenceFrames > 0)
            {
                // Explicitly zero-fill the rest of this buffer (not guaranteed pre-zeroed by
                // every host API) so nothing but silence follows the tone.
                var zero = new float[silenceFrames];
                Marshal.Copy(zero, 0, output + toWrite * sizeof(float), silenceFrames);
            }

            if (position >= samples.Length)
            {
                doneSignal.Set();
                return StreamCallbackResult.Complete;
            }
            return StreamCallbackResult.Continue;
        }

        int deviceIndex = PortAudio.DefaultOutputDevice;
        if (deviceIndex == PortAudio.NoDevice)
            throw new InvalidOperationException("No default audio output device is available for cue playback.");

        var info = PortAudio.GetDeviceInfo(deviceIndex);
        var outParams = new StreamParameters
        {
            device = deviceIndex,
            channelCount = 1,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        using var stream = new PaStream(
            inParams: null, outParams: outParams, sampleRate: sampleRate,
            framesPerBuffer: FramesPerBuffer, streamFlags: StreamFlags.ClipOff, callback: Callback, userData: 0);
        stream.Start();

        // The tone itself is ~40ms; generous headroom against output-device open/scheduling
        // latency so a slow device doesn't cut the cue off, but still bounded so a stuck
        // stream can't hang the caller (this must never block a real dictation indefinitely).
        doneSignal.Wait(TimeSpan.FromSeconds(2));
        try { stream.Stop(); } catch { /* best effort */ }
    }

    private void EnsurePortAudioInitialized()
    {
        if (_paInitializedHere) return;
        PortAudio.Initialize();
        _paInitializedHere = true;
    }

    public void Dispose()
    {
        if (_paInitializedHere)
        {
            try { PortAudio.Terminate(); }
            catch (Exception ex) { _logger.LogWarning(ex, "PortAudio.Terminate() failed during AudioCuePlayer dispose"); }
            _paInitializedHere = false;
        }
    }
}
