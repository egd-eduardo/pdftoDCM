using System.Globalization;
using System.Text;
using WorklistExplorer.Core.Configuration;
using WorklistExplorer.Core.Dicom;
using WorklistExplorer.Core.Query;
using WorklistExplorer.Core.Text;

namespace WorklistExplorer.Cli;

/// <summary>
/// Console mode: same configuration, same query, CSV out, full diagnostics on screen.
/// Exit code 0 when at least one row was returned, 1 when every server answered with
/// nothing, 2 when something failed or the ceiling was reached.
/// </summary>
internal static class Program
{
    private const string Help = """
        Worklist Explorer, console mode. Read only: C-ECHO and C-FIND only.

        Usage: WorklistExplorer.Cli [options]

        Configuration
          --config PATH         PDF to DICOM config.json to import (opened read only).
                                Default: the PDF to DICOM application's own file.
          --list                Show the imported servers and their settings, then stop.
          --server NAME         Query only this worklist (repeatable). Default: every enabled one.
          --include-disabled    Also query worklists with IsEnabled false.

        Server entered by hand (instead of, or when there is no, configuration)
          --host H --port P --called AE --calling AE [--tls] [--tls-accept-any] [--name N]

        Question
          --date MODE           any (default) | today | yesterday | week | range
          --from yyyy-MM-dd     with --date range; either end may be omitted
          --to yyyy-MM-dd
          --modality M          send this modality instead of the imported one ("" for every modality)
          --station AE          send this scheduled station instead of the imported one
          --clear-filters       send empty modality and station keys (the plainest question)
          --timeout S           seconds without a response before giving up (overrides the file)

        Output
          --out FILE.csv        CSV of every row fetched (UTF-8 with BOM). Default: a timestamped
                                file in the current folder. Use - for standard output; the
                                diagnostics then go to standard error.
          --ceiling N           rows kept per server before further rows are only counted
                                (default 1000000). Reaching it is always reported.
          --raw FOLDER          write every received dataset to a text file in FOLDER.
                                These files contain patient identifiable data.
          --echo                C-ECHO the selected servers and stop.
        """;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Options o;
        try
        {
            o = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Use --help for the options.");
            return 2;
        }
        if (o.ShowHelp)
        {
            Console.WriteLine(Help);
            return 0;
        }

        // Diagnostics go to standard error when the CSV is going to standard output, so the
        // two never mix in a redirected file.
        var info = o.OutPath == "-" ? Console.Error : Console.Out;

