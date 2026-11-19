using System.Windows;
using System.Windows.Threading;

namespace WorklistExplorer.Wpf;

public partial class App : Application
{
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Shown rather than swallowed: a diagnostic tool that hides its own faults misleads.
        MessageBox.Show(e.Exception.ToString(), "Worklist Explorer, unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
