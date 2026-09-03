using Soneto.Platform.Linux;

namespace Soneto.Platform.Linux.Tests;

/// <summary>
/// Tests plan §1.9's multi-keyboard filter DECISION logic ("keep every node that reports
/// EV_KEY and contains KEY_A-through-KEY_Z") against synthetic capability bitmasks, exactly
/// as this project's convention requires when a real ioctl/kernel isn't available -- these
/// bitmask byte arrays are hand-built to mimic what a real device would report, not read
/// from any real hardware.
/// </summary>
public class KeyboardDeviceFilterTests
{
    private static byte[] SetBit(byte[] bytes, int code)
    {
        var copy = (byte[])bytes.Clone();
        copy[code / 8] |= (byte)(1 << (code % 8));
        return copy;
    }

    private static byte[] BuildKeyBitsWithFullAlphabet()
    {
        var bits = new byte[(EvdevConstants.KEY_MAX / 8) + 1];
        foreach (var code in EvdevKeyCodes.AlphaKeyCodes)
            bits = SetBit(bits, code);
        return bits;
    }

    [Fact]
    public void IsKeyboardLike_TrueForRealKeyboardDevice()
    {
        var evBits = SetBit(new byte[4], EvdevConstants.EV_KEY);
        var keyBits = BuildKeyBitsWithFullAlphabet();

        Assert.True(KeyboardDeviceFilter.IsKeyboardLike(evBits, keyBits));
    }

    [Fact]
    public void IsKeyboardLike_FalseWhenEvKeyNotClaimed()
    {
        var evBits = new byte[4]; // no bits set at all -- e.g. a pure EV_ABS device.
        var keyBits = BuildKeyBitsWithFullAlphabet();

        Assert.False(KeyboardDeviceFilter.IsKeyboardLike(evBits, keyBits));
    }

    [Fact]
    public void IsKeyboardLike_FalseForPowerButtonNode()
    {
        // A power-button node claims EV_KEY but only reports KEY_POWER, not the alphabet.
        var evBits = SetBit(new byte[4], EvdevConstants.EV_KEY);
        var keyBits = SetBit(new byte[(EvdevConstants.KEY_MAX / 8) + 1], EvdevKeyCodes.KEY_POWER);

        Assert.False(KeyboardDeviceFilter.IsKeyboardLike(evBits, keyBits));
    }

    [Fact]
    public void IsKeyboardLike_FalseForMediaKeyNodeMissingMostOfAlphabet()
    {
        // A consumer-control node claims EV_KEY and a handful of individual key codes but
        // not the full alphanumeric row.
        var evBits = SetBit(new byte[4], EvdevConstants.EV_KEY);
        var keyBits = new byte[(EvdevConstants.KEY_MAX / 8) + 1];
        keyBits = SetBit(keyBits, EvdevKeyCodes.KEY_A);
        keyBits = SetBit(keyBits, EvdevKeyCodes.KEY_Q);
        // Only 2 of 26 alphabet codes present -- should not pass.

        Assert.False(KeyboardDeviceFilter.IsKeyboardLike(evBits, keyBits));
    }

    [Fact]
    public void IsKeyboardLike_FalseWhenKeyBitsArrayTooShort()
    {
        var evBits = SetBit(new byte[4], EvdevConstants.EV_KEY);
        var keyBits = new byte[1]; // far too short to contain any alphabet bit.

        Assert.False(KeyboardDeviceFilter.IsKeyboardLike(evBits, keyBits));
    }
}