        var targets = new List<QueryTarget>();
        if (o.Host is not null)
        {
            targets.Add(new QueryTarget
            {
                Name = o.ManualName ?? "manual",
                Host = o.Host,
                Port = o.Port ?? 104,
                CalledAeTitle = o.Called ?? "",
                CallingAeTitle = o.Calling ?? "",
                UseTls = o.Tls,
                AcceptAnyTlsCertificate = o.TlsAcceptAny
            });
        }
        else
        {
            var path = o.ConfigPath ?? ConfigurationReader.DefaultPath();
            ImportedConfiguration config;
            try
            {
                config = ConfigurationReader.Read(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"The configuration could not be read from {path}: {ex.Message}");
                Console.Error.WriteLine("Point at a file with --config, or enter a server with --host, --port, --called and --calling.");
                return 2;
            }

            info.WriteLine($"Configuration: {config.SourcePath} (opened read only)");
            info.WriteLine($"Top level CallingAeTitle: '{config.CallingAeTitle}'");
            foreach (var note in config.Notes) info.WriteLine($"  {note}");

            var imported = config.Worklists.Select(w => QueryTarget.FromImported(w, config.CallingAeTitle)).ToList();
            PrintServers(info, imported);
            if (o.ListOnly) return 0;

            if (imported.Count == 0)
            {
                Console.Error.WriteLine("No worklist to query. Enter one with --host, --port, --called and --calling.");
                return 2;
            }

            foreach (var t in imported)
            {
                var named = o.Servers.Count == 0 || o.Servers.Contains(t.Name, StringComparer.OrdinalIgnoreCase);
                var enabledOk = t.ImportedIsEnabled || o.IncludeDisabled || o.Servers.Count > 0;
                if (named && enabledOk) targets.Add(t);
            }
            var unknown = o.Servers.Where(s => !imported.Any(t => string.Equals(t.Name, s, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0)
            {
                Console.Error.WriteLine($"No imported worklist is named {string.Join(", ", unknown.Select(u => "'" + u + "'"))}.");
                return 2;
            }
        }

        foreach (var t in targets)
        {
            if (o.ClearFilters) { t.Modality = ""; t.ScheduledStationAeTitle = ""; }
            if (o.Modality is not null) t.Modality = o.Modality;
            if (o.Station is not null) t.ScheduledStationAeTitle = o.Station;
            if (o.TimeoutSeconds is { } s) t.TimeoutSeconds = s;
            if (!string.IsNullOrEmpty(t.Modality) || !string.IsNullOrEmpty(t.ScheduledStationAeTitle))
            {
                info.WriteLine($"NOTE: {t.Name} will be asked with modality '{t.Modality}' and scheduled station '{t.ScheduledStationAeTitle}'. " +
                               "These keys exclude every other modality or station. Use --clear-filters for the plainest question.");
            }
        }

        if (targets.Count == 0)
        {
            Console.Error.WriteLine("Every imported worklist is disabled. Name one with --server, or use --include-disabled.");
            return 2;
        }

        if (o.EchoOnly)
        {
            foreach (var line in await Task.WhenAll(targets.Select(t => WorklistQueryRunner.EchoAsync(t, CancellationToken.None))))
                info.WriteLine(line);
            return 0;
        }

        if (o.RawFolder is not null)
        {
            info.WriteLine($"WARNING: raw mode is on. Every received dataset will be written to {Path.GetFullPath(o.RawFolder)}. " +
                           "These files contain patient identifiable data; store and delete them according to Affidea policy.");
        }

        var options = new QueryOptions { Dates = o.Dates, Ceiling = o.Ceiling, RawFolder = o.RawFolder };
        var log = new DiagnosticLog();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Progress<T> would post to a thread pool with no ordering; a direct writer keeps the
        // console lines in the order they happened.
        var progress = new DirectProgress(line => { lock (info) info.WriteLine(line); });
        var results = await Task.WhenAll(targets.Select(t => WorklistQueryRunner.RunAsync(t, options, progress, cts.Token)));

        var exit = 0;
        var totalRows = 0;
        foreach (var r in results)
        {
            var text = r.Report.ToText();
            info.WriteLine();
            info.Write(text);
            log.Write(text);
            totalRows += r.Rows.Count;
            if (r.Report.Failure is not null || r.Report.CeilingReached || !r.Report.FinalStatusIsSuccess) exit = 2;
        }

        var allRows = results.SelectMany(r => r.Rows).ToList();
        if (o.OutPath == "-")
        {
            Console.OutputEncoding = CsvExporter.Utf8WithBom;
            CsvExporter.Write(Console.Out, allRows);
        }
        else
        {
            var outPath = o.OutPath ?? $"worklist_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            CsvExporter.Write(outPath, allRows);
            info.WriteLine();
            info.WriteLine($"CSV: {Path.GetFullPath(outPath)} ({allRows.Count.ToString("N0", CultureInfo.InvariantCulture)} rows). It contains patient identifiable data.");
        }
        info.WriteLine($"Diagnostic log (no patient data): {log.FilePath}");
        info.WriteLine($"Total scheduled procedure steps kept across {results.Length} server(s): {totalRows.ToString("N0", CultureInfo.InvariantCulture)}");

        if (exit == 0 && totalRows == 0) exit = 1;
        return exit;
    }

    private static void PrintServers(TextWriter w, List<QueryTarget> targets)
    {
        w.WriteLine($"Imported worklists: {targets.Count}");
        foreach (var t in targets)
        {
            w.WriteLine($"  {t.Name}{(t.ImportedIsEnabled ? "" : " (DISABLED in the file)")}");
            w.WriteLine($"    {DicomConnectionText(t)}");
            w.WriteLine($"    Modality filter:          '{t.ImportedModality}'{(t.ImportedModality.Length == 0 ? " (every modality)" : " <- excludes every other modality")}");
            w.WriteLine($"    Scheduled station filter: '{t.ImportedStation}'{(t.ImportedStation.Length == 0 ? " (every station)" : " <- excludes every other station")}");
            w.WriteLine($"    DateRange in the file:    {t.ImportedDateRange} (this tool uses its own date mode instead)");
            w.WriteLine($"    MaximumResults in file:   {t.ImportedMaximumResults} (display cap in the other application; not a limit here)");
        }
    }

    private static string DicomConnectionText(QueryTarget t) =>
        $"calling '{t.CallingAeTitle}', called '{t.CalledAeTitle}', {t.Host}:{t.Port}, " +
        $"{(t.UseTls ? (t.AcceptAnyTlsCertificate ? "TLS accepting any certificate" : "TLS") : "no TLS")}, timeout {t.TimeoutSeconds} s";

    private sealed class DirectProgress(Action<string> write) : IProgress<string>
    {
        public void Report(string value) => write(value);
    }

    private sealed class Options
    {
        public bool ShowHelp, ListOnly, IncludeDisabled, ClearFilters, EchoOnly, Tls, TlsAcceptAny;
        public string? ConfigPath, Host, Called, Calling, ManualName, Modality, Station, OutPath, RawFolder;
        public int? Port, TimeoutSeconds;
        public int Ceiling = QueryOptions.DefaultCeiling;
        public List<string> Servers { get; } = new();
        public DateSelection Dates = DateSelection.Any;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            string? dateMode = null;
            DateTime? from = null, to = null;
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
                switch (args[i])
                {
                    case "--help" or "-h" or "/?": o.ShowHelp = true; break;
                    case "--config": o.ConfigPath = Next(); break;
                    case "--list": o.ListOnly = true; break;
                    case "--server": o.Servers.Add(Next()); break;
                    case "--include-disabled": o.IncludeDisabled = true; break;
                    case "--host": o.Host = Next(); break;
                    case "--port": o.Port = ParseInt(args[i], Next()); break;
                    case "--called": o.Called = Next(); break;
                    case "--calling": o.Calling = Next(); break;
                    case "--name": o.ManualName = Next(); break;
                    case "--tls": o.Tls = true; break;
                    case "--tls-accept-any": o.Tls = true; o.TlsAcceptAny = true; break;
                    case "--date": dateMode = Next().ToLowerInvariant(); break;
                    case "--from": from = ParseDate(args[i], Next()); break;
                    case "--to": to = ParseDate(args[i], Next()); break;
                    case "--modality": o.Modality = Next(); break;
                    case "--station": o.Station = Next(); break;
                    case "--clear-filters": o.ClearFilters = true; break;
                    case "--timeout": o.TimeoutSeconds = ParseInt(args[i], Next()); break;
                    case "--out": o.OutPath = Next(); break;
                    case "--ceiling": o.Ceiling = ParseInt(args[i], Next()); break;
                    case "--raw": o.RawFolder = Next(); break;
                    case "--echo": o.EchoOnly = true; break;
                    default: throw new ArgumentException($"Unknown option {args[i]}.");
                }
            }

            o.Dates = dateMode switch
            {
                null or "any" => DateSelection.Any,
                "today" => new DateSelection(DateMode.Today),
                "yesterday" => new DateSelection(DateMode.YesterdayAndToday),
                "week" => new DateSelection(DateMode.LastSevenDays),
                "range" => new DateSelection(DateMode.Explicit, from, to),
                _ => throw new ArgumentException($"Unknown date mode '{dateMode}'.")
            };
            if (dateMode != "range" && (from is not null || to is not null))
                throw new ArgumentException("--from and --to need --date range.");
            if (o.Ceiling < 1) throw new ArgumentException("--ceiling must be at least 1.");
            if (o.Host is not null && (o.Called is null || o.Calling is null))
                throw new ArgumentException("A server entered by hand needs --called and --calling as well as --host.");
            return o;
        }

        private static int ParseInt(string name, string value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException($"{name} expects a whole number, not '{value}'.");

        private static DateTime ParseDate(string name, string value) =>
            DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : throw new ArgumentException($"{name} expects yyyy-MM-dd, not '{value}'.");
    }
}
