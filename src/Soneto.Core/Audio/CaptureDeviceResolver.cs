using Microsoft.Extensions.Logging;
using Soneto.Core.Abstractions;

namespace Soneto.Core.Audio;

/// <summary>
/// Pure device-resolution logic for plan §1.5's "Device changes" rule: resolve the device
/// fresh on every key-down (here: every <see cref="Abstractions.IAudioCapture.StartAsync"/>
/// call); if the configured device is gone, fall back to the system default and log it —
/// don't fail the dictation. Kept independent of any actual PortAudio call so it's testable
/// with fakes and requires no audio hardware (plan §1.13).
/// </summary>
public static class CaptureDeviceResolver
{
    /// <summary>
    /// Resolves which device index to open.
    /// </summary>
    /// <param name="requested">
    /// The configured device, or null for "system default" (resolved fresh every time,
    /// never cached).
    /// </param>
    /// <param name="deviceExists">
    /// Returns true if the given index currently refers to a valid, input-capable device.
    /// </param>
    /// <param name="defaultInputDeviceIndex">The host API's current default input device index.</param>
    /// <param name="logger">Used to log a warning if a configured device fell back to default.</param>
    public static int Resolve(
        AudioDeviceId? requested,
        Func<int, bool> deviceExists,
        int defaultInputDeviceIndex,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(deviceExists);
        ArgumentNullException.ThrowIfNull(logger);

        if (requested is null)
            return defaultInputDeviceIndex;

        if (deviceExists(requested.Index))
            return requested.Index;

        logger.LogWarning(
            "Configured audio device {DeviceName} (index {DeviceIndex}) is no longer present; "
            + "falling back to the system default input device (index {DefaultIndex})",
            requested.Name, requested.Index, defaultInputDeviceIndex);
        return defaultInputDeviceIndex;
    }
}
