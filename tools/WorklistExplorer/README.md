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

## How this differs from the application's own worklist query

Compared key by key against `WorklistService.BuildQuery` in `PdfToDicom.Core`. **Both send
the same eighteen keys**, the same nine top level return keys, the same seven inside one
`ScheduledProcedureStepSequence` item, the same `SpecificCharacterSet` of `ISO_IR 192`, the
same SOP class, the same query retrieve level of not applicable, and the same effective
priority. Nothing is asked for by one and not the other, so a row this tool sees and the
application does not is not explained by a missing key.

Four differences remain, and each one matters when reading an answer:

| | Application | This tool |
|---|---|---|
| `PatientName`, `PatientID` | The operator's text. Contains matching wraps it as `*text*`, and a search that returns nothing is retried with these two emptied and the rows matched locally, ignoring case and accents | Always empty. There is no patient filter, so the question is deliberately the widest one |
| `Modality`, `ScheduledStationAETitle` | Trimmed before sending | Sent exactly as the file holds them. A stray space in the configuration is therefore sent, which a literally matching server will not match. That is on purpose, since the point is to show what the file would ask, but it is the one case where this tool can find less than the application |
| `ScheduledProcedureStepStartDate` | Empty for any date, a plain `yyyyMMdd` for one day, `from-to` for a range. Suppressed entirely when a patient ID was typed, so an old examination can be found by number. An explicit range missing one end is completed with today | The same three forms, and additionally an open ended `from-` or `-to`, which the application cannot express |
| Dataset validation | Left on | `NotValidated()` on both datasets, so the string shown is exactly the string sent |

The last row was checked rather than assumed: fo-dicom 5.2.2 accepts `20251101-20261119` and
`-20251231` in a validated `DA` element, so the application is not at risk from its own
ranges and the flag only guarantees that this tool can never alter a value on the way out.

## Verification status

Compiled against the real **fo-dicom 5.2.2**, the version `PdfToDicom.Core` uses, with no
errors and no warnings, and exercised end to end against `PdfToDicom.TestWorklistServer`:
509 pending responses, 509 rows kept, none discarded, `ISO_IR 192` declared on every
response, final status Success, and a step scheduled on 2025-12-04 present in the CSV with
its accents intact.

The three fo-dicom members this file previously flagged as unverified are all correct as
written, confirmed by reflection against the 5.2.2 assembly:

1. `DefaultTlsInitiator` is in `FellowOakDicom.Network.Tls` and has `IgnoreSslPolicyErrors`;
   `DicomClientFactory.Create(string host, int port, ITlsInitiator tlsInitiator, string callingAe, string calledAe)`
   exists. **Compile verified only**: no TLS worklist server was available, so the branch has
   not been exercised.
2. `IDicomClient.ClientOptions.AssociationRequestTimeoutInMs` exists and is settable.
3. `IDicomClient.SendAsync(CancellationToken cancellationToken, DicomClientCancellationMode cancellationMode)`
   exists, and `DicomClientCancellationMode.ImmediatelyReleaseAssociation` is a member.

**The WPF window has still not been compiled or run.** It is now part of
`PDF to DICOM.sln`, so continuous integration compiles it on its Windows runners, which is
evidence that it builds and none at all that it opens. Its twenty three event handlers were
checked against the XAML by hand; nothing else about it has been verified.
