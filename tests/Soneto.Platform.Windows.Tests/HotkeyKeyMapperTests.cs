using SharpHook.Data;

namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Pure logic tests for <see cref="HotkeyKeyMapper"/> — no hook, no window, no simulated
/// input needed, so these run fully in-process with zero OS interaction.
/// </summary>
public sealed class HotkeyKeyMapperTests
{
    [Theory]
    [InlineData("RightControl", KeyCode.VcRightControl)]
    [InlineData("RightCtrl", KeyCode.VcRightControl)]
    [InlineData("LeftControl", KeyCode.VcLeftControl)]
    [InlineData("LeftCtrl", KeyCode.VcLeftControl)]
    [InlineData("RightShift", KeyCode.VcRightShift)]
    [InlineData("LeftShift", KeyCode.VcLeftShift)]
    [InlineData("RightAlt", KeyCode.VcRightAlt)]
    [InlineData("RightMenu", KeyCode.VcRightAlt)]
    [InlineData("LeftAlt", KeyCode.VcLeftAlt)]
    [InlineData("LeftMenu", KeyCode.VcLeftAlt)]
    [InlineData("RightWin", KeyCode.VcRightMeta)]
    [InlineData("RightMeta", KeyCode.VcRightMeta)]
    [InlineData("LeftWin", KeyCode.VcLeftMeta)]
    [InlineData("LeftMeta", KeyCode.VcLeftMeta)]
    [InlineData("CapsLock", KeyCode.VcCapsLock)]
    [InlineData("ContextMenu", KeyCode.VcContextMenu)]
    // Phase 3 item 8 post-review fix: Soneto.App's KeyCaptureField control captures
    // Avalonia's own Key enum names verbatim ("LWin"/"RWin"/"Apps"), which don't match this
    // schema's own naming above -- these three aliases exist so a hotkey captured via the
    // Settings UI still resolves correctly instead of throwing at SessionController startup.
    [InlineData("LWin", KeyCode.VcLeftMeta)]
    [InlineData("RWin", KeyCode.VcRightMeta)]
    [InlineData("Apps", KeyCode.VcContextMenu)]
    public void ToKeyCode_resolves_known_aliases(string alias, KeyCode expected)
    {
        Assert.Equal(expected, HotkeyKeyMapper.ToKeyCode(alias));
    }

    [Theory]
    [InlineData("rightcontrol")]
    [InlineData("RIGHTCONTROL")]
    [InlineData("RightControl")]
    public void ToKeyCode_alias_lookup_is_case_insensitive(string alias)
    {
        Assert.Equal(KeyCode.VcRightControl, HotkeyKeyMapper.ToKeyCode(alias));
    }

    [Fact]
    public void ToKeyCode_falls_back_to_direct_KeyCode_name_when_not_an_alias()
    {
        // "VcF13" is a real KeyCode enum member but not in the alias table -- must resolve
        // via the direct-parse fallback, not throw.
        Assert.Equal(KeyCode.VcF13, HotkeyKeyMapper.ToKeyCode("VcF13"));
    }

    [Fact]
    public void ToKeyCode_falls_back_to_Vc_prefixed_name_when_bare_name_given()
    {
        // "F13" isn't a KeyCode member on its own (the real member is "VcF13") -- must
        // resolve via the "Vc" + name fallback, not throw.
        Assert.Equal(KeyCode.VcF13, HotkeyKeyMapper.ToKeyCode("F13"));
    }

    [Fact]
    public void ToKeyCode_direct_parse_is_tried_before_Vc_prefix_fallback()
    {
        // "VcF13" parses directly; make sure the fallback path isn't somehow double-prefixing
        // it into a nonsense "VcVcF13" lookup that would only work by accident.
        Assert.Equal(KeyCode.VcF13, HotkeyKeyMapper.ToKeyCode("VcF13"));
    }

    [Theory]
    [InlineData("NotARealKey")]
    [InlineData("VcNotARealKey")]
    public void ToKeyCode_throws_ArgumentException_with_clear_message_for_unknown_key(string key)
    {
        var ex = Assert.Throws<ArgumentException>(() => HotkeyKeyMapper.ToKeyCode(key));
        Assert.Contains(key, ex.Message);
        Assert.Contains("Unrecognized hotkey key", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ToKeyCode_throws_ArgumentException_for_null_empty_or_whitespace(string? key)
    {
        Assert.Throws<ArgumentException>(() => HotkeyKeyMapper.ToKeyCode(key!));
    }
}
