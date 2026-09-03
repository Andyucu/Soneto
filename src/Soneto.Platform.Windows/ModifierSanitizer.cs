using Microsoft.Extensions.Logging;
using SharpHook.Data;
using Soneto.Platform.Windows.Interop;

namespace Soneto.Platform.Windows;

/// <summary>
/// Plan §1.8 step 6/9. Suppresses physically-held Shift/Alt/Win/Left-Control before the
/// paste chord, and restores only what's STILL physically held afterward, per
/// <c>spikes/s4-inject-win/ModifierSanitizer.cs</c>'s already-validated algorithm (its
/// README's adversarial-Shift-hold case is the reference).
///
/// <para>
/// <b>Why Left Control IS in this class's modifier list (unlike an earlier version of this
/// class, which excluded Control entirely as "nothing to sanitise here"):</b>
/// <see cref="WindowsTextInjector.SendPasteChord"/> always brackets every chord with its
/// own synthetic <c>VK_LCONTROL</c> down/up, regardless of what this class does. That is
/// fine when the user isn't otherwise holding Left Control -- the chord needs Ctrl down
/// anyway, and the chord's own key-up leaves the logical state correctly "up" afterward.
/// But if the user is physically holding Left Control for a reason UNRELATED to the
/// trigger (e.g. the trigger is the default <c>RightControl</c>, and the user's other hand
/// is genuinely holding Left Ctrl mid-gesture in the target app), the chord's own terminal
/// synthetic Left-Ctrl key-up desyncs the target app's LOGICAL modifier state to "up" even
/// though the physical key is still down -- the exact same "orphan key-up" problem the plan
/// describes for Shift/Alt/Win, just for Control. <c>VK_LCONTROL</c> is therefore in
/// <see cref="Modifiers"/> below, suppressed/restored by <see cref="Suppress"/>/
/// <see cref="Restore"/> exactly like Shift/Alt/Win, using the same trigger-exclusion
/// mechanism (skipping it if the configured trigger itself resolves to
/// <c>VK_LCONTROL</c> -- the item 6/7 collision-fix territory, not this sanitiser's).
/// </para>
///
/// <para>
/// <b>Why generic <c>VK_CONTROL</c> and <c>VK_RCONTROL</c> are still deliberately excluded:</b>
/// generic <c>VK_CONTROL</c> doesn't distinguish left/right and is ambiguous with a
/// Right-Ctrl trigger itself (S3's finding, see <see cref="NativeMethods.VK_LCONTROL"/>'s
/// doc comment). <c>VK_RCONTROL</c> is excluded because <see cref="WindowsTextInjector.SendPasteChord"/>
/// never synthesizes Right Control at all -- there is nothing for a Right-Ctrl-hold to
/// collide with, so sanitising it would be pure overhead with no corresponding bug to fix.
/// Right-Ctrl's own trigger-collision problem (a Right-Ctrl trigger colliding with the
/// chord's own synthetic Left-Ctrl) was already fixed at the hook layer in item 7, via
/// SharpHook's <c>IsEventSimulated</c> -- not this sanitiser's job either.
/// </para>
///
/// <para>
/// <b>The trigger-key-collision risk this item introduces, generalizing item 6's
/// Control-specific left/right disambiguation (S3's finding) to whichever modifier family
/// the trigger belongs to:</b> <c>HotkeyKeyMapper</c> explicitly supports binding the
/// trigger itself to a Shift/Alt/Win/Left-Control key (e.g. <c>"LeftShift"</c>,
/// <c>"RightAlt"</c>, <c>"LeftWin"</c>, <c>"LeftControl"</c>) -- both via its friendly-alias
/// table AND its fallback raw <see cref="KeyCode"/>-name parsing (e.g. a config value of
/// <c>"VcLeftShift"</c> is also a valid, accepted trigger). By the time
/// <see cref="WindowsTextInjector.InjectAsync"/> runs, the trigger's key-up has already
/// fired (that's what ended the recording), so in the ordinary case the trigger key is no
/// longer physically down at all. But this class must not assume that -- if a future
/// caller ever races an injection while the trigger is still (or again) physically held,
/// reading its exact virtual-key code via <c>GetAsyncKeyState</c> is indistinguishable from
/// "the user is additionally holding that modifier for paste purposes" unless the sanitiser
/// is told which VK code IS the trigger and skips it, the same way
/// <see cref="NativeMethods.VK_LCONTROL"/>-not-generic-<c>VK_CONTROL</c> disambiguated the
/// trigger from a genuinely-held Ctrl for item 6.
/// <see cref="Suppress"/> therefore takes the configured trigger key's config-schema string
/// (<see cref="Soneto.Core.Abstractions.InjectionOptions.TriggerKey"/>) and excludes
/// whichever single VK code it maps to (if any) from both the suppress and the restore
/// list -- that key's physical-hold state is item 6/7's concern, never this sanitiser's.
/// <see cref="ResolveExcludedVk"/> delegates to <see cref="HotkeyKeyMapper.ToKeyCode"/> --
/// the exact same resolution <see cref="WindowsHotkeySource"/> itself uses -- rather than
/// re-parsing the trigger string against a second, independently-maintained alias table, so
/// there is one shared source of truth for "what does this trigger string resolve to" and
/// the two can't drift apart (a real gap found by review: a raw <see cref="KeyCode"/> name
/// like <c>"VcLeftShift"</c> is a valid trigger that an alias-only table would silently miss).
/// </para>
/// </summary>
internal static class ModifierSanitizer
{
    /// <summary>
    /// The exact set this sanitiser cares about: Shift/Alt/Win (left AND right read
    /// separately -- never the generic <c>VK_SHIFT</c>/<c>VK_MENU</c> codes, same
    /// left/right-specific-reads discipline S3 established for Control, generalized here
    /// to every modifier family a trigger key could plausibly be bound to), plus Left
    /// Control specifically (see this class's doc comment for why Left-only, matching
    /// <see cref="WindowsTextInjector.SendPasteChord"/>'s own hardcoded Left-Ctrl usage).
    /// </summary>
    private static readonly (string Name, int Vk)[] Modifiers =
    [
        ("Shift(L)", NativeMethods.VK_LSHIFT),
        ("Shift(R)", NativeMethods.VK_RSHIFT),
        ("Alt(L)", NativeMethods.VK_LMENU),
        ("Alt(R)", NativeMethods.VK_RMENU),
        ("Win(L)", NativeMethods.VK_LWIN),
        ("Win(R)", NativeMethods.VK_RWIN),
        ("Control(L)", NativeMethods.VK_LCONTROL),
    ];

