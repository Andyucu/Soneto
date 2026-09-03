using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Soneto.App.ViewModels;

/// <summary>
/// One row in the Permissions Doctor's list (§3.13) — a name, a real, live-updating
/// <see cref="CheckStatus"/>, and a human-readable diagnostic detail (an exception message,
/// a resolved path, a hash, etc.). Hand-rolled <see cref="INotifyPropertyChanged"/>, same
/// style as every other ViewModel in this project.
/// </summary>
public sealed class PermissionCheckViewModel : INotifyPropertyChanged
{
    private CheckStatus _status = CheckStatus.Pending;
    private string _detail = "Not yet run.";

    public PermissionCheckViewModel(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public CheckStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(IsGreen));
            OnPropertyChanged(nameof(IsRed));
            OnPropertyChanged(nameof(IsNotTested));
            OnPropertyChanged(nameof(IsPending));
            OnPropertyChanged(nameof(IsWarn));
        }
    }

    public string Detail
    {
        get => _detail;
        set { _detail = value; OnPropertyChanged(); }
    }

    /// <summary>Short, uppercase status word for the view — kept here rather than an
    /// enum-to-string converter in XAML, matching this project's "small view-facing derived
    /// properties on the ViewModel, not converters" precedent elsewhere.</summary>
    public string StatusLabel => Status switch
    {
        CheckStatus.Green => "OK",
        CheckStatus.Red => "FAILED",
        CheckStatus.NotTested => "NOT TESTED",
        _ => "CHECKING…",
    };

    // Four mutually-exclusive booleans, bound via XAML's Classes.<Name>="{Binding ...}"
    // syntax (the same pattern SettingsView.axaml already uses for StatusOk/StatusError) so
    // the view can style each state from the Success/Warning/Danger design tokens without a
    // converter.
    public bool IsGreen => Status == CheckStatus.Green;
    public bool IsRed => Status == CheckStatus.Red;
    public bool IsNotTested => Status == CheckStatus.NotTested;
    public bool IsPending => Status == CheckStatus.Pending;

    /// <summary>Post-review fix (item 9): the view's own doc comment claims "NotTested and
    /// Pending both use the Warning token" — this is the property that actually makes that
    /// true; previously only <see cref="IsNotTested"/> was bound to the Warning style class,
    /// so a check mid-run ("CHECKING…") rendered with the default Body foreground instead.</summary>
    public bool IsWarn => Status is CheckStatus.NotTested or CheckStatus.Pending;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
