namespace Soneto.Platform.Linux;

public enum ClipboardBackendKind
{
    Wayland,
    X11,
}

/// <summary>
/// Pure <c>XDG_SESSION_TYPE</c> -> clipboard backend selection logic, per plan §1.9:
/// "Detect session type from <c>XDG_SESSION_TYPE</c> at startup and select the
/// implementation; log which one was chosen." Pulled out as a pure function of the env var
/// value (not reading the environment itself) so it's directly unit-testable, mirroring
/// this project's established "separate pure decision from the actual native/process call"
/// convention (e.g. <c>CaptureDeviceResolver</c>).
/// </summary>
public static class ClipboardBackendSelector
{
    /// <summary>
    /// <c>"wayland"</c> (case-insensitive) selects <see cref="ClipboardBackendKind.Wayland"/>
    /// (<c>wl-copy</c>/<c>wl-paste</c>). Everything else -- <c>"x11"</c>, unset, empty, or any
    /// other value -- selects <see cref="ClipboardBackendKind.X11"/> (<c>xclip</c>) as the
    /// safe default, since a login manager or nested session can leave
    /// <c>XDG_SESSION_TYPE</c> unset even on a real X11 desktop.
    /// </summary>
    public static ClipboardBackendKind Select(string? xdgSessionType)
    {
        return string.Equals(xdgSessionType, "wayland", StringComparison.OrdinalIgnoreCase)
            ? ClipboardBackendKind.Wayland
            : ClipboardBackendKind.X11;
    }
}
