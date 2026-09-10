using System.Windows;
using PdfToDicom.App.ViewModels;
using PdfToDicom.Core.Logging;

namespace PdfToDicom.App.Views;

/// <summary>
/// A live view of the application log, so a support engineer can watch a failing
/// transmission without leaving the application.
/// </summary>
public partial class LogViewerWindow : Window
{
    private LogViewerViewModel? _viewModel;

    /// <summary>Creates the window.</summary>
    public LogViewerWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.EntryAppended -= OnEntryAppended;
        }

        _viewModel = e.NewValue as LogViewerViewModel;

        if (_viewModel is not null)
        {
            _viewModel.EntryAppended += OnEntryAppended;
        }
    }

    /// <summary>Keeps the newest entry in view while "follow" is enabled.</summary>
    private void OnEntryAppended(object? sender, LogEntry entry)
    {
        try
        {
            EntriesGrid.ScrollIntoView(entry);
        }
        catch
        {
            // Scrolling is a convenience; a race with virtualisation must not surface
            // as an error dialog over the operator's work.
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.EntryAppended -= OnEntryAppended;
        }

        (DataContext as IDisposable)?.Dispose();
    }
}