    /// <summary>
    /// Resolves the configured trigger key string onto the specific VK code this sanitiser
    /// must skip, or null if the trigger doesn't resolve to one of Shift/Alt/Win/Left-Control
    /// at all (e.g. the default "RightControl", or any non-modifier key) -- in which case
    /// there is no collision risk and every entry in <see cref="Modifiers"/> is checked
    /// normally. Delegates to <see cref="HotkeyKeyMapper.ToKeyCode"/> (the same resolution
    /// <see cref="WindowsHotkeySource"/> uses) rather than a separate alias table, so this
    /// correctly recognizes every trigger string that mapper accepts, including its
    /// fallback raw-<see cref="KeyCode"/>-name parsing path (e.g. "VcLeftShift"), not just
    /// its friendly-alias half.
    /// </summary>
    internal static int? ResolveExcludedVk(string? triggerKey)
    {
        if (string.IsNullOrWhiteSpace(triggerKey))
            return null;

        KeyCode keyCode;
        try
        {
            keyCode = HotkeyKeyMapper.ToKeyCode(triggerKey);
        }
        catch (ArgumentException)
        {
            // Not a key HotkeyKeyMapper recognizes at all -- WindowsHotkeySource would
            // itself fail to start with this trigger, so there is no real trigger to
            // collide with. Nothing to exclude.
            return null;
        }

        return keyCode switch
        {
            KeyCode.VcLeftShift => NativeMethods.VK_LSHIFT,
            KeyCode.VcRightShift => NativeMethods.VK_RSHIFT,
            KeyCode.VcLeftAlt => NativeMethods.VK_LMENU,
            KeyCode.VcRightAlt => NativeMethods.VK_RMENU,
            KeyCode.VcLeftMeta => NativeMethods.VK_LWIN,
            KeyCode.VcRightMeta => NativeMethods.VK_RWIN,
            KeyCode.VcLeftControl => NativeMethods.VK_LCONTROL,
            _ => null,
        };
    }

    /// <summary>
    /// Step 6: suppress every physically-held modifier EXCEPT the one that exactly
    /// matches <paramref name="triggerKey"/> (if any), returning what was actually
    /// suppressed so <see cref="Restore"/> can re-check it after the paste chord.
    /// </summary>
    internal static List<(string Name, int Vk)> Suppress(string? triggerKey, ILogger logger)
    {
        int? excludedVk = ResolveExcludedVk(triggerKey);
        var held = new List<(string Name, int Vk)>();

        foreach (var (name, vk) in Modifiers)
        {
            if (excludedVk.HasValue && vk == excludedVk.Value)
            {
                logger.LogDebug(
                    "Modifier sanitiser: skipping {Modifier} -- it is the configured hotkey "
                    + "trigger key ('{TriggerKey}') itself, not a user-held paste modifier "
                    + "(its physical-hold state is item 6/7's concern, not this sanitiser's).",
                    name, triggerKey);
                continue;
            }

            if (NativeMethods.IsDown(vk))
            {
                held.Add((name, vk));
                WindowsTextInjector.SendSingleKey(vk, keyUp: true, logger);
                logger.LogDebug("Modifier sanitiser: suppressed held modifier {Modifier} before the paste chord.", name);
            }
        }

        return held;
    }

    /// <summary>
    /// Step 9: re-check physical state before restoring -- do NOT restore blindly. The
    /// user may have released the modifier during the ~200ms paste sequence; restoring
    /// unconditionally would leave a stuck modifier (plan §1.8's own explicit warning).
    /// </summary>
    internal static void Restore(List<(string Name, int Vk)> held, ILogger logger)
    {
        foreach (var (name, vk) in held)
        {
            if (NativeMethods.IsDown(vk))
            {
                WindowsTextInjector.SendSingleKey(vk, keyUp: false, logger);
                logger.LogDebug("Modifier sanitiser: restored still-held modifier {Modifier} after the paste chord.", name);
            }
            else
            {
                logger.LogDebug(
                    "Modifier sanitiser: NOT restoring {Modifier} -- released during injection "
                    + "(restoring blindly would leave a stuck modifier).",
                    name);
            }
        }
    }
}
