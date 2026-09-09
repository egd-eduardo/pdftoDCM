using System.Windows;

namespace PdfToDicom.App.Views;

/// <summary>
/// A read only listing of every attribute in a generated object, so that an advanced user
/// can inspect exactly what will be sent before it leaves the workstation.
/// </summary>
public partial class TagViewerWindow : Window
{
    /// <summary>Creates the window.</summary>
    public TagViewerWindow() => InitializeComponent();
}
