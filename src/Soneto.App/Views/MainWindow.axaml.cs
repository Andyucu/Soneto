using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Soneto.Core.Configuration;
using Soneto.Core.Dictionary;
using Soneto.Core.History;

namespace Soneto.App.Views;

public partial class MainWindow : Window
{
    // The four nav destinations, in NavList's exact display order (History first —
    // §3.8's own explicit ordering rationale: "most-used surface day to day"). Built
    // once and cached rather than re-created per selection, since re-creating an
    // empty placeholder view on every click would be pointless churn.
    private readonly Control[] _pages = [];

    /// <summary>
    /// Required by the Avalonia XAML compiler (AVLN3001 -- "no public constructor was found",
    /// needed for avares:// dynamic/design-time XAML resolution). Never used by production code
    /// (<c>App.axaml.cs</c> always calls the <see cref="IHistoryStore"/>-taking constructor
    /// below, since a real history store is always available by the time <see cref="MainWindow"/>
    /// is constructed there); leaves the nav rail's content empty.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    // Item 6 (§3.10): MainWindow now takes the real IHistoryStore the composition root
    // (App.axaml.cs) constructs eagerly at startup, so it can pass it into HistoryView's
    // constructor — replacing item 4's parameterless placeholder. See HistoryPaths's own
    // doc comment for why history persistence is deliberately NOT gated behind
    // PipelineHost.Started's success.
    //
    // Item 7 (§3.11): same shape for the real, already-synchronously-loaded
    // IDictionaryService (see App.axaml.cs's own doc comment for why the dictionary load is
    // synchronous here, unlike PipelineHost's deliberately-async pipeline startup) — passed
    // into DictionaryEditorView's constructor.
    //
    // Item 8 (§3.12): same shape again for the real, already-synchronously-loaded
    // IConfigService — passed into SettingsView's constructor.
    //
    // Item 9 (§3.13): the Permissions Doctor needs an ILoggerFactory of its own (to build
    // throwaway PortAudioCapture/ITextInjector/ModelManager instances for its real checks —
    // see PermissionsDoctorViewModel's own doc comment for why these are always FRESH,
    // throwaway instances, never the real pipeline's own), so the composition root's
    // ILoggerFactory is threaded through here too.
    public MainWindow(
        IHistoryStore historyStore, IDictionaryService dictionaryService, IConfigService configService,
        ILoggerFactory loggerFactory)
        : this()
    {
        _pages =
        [
            new HistoryView(historyStore),
            new DictionaryEditorView(dictionaryService),
            // Item 10 (§3.14): SettingsView now also takes the SAME real IHistoryStore
            // HistoryView above uses, so its panic-wipe control has something real to call.
            new SettingsView(configService, historyStore),
            new PermissionsDoctorView(configService, loggerFactory),
        ];

        NavContent.Content = _pages[0];
        // Set after InitializeComponent (rather than in XAML) so the resulting
        // SelectionChanged fires only once NavContent is fully wired up.
        NavList.SelectedIndex = 0;
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Defensive guard, not currently load-bearing given the constructor's ordering
        // (InitializeComponent before NavList.SelectedIndex is set) -- but this exact
        // "event fires before a dependent field is ready" shape already caused a real
        // NullReferenceException once (see MainWindow constructor's own comment), so a
        // future edit that reintroduces SelectedIndex="0" in XAML fails safely here
        // instead of crashing on every launch again.
        if (NavContent is null)
            return;

        var index = NavList.SelectedIndex;
        if (index >= 0 && index < _pages.Length)
        {
            NavContent.Content = _pages[index];
        }
    }

    /// <summary>
    /// Restores the window from a minimized/hidden state and brings it to the front.
    /// Used by both the tray icon's left-click and its "Open Soneto" menu item
    /// (App.axaml.cs) — kept here since it's a MainWindow-specific operation, not
    /// tray-specific.
    /// </summary>
    public void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
