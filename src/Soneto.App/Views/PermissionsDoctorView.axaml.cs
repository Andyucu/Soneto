using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using Soneto.App.ViewModels;
using Soneto.Core.Configuration;

namespace Soneto.App.Views;

public partial class PermissionsDoctorView : UserControl
{
    /// <summary>
    /// Required by the Avalonia XAML compiler (AVLN3001 -- "no public constructor was found",
    /// needed for avares:// dynamic/design-time XAML resolution). Never used by production
    /// code, which always calls the <see cref="IConfigService"/>/<see cref="ILoggerFactory"/>-
    /// taking constructor below; leaves <see cref="UserControl.DataContext"/> unset.
    /// </summary>
    public PermissionsDoctorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Item 9 (§3.13): replaces item 4's parameterless placeholder. Builds this view's real
    /// <see cref="PermissionsDoctorViewModel"/> and wires it to this view's own hidden
    /// self-test <see cref="TextBox"/> (constraint 2 — see that ViewModel's own doc comment)
    /// and, on non-Windows platforms, this view's own <see cref="TopLevel"/> clipboard (see
    /// the ViewModel's doc comment for why Windows uses
    /// <c>Soneto.Platform.Windows.ClipboardManager</c> directly instead).
    /// </summary>
    public PermissionsDoctorView(IConfigService configService, ILoggerFactory loggerFactory) : this()
    {
        var viewModel = new PermissionsDoctorViewModel(configService, loggerFactory);
        viewModel.AttachSelfTestTextBox(SelfTestTextBox);
        viewModel.AttachNonWindowsClipboardAccessor(() => TopLevel.GetTopLevel(this)?.Clipboard);
        DataContext = viewModel;

        // Real bug, hit and fixed during this item's own live verification: MainWindow
        // eagerly constructs all four nav pages up front (§3.8's own "built once and
        // cached" shell), so this view's constructor runs — and a naive "run checks in the
        // ViewModel's own constructor" design would fire — long before this page is ever
        // actually attached to the visual tree (i.e. before the user has clicked the
        // "Permissions" nav item even once). The hidden self-test TextBox cannot receive
        // real OS focus while unattached, which would make the injection self-test silently
        // meaningless (Focus() a no-op) rather than a real test. Loaded only fires once this
        // control is genuinely part of a live window's visual tree — see
        // PermissionsDoctorViewModel.RunInitialChecksIfNeededAsync's own doc comment for why
        // this is safe to call more than once (Loaded can re-fire across nav-tab switches).
        Loaded += async (_, _) => await viewModel.RunInitialChecksIfNeededAsync();
    }

    private PermissionsDoctorViewModel? ViewModel => DataContext as PermissionsDoctorViewModel;

    private async void OnRecheckAllClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await vm.RecheckAllAsync();
    }
}
