using Soneto.Core.Abstractions;
using Soneto.Core.Configuration;
using ConfigInjectionMethod = Soneto.Core.Configuration.InjectionMethod;
using AbstractionsInjectionMethod = Soneto.Core.Abstractions.InjectionMethod;

namespace Soneto.Core.Tests;

/// <summary>
/// Phase 4 item 2 (§4.4): the pure "given a process name + a PerApp table, which override
/// applies and what are the resulting effective <see cref="InjectionOptions"/>" decision,
/// exercised with no Win32 call, no real window handle and no real process lookup — the
/// reason the merge logic was pulled out of <c>WindowsTextInjector</c> in the first place.
///
/// <para>
/// The single most important property asserted here is the <b>no-match fall-through</b>:
/// an unmatched (or unresolvable) process must yield the caller's own base
/// <see cref="InjectionOptions"/> instance, reference-identical and unmutated, so the
/// pre-Phase-4 injection path is provably unchanged for every app that has no override.
/// <c>WindowsTextInjector</c> additionally relies on that reference identity to decide
/// whether to log "applying PerApp override" at all.
/// </para>
/// </summary>
public sealed class PerAppOverrideResolverTests
{
    private static InjectionOptions BaseOptions() => new(
        Method: AbstractionsInjectionMethod.ClipboardPaste,
        PasteChord: "ctrl+v",
        PreDelay: TimeSpan.FromMilliseconds(20),
        ClipboardRestoreDelay: TimeSpan.FromMilliseconds(150),
        RestoreClipboard: true);

    private static Dictionary<string, PerAppOverride> Table(
        params (string Key, PerAppOverride Value)[] entries)
    {
        var d = new Dictionary<string, PerAppOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
            d[key] = value;
        return d;
    }

    [Fact]
    public void No_table_returns_the_base_options_instance_unchanged()
    {
        var b = BaseOptions();

        Assert.Same(b, PerAppOverrideResolver.Resolve(b, "notepad.exe", null));
        Assert.Same(b, PerAppOverrideResolver.Resolve(b, "notepad.exe", Table()));
    }

    [Fact]
    public void Unresolvable_process_name_returns_the_base_options_instance_unchanged()
    {
        // WindowsTextInjector.TryGetProcessExecutableName returns null on an expected race
        // (foreground app exited) or access-denied -- that must behave exactly like "no match".
        var b = BaseOptions();
        var table = Table(("notepad.exe", new PerAppOverride { PasteChord = "ctrl+shift+v" }));

        Assert.Same(b, PerAppOverrideResolver.Resolve(b, null, table));
    }

    [Fact]
    public void Unmatched_process_returns_the_base_options_instance_unchanged()
    {
        var b = BaseOptions();
        var table = Table(("WindowsTerminal.exe", new PerAppOverride { PasteChord = "ctrl+shift+v" }));

        Assert.Same(b, PerAppOverrideResolver.Resolve(b, "notepad.exe", table));
    }

    [Fact]
    public void Matching_entry_overrides_only_the_fields_it_sets()
    {
        var b = BaseOptions();
        var table = Table(("WindowsTerminal.exe", new PerAppOverride { PasteChord = "ctrl+shift+v" }));

        var result = PerAppOverrideResolver.Resolve(b, "WindowsTerminal.exe", table);

        Assert.NotSame(b, result);
        Assert.Equal("ctrl+shift+v", result.PasteChord);
        // Everything the override left null is untouched.
        Assert.Equal(b.Method, result.Method);
        Assert.Equal(b.ClipboardRestoreDelay, result.ClipboardRestoreDelay);
        Assert.Equal(b.PreDelay, result.PreDelay);
        Assert.Equal(b.RestoreClipboard, result.RestoreClipboard);
        Assert.Equal(b.SanitizeModifiers, result.SanitizeModifiers);
        Assert.Equal(b.Policy, result.Policy);
        // The caller's shared record is never mutated.
        Assert.Equal("ctrl+v", b.PasteChord);
    }

    [Fact]
    public void ClipboardRestoreDelayMs_override_is_applied_as_milliseconds()
    {
        var b = BaseOptions();
        var table = Table(("Teams.exe", new PerAppOverride { ClipboardRestoreDelayMs = 300 }));

        var result = PerAppOverrideResolver.Resolve(b, "Teams.exe", table);

        Assert.Equal(TimeSpan.FromMilliseconds(300), result.ClipboardRestoreDelay);
        Assert.Equal(b.PasteChord, result.PasteChord);
    }

    [Fact]
    public void Zero_clipboard_restore_delay_override_is_honoured_not_treated_as_unset()
    {
        // int? -- 0 is a real, explicitly-configured value ("don't wait"), distinct from null.
        var b = BaseOptions();
        var table = Table(("Teams.exe", new PerAppOverride { ClipboardRestoreDelayMs = 0 }));

        var result = PerAppOverrideResolver.Resolve(b, "Teams.exe", table);

        Assert.Equal(TimeSpan.Zero, result.ClipboardRestoreDelay);
    }

