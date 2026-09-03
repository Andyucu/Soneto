using Soneto.Platform.Linux;

namespace Soneto.Platform.Linux.Tests;

public class EvdevKeyMapperTests
{
    [Theory]
    [InlineData("RightControl", EvdevKeyCodes.KEY_RIGHTCTRL)]
    [InlineData("RightCtrl", EvdevKeyCodes.KEY_RIGHTCTRL)]
    [InlineData("LeftControl", EvdevKeyCodes.KEY_LEFTCTRL)]
    [InlineData("LeftShift", EvdevKeyCodes.KEY_LEFTSHIFT)]
    [InlineData("RightShift", EvdevKeyCodes.KEY_RIGHTSHIFT)]
    [InlineData("LeftAlt", EvdevKeyCodes.KEY_LEFTALT)]
    [InlineData("RightAlt", EvdevKeyCodes.KEY_RIGHTALT)]
    [InlineData("LeftWin", EvdevKeyCodes.KEY_LEFTMETA)]
    [InlineData("RightWin", EvdevKeyCodes.KEY_RIGHTMETA)]
    [InlineData("CapsLock", EvdevKeyCodes.KEY_CAPSLOCK)]
    // Phase 3 item 8 post-review fix -- kept in sync with HotkeyKeyMapperTests' matching
    // aliases so a hotkey captured via Soneto.App's Settings-page KeyCaptureField control
    // (which emits Avalonia's own Key enum names verbatim) resolves identically on both
    // platforms.
    [InlineData("LWin", EvdevKeyCodes.KEY_LEFTMETA)]
    [InlineData("RWin", EvdevKeyCodes.KEY_RIGHTMETA)]
    [InlineData("Apps", EvdevKeyCodes.KEY_COMPOSE)]
    public void ToKeyCode_ResolvesKnownAliases(string alias, ushort expected)
    {
        Assert.Equal(expected, EvdevKeyMapper.ToKeyCode(alias));
    }

    [Fact]
    public void ToKeyCode_IsCaseInsensitive()
    {
        Assert.Equal(EvdevKeyMapper.ToKeyCode("rightcontrol"), EvdevKeyMapper.ToKeyCode("RightControl"));
    }

    [Theory]
    [InlineData("KEY_A", EvdevKeyCodes.KEY_A)]
    [InlineData("A", EvdevKeyCodes.KEY_A)]
    public void ToKeyCode_FallsBackToDirectConstantName(string key, ushort expected)
    {
        Assert.Equal(expected, EvdevKeyMapper.ToKeyCode(key));
    }

    [Fact]
    public void ToKeyCode_ThrowsOnUnrecognizedKey()
    {
        Assert.Throws<ArgumentException>(() => EvdevKeyMapper.ToKeyCode("NotARealKey"));
    }

    [Fact]
    public void ToKeyCode_ThrowsOnEmpty()
    {
        Assert.Throws<ArgumentException>(() => EvdevKeyMapper.ToKeyCode(""));
    }
}
