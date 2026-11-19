using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using WorklistExplorer.Core.Configuration;
using WorklistExplorer.Core.Dicom;
using WorklistExplorer.Core.Query;
using WorklistExplorer.Core.Text;

namespace WorklistExplorer.Wpf;

/// <summary>
/// Code behind rather than a view model framework: the window is a thin shell over the
/// Core library, and keeping it in one file makes it easy to audit that nothing here
/// talks to the network except through the Core runner.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<QueryTarget> _servers = new();
    private readonly DiagnosticLog _log = new();
    private readonly Dictionary<QueryTarget, (string Modality, string Station)> _importedFilters = new();

    /// <summary>Everything fetched by the last query, in arrival order. Never filtered.</summary>
    private List<WorklistRow> _all = new();

    /// <summary>The rows after the local filter and the current sort; the pager pages over this.</summary>
    private List<WorklistRow> _view = new();

    private string? _sortProperty;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private int _page = 1;
    private int _pageSize = 500;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        ServersGrid.ItemsSource = _servers;
        LogPathText.Text = $"Also written to {_log.FilePath} (no patient data in this log).";

        foreach (var c in WorklistRow.Columns)
        {
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = c.Header,
                Binding = new Binding(c.Property) { Mode = BindingMode.OneWay },
                SortMemberPath = c.Property
            });
        }

        ConfigPathBox.Text = ConfigurationReader.DefaultPath();
        if (File.Exists(ConfigPathBox.Text)) LoadConfig(ConfigPathBox.Text);
        else ConfigNotes.Text = $"No configuration at {ConfigPathBox.Text}. Browse to a copy or add a server by hand.";
        RefreshPage();
    }

    // ---------- configuration ----------

    private void OnBrowseConfig(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Configuration (*.json)|*.json|All files|*.*", CheckFileExists = true };
        if (File.Exists(ConfigPathBox.Text)) dialog.InitialDirectory = Path.GetDirectoryName(ConfigPathBox.Text);
        if (dialog.ShowDialog(this) == true)
        {
            ConfigPathBox.Text = dialog.FileName;
            LoadConfig(dialog.FileName);
        }
    }

    private void OnLoadConfig(object sender, RoutedEventArgs e) => LoadConfig(ConfigPathBox.Text);

    private void LoadConfig(string path)
    {
        ImportedConfiguration config;
        try
        {
            config = ConfigurationReader.Read(path);
        }
        catch (Exception ex)
        {
            ConfigNotes.Text = $"The configuration could not be read: {ex.Message}. Add a server by hand if needed.";
            AppendDiagnostics($"Configuration {path} could not be read: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Servers entered by hand are kept; imported ones are replaced.
        foreach (var old in _servers.Where(s => s.Origin == "imported").ToList()) _servers.Remove(old);
        _importedFilters.Clear();
        foreach (var w in config.Worklists)
        {
            var t = QueryTarget.FromImported(w, config.CallingAeTitle);
            _servers.Add(t);
            _importedFilters[t] = (t.Modality, t.ScheduledStationAeTitle);
        }

        ConfigNotes.Text = $"{config.SourcePath}; top level CallingAeTitle '{config.CallingAeTitle}'. " + string.Join(" ", config.Notes);
        var sb = new StringBuilder();
        sb.AppendLine($"Configuration loaded read only from {config.SourcePath}");
        sb.AppendLine($"  Top level CallingAeTitle: '{config.CallingAeTitle}'");
        foreach (var note in config.Notes) sb.AppendLine($"  {note}");
        foreach (var t in _servers.Where(s => s.Origin == "imported"))
        {
            sb.AppendLine($"  {t.Name}: calling '{t.CallingAeTitle}', called '{t.CalledAeTitle}', {t.Host}:{t.Port}, TLS {t.UseTls}, " +
                          $"modality '{t.ImportedModality}', station '{t.ImportedStation}', DateRange {t.ImportedDateRange}, " +
                          $"MaximumResults {t.ImportedMaximumResults}, enabled {t.ImportedIsEnabled}");
            if (t.ImportedModality.Length > 0 || t.ImportedStation.Length > 0)
                sb.AppendLine($"    NOTE: this entry carries a modality or station filter; it excludes every other modality or station.");
        }
        AppendDiagnostics(sb.ToString());

        // The old display cap becomes the default page size, never a fetch limit.
        var cap = _servers.FirstOrDefault(s => s.ImportedMaximumResults > 0)?.ImportedMaximumResults;
        if (cap is > 0)
        {
            _pageSize = cap.Value;
            PageSizeBox.Text = _pageSize.ToString(CultureInfo.InvariantCulture);
        }
        StatusText.Text = $"{_servers.Count} server(s) listed. Check the red columns before querying.";
    }

    // ---------- servers ----------

    private void OnAddServer(object sender, RoutedEventArgs e)
    {
        var t = new QueryTarget { Name = "manual", Port = 104 };
        _servers.Add(t);
        ServersGrid.SelectedItem = t;
        ServersGrid.ScrollIntoView(t);
    }

    private void OnRemoveServer(object sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is QueryTarget t)
        {
            _servers.Remove(t);
            _importedFilters.Remove(t);
        }
    }

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        ServersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var t in _servers)
        {
            t.Modality = "";
            t.ScheduledStationAeTitle = "";
        }
        ServersGrid.Items.Refresh();
        StatusText.Text = "Modality and station keys will be sent empty (every modality, every station). The file is unchanged.";
    }

    private void OnRestoreFilters(object sender, RoutedEventArgs e)
    {
        ServersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var (t, f) in _importedFilters)
        {
            t.Modality = f.Modality;
            t.ScheduledStationAeTitle = f.Station;
        }
        ServersGrid.Items.Refresh();
        StatusText.Text = "Modality and station keys restored to the values in the configuration file.";
    }

    private async void OnEcho(object sender, RoutedEventArgs e)
    {
        ServersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var ticked = _servers.Where(s => s.Selected).ToList();
        if (ticked.Count == 0) { StatusText.Text = "No server is ticked."; return; }
        StatusText.Text = "C-ECHO in progress...";
        var lines = await Task.WhenAll(ticked.Select(t => WorklistQueryRunner.EchoAsync(t, CancellationToken.None)));
        var text = string.Join(Environment.NewLine, lines);
        AppendDiagnostics(text);
        StatusText.Text = text.Replace(Environment.NewLine, "  |  ");
    }

    // ---------- question ----------

    private void OnDateModeChanged(object sender, SelectionChangedEventArgs e)
    {
        // Raised once during InitializeComponent, before the pickers exist.
        if (FromPicker is null || ToPicker is null) return;
        var isExplicit = SelectedDateMode() == DateMode.Explicit;
        FromPicker.IsEnabled = isExplicit;
        ToPicker.IsEnabled = isExplicit;
    }

    private DateMode SelectedDateMode() =>
        DateModeBox.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<DateMode>(tag, out var mode)
            ? mode : DateMode.AnyDate;

    private void OnRawModeChanged(object sender, RoutedEventArgs e)
    {
        var on = RawModeBox.IsChecked == true;
        RawFolderBox.IsEnabled = on;
        RawBrowseButton.IsEnabled = on;
        RawWarning.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowseRawFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Folder for raw datasets (will contain patient identifiable data)" };
        if (dialog.ShowDialog(this) == true) RawFolderBox.Text = dialog.FolderName;
    }

    private async void OnQuery(object sender, RoutedEventArgs e)
    {
        ServersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var ticked = _servers.Where(s => s.Selected).ToList();
        if (ticked.Count == 0) { StatusText.Text = "No server is ticked."; return; }

        if (!int.TryParse(CeilingBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ceiling) || ceiling < 1)
        {
            StatusText.Text = "The safety ceiling must be a whole number of at least 1.";
            return;
        }

        string? rawFolder = null;
        if (RawModeBox.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(RawFolderBox.Text)) { StatusText.Text = "Raw mode is on but no folder is chosen."; return; }
            rawFolder = RawFolderBox.Text;
        }

        var dates = new DateSelection(SelectedDateMode(), FromPicker.SelectedDate, ToPicker.SelectedDate);
        var options = new QueryOptions { Dates = dates, Ceiling = ceiling, RawFolder = rawFolder };

        QueryButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        _cts = new CancellationTokenSource();
        StatusText.Text = $"Querying {ticked.Count} server(s)...";
        AppendDiagnostics($"---- Query started {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {ticked.Count} server(s) ----");
        if (rawFolder is not null) AppendDiagnostics($"Raw mode ON: datasets with patient identifiable data are being written under {rawFolder}");

        // Progress<T> captures the UI context, so log lines from the network threads land
        // on the dispatcher in order.
        var progress = new Progress<string>(AppendDiagnostics);
        QueryResult[] results;
        try
        {
            results = await Task.WhenAll(ticked.Select(t => WorklistQueryRunner.RunAsync(t, options, progress, _cts.Token)));
        }
        finally
        {
            QueryButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _cts.Dispose();
            _cts = null;
        }

        var summary = new StringBuilder();
        var ceilingNotes = new List<string>();
        foreach (var r in results)
        {
            var text = r.Report.ToText();
            AppendDiagnostics(text);
            _log.Write(text);

            var rep = r.Report;
            if (rep.RowsKept == 0 && rep.RowsBeyondCeiling == 0)
                summary.AppendLine($"{rep.ServerName}: no scheduled procedures were returned; {rep.Question}. Final status {rep.FinalStatus}." +
                                   (rep.Failure is null ? "" : $" {rep.Failure}."));
            else
                summary.AppendLine($"{rep.ServerName}: {rep.RowsKept:N0} row(s) kept from {rep.PendingResponses:N0} pending response(s), " +
                                   $"{rep.Discards.Count} discarded, final status {rep.FinalStatus}, {rep.Elapsed.TotalSeconds:0.0} s." +
                                   (rep.Failure is null ? "" : $" {rep.Failure}."));
            if (rep.CeilingReached)
                ceilingNotes.Add($"{rep.ServerName}: the ceiling of {rep.Ceiling:N0} rows was reached and {rep.RowsBeyondCeiling:N0} further row(s) were counted but not kept");
        }

        _all = results.SelectMany(r => r.Rows).ToList();
        SummaryText.Text = summary.ToString().TrimEnd();
        CeilingBanner.Text = ceilingNotes.Count == 0 ? "" : "INCOMPLETE. " + string.Join("; ", ceilingNotes) + ". Raise the ceiling to see everything.";
        CeilingBanner.Visibility = ceilingNotes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        _page = 1;
        ApplyFilterAndSort();
        StatusText.Text = $"Query finished. {_all.Count:N0} row(s) fetched in total. Details on the Diagnostics tab.";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---------- results, filter, sort, pager ----------

    private void OnLocalFilterChanged(object sender, TextChangedEventArgs e)
    {
        _page = 1;
        ApplyFilterAndSort();
    }

    private void OnClearLocalFilter(object sender, RoutedEventArgs e) => LocalFilterBox.Text = "";

    private void ApplyFilterAndSort()
    {
        var filter = new LocalFilter(LocalFilterBox.Text);
        IEnumerable<WorklistRow> rows = filter.IsEmpty
            ? _all
            : _all.Where(r => filter.Matches(r.PatientName) || filter.Matches(r.PatientID) || filter.Matches(r.AccessionNumber));

        if (_sortProperty is not null)
        {
            var column = WorklistRow.Columns.First(c => c.Property == _sortProperty);
            // OrderBy is stable, so rows that compare equal keep their arrival order.
            rows = _sortProperty == nameof(WorklistRow.ResponseNumber)
                ? (_sortDirection == ListSortDirection.Ascending ? rows.OrderBy(r => r.ResponseNumber) : rows.OrderByDescending(r => r.ResponseNumber))
                : (_sortDirection == ListSortDirection.Ascending
                    ? rows.OrderBy(column.Get, StringComparer.CurrentCultureIgnoreCase)
                    : rows.OrderByDescending(column.Get, StringComparer.CurrentCultureIgnoreCase));
        }

        _view = rows.ToList();
        RefreshPage();
    }

    /// <summary>Sorts the whole result set, not just the visible page.</summary>
    private void OnResultsSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var property = e.Column.SortMemberPath;
        _sortDirection = _sortProperty == property && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _sortProperty = property;
        foreach (var c in ResultsGrid.Columns) c.SortDirection = null;
        e.Column.SortDirection = _sortDirection;
        ApplyFilterAndSort();
    }

    private PageWindow CurrentWindow() => PageWindow.For(_view.Count, _pageSize, _page);

    private List<WorklistRow> CurrentPageRows()
    {
        var w = CurrentWindow();
        return _view.GetRange(w.Skip, w.Take);
    }

    private void RefreshPage()
    {
        var w = CurrentWindow();
        _page = w.Page;
        ResultsGrid.ItemsSource = _view.GetRange(w.Skip, w.Take);
        PageText.Text = w.Describe();
        var filtered = _view.Count == _all.Count ? "" : $"; {_view.Count:N0} match the local filter";
        TotalsText.Text = $"Fetched in total: {_all.Count:N0}{filtered}; on this page: {w.Take:N0}";
        ExportAllButton.Content = $"Export everything fetched to CSV ({_all.Count:N0} rows, filter ignored)";
    }

    private void OnFirstPage(object sender, RoutedEventArgs e) { _page = 1; RefreshPage(); }
    private void OnPreviousPage(object sender, RoutedEventArgs e) { _page--; RefreshPage(); }
    private void OnNextPage(object sender, RoutedEventArgs e) { _page++; RefreshPage(); }
    private void OnLastPage(object sender, RoutedEventArgs e) { _page = int.MaxValue; RefreshPage(); }

    private void OnApplyPageSize(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PageSizeBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) || size < 1)
        {
            StatusText.Text = "The page size must be a whole number of at least 1.";
            return;
        }
        // Keep the first visible row on screen after the change.
        var firstRow = CurrentWindow().Skip;
        _pageSize = size;
        _page = firstRow / size + 1;
        RefreshPage();
    }

    // ---------- export ----------

    private void OnExportPage(object sender, RoutedEventArgs e) => Export(CurrentPageRows(), "page");

    private void OnExportAll(object sender, RoutedEventArgs e) => Export(_all, "all");

    private void Export(List<WorklistRow> rows, string kind)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"worklist_{kind}_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            CsvExporter.Write(dialog.FileName, rows);
            StatusText.Text = $"{rows.Count:N0} row(s) written to {dialog.FileName}. The file contains patient identifiable data.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"The CSV could not be written: {ex.Message}";
        }
    }

    // ---------- diagnostics ----------

    private void AppendDiagnostics(string text)
    {
        DiagnosticsBox.AppendText(text.EndsWith(Environment.NewLine) ? text : text + Environment.NewLine);
        DiagnosticsBox.ScrollToEnd();
    }

    private void OnSaveDiagnostics(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = $"worklist_diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt" };
        if (dialog.ShowDialog(this) == true)
            File.WriteAllText(dialog.FileName, DiagnosticsBox.Text, new UTF8Encoding(true));
    }

    private void OnClearDiagnostics(object sender, RoutedEventArgs e) => DiagnosticsBox.Clear();

    protected override void OnClosing(CancelEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }
}
