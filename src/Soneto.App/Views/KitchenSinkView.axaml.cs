using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace Soneto.App.Views;

/// <summary>
/// Item 3's scratch "kitchen sink" view (§3.7) — renders every design token
/// (Colors/Typography/Spacing/Elevation/Motion) so the "views pull from tokens, not
/// hardcoded values" discipline is established as a precedent before any real view
/// exists. Item 4 replaces MainWindow's content with the real nav-rail shell; this
/// view is not meant to survive past this item except as a reference/dev page.
/// </summary>
public partial class KitchenSinkView : UserControl
{
    public KitchenSinkView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Motion demo: fade the whole page in using the MotionNormal token (set on
        // the RootFade border in XAML) rather than a hardcoded duration/opacity jump.
        RootFade.Opacity = 1;
    }

    private void OnToggleThemeClick(object? sender, RoutedEventArgs e)
    {
        // Runtime toggle so both the light and dark variants of Colors.axaml can be
        // visually confirmed in one running app, per this item's own verification
        // requirement — not just by editing the OS theme setting and restarting.
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var effective = app.ActualThemeVariant;
        app.RequestedThemeVariant = effective == ThemeVariant.Dark
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
    }
}
