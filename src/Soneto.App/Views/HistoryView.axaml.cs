using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Soneto.App.ViewModels;
using Soneto.Core.History;

namespace Soneto.App.Views;

public partial class HistoryView : UserControl
{
    /// <summary>
    /// Required by the Avalonia XAML compiler (AVLN3001 -- "no public constructor was found",
    /// needed for avares:// dynamic/design-time XAML resolution). Never used by production code,
    /// which always calls the <see cref="IHistoryStore"/>-taking constructor below; leaves
    /// <see cref="UserControl.DataContext"/> unset.
    /// </summary>
    public HistoryView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Item 6 (§3.10): replaces the item-4 parameterless placeholder. Takes the real
    /// <see cref="IHistoryStore"/> the composition root (<c>App.axaml.cs</c>) constructs eagerly
    /// at startup (see <c>HistoryPaths</c>'s own doc comment for the "decoupled from live-session
    /// success" architecture decision) and builds this view's <see cref="HistoryViewModel"/> from
    /// it — this view only ever talks to <see cref="IHistoryStore"/> via its ViewModel, never to
    /// <c>SessionController</c>/<c>PipelineHost</c> directly, so it renders/searches identically
    /// whether or not a live dictation session happens to be running this session.
    /// </summary>
    public HistoryView(IHistoryStore historyStore) : this()
    {
        DataContext = new HistoryViewModel(historyStore);
    }

    private async void OnCopyEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HistoryEntry entry })
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        var clipboard = topLevel?.Clipboard;
        if (clipboard is null)
            return;

        // Avalonia 12.0.4's real IClipboard API, confirmed via reflection against the actually-
        // pinned package version rather than assumed (this shape changed from earlier Avalonia
        // releases): IClipboard itself is now a data-transfer-based API with no SetTextAsync
        // method of its own -- the string convenience overload is
        // Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(IClipboard, string).
        await clipboard.SetTextAsync(entry.FinalText);
    }
}
