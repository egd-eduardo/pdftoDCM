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
    /// Carries the typed password to the view model.
    /// </summary>
    /// <remarks>
    /// PasswordBox deliberately does not expose its value as a bindable dependency
    /// property, because a bound password sits in memory as a plain string for as long as
    /// the binding lives. Passing it across explicitly, and clearing the box once it has
    /// been checked, keeps that window short.
    /// </remarks>
    private void OnWorklistPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && sender is System.Windows.Controls.PasswordBox box)
        {
            viewModel.WorklistPasswordAttempt = box.Password;
        }
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
