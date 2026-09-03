using System.Threading.Tasks;
using Avalonia.Controls;

namespace Soneto.App.Views;

/// <summary>
/// Phase 3 item 10 (§3.14): a small, reusable modal confirmation dialog -- this project's
/// first, since no confirmation-dialog pattern existed anywhere before this item. Kept
/// deliberately simple/proportionate (one message, one Cancel, one destructive Confirm button)
/// per this project's established "don't over-engineer for a handful of fixed cases" taste
/// (mirroring item 4's nav-rail/item 7's per-type add-form precedent) -- no general-purpose
/// dialog-service abstraction, just a Window a caller constructs and awaits.
///
/// <para>
/// <b>"Requires a real second confirming action" (this item's own explicit requirement, not a
/// single accidental-click delete):</b> the caller's own trigger button (e.g. Settings'
/// "Panic wipe...") is the FIRST action; this dialog's own <see cref="ConfirmButton"/> is a
/// SECOND, genuinely separate click on a distinct control, in a distinct window that must be
/// explicitly dismissed either way -- there is no code path that reaches the destructive action
/// from a single click anywhere in the call chain.
/// </para>
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the dialog modally over <paramref name="owner"/> and returns whether the user
    /// clicked <see cref="ConfirmButton"/> (true) or cancelled/closed it any other way (false).
    /// </summary>
    public static async Task<bool> ShowAsync(
        Window owner, string title, string message, string confirmText = "Confirm")
    {
        var dialog = new ConfirmDialog
        {
            Title = title,
        };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmText;

        bool confirmed = false;
        dialog.ConfirmButton.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        dialog.CancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
        return confirmed;
    }
}
