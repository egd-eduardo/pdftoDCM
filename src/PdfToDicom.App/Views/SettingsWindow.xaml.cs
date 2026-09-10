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
