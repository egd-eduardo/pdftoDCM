using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using PdfToDicom.App.Infrastructure;
using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Logging;
using PdfToDicom.Core.Services;

namespace PdfToDicom.App.ViewModels;

/// <summary>
/// View model for the live log viewer.
/// </summary>
/// <remarks>
/// The viewer shows the entries retained in memory by the logger and appends new ones as
/// they occur, which lets a support engineer watch a failing transmission in real time
/// without leaving the application or opening the log file.
/// </remarks>
public sealed class LogViewerViewModel : ObservableObject, IDisposable
{
    private readonly ApplicationServices _services;
    private readonly Dispatcher _dispatcher;
    private readonly ICollectionView _view;

    private string _searchText = string.Empty;
    private bool _followNewEntries = true;
    private bool _showDebug;

    /// <summary>Creates the view model and subscribes to new log entries.</summary>
    public LogViewerViewModel(ApplicationServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _dispatcher = Dispatcher.CurrentDispatcher;

        Entries = new ObservableCollection<LogEntry>(_services.AppLogger.RecentEntries);

        _view = CollectionViewSource.GetDefaultView(Entries);
        _view.Filter = MatchesFilter;

        OpenFolderCommand = new RelayCommand(OpenLogFolder);

        _services.AppLogger.EntryWritten += OnEntryWritten;
    }

    /// <summary>Entries currently held in memory.</summary>
    public ObservableCollection<LogEntry> Entries { get; }

    /// <summary>Opens the log folder in Windows Explorer.</summary>
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>Free text filter applied to the message.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _view.Refresh();
            }
        }
    }

    /// <summary>Scroll to the newest entry as it arrives.</summary>
    public bool FollowNewEntries
    {
        get => _followNewEntries;
        set => SetProperty(ref _followNewEntries, value);
    }

    /// <summary>Include debug level entries in the list.</summary>
    public bool ShowDebug
    {
        get => _showDebug;
        set
        {
            if (SetProperty(ref _showDebug, value))
            {
                _view.Refresh();
            }
        }
    }

    /// <summary>Folder the log files are written to.</summary>
    public string LogFolder => _services.Logger.LogFolder;

    /// <summary>Raised when a new entry is appended, so the view can scroll to it.</summary>
    public event EventHandler<LogEntry>? EntryAppended;

    private bool MatchesFilter(object item)
    {
        if (item is not LogEntry entry)
        {
            return false;
        }

        if (!ShowDebug && entry.Level == LogLevel.Debug)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(SearchText)
            || entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void OnEntryWritten(object? sender, LogEntry entry)
    {
        // Entries are written from background threads during a transmission.
        void Append()
        {
            Entries.Add(entry);

            // The in memory list is bounded so a long running session cannot exhaust
            // memory through the viewer alone.
            while (Entries.Count > 2000)
            {
                Entries.RemoveAt(0);
            }

            if (FollowNewEntries)
            {
                EntryAppended?.Invoke(this, entry);
            }
        }

        if (_dispatcher.CheckAccess())
        {
            Append();
        }
        else
        {
            _dispatcher.BeginInvoke(Append);
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(LogFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _services.Logger.Warning($"The log folder could not be opened: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _services.AppLogger.EntryWritten -= OnEntryWritten;
}
