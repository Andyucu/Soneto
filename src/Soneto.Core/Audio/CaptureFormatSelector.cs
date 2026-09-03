namespace Soneto.Core.Audio;

/// <summary>Which stream-open path was chosen, per plan §1.5's "Stream configuration and resampling".</summary>
public enum CapturePathKind
{
    /// <summary>The device natively supports 16 kHz mono float32 — opened directly, no resampling.</summary>
    Direct16k,

    /// <summary>The device does not; opened at its native rate mono float32 and resampled in-process.</summary>
    ResampleFromNative,
}

/// <summary>The decided capture plan: which path, and the rate to actually request from <c>Pa_OpenStream</c>.</summary>
public readonly record struct CapturePlan(CapturePathKind Kind, int OpenRate);

/// <summary>
/// Pure decision logic for plan §1.5's "Correct sequence": probe <c>Pa_IsFormatSupported</c>
/// for 16 kHz mono float32; if supported, use it directly; otherwise open at the device's
/// native rate and resample in-process. Kept independent of any actual PortAudio call so the
/// decision itself is unit-testable with a fake <c>Pa_IsFormatSupported</c> result, per plan
/// §1.13 (no audio hardware required for the default test run).
/// </summary>
public static class CaptureFormatSelector
{
    public const int TargetSampleRate = 16000;

    public static CapturePlan Select(bool supports16kMonoFloat32Direct, double deviceDefaultSampleRate)
    {
        if (supports16kMonoFloat32Direct)
            return new CapturePlan(CapturePathKind.Direct16k, TargetSampleRate);

        if (deviceDefaultSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(deviceDefaultSampleRate),
                "Device default sample rate must be positive.");

        return new CapturePlan(CapturePathKind.ResampleFromNative, (int)Math.Round(deviceDefaultSampleRate));
    }
}
