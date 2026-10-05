using System.Windows;
using PdfToDicom.App.ViewModels;

namespace PdfToDicom.App.Views;

/// <summary>
/// The worklist search window.
/// </summary>
public partial class WorklistWindow : Window
{
    /// <summary>Creates the window.</summary>
    public WorklistWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Confirms the selection and closes.
    /// </summary>
    /// <remarks>
    /// Double clicking a row does the same thing, which is what anyone used to a worklist
    /// will try first.
    /// </remarks>
    private void OnUse(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorklistViewModel { HasSelection: true })
        {
            DialogResult = true;
        }
    }
}
