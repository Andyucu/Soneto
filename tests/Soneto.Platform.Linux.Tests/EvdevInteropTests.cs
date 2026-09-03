using Soneto.Platform.Linux;

namespace Soneto.Platform.Linux.Tests;

/// <summary>
/// Verifies <see cref="EvdevInterop.EVIOCGBIT"/>'s pure integer arithmetic reproduces the
/// published <c>_IOC(_IOC_READ, 'E', 0x20 + ev, len)</c> macro formula from
/// <c>linux/input.h</c>/<c>asm-generic/ioctl.h</c>. This confirms the arithmetic is
/// internally consistent with the documented formula -- it does NOT and cannot confirm
/// that a real Linux kernel actually accepts the resulting request code correctly (that
/// requires a real <c>ioctl()</c> call against a real device, unavailable in this
/// environment -- see <see cref="EvdevInterop"/>'s own doc comment).
/// </summary>
public class EvdevInteropTests
{
    private const uint IOC_READ = 2;
    private const int DIRSHIFT = 30;
    private const int TYPESHIFT = 8;
    private const int SIZESHIFT = 16;
    private const uint TYPE_E = (uint)'E';

    private static uint ExpectedIoc(int ev, int len) =>
        (IOC_READ << DIRSHIFT) | (TYPE_E << TYPESHIFT) | ((uint)(0x20 + ev)) | ((uint)len << SIZESHIFT);

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 96)]
    [InlineData(0x1f, 8)]
    public void EVIOCGBIT_MatchesPublishedMacroFormula(int ev, int len)
    {
        var actual = EvdevInterop.EVIOCGBIT(ev, len);
        Assert.Equal((nuint)ExpectedIoc(ev, len), actual);
    }

    [Fact]
    public void EVIOCGBIT_DirectionBitsIndicateRead()
    {
        var code = (uint)EvdevInterop.EVIOCGBIT(EvdevConstants.EV_KEY, 96);
        uint dir = code >> DIRSHIFT;
        Assert.Equal(IOC_READ, dir);
    }

    [Fact]
    public void ParseInputEvent_ExtractsTypeCodeValueFromKnownByteLayout()
    {
        // struct input_event { struct timeval time (16 bytes); u16 type; u16 code; s32 value; }
        var buf = new byte[24];
        BitConverter.GetBytes((ushort)1).CopyTo(buf, 16); // EV_KEY
        BitConverter.GetBytes((ushort)97).CopyTo(buf, 18); // KEY_RIGHTCTRL
        BitConverter.GetBytes(1).CopyTo(buf, 20); // value: down

        var (type, code, value) = EvdevInterop.ParseInputEvent(buf);

        Assert.Equal((ushort)1, type);
        Assert.Equal((ushort)97, code);
        Assert.Equal(1, value);
    }
}
