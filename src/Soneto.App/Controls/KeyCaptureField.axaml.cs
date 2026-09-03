using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Soneto.App.Controls;

/// <summary>
/// Phase 3 item 8 (§3.12): a small, genuinely reusable "click to capture a key" control, built
/// once and reused for BOTH the primary Hotkey rebind field (<c>SettingsView</c>'s Hotkey
/// section) and the second-hotkey language-profile-hint capture field (same view's Language
/// profile binding section), per this item's own explicit "you'll reuse this same control...
/// build it as a small reusable component, not copy-pasted twice" instruction.
///
/// <para>
/// <b>Safe, ordinary, in-window keyboard input — NOT a global hook.</b> Clicking the button puts
/// this control into a "capturing" state and moves focus to the button; the NEXT
/// <see cref="Button.KeyDown"/> this button itself receives (an ordinary, routed, in-window
/// Avalonia input event, exactly like any other control's <c>KeyDown</c>) is captured as the new
/// <see cref="CapturedKey"/> and the control exits capturing mode. This has nothing in common
/// with <c>IHotkeySource</c>/SharpHook's global, cross-application hook registration — it is
/// ordinary focused-control keyboard input, safe to build and exercise freely.
/// </para>
/// </summary>
public partial class KeyCaptureField : UserControl
{
    /// <summary>
    /// The currently captured key, as its <see cref="Key"/> enum name (e.g. "RightControl") —
    /// the exact same schema-string shape <c>HotkeyConfig.Key</c>/
    /// <c>LanguageProfileConfig.SecondaryTriggerKey</c> already use. Two-way bindable so a
    /// <c>SettingsViewModel</c> property can both seed the field's initial displayed value and
    /// receive updates when the user captures a new key.
    /// </summary>
    public static readonly StyledProperty<string?> CapturedKeyProperty =
        AvaloniaProperty.Register<KeyCaptureField, string?>(
            nameof(CapturedKey), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Placeholder text shown while no key has ever been captured.</summary>
    public static readonly StyledProperty<string> UnsetLabelProperty =
        AvaloniaProperty.Register<KeyCaptureField, string>(nameof(UnsetLabel), "(unset — click to capture)");

    private bool _isCapturing;

    public KeyCaptureField()
    {
        InitializeComponent();
        UpdateButtonContent();
    }

    public string? CapturedKey
    {
        get => GetValue(CapturedKeyProperty);
        set => SetValue(CapturedKeyProperty, value);
    }

    public string UnsetLabel
    {
        get => GetValue(UnsetLabelProperty);
        set => SetValue(UnsetLabelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Keeps the button's displayed text in sync whenever CapturedKey changes for ANY
        // reason — a fresh capture below, or an external bound ViewModel property being
        // (re)seeded (e.g. SettingsViewModel's own ConfigChanged-driven refresh, §3.12).
        if (change.Property == CapturedKeyProperty || change.Property == UnsetLabelProperty)
            UpdateButtonContent();
    }

    private void OnCaptureButtonClick(object? sender, RoutedEventArgs e)
    {
        _isCapturing = true;
        UpdateButtonContent();
        CaptureButton.Focus();
    }

    private void OnCaptureButtonKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isCapturing)
            return;

        // Consume the key ourselves so it never also bubbles up as an ordinary keyboard
        // shortcut/text-entry event elsewhere in the window while we're capturing.
        e.Handled = true;
        _isCapturing = false;
        CapturedKey = e.Key.ToString();
    }

    private void UpdateButtonContent()
    {
        // Guard against InitializeComponent() not having run yet (design-time / very early
        // construction) — CaptureButton is a named XAML element, null until InitializeComponent
        // completes.
        if (CaptureButton is null)
            return;

        CaptureButton.Content = _isCapturing
            ? "Press a key..."
            : string.IsNullOrEmpty(CapturedKey) ? UnsetLabel : CapturedKey;
    }
}