    [Fact]
    public void Method_override_maps_the_config_enum_onto_the_abstractions_enum()
    {
        // The §4.4 "for wt.exe, use char-by-char" case -- the field this item added.
        var b = BaseOptions();
        var table = Table(("wt.exe", new PerAppOverride { Method = ConfigInjectionMethod.UnicodeSynth }));

        var result = PerAppOverrideResolver.Resolve(b, "wt.exe", table);

        Assert.Equal(AbstractionsInjectionMethod.UnicodeSynth, result.Method);
    }

    [Fact]
    public void Method_override_can_also_force_ClipboardPaste_over_a_UnicodeSynth_base()
    {
        var b = BaseOptions() with { Method = AbstractionsInjectionMethod.UnicodeSynth };
        var table = Table(("notepad.exe", new PerAppOverride { Method = ConfigInjectionMethod.ClipboardPaste }));

        var result = PerAppOverrideResolver.Resolve(b, "notepad.exe", table);

        Assert.Equal(AbstractionsInjectionMethod.ClipboardPaste, result.Method);
    }

    [Fact]
    public void All_three_override_fields_can_apply_at_once()
    {
        var b = BaseOptions();
        var table = Table(("Code.exe", new PerAppOverride
        {
            PasteChord = "ctrl+shift+v",
            ClipboardRestoreDelayMs = 400,
            Method = ConfigInjectionMethod.UnicodeSynth,
        }));

        var result = PerAppOverrideResolver.Resolve(b, "Code.exe", table);

        Assert.Equal("ctrl+shift+v", result.PasteChord);
        Assert.Equal(TimeSpan.FromMilliseconds(400), result.ClipboardRestoreDelay);
        Assert.Equal(AbstractionsInjectionMethod.UnicodeSynth, result.Method);
    }

    [Fact]
    public void An_entry_that_overrides_nothing_still_leaves_every_value_equal_to_the_base()
    {
        var b = BaseOptions();
        var table = Table(("notepad.exe", new PerAppOverride()));

        var result = PerAppOverrideResolver.Resolve(b, "notepad.exe", table);

        Assert.Equal(b, result);
    }

    [Fact]
    public void Lookup_case_sensitivity_follows_the_dictionarys_own_comparer()
    {
        // Documented contract: the resolver applies no comparer of its own -- the composition
        // root is the single place that decides matching is case-insensitive. Both halves
        // asserted here so a regression in DaemonComposition's OrdinalIgnoreCase wrapping
        // shows up as a behaviour change, not a silent one.
        var b = BaseOptions();
        var insensitive = Table(("WindowsTerminal.exe", new PerAppOverride { PasteChord = "ctrl+shift+v" }));
        var sensitive = new Dictionary<string, PerAppOverride>(StringComparer.Ordinal)
        {
            ["WindowsTerminal.exe"] = new PerAppOverride { PasteChord = "ctrl+shift+v" },
        };

        Assert.Equal("ctrl+shift+v", PerAppOverrideResolver.Resolve(b, "windowsterminal.exe", insensitive).PasteChord);
        Assert.Same(b, PerAppOverrideResolver.Resolve(b, "windowsterminal.exe", sensitive));
    }

    [Fact]
    public void Null_base_options_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PerAppOverrideResolver.Resolve(null!, "notepad.exe", Table()));
    }

    [Fact]
    public void An_out_of_range_method_value_throws_rather_than_being_silently_ignored()
    {
        var b = BaseOptions();
        var table = Table(("notepad.exe", new PerAppOverride { Method = (ConfigInjectionMethod)42 }));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PerAppOverrideResolver.Resolve(b, "notepad.exe", table));
    }

    [Fact]
    public void The_shipped_default_config_table_resolves_its_own_example_entries()
    {
        // Guards the real end-to-end shape: the two entries SonetoConfig ships by default,
        // wrapped the way DaemonComposition wraps them, through ToOptions()'s real base value.
        var config = new SonetoConfig();
        var baseOptions = config.Injection.ToOptions();
        var table = new Dictionary<string, PerAppOverride>(
            config.Injection.PerApp, StringComparer.OrdinalIgnoreCase);

        var terminal = PerAppOverrideResolver.Resolve(baseOptions, "WindowsTerminal.exe", table);
        var teams = PerAppOverrideResolver.Resolve(baseOptions, "Teams.exe", table);

        Assert.Equal("ctrl+shift+v", terminal.PasteChord);
        Assert.Equal(baseOptions.ClipboardRestoreDelay, terminal.ClipboardRestoreDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(300), teams.ClipboardRestoreDelay);
        Assert.Equal(baseOptions.PasteChord, teams.PasteChord);
        Assert.Same(baseOptions, PerAppOverrideResolver.Resolve(baseOptions, "notepad.exe", table));
    }
}
