namespace Soneto.Platform.Linux;

/// <summary>
/// Pure decision logic for plan §1.9's multi-keyboard enumeration filter: "keep every node
/// that reports <c>EV_KEY</c> AND contains standard alphanumeric scancodes (<c>KEY_A</c>
/// through <c>KEY_Z</c>). The alphanumeric test is what filters out power buttons and
/// media-key nodes, which also claim <c>EV_KEY</c>."
///
/// <para>
/// Deliberately pulled out of <see cref="KeyboardDeviceEnumerator"/> (which does the real
/// <c>ioctl(EVIOCGBIT)</c> syscalls) so this decision can be unit-tested against synthetic
/// capability-bitmask byte arrays without a real Linux kernel or evdev device -- same
/// "pull the pure decision out of the native-call method" precedent as
/// <c>Soneto.Platform.Windows.InjectionOutcomeMapper</c>.
/// </para>
/// </summary>
public static class KeyboardDeviceFilter
{
    /// <summary>
    /// <paramref name="evTypeBits"/> is the bitmask returned by <c>ioctl(fd, EVIOCGBIT(0, ...))</c>
    /// (the device's supported event TYPES, e.g. <c>EV_KEY</c>/<c>EV_REL</c>/<c>EV_ABS</c>).
    /// <paramref name="keyCodeBits"/> is the bitmask returned by
    /// <c>ioctl(fd, EVIOCGBIT(EV_KEY, ...))</c> (which specific key/button codes this
    /// device can report). Both are little-endian bit arrays as returned directly by the
    /// kernel (bit N of byte N/8 corresponds to event/key code N).
    /// </summary>
    public static bool IsKeyboardLike(ReadOnlySpan<byte> evTypeBits, ReadOnlySpan<byte> keyCodeBits)
    {
        if (!HasBit(evTypeBits, EvdevConstants.EV_KEY))
            return false;

        // Require ALL 26 standard QWERTY alphanumeric scancodes to be present, not just
        // one -- a media-key node can plausibly claim a handful of individual KEY_* codes
        // (e.g. KEY_MUTE happens to sit near real key codes on some layouts) but will not
        // plausibly claim the full alphanumeric row the way an actual keyboard does.
        foreach (var code in EvdevKeyCodes.AlphaKeyCodes)
        {
            if (!HasBit(keyCodeBits, code))
                return false;
        }
        return true;
    }

    private static bool HasBit(ReadOnlySpan<byte> bits, int code)
    {
        int byteIndex = code / 8;
        int bitIndex = code % 8;
        if (byteIndex >= bits.Length)
            return false;
        return (bits[byteIndex] & (1 << bitIndex)) != 0;
    }
}

/// <summary>Evdev event-type constants needed by <see cref="KeyboardDeviceFilter"/> and the
/// real ioctl calls in <see cref="KeyboardDeviceEnumerator"/>. From
/// <c>linux/input-event-codes.h</c> (ABI-stable).</summary>
public static class EvdevConstants
{
    public const int EV_SYN = 0x00;
    public const int EV_KEY = 0x01;
    public const int EV_REL = 0x02;
    public const int EV_ABS = 0x03;

    /// <summary>Kernel's declared max event-type/key-code count, used to size capability
    /// bitmask buffers (<c>KEY_MAX</c> is 0x2ff in current kernels; round up generously).</summary>
    public const int EV_MAX = 0x1f;
    public const int KEY_MAX = 0x2ff;
}
