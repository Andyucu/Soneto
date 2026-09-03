namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Pure-logic coverage for <c>ModifierSanitizer</c>'s trigger-exclusion mapping -- no
/// hardware, no <c>SendInput</c>, no held keys involved. <c>ModifierSanitizer</c> and its
/// <c>ResolveExcludedVk</c> method are deliberately <c>internal</c> (see that class's doc
/// comment -- it is not part of any public surface); this project's
/// <c>InternalsVisibleTo</c> wiring for <c>Soneto.Platform.Windows.Tests</c> (see
/// <c>Soneto.Platform.Windows.csproj</c>) lets this file call <see cref="ModifierSanitizer.ResolveExcludedVk"/>
/// directly rather than via reflection.
///
/// <para>
/// The expected VK codes below are hardcoded to the exact values declared in
/// <c>Soneto.Platform.Windows.Interop.NativeMethods</c> (0xA0/0xA1 Shift L/R, 0xA2 Control
/// L, 0xA4/0xA5 Alt L/R, 0x5B/0x5C Win L/R) -- these are frozen Win32 virtual-key
/// constants, not implementation details likely to drift, and hardcoding them here mirrors
/// this test project's existing precedent of inlining raw VK literals directly (see
/// <see cref="WindowsTextInjectorNotepadSelfCheckTests"/>'s own local <c>SendKey</c> helper).
/// </para>
/// </summary>
public sealed class ModifierSanitizerTests
{
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [Theory]
    [InlineData("LeftShift", VK_LSHIFT)]
    [InlineData("RightShift", VK_RSHIFT)]
    [InlineData("LeftAlt", VK_LMENU)]
    [InlineData("LeftMenu", VK_LMENU)]
    [InlineData("RightAlt", VK_RMENU)]
    [InlineData("RightMenu", VK_RMENU)]
    [InlineData("LeftWin", VK_LWIN)]
    [InlineData("LeftMeta", VK_LWIN)]
    [InlineData("RightWin", VK_RWIN)]
    [InlineData("RightMeta", VK_RWIN)]
    [InlineData("LeftControl", VK_LCONTROL)]
    [InlineData("LeftCtrl", VK_LCONTROL)]
    public void ResolveExcludedVk_maps_every_supported_Shift_Alt_Win_LeftControl_trigger_alias_to_its_exact_VK_code(string triggerKey, int expectedVk)
    {
        Assert.Equal(expectedVk, ModifierSanitizer.ResolveExcludedVk(triggerKey));
    }

    // A raw SharpHook KeyCode name (HotkeyKeyMapper.ToKeyCode's fallback parsing path,
    // not its friendly-alias table) must resolve identically to the aliased form above --
    // this is exactly the gap a from-scratch second alias table would have missed.
    [Theory]
    [InlineData("VcLeftShift", VK_LSHIFT)]
    [InlineData("VcLeftControl", VK_LCONTROL)]
    public void ResolveExcludedVk_also_recognizes_HotkeyKeyMappers_raw_KeyCode_fallback_names(string triggerKey, int expectedVk)
    {
        Assert.Equal(expectedVk, ModifierSanitizer.ResolveExcludedVk(triggerKey));
    }

    [Theory]
    [InlineData("leftshift")]
    [InlineData("LEFTSHIFT")]
    [InlineData("LeFtShIfT")]
    public void ResolveExcludedVk_is_case_insensitive(string triggerKey)
    {
        Assert.Equal(VK_LSHIFT, ModifierSanitizer.ResolveExcludedVk(triggerKey));
    }

    // The default binding, and the general case: a trigger key that is NOT one of Shift/Alt/
    // Win/Left-Control must exclude nothing at all -- every entry in the sanitiser's
    // Modifiers set remains eligible for suppression/restoration, per ResolveExcludedVk's
    // own doc comment. Right Control is deliberately in this "excludes nothing" list too --
    // the chord never synthesizes Right Control, so it was never in Modifiers to begin
    // with, and there is nothing for a Right-Ctrl trigger to collide with.
    [Theory]
    [InlineData("RightControl")] // this project's documented default hotkey binding
    [InlineData("RightCtrl")]
    [InlineData("F13")]
    [InlineData("Space")]
    [InlineData("A")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("NotARealKeyName")]
    public void ResolveExcludedVk_returns_null_for_any_trigger_that_is_not_Shift_Alt_Win_or_LeftControl(string? triggerKey)
    {
        Assert.Null(ModifierSanitizer.ResolveExcludedVk(triggerKey));
    }
}
