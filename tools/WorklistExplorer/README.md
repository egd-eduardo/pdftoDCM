# Worklist Explorer

A read only diagnostic tool that asks a DICOM Modality Worklist the plainest possible
question and shows everything that comes back, together with exactly what was asked.

It sends C-ECHO and C-FIND only. There is no code path for C-STORE, N-CREATE, N-SET or MPPS;
the whole network surface is in `src/WorklistExplorer.Core/Dicom/DicomConnection.cs` and
`WorklistQueryRunner.cs`.

## Structure

| Project | Target | Purpose |
|---|---|---|
| `WorklistExplorer.Core` | net8.0, fo-dicom 5.2.2 | Configuration reader (read only), query builder, C-FIND runner, diagnostic report, raw dump, local name folding, CSV, page arithmetic |
| `WorklistExplorer.Cli` | net8.0 | Console mode: same configuration, CSV out, full report on screen |
| `WorklistExplorer.Wpf` | net8.0-windows | Window with server list, date mode, paged grid, exports, raw mode |

## Build and publish (Windows, .NET 8 SDK)

```
publish.cmd
```

produces `publish\WorklistExplorer.exe` (window) and `publish\WorklistExplorer.Cli.exe`
(console), both self contained single files for win-x64.

## Console examples

```
WorklistExplorer.Cli --list
WorklistExplorer.Cli --out all.csv
WorklistExplorer.Cli --clear-filters --out plain.csv
WorklistExplorer.Cli --date range --from 2025-12-01 --to 2025-12-31 --clear-filters --raw D:\secure\raw
WorklistExplorer.Cli --host 10.0.0.5 --port 104 --called WORKLIST --calling PDF2DCM --echo
```

Exit code 0: rows returned. 1: every server answered with nothing. 2: a failure, a non
Success final status, or the ceiling was reached.

## Behaviour worth knowing

* **Filters.** Imported `Modality` and `ScheduledStationAeTitle` are sent as imported by
  default, so the first query reproduces the other application's question. They are shown in
  red in the window; *Clear modality and station filters* (or `--clear-filters`) sends them
  empty. The configuration file is never changed.
* **Dates.** Default is any date, which sends a zero length Scheduled Procedure Step Start
  Date. The file's own `DateRange` is displayed for comparison but not used.
* **Ceiling.** Default 1 000 000 rows per server. Beyond it, rows are still received and
  counted, but not kept; the screen, the report and the exit code all say so.
* **Timeout.** `TimeoutSeconds` is an inactivity timeout, reset by every response, so a long
  but steady answer is never cut short.
* **Discards.** A pending response is discarded only when it carries no dataset or its
  dataset cannot be read; each discard is reported with its reason and response number.
  A response without a step sequence is kept as a row with empty step fields and a remark.
* **Logs.** The diagnostic report contains no patient data and is appended to
  `%LOCALAPPDATA%\WorklistExplorer\logs`. Patient data is only written to CSV files you
  export and to the raw folder you choose.

## Verification status

Built in a Linux sandbox that could not reach NuGet. The Core and console code were compiled
and exercised (63 checks: dates, query dataset, configuration import and read only access,
folding, filter, CSV, pager, ceiling, discards, timeouts) against a stand in for the fo-dicom
types. **Neither project has yet been compiled against the real fo-dicom 5.2.2, and the WPF
window has not been compiled or run.** On the first Windows build, please check these fo-dicom
members in particular, since they are the ones most likely to differ:

1. `DefaultTlsInitiator` (namespace `FellowOakDicom.Network.Tls`, property
   `IgnoreSslPolicyErrors`) and the `DicomClientFactory.Create` overload that takes it; only
   used when `AcceptAnyTlsCertificate` is true.
2. `IDicomClient.ClientOptions.AssociationRequestTimeoutInMs`.
3. `IDicomClient.SendAsync(CancellationToken, DicomClientCancellationMode)`.
