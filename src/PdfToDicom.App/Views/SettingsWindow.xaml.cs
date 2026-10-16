using System.Windows;
using PdfToDicom.App.ViewModels;

namespace PdfToDicom.App.Views;

/// <summary>
/// The settings dialog.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>Creates the dialog.</summary>
    public SettingsWindow() => InitializeComponent();

    /// <summary>
    /// Offers the typed password, then clears the box whatever the answer was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PasswordBox deliberately does not expose its value as a bindable dependency
    /// property, because a bound password sits in memory as a plain string for as long as
    /// the binding lives. Handing it over here, at the moment the button is pressed, and
    /// clearing the box immediately afterwards keeps that window as short as the framework
    /// allows.
    /// </para>
    /// <para>
    /// Cleared on success and on failure alike. A wrong password left in the box invites
    /// the same wrong password to be submitted again, and a right one left in the box is a
    /// credential sitting on screen on a shared workstation.
    /// </para>
    /// <para>
    /// One handler serves both protected pages. The button names the box through its
    /// <c>Tag</c>, so the two pages share this code rather than each having a copy of it.
    /// </para>
    /// </remarks>
    private void OnUnlockSettings(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel ||
            sender is not FrameworkElement source ||
            source.Tag is not System.Windows.Controls.PasswordBox box)
        {
            return;
        }

        viewModel.TryUnlock(box.Password);
        box.Clear();
    }

    /// <summary>
    /// Attempts to save. The dialog stays open when validation rejects the configuration,
    /// so the operator can correct the offending value without losing the rest.
    /// </summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && viewModel.Save())
        {
            DialogResult = true;
        }
    }
}
