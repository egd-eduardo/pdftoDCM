using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfToDicom.App.ViewModels;

namespace PdfToDicom.App.Views;

/// <summary>
/// The single window that hosts the whole workflow.
/// </summary>
/// <remarks>
/// The code behind is limited to concerns that genuinely belong to the view: drag and
/// drop, which is expressed in terms of WPF events, and the initial keyboard focus.
/// Everything else lives in <see cref="MainViewModel"/>.
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>Creates the window.</summary>
    public MainWindow()
    {
        InitializeComponent();

        // Drag and drop is handled at window level as well as on the drop zone, so a
        // document dropped anywhere on the window is accepted. An operator should not
        // have to aim.
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Focusing the Patient ID field means a barcode scanner, which behaves as a
        // keyboard, can be used the moment the window appears without a click.
        PatientIdBox.Focus();
        Keyboard.Focus(PatientIdBox);
    }

    /// <summary>
    /// Confirms before closing, then releases what the view model holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single place leaving the application is decided, so the exit button in the
    /// header, the window's own close button and Alt+F4 all behave the same way. The
    /// button simply closes the window and arrives here.
    /// </para>
    /// <para>
    /// Cancelling the close when the operator says no is the whole point: this application
    /// is left open all day on a shared workstation, and a stray click should not discard
    /// a form somebody has just filled in, nor abandon a transmission half way through.
    /// </para>
    /// </remarks>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (ViewModel is not null && !ViewModel.ConfirmExit())
        {
            e.Cancel = true;
            return;
        }

        (DataContext as IDisposable)?.Dispose();
    }

    // ------------------------------------------------------------------ Drag and drop

    private void OnDragEnter(object sender, DragEventArgs e) => UpdateDragState(e);

    private void OnDragOver(object sender, DragEventArgs e) => UpdateDragState(e);

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.IsDragOver = false;
        }
    }

    /// <summary>
    /// Accepts the drag when it carries exactly one file, and reflects that in the cursor
    /// so the operator knows before releasing whether the drop will be taken.
    /// </summary>
    /// <remarks>
    /// Whether the file is convertible is decided after the drop, from its content, not
    /// here from its name. Rejecting during the drag would mean either opening the file on
    /// every mouse move, which is wasteful, or judging it by its extension, which is
    /// exactly the guess the rest of this application refuses to make. A file that turns
    /// out to be unconvertible is reported plainly instead.
    /// </remarks>
    private void UpdateDragState(DragEventArgs e)
    {
        bool acceptable = TryGetDroppedFile(e, out _);

        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (ViewModel is not null)
        {
            ViewModel.IsDragOver = acceptable;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (ViewModel is null)
        {
            return;
        }

        ViewModel.IsDragOver = false;

        if (TryGetDroppedFile(e, out string? path) && path is not null)
        {
            ViewModel.LoadPdf(path);
        }
    }

    /// <summary>
    /// Extracts a single file path from the drag payload.
    /// </summary>
    /// <remarks>
    /// Only a single file is accepted. Silently taking the first of several dropped files
    /// would be ambiguous, and a document filed against the wrong patient is exactly the
    /// error this application must not make easy.
    /// </remarks>
    private static bool TryGetDroppedFile(DragEventArgs e, out string? path)
    {
        path = null;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1)
        {
            return false;
        }

        string candidate = files[0];

        if (!File.Exists(candidate))
        {
            return false;
        }

        path = candidate;
        return true;
    }
}
