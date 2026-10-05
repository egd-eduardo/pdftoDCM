using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PdfToDicom.App.Infrastructure;
using PdfToDicom.App.ViewModels;
using PdfToDicom.App.Views;
using PdfToDicom.Core.Configuration;
using PdfToDicom.Core.Services;

namespace PdfToDicom.App;

/// <summary>
/// Application entry point and lifetime host.
/// </summary>
/// <remarks>
/// Responsible for building the service graph, applying the saved theme and language,
/// installing the last resort exception handlers, and making sure the audit trail records
/// the start and the close of every session.
/// </remarks>
public partial class App : Application
{
    private ApplicationServices? _services;
    private Mutex? _singleInstanceMutex;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Unhandled exceptions must never present the operator with a .NET crash dialog.
        // They are logged, explained in plain language and, where possible, survived.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        if (!TryClaimSingleInstance())
        {
            // A second instance would run a second folder monitor over the same folder,
            // and both would convert and transmit the same document. The existing window
            // is brought forward instead.
            ActivateExistingInstance();
            Shutdown(0);
            return;
        }

        try
        {
            _services = new ApplicationServices(verboseLogging: e.Args.Contains("--verbose"));
            AppConfiguration configuration = _services.ConfigurationService.Current;

            ApplyCulture(configuration.Language);
            ThemeManager.Apply(configuration.DarkMode);
            LocalizationSource.Instance.Attach(_services.Localizer);

            _services.Logger.Information("The application is starting.");
            _services.AuditLogger.ApplicationStarted(ProductVersion);

            var viewModel = new MainViewModel(_services);
            var window = new MainWindow { DataContext = viewModel };

            MainWindow = window;
            window.Show();

            // A file passed on the command line, for example by "Open with", is loaded once
            // the window exists so that any validation message has somewhere to appear.
            string? initialPdf = e.Args.FirstOrDefault(
                a => !a.StartsWith("--", StringComparison.Ordinal) &&
                     a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

            if (initialPdf is not null && File.Exists(initialPdf))
            {
                viewModel.LoadPdf(initialPdf);
            }
        }
        catch (Exception ex)
        {
            // Startup failure is the one case where there may be no log and no window, so
            // the operator is told directly and the detail is written beside the executable.
            ReportFatalStartupFailure(ex);
            Shutdown(1);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _services?.Logger.Information("The application is closing.");
            _services?.AuditLogger.ApplicationClosed();
            _services?.Dispose();
        }
        catch
        {
            // Nothing useful can be done at this point and the process is ending anyway.
        }
        finally
        {
            _singleInstanceMutex?.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Attempts to become the only running instance for the current user.
    /// </summary>
    /// <remarks>
    /// The mutex name is scoped to the local session with the "Local" prefix, so each
    /// user of a shared clinical workstation, and each Remote Desktop session, gets its
    /// own instance. A machine wide lock would prevent a second nurse from working.
    /// </remarks>
    /// <returns>True when this process holds the instance lock.</returns>
    private bool TryClaimSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\PdfToDicom.SingleInstance", out bool createdNew);
            return createdNew;
        }
        catch (Exception)
        {
            // If the lock cannot be evaluated, starting is the safer failure: refusing to
            // start would leave the operator with no way to send a document at all.
            return true;
        }
    }

    /// <summary>
    /// Brings the window of the already running instance to the foreground so the operator
    /// sees a response to their double click rather than nothing at all.
    /// </summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            Process current = Process.GetCurrentProcess();

            Process? existing = Process.GetProcessesByName(current.ProcessName)
                .FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero);

            if (existing is not null)
            {
                NativeMethods.ShowWindow(existing.MainWindowHandle, NativeMethods.ShowRestore);
                NativeMethods.SetForegroundWindow(existing.MainWindowHandle);
            }
        }
        catch
        {
            // Best effort only. Failing to raise the other window is not worth an error.
        }
    }

    /// <summary>
    /// Applies the configured language to the current thread so that dates, numbers and
    /// the date picker follow the same locale as the translated text.
    /// </summary>
    private static void ApplyCulture(string language)
    {
        try
        {
            var culture = new CultureInfo(language);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // An unrecognised culture name falls back to the machine default rather than
            // preventing the application from starting.
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Logger.Error("An unhandled error reached the user interface thread.", e.Exception);

        MessageBox.Show(
            BuildErrorMessage(e.Exception),
            "PDF to DICOM",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // The exception is marked handled so that a fault in one action does not discard
        // patient information the operator has already typed into the form.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _services?.Logger.Error("An unhandled error occurred on a background thread.", exception);
            _services?.Logger.Flush();
        }
    }

    private string BuildErrorMessage(Exception exception)
    {
        string logLocation = _services?.Logger.LogFolder ?? "the log folder";

        return
            "The application encountered an unexpected problem." + Environment.NewLine + Environment.NewLine +
            exception.Message + Environment.NewLine + Environment.NewLine +
            "Your entries have been kept. The full technical detail has been written to:" + Environment.NewLine +
            logLocation;
    }

    /// <summary>
    /// Reports a failure that occurred before logging was available, writing the detail
    /// next to the user's application data so it can be sent to support.
    /// </summary>
    private static void ReportFatalStartupFailure(Exception exception)
    {
        string detail = exception.ToString();

        try
        {
            string folder = ConfigurationPaths.ApplicationDataFolder;
            Directory.CreateDirectory(folder);

            File.WriteAllText(
                Path.Combine(folder, $"startup-failure-{DateTime.Now:yyyyMMdd-HHmmss}.txt"),
                detail);
        }
        catch
        {
            // If even this fails, the message box below is the only channel left.
        }

        MessageBox.Show(
            "PDF to DICOM could not start." + Environment.NewLine + Environment.NewLine +
            exception.Message + Environment.NewLine + Environment.NewLine +
            "Please contact your IT department and quote the message above.",
            "PDF to DICOM",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    /// <summary>Version reported in the audit trail and the About box.</summary>
    public static string ProductVersion =>
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "3.0.0";
}

/// <summary>
/// The two window functions needed to raise the existing instance. Declared locally so
/// the application takes no dependency on a wrapper library for two calls.
/// </summary>
internal static class NativeMethods
{
    /// <summary>SW_RESTORE: shows a window and restores it if it was minimised.</summary>
    public const int ShowRestore = 9;

    /// <summary>Sets a window's show state.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr windowHandle, int commandShow);

    /// <summary>Brings a window to the foreground and activates it.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr windowHandle);
}
