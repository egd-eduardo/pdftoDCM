using PdfToDicom.Core.Abstractions;

namespace PdfToDicom.Core.Monitoring;

/// <summary>
/// Watches a folder and raises an event when a PDF document appears in it.
/// </summary>
/// <remarks>
/// <para>
/// A file system notification arrives as soon as the file is created, which is typically
/// before the application producing it has finished writing. The monitor therefore waits
/// for a settle period and then confirms the file can be opened for exclusive reading
/// before announcing it. Without that check the operator would routinely be handed a
/// truncated document.
/// </para>
/// <para>
/// The event is raised on a thread pool thread. Consumers that update a user interface
/// must marshal to their own dispatcher.
/// </para>
/// </remarks>
public sealed class FolderMonitor : IDisposable
{
    private readonly IAppLogger _logger;
    private readonly TimeSpan _settleDelay;
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();

    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <summary>Raised with the full path of a PDF that is complete and readable.</summary>
    public event EventHandler<string>? PdfDetected;

    /// <summary>Raised when the watcher itself fails, for example if the folder is removed.</summary>
    public event EventHandler<string>? MonitoringFailed;

    /// <summary>Creates the monitor.</summary>
    /// <param name="logger">Diagnostic log.</param>
    /// <param name="settleDelayMilliseconds">
    /// How long to wait after a file appears before attempting to read it.
    /// </param>
    public FolderMonitor(IAppLogger logger, int settleDelayMilliseconds = 1500)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settleDelay = TimeSpan.FromMilliseconds(Math.Clamp(settleDelayMilliseconds, 100, 60_000));
    }

    /// <summary>True while a folder is being watched.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _watcher is not null;
            }
        }
    }

    /// <summary>The folder currently being watched, or null.</summary>
    public string? WatchedFolder { get; private set; }

    /// <summary>
    /// Begins watching the supplied folder, replacing any folder already being watched.
    /// </summary>
    /// <returns>True when monitoring started.</returns>
    public bool Start(string folder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        if (string.IsNullOrWhiteSpace(folder))
        {
            _logger.Warning("Folder monitoring was requested but no folder is configured.");
            return false;
        }

        if (!Directory.Exists(folder))
        {
            _logger.Warning($"Folder monitoring could not start because '{folder}' does not exist.");
            MonitoringFailed?.Invoke(this, $"The monitored folder '{folder}' does not exist.");
            return false;
        }

        try
        {
            var watcher = new FileSystemWatcher(folder, "*.pdf")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };

            watcher.Created += OnFileAppeared;
            watcher.Renamed += OnFileAppeared;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            lock (_stateLock)
            {
                _watcher = watcher;
                _announced.Clear();
            }

            WatchedFolder = folder;
            _logger.Information($"Folder monitoring started on '{folder}'.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Folder monitoring could not start on '{folder}'.", ex);
            MonitoringFailed?.Invoke(this, $"Folder monitoring could not start: {ex.Message}");
            return false;
        }
    }

    /// <summary>Stops watching. Safe to call when not running.</summary>
    public void Stop()
    {
        FileSystemWatcher? watcher;

        lock (_stateLock)
        {
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnFileAppeared;
            watcher.Renamed -= OnFileAppeared;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warning($"An error occurred while stopping folder monitoring: {ex.Message}");
        }

        _logger.Information($"Folder monitoring stopped on '{WatchedFolder}'.");
        WatchedFolder = null;
    }

    /// <summary>
    /// Forgets that a path was already announced, so that a file replaced under the same
    /// name is offered to the operator again.
    /// </summary>
    public void ForgetAnnounced(string path)
    {
        lock (_stateLock)
        {
            _announced.Remove(path);
        }
    }

    private void OnFileAppeared(object sender, FileSystemEventArgs e)
    {
        // The handler returns immediately so the watcher's internal buffer is not held
        // while the file is being polled for completeness.
        _ = Task.Run(() => AnnounceWhenReadyAsync(e.FullPath));
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Exception ex = e.GetException();
        _logger.Error("The folder monitor reported an error and has stopped.", ex);
        MonitoringFailed?.Invoke(this, $"Folder monitoring stopped unexpectedly: {ex.Message}");
    }

    /// <summary>
    /// Waits for the file to settle, confirms it can be opened and is not still growing,
    /// then raises <see cref="PdfDetected"/> exactly once for that path.
    /// </summary>
    private async Task AnnounceWhenReadyAsync(string path)
    {
        try
        {
            lock (_stateLock)
            {
                if (!_announced.Add(path))
                {
                    return;
                }
            }

            await Task.Delay(_settleDelay).ConfigureAwait(false);

            // Up to roughly fifteen seconds is allowed for a large report to finish being
            // written, which is generous but still bounded.
            const int maximumChecks = 30;
            long previousLength = -1;

            for (int check = 0; check < maximumChecks; check++)
            {
                if (!File.Exists(path))
                {
                    lock (_stateLock)
                    {
                        _announced.Remove(path);
                    }

                    return;
                }

                long length = new FileInfo(path).Length;

                if (length > 0 && length == previousLength && CanOpenExclusively(path))
                {
                    _logger.Information($"The monitored folder produced a new document: '{path}'.");
                    PdfDetected?.Invoke(this, path);
                    return;
                }

                previousLength = length;
                await Task.Delay(500).ConfigureAwait(false);
            }

            _logger.Warning(
                $"The file '{path}' appeared in the monitored folder but was still being written after " +
                "the maximum wait, so it was not offered for conversion.");

            lock (_stateLock)
            {
                _announced.Remove(path);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"An error occurred while handling '{path}' from the monitored folder.", ex);
        }
    }

    /// <summary>
    /// Confirms the file is no longer held open by the producing application.
    /// </summary>
    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return stream.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
