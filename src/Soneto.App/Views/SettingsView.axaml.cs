using Avalonia.Controls;
using Avalonia.Interactivity;
using Soneto.App.ViewModels;
using Soneto.Core.Configuration;
using Soneto.Core.History;

namespace Soneto.App.Views;

public partial class SettingsView : UserControl
{
    /// <summary>
    /// Required by the Avalonia XAML compiler (AVLN3001 -- "no public constructor was found",
    /// needed for avares:// dynamic/design-time XAML resolution). Never used by production
    /// code, which always calls the <see cref="IConfigService"/>-taking constructor below;
    /// leaves <see cref="UserControl.DataContext"/> unset.
    /// </summary>
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Item 8 (§3.12): replaces item 4's parameterless placeholder. Takes the real
    /// <see cref="IConfigService"/> the composition root (<c>App.axaml.cs</c>) constructs and
    /// synchronously loads at startup (mirroring item 7's <c>DictionaryEditorView</c>
    /// constructor shape exactly) and builds this view's <see cref="SettingsViewModel"/> from it.
    ///
    /// <para>
    /// Item 10 (§3.14): also takes the real, already-eagerly-constructed
    /// <see cref="IHistoryStore"/> (the same instance <c>HistoryView</c> uses — see
    /// <c>App.axaml.cs</c>'s own composition-root ordering) so the panic-wipe control has
    /// something real to call.
    /// </para>
    /// </summary>
    public SettingsView(IConfigService configService, IHistoryStore historyStore) : this()
    {
        DataContext = new SettingsViewModel(configService, historyStore);
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.SaveAsync();
    }

    /// <summary>
    /// Item 10 (§3.14): the panic-wipe trigger button's own click is deliberately NOT the
    /// destructive action itself — it only opens <see cref="ConfirmDialog"/>, whose own Confirm
    /// button is the genuine second confirming action that actually calls
    /// <see cref="SettingsViewModel.PanicWipeAsync"/>.
    /// </summary>
    private async void OnPanicWipeClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        bool confirmed = await ConfirmDialog.ShowAsync(
            owner,
            title: "Wipe all history and debug audio?",
            message: "This permanently deletes every saved dictation and every debug audio clip. "
                     + "This cannot be undone.",
            confirmText: "Yes, delete everything");

        if (confirmed)
            await vm.PanicWipeAsync();
    }
}
