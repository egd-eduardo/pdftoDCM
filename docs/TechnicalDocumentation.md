# PDF to DICOM — Technical Documentation

**Version 3.0 · For developers, PACS administrators and hospital IT**

---

## Contents

1. [What this application does](#1-what-this-application-does)
2. [Architecture](#2-architecture)
3. [Project layout](#3-project-layout)
4. [Key design decisions](#4-key-design-decisions)
5. [The conversion workflow](#5-the-conversion-workflow)
6. [DICOM object construction](#6-dicom-object-construction)
7. [UID generation](#7-uid-generation)
8. [DICOM networking](#8-dicom-networking)
8a. [Modality Worklist](#8a-modality-worklist)
8b. [Importing from the file name](#8b-importing-from-the-file-name)
9. [Validation](#9-validation)
9a. [The ImageSharp advisories](#9a-the-imagesharp-advisories)
9b. [Closing the application](#9b-closing-the-application)
9c. [Memory](#9c-memory)
10. [Configuration](#10-configuration)
10a. [Carrying settings between workstations](#10a-carrying-settings-between-workstations)
11. [Logging and audit](#11-logging-and-audit)
12. [Building](#12-building)
13. [Testing and verification](#13-testing-and-verification)
14. [The command line tool](#14-the-command-line-tool)
15. [Deployment](#15-deployment)
16. [Security considerations](#16-security-considerations)
17. [Extending the application](#17-extending-the-application)
18. [Known limitations](#18-known-limitations)

---

## 1. What this application does

It converts a PDF into a **DICOM Encapsulated PDF Storage SOP Instance**
(`1.2.840.10008.5.1.4.1.1.104.1`) carrying patient demographics, and transmits it to one
or more DICOM Storage SCPs. Images are converted to **Secondary Capture Image Storage**
(`1.2.840.10008.5.1.4.1.1.7`) the same way.

The demographics are taken from a **Modality Worklist** entry where one is configured, or
supplied by the operator where one is not.

Everything except the worklist query and the transmission happens offline.

---

## 2. Architecture

Three layers, with dependencies pointing in one direction only.

```
┌──────────────────────────┐   ┌──────────────────────────┐
│   PdfToDicom.App (WPF)   │   │   PdfToDicom.Cli         │
│   net8.0-windows         │   │   net8.0                 │
│   Views, ViewModels,     │   │   Argument parsing and   │
│   themes, localization   │   │   console output         │
└────────────┬─────────────┘   └────────────┬─────────────┘
             │                              │
             └───────────────┬──────────────┘
                             ▼
             ┌───────────────────────────────┐
             │      PdfToDicom.Core          │
             │      net8.0                   │
             ├───────────────────────────────┤
             │ Services   workflow, wiring   │
             │ Dicom      object building    │
             │ Networking C-ECHO, C-STORE    │
             │ Validation input and IOD      │
             │ Configuration  JSON store     │
             │ Logging    diagnostic, audit  │
             │ Pdf        inspection         │
             │ Monitoring folder watcher     │
             │ Localization en-US, pt-PT     │
             └───────────────┬───────────────┘
                             ▼
             ┌───────────────────────────────┐
             │ fo-dicom 5.2  ·  PdfPig 0.1   │
             │ Serilog 4                     │
             └───────────────────────────────┘
```

**Core contains no presentation code and targets plain `net8.0`, not `net8.0-windows`.**
The product ships for Windows only, in its three architectures. Keeping Core free of a
Windows target is a build time decision rather than a product one: it is what allows the
business logic, DICOM construction and networking to be unit tested and exercised on a CI
runner or a developer's machine of any kind. It is also what makes the desktop application
and the command line tool provably identical in behaviour: both drive the same
`ConversionWorkflowService`.

### SOLID in practice

| Principle | Where it shows |
|---|---|
| **Single responsibility** | `EncapsulatedPdfBuilder` builds; `EncapsulatedPdfValidator` checks; `DicomNetworkService` transmits; `DicomFileWriter` persists. None of them knows about the others. |
| **Open/closed** | A new destination type or a new validation rule is a new class implementing an existing interface. No existing class is edited. |
| **Liskov substitution** | `StubNetworkService` in the tests replaces `DicomNetworkService` with no change to the workflow. |
| **Interface segregation** | `IAppLogger`, `IAuditLogger`, `IPdfInspector`, `IUidGenerator`, `IDicomObjectBuilder`, `IDicomValidator`, `IDicomNetworkService` are separate and narrow. Nothing depends on methods it does not call. |
| **Dependency inversion** | Every collaborator arrives through a constructor parameter typed as an interface. `ApplicationServices` is the only place that names concrete types. |

---

## 3. Project layout

```
pdftoDCM/
├── PDF to DICOM.sln
├── Directory.Build.props            Shared compiler settings
├── global.json                      SDK version, permissive roll-forward
│
├── src/
│   ├── PdfToDicom.Core/             All logic. net8.0
│   │   ├── Abstractions/            The interfaces listed above
│   │   ├── Models/                  Plain data carriers
│   │   ├── Configuration/           AppConfiguration, PacsDestination, JSON store
│   │   ├── Validation/              Input rules, VR rules, IOD checks
│   │   ├── Dicom/                   Builder, validator, UIDs, tag inspector, writer
│   │   ├── Networking/              Verification, Storage and Worklist SCU
│   │   ├── Pdf/                     PdfPig based inspection
│   │   ├── Security/                Password verifier for the protected settings
│   │   ├── Imaging/                 Image decoding and PDF page rendering
│   │   ├── Logging/                 Serilog logger, audit trail, log export
│   │   ├── Monitoring/              FileSystemWatcher based folder monitor
│   │   ├── Importing/               Reads patient details out of a file name
│   │   ├── Localization/            Localizer + embedded en-US.json, pt-PT.json
│   │   └── Services/                ConversionWorkflowService, ApplicationServices
│   │
│   ├── PdfToDicom.App/              WPF desktop application. net8.0-windows
│   │   ├── Views/                   MainWindow, Settings, Worklist, TagViewer, LogViewer
│   │   ├── ViewModels/              MVVM view models
│   │   ├── Infrastructure/          ObservableObject, commands, theming, localization
│   │   ├── Converters/              Value converters
│   │   ├── Themes/                  Light.xaml, Dark.xaml, Controls.xaml
│   │   └── app.manifest             asInvoker, per monitor DPI
│   │
│   └── PdfToDicom.Cli/              Headless front end for scripted conversion
│
├── tests/PdfToDicom.Core.Tests/     265 xUnit tests
│
├── tools/
│   └── PdfToDicom.TestWorklistServer/   A Modality Worklist SCP for testing only.
│                                        Never packaged; see section 8a.
│
├── build/
│   ├── build.sh                     Cross compiles the Windows binaries from Unix
│   ├── build.ps1                    Windows build, publish and installer
│   ├── verify-against-dcmtk.sh      End to end interoperability verification
│   └── installer/PdfToDicom.iss     Inno Setup, per user installation
│
├── docs/                            This file, user manual, conformance statement
├── samples/                         Sample config, example PDF, example DICOM
└── .github/workflows/               Windows build and release pipeline
```

---

## 4. Key design decisions

Each of these was a real choice with a real alternative, so the reasoning is recorded.

### Self contained single file deployment

`PublishSingleFile` + `SelfContained` + `IncludeNativeLibrariesForSelfExtract`. The .NET
runtime and WPF are embedded, producing a ~69 MB executable that runs on a clean Windows
10 or 11 machine.

*Alternative rejected:* a framework dependent build, roughly 2 MB. That would require the
.NET Desktop Runtime on every workstation, which the requirements explicitly exclude and
which is a real obstacle in a locked down hospital estate.

**Trimming is deliberately off.** WPF resolves XAML generated types by reflection; a
trimmed build fails at runtime rather than at build time, which is the worst possible
place for it to fail.

### fo-dicom as the DICOM toolkit

Mature, actively maintained, MS-PL licensed, and it implements both the encoding and the
network layer. `EvilDICOM` is comparatively thin on networking; hand rolling the encoding
was explicitly ruled out and would in any case be an unjustifiable risk in a clinical
context.

### PdfPig for PDF inspection

Fully managed and MIT licensed. A native PDF component would need shipping per
architecture and would defeat the single file requirement.

*What it is used for:* page count, document title, encryption detection and a structural
readability check. **The PDF is never re-encoded**; the original bytes are embedded
verbatim.

### Explicit VR Little Endian

Uncompressed, unambiguous, and universally supported. It removes any dependency on native
codec libraries, which again protects the single file deployment.

### `Modality` defaults to `OT`

The requirement specified `OT`, and it is accepted by essentially every archive in the
field.

**Worth flagging:** the defined term for encapsulated document series in the current
standard is `DOC`. The setting is therefore configurable, with `DOC` offered in the
settings dialog, and the trade-off is documented for the PACS administrator. Sites should
use whatever their archive expects.

### The page count lives in a private attribute

The standard defines no attribute for the page count of an encapsulated document. Rather
than forcing the value into an unrelated standard attribute, it is written to a correctly
reserved private block (`PDFTODICOM`, group `0x7771`), which PS3.5 section 7.8 permits and
which conformant systems ignore. It can be switched off for archives intolerant of private
data.

### Hand wired composition root

`ApplicationServices` assembles the object graph by hand. The graph is small, fixed and
built once; a DI container would add a dependency and a layer of indirection for no gain.

### No MVVM toolkit package

`ObservableObject` and `RelayCommand` are a few dozen lines. Every dependency avoided is
one less assembly in the executable and one less licence to account for.

---

## 5. The conversion workflow

`ConversionWorkflowService.RunAsync` is the single definition of the order of operations.

```
 1. Validate inputs        PDF readable? Demographics complete? Destination valid?
        │ fail ──────────► stop. Nothing generated, nothing sent.
        ▼
 2. Build the object       EncapsulatedPdfBuilder
        │ throw ─────────► stop, reported as a generation failure.
        ▼
 3. Validate the object    EncapsulatedPdfValidator, against the IOD
        │ fail ──────────► stop. A non conformant object is never transmitted.
        ▼
 4. Save locally           When configured, or always for "Generate only"
        │ fail ──────────► warning only; transmission proceeds from memory.
        ▼
 5. Transmit               Per destination: optional C-ECHO, then C-STORE, with retries
        ▼
 6. Clean up               Delete the local copy only if every destination accepted it
```

Two properties of this order matter clinically:

- **Step 3 gates step 5.** An object that fails validation is never transmitted, because a
  defective record in a patient's imaging history is worse than no record.
- **Step 6 is conditional on complete success.** A partial success keeps the local file so
  the operator can retry.

---

## 6. DICOM object construction

`EncapsulatedPdfBuilder` populates the modules of the Encapsulated PDF IOD
(PS3.3 Annex A.45.1). Full attribute detail is in `DicomConformanceStatement.md`.

Order matters in one place: **Specific Character Set (0008,0005) is written first**, so
the toolkit encodes every subsequent text value with the intended repertoire.

### The payload

```csharp
byte[] payload = pdfBytes;
if (payload.Length % 2 != 0)
{
    payload = new byte[pdfBytes.Length + 1];
    Buffer.BlockCopy(pdfBytes, 0, payload, 0, pdfBytes.Length);
    payload[^1] = 0x00;
}
dataset.AddOrUpdate(new DicomOtherByte(DicomTag.EncapsulatedDocument, new MemoryByteBuffer(payload)));
```

A value with VR OB must be of even length. A single null byte is appended when needed,
which PDF readers ignore because the document is delimited by its own trailer. The
verification script asserts byte for byte identity of the recovered document.

### File meta information

fo-dicom rebuilds the file meta group from **process wide statics** on every save, so
setting `ImplementationClassUID` per file has no effect. `DicomRuntime.EnsureInitialized()`
sets `DicomImplementation.ClassUID` and `.Version` once, and is called from the
constructors of both the builder and the network service so it cannot be forgotten.

Without it, every object would be attributed to the underlying library rather than to this
application, making an instance impossible to trace back to its source.

---

## 6a. Image conversion

An image file becomes one or more Secondary Capture Image Storage objects
(`1.2.840.10008.5.1.4.1.1.7`). The pipeline is deliberately parallel to the PDF one:
`ImageInspector` reads the file, `SecondaryCaptureImageBuilder` constructs the objects, and
`SecondaryCaptureImageValidator` checks them, with `SopClassDicomValidator` choosing
between the two validators by reading the SOP Class the object declares.

### Decoding

Decoding uses **SixLabors.ImageSharp 2.1.11**, which handles JPEG, PNG, TIFF, BMP, GIF,
WebP, TGA and PBM.

Two things about that choice are deliberate:

- **The 2.1 series is pinned** because it is licensed under the plain Apache Licence 2.0.
  ImageSharp 3.0 moved to the Six Labors Split License, which is free only for open source
  projects and organisations under a revenue threshold. Pinning keeps the licence position
  of this software uniform and simple for a hospital's procurement to accept.
- **It is pure managed code.** No native codec libraries means the application stays a
  single self contained executable, behaves identically on ARM64, and does not depend on
  any imaging component installed on the workstation.

### What the file is, is read rather than assumed

`ImageInspector.DetectKind` looks at content: `%PDF-` within the first kilobyte means a
PDF, otherwise ImageSharp's format sniffing decides. The extension is used only to populate
the file picker's filter.

This is not pedantry. Scanners that write a TIFF and give it a `.jpg` extension are
commonplace, and so are renamed files. Trusting the name would reject files that convert
perfectly well.

### Colour is decided from the pixels

`ImagePixelAnalyzer` scans every frame and reports colour only if some pixel has channels
that are not all equal.

Asking the decoder is not an option: ImageSharp's non generic loader promotes almost
everything to a 32 bit RGBA buffer, so a greyscale PNG reports itself as colour. The first
version of this code did exactly that and would have tripled the size of every scanned
document.

Reading the file's own metadata would be format specific and would still answer wrongly
for the commonest case here: a flatbed scanner emitting an RGB JPEG of a black and white
page. Deciding from content is simpler and better, and it cannot lose information.

### Multi page sources

One SOP Instance per page, sharing one Series Instance UID, numbered sequentially in
Instance Number. Single frame Secondary Capture is chosen over the multi frame SOP Classes
because it is the most widely supported image class in the field.

### Padding

Every DICOM value must occupy an even number of bytes. A greyscale image with an odd pixel
count cannot manage that on its own, so one null byte is appended. It sits outside the
declared `Rows` by `Columns` extent, so no viewer ever sees it, and the validator allows
exactly one such byte and no more.

---

## 6b. Study grouping

`StudyGroupingService` decides which study a conversion joins.

| Mode | Behaviour |
|---|---|
| `SeparateStudy` (default) | Every conversion creates a new study |
| `GroupByPatient` | Conversions for the same Patient ID reuse one Study Instance UID |

Each file always gets its own Series Instance UID, in both modes. That is what keeps the
files distinguishable inside a study and why grouping loses nothing.

**The association is held in memory and never persisted.** A patient returning in three
months is having a new examination; their new report must not silently join the study
created for the old one. Restarting the application, or moving to a different patient,
starts a new study.

Patient identifiers are compared with `StringComparison.Ordinal`. They are opaque strings
assigned by another system, and treating `ab1` and `AB1` as one patient is a guess whose
failure mode is filing a document against the wrong person.

Precedence, strongest first:

1. a Study Instance UID typed by the operator or passed as `--study-uid`;
2. the study grouping supplied for this run;
3. a newly generated study.

Because the command line tool is a fresh process per invocation, grouping there works
within a single run. `--file` may be repeated for that reason; a script that needs to group
across separate invocations should generate a UID itself and pass `--study-uid`.

---

## 7. UID generation

Two strategies, both guaranteeing uniqueness without central coordination.

### UUID derived (default)

A version 4 UUID is read as a big endian 128 bit unsigned integer and appended to `2.25.`,
as permitted by **PS3.5 Annex B.2**. Requires no registered root and cannot collide with
any other system's identifiers.

### Registered root

`1.2.826.0.1.3680043.9.6008.<discriminator>.<yyyyMMddHHmmssfff>.<random>`

The root is registered with Medical Connections Ltd to the author of this software. It is
a constant, `UidGenerator.RegisteredUidRoot`, and is **not configurable**: a registered
root identifies its registrant, so a site generating identifiers beneath it would be
claiming an arc it does not own. `OrganizationUidRoot` survives in the configuration file
only so that a file written by an earlier version still loads; its value is ignored.

The registration carries two conditions, and both are met:

- **UIDs beneath the prefix must not be capable of duplication.** The suffix combines the
  UTC time to the millisecond with a fifteen digit cryptographically random component,
  which is a thousand million million values within any one millisecond, so two
  workstations generating simultaneously still do not collide. The property is tested
  rather than asserted: `UidsBeneathTheRegisteredRootDoNotDuplicate` generates 20,000
  identifiers in a tight loop, where most share a timestamp and uniqueness therefore rests
  entirely on the random component.
- **Sub-delegation is permitted where it is not for profit, or forms part of another
  product.** This is the latter, and the software is supplied free of charge.

A generated identifier is 62 characters, two inside the 64 character limit. The settings
dialog shows the root partly masked and disabled. That stops it being read off or copied
from a screen; it is not secrecy, since every object the software produces carries UIDs
built from it.

The maximum root length is **derived** from the component widths:

```csharp
private const int MaximumRootLength =
    DicomValueRules.MaxUidLength - (1 + 1 + 1 + TimestampWidth + 1 + RandomComponentWidth);
```

> This was originally a hard coded constant that did not match the suffix actually
> produced, and a 26 character root generated 66 character UIDs. The unit tests caught it.
> Deriving the budget from the component widths means a change to any component cannot
> silently reintroduce the defect.

Every generated UID is asserted conformant before being returned; a violation raises rather
than being emitted.

---

## 8. DICOM networking

`DicomNetworkService` implements Verification and Storage as an SCU.

### Presentation contexts

Both **Explicit VR Little Endian** (how the object is encoded) and **Implicit VR Little
Endian** (mandatory for every Storage SCP) are offered, so negotiation succeeds even
against an archive with an unusually narrow context list.

### Retry policy

Retries are handled here rather than by the client library, so each attempt is logged and
reported individually.

**Not every failure is retried.** A rejection whose status indicates a permanent condition
stops the loop immediately; only `0xA7xx` (Refused: Out of Resources) and transport level
failures are treated as transient. Retrying an object the archive has definitively refused
wastes the operator's time and tells them nothing.

### Sequential, not parallel

Destinations are contacted one at a time. Hospital workstations frequently sit behind
firewalls limiting concurrent associations, and sequential progress is far easier for an
operator to follow.

### Error messages

`DescribeException` maps transport failures to actionable text. It **unwraps
`AggregateException` first**: the asynchronous client wraps connection errors in one whose
own message is "One or more errors occurred", which tells the operator nothing.

| Condition | Message |
|---|---|
| `ConnectionRefused` | "The connection to … was refused. Confirm the archive is running and that the port is correct." |
| `HostNotFound` | "The host name … could not be resolved. Check the spelling, or use the IP address instead." |
| `TimedOut` | "The connection to … timed out. A firewall may be blocking the port." |
| Association rejected | "… rejected the association. Confirm the called and calling AE Titles are the ones the archive expects." |

---

## 8a. Modality Worklist

`WorklistService` implements the Modality Worklist Information Model — FIND
(`1.2.840.10008.5.1.4.31`) as an SCU. It is the only place in the product that reads
patient data from another system rather than writing it.

Worklist settings live in `WorklistConfiguration`, deliberately separate from
`PacsDestination`. A worklist server is usually a different system from the archive,
often on a different host, and conflating the two would mean a site could not point at
both.

### The query is assembled by hand

fo-dicom offers `DicomCFindRequest.CreateWorklistQuery`, and it is not used. That helper
sets `ScheduledProcedureStepStartTime` to `000000-000000`, which asks for steps scheduled
at exactly midnight and returns nothing from a real department's worklist. The query here
is built attribute by attribute instead.

### Empty is not missing

Every attribute the application wants back is sent with an **empty value**. In DICOM that
is universal matching: match anything, and report what it was. An attribute that is not
sent at all is not returned at all, which is the usual reason a worklist query appears to
work and comes back without a `StudyInstanceUID`.

The same mechanism carries the configured filters. `Modality` and
`ScheduledStationAETitle` are match keys when set and universal matches when blank, so
"any modality" needs no special case anywhere in the code.

### Dates

`WorklistConfiguration.ResolveDates` turns the configured range into two dates, or into
`null` for "any date", in which case no date key is sent. `ResolveDates` takes today as a
parameter rather than reading the clock, which is what makes the five ranges testable.

A single day is sent as a plain `YYYYMMDD` rather than as a range of one. Some servers
handle the degenerate `YYYYMMDD-YYYYMMDD` form poorly and the plain form is unambiguous.

### Person Name, and the sixty four character limit

PS3.5 allows **64 characters per component group** of a Person Name. That is the whole
`Family^Given^Middle^Prefix^Suffix` group, not 64 per component. fo-dicom enforces it and
throws rather than truncating, so an over-length worklist name would otherwise stop the
conversion dead at the point of building the object.

A few Portuguese names are longer than 64 characters, so this is a real case and not a
theoretical one. `PersonNameShortener` applies the rule the department chose:

| Case | Result |
|---|---|
| The composed name fits | Returned untouched |
| The name is too long but the surname fits | Surname kept whole, given name cut at a word boundary |
| The surname alone exceeds the limit | Surname cut at a word boundary, given name dropped |
| A single word too long to break | Cut mid-word, because returning nothing loses the name entirely |

The surname survives because that is what an archive indexes and what a clinician
searches by. Cutting at a word boundary matters more than it looks: *JOSÉ ANTÓNIO* reads
as a name, *JOSÉ ANTÓNIO FRAN* reads as a defect and invites a clinician to doubt the
rest of the record.

Shortening is **silent to the operator**, as specified, and never silent to the record:
the audit log records the original length and what was sent. The worklist window also
shows the name that will actually be used underneath the selected row, so the difference
is visible before anything is sent rather than discovered afterwards.

The middle name, prefix and suffix are read and discarded. The department does not use
them, and dropping them means they cannot consume characters the first and last name
need.

### Only the alphabetic component group

`PatientName` may carry up to three component groups separated by `=`: alphabetic,
ideographic and phonetic. They hold the **same name in different scripts**, not
additional name text. Only the first is read.

### What a worklist entry becomes

`WorklistEntry` holds the values exactly as they arrived, before any limit is applied, so
the operator can always be shown what the worklist actually said. Shortening happens when
the entry is turned into `PatientDemographics`, at the moment the operator presses *Use
this patient*.

The most valuable field in the response is `StudyInstanceUID`. The order has already
created the study the images will land in, so carrying its identifier is what puts the
report beside them rather than in a study of its own. It is carried as
`ExistingStudyInstanceUid`, the same path a Study Instance UID typed by the operator
takes, so it overrides the configured study grouping exactly as that does.

Selecting an entry **replaces every patient field**. Merging would risk a form holding one
patient's identifier beside another's name, and no merge rule is worth that.

### Several servers

A hospital does not have one worklist. Radiology and cardiology are usually different
systems, and until 3.3 this application could be pointed at only one of them at a time.
`AppConfiguration.Worklists` is now a list, each entry carrying its own connection, match
keys, date range and limits, plus an `Id` and a `Name`.

| Concern | Where it is decided |
|---|---|
| Is the worklist offered at all | `AppConfiguration.IsWorklistAvailable`: true when **any** server is enabled |
| Which server is offered first | `PreferredWorklist`: the one used last, else the first enabled |
| Which servers a query asks | `WorklistViewModel.ServersToQuery`, resolved against the live configuration each time |

The identifier exists so that renaming a server does not make the application forget it was
the one in use; `LastUsedWorklistId` is written when the operator picks one. An identifier
that no longer matches anything is ignored rather than repaired, and the first enabled
server is used.

**The operator chooses in the worklist window**, not in Settings, because the choice belongs
to the document in their hand rather than to the workstation. The picker is hidden when only
one server is enabled: a picker with a single entry is a decision that looks like a choice.

**"All worklists"** appears once there is more than one. It is there for the way a patient
number is actually looked up: somebody at a desk with a document does not necessarily know
which department scheduled the examination, and making them try each server in turn turns
one search into four. Each server reports for itself, by name, so one that is unreachable is
visibly one server rather than "the worklist is down", and the results carry
`WorklistEntry.SourceWorklistName` so two departments holding a step for the same patient
can be told apart. That column is hidden when a single server was asked, where it would be
noise.

A `DataGridColumn` is not in the visual tree, so it has no DataContext and no ancestor to
bind through; hiding that column needs the `BindingProxy` freezable in `Converters`. The
obvious `RelativeSource AncestorType=Window` binding resolves to nothing there and WPF
reports it only as a trace message, so the symptom would be a column that never hides and
no error anywhere.

### Migrating from a single server

Versions up to 3.2 held one server under a `Worklist` key. That key is **no longer part of
the model**, because two sources of truth for the same thing is how they come to disagree.
`ConfigurationService.MigrateSingleWorklist` reads it straight out of the JSON on load, adds
it to the list, and the file is written back at once so the migration happens once and the
old key stops being carried.

Three rules, each with a test:

- **Only when the list is empty.** A file that already has `Worklists` was written by this
  version or later, and a leftover `Worklist` key in it is stale; importing it would
  resurrect a server somebody deleted.
- **It never throws.** A malformed legacy node means the site has no worklist configured,
  which is the state every site starts in.
- **The migrated server is usable**: it gets an identifier and a name, without which it
  could be neither remembered nor chosen from a list.

This matters more than most migrations. A worklist connection is agreed with another
department and tested against a live server; losing it silently means somebody retypes an AE
Title from memory, and a wrong AE Title offers the wrong patients.

### Saying when the settings file changes

`AppConfiguration.CurrentVersion` is the schema version this build writes, and
`ConfigurationVersion` is the one the file was read with. Comparing them on load gives
three outcomes, and the distinction between them matters more than it looks:

| File | Outcome |
|---|---|
| Older | Upgraded in place, `LoadNotice` says so, the file is rewritten and stamped |
| Current | Nothing said |
| **Newer** | `IsFromNewerVersion`, `LoadProblem` warns, **and the file is not written** |

The newer case is the only genuine incompatibility. This build cannot model what a later
one added, so saving writes the file back without it; the warning is worthless if loading
has already destroyed what it warns about, so load leaves it alone.

An older file is **not** incompatible and must never be described that way. It is upgraded
correctly, and telling a site to recreate it would cost them a worklist connection agreed
with another department and a set of destinations, for nothing. The notice exists so the
rewrite is visible, not to suggest anything is wrong.

Bump `CurrentVersion` only for a change an older build would get *wrong*, not for every
added setting: an unknown setting is ignored and the default stands, which is harmless.

### When the Worklist button does not appear

The button is shown only when at least one server is **enabled**, on the principle that a
button which always fails is worse than no button. The cost is that a site which configured
a server and left it disabled sees nothing and has nothing to read, so two things now answer
the question outright:

- **`pdftodicom config`** lists every configured server with its enabled state and then says
  in as many words whether the button is shown.
- **A configuration file that could not be read says so.** It is still set aside and defaults
  still take over, because the application must always start, but `IConfigurationService`
  now carries a `LoadProblem` and the main window shows it. Before this, a file hand edited
  into invalid JSON meant every destination and every worklist silently reverted to
  defaults, with the only record a text file beside the quarantined one that nobody would
  think to look for.

### Password protection

`ProtectedSettingsLock` guards **both** the Worklist and the Destinations tabs in Settings
behind one password compiled into the binary.

Both pages, because they are the same kind of setting: addresses and AE Titles that decide
where patient data comes from and where it goes. A mistyped destination sends a report to
the wrong archive and a mistyped worklist offers the wrong patients to choose from, and the
same person is trusted with both. Unlocking either page opens the other, since whoever
opened one can open the other and typing the password twice in one dialog would protect
nothing. The unlock is not remembered between dialogs: closing Settings locks both again, so
an unattended workstation does not sit there open.

The password is never a bindable property. `PasswordBox` deliberately does not expose its
value as one, because a bound password lives on the view model as a plain string for as long
as the binding does; the button hands it over at the moment it is pressed and the box is
cleared immediately afterwards, on success and on failure alike. Both outcomes are written
to the audit trail: one wrong attempt is a typo, a run of them is somebody trying.

It stores a **PBKDF2-HMAC-SHA256 verifier**, 210,000 iterations (the OWASP figure for
that combination), with a 16 byte random salt, in the form
`PBKDF2-SHA256$iterations$salt$hash`. Verification uses the iteration count recorded in
the verifier rather than today's constant, so raising the constant later cannot
invalidate a password already set, and the comparison is constant time.

The request was for the password to be stored *encrypted*. A hash is used instead and is
strictly stronger: anything encrypted inside the binary must carry its key inside the
binary too, so it can be decrypted back to the password. What is stored here cannot be
turned back into the password at all.

**What it protects against, stated honestly.** It stops a curious member of staff
wandering into the worklist configuration and changing an address or an AE Title, which
is the realistic risk on a shared clinical workstation. It does not stop someone
determined: the check runs on the user's own machine, and anyone able to decompile or
patch the executable can remove it. That is true of every password a desktop application
checks by itself.

The password is set by the author and by nobody else:

```
pdftodicom settings-password
```

reads it without echoing, derives the verifier locally, and prints the single line to
paste into `ProtectedSettingsLock.Verifier` before rebuilding. The password itself is
never written to a file, a log, a commit or a message. The old name
`pdftodicom worklist-password` still works, because a note written down at a hospital six
months ago says that.

An empty `Verifier` constant means no password has been set, and both tabs are then open.
That is deliberate: shipping with a placeholder nobody knows would lock the author out of
their own software, which is a worse failure than an unset lock. The interface reports the
setting as unprotected rather than implying a protection that is not there.

**The password in effect is recorded in two places**, the comment on the `Verifier`
constant and a test that asserts it. They have to agree. A verifier that no longer matches
the password everybody believes is set locks the copyright holder out of their own software,
the only symptom is an unlock that quietly refuses, and there is nothing to debug from
because a verifier cannot be turned back into a password. The test fails until the two
records agree, which is the point of having it.

### The test worklist server

`tools/PdfToDicom.TestWorklistServer` is a Modality Worklist SCP that serves seven
invented scheduled procedure steps. It exists because a site adopting this feature
usually has no worklist server it can point a test workstation at: the real one holds
live patient data and is not somewhere to try new software against.

```bash
dotnet run --project tools/PdfToDicom.TestWorklistServer -- --port 1070 --aet TESTWL
```

It serves two sets joined. **Seven fixed entries**, always present and chosen to be
awkward rather than convenient: accented names, a patient with no given name, a different
modality, a 73 character name whose surname fits, and a 75 character name whose surname
alone is over the limit. It builds its data sets with `NotValidated()` precisely so it can
serve names fo-dicom would otherwise refuse to construct, which is the only way to
exercise the shortening rule against a real association.

**Ten more drawn at random each run** from a pool of five hundred ordinary Portuguese
names, read from an embedded list that holds each one already split as
`Family^Given|Sex`. The split is data rather than a heuristic applied at run time, because
Portuguese names do not split by rule: the last token is the family name often enough to
look right, and no rule gets "Maria do Carmo Farinha" and "Abel Cordeiro Brandão" both
right without being told. `--random n` changes the count, `--random 0` gives the fixed
seven, and `--seed n` reproduces a draw.

The awkward entries are kept **out** of the pool deliberately. Measured, the pool's
longest name composes to 31 characters against the 64 the standard allows, so a random
draw from it would exercise the shortening rule roughly never, and the server would
quietly become a test of nothing.

Everything about a pool entry derives from its position, so a name always carries the same
Patient ID, accession number, birth date and scheduled time; only its presence in the draw
varies. That is what makes `--seed` reproduce a run and what makes converting twice for
the same patient file into the same study.

**Everything it serves is marked.** `TESTE` is stamped on the Patient ID, the accession
number, both descriptions and both physician names, and Study Instance UIDs are issued
under `…9.6008.99.`. A study generated from this list is therefore recognisable as a test
in a study list, a patient lookup and a raw dump, which matters because commissioning a
worklist is one wrong destination away from putting these studies into a live archive. The
patient names are left realistic, since their length and their accents are the thing under
test, and the identifiers carry the mark instead.

> The embedded list is named `names-pt-PT.txt` with a hyphen, not `names.pt-PT.txt`.
> MSBuild reads the segment between the last two dots of a resource file name as a
> **culture**, so the dotted form was compiled into a `pt-PT` satellite assembly instead of
> into the executable: the resource could not be found at run time and the single file
> publish gained a loose directory beside it. The project file pins the `LogicalName` as
> well, so a future rename cannot quietly change it again.

**It is never packaged.** The build scripts copy a named list of files into the customer
package and it is not on that list; `assert_package_is_sound` checks the list rather than
the folder, so it cannot be added by accident.

---

## 8b. Importing from the file name

A document arriving from another system almost always carries its identifiers in its own
name. Retyping them is where a report ends up in the wrong record, so where a naming
layout is agreed, the name is read instead.

Three pieces, all in `PdfToDicom.Core.Importing`, none of which knows anything about a
window:

| Class | Responsibility |
|---|---|
| `FilenamePattern` | Compiles one token template into a `Regex` and matches a file name against it |
| `FilenameMetadataReader` | Picks the pattern that reads the most, maps captures onto `PatientDemographics`, reports what it could not read |
| `ImportedFileFiler` | Moves or renames the source file afterwards so it is not picked up twice |

### Patterns

A pattern is written the way the file names look, with the fields in braces:

```
{PatientId}_{Name}_{AccessionNumber}_{DateOfBirth}
```

Tokens: `{PatientId}`, `{Name}`, `{FamilyName}`, `{GivenName}`, `{AccessionNumber}`,
`{DateOfBirth}`, `{StudyDescription}`, and `{*}` for a part to skip. Aliases are accepted
case insensitively, `{MRN}`, `{Processo}`, `{Surname}`, `{DOB}`, `{Acession}` and others
among them. The misspelling is deliberate: it is the one people type, and refusing it
would teach nothing. Everything outside the braces is matched literally, after escaping.
The extension is removed before matching, so a pattern never mentions it.

Anything the token vocabulary cannot express is written as a regular expression with named
groups, prefixed `regex:`. Both forms compile to the same thing, with a 250 ms match
timeout against a pathological name.

**Why tokens are bounded rather than lazy.** The obvious translation of `{Name}` is `.+?`,
and it is wrong. With `{PatientId} {Name} {AccessionNumber}` against
`77 MARIA DO CARMO SILVA ACC9`, lazy groups give `Name` = `MARIA` and `AccessionNumber` =
`DO CARMO SILVA ACC9`: the wrong accession number on a real patient. So a token is
compiled as "anything but the separator that follows", `[^_]+` for an underscore
delimited pattern, and only `{Name}` and `{StudyDescription}` stay greedy enough to hold a
separator, because a name genuinely contains spaces. This was found by a test, not by
reading the code.

**A malformed pattern never stops the application.** `Compile` throws `FormatException`
with a message naming the problem, the reader catches it, keeps it in `PatternErrors`, and
carries on with whatever patterns are usable. `pdftodicom config` prints those errors,
because a rejected pattern would otherwise fail silently: the form would simply never fill
in.

### Choosing between patterns

Patterns are tried in the configured order, but **the one that reads the most fields wins**,
with the configured order only breaking a tie. A site that lists

```
{PatientId}_{Name}
{PatientId}_{Name}_{AccessionNumber}
```

would otherwise lose every accession number, because the shorter pattern matches first and
matches everything the longer one does.

### What is filled in, and what is deliberately not

- **Sex** is not in any naming scheme seen so far, so it is taken from `DefaultSex`,
  `"O"` (Other) by default. An empty string leaves Patient Sex present and empty, which is
  conformant for a Type 2 attribute.
- **Date of birth** is parsed against an explicit, ordered format list rather than a
  locale, because a locale makes `05-11-1970` ambiguous and silence is the worst answer. A
  value that matches nothing is **left blank and noted**, never guessed at. No default
  format contains `/`: a file name cannot hold a directory separator, so such a format
  could never match, and a test asserts it.
- **The name** is split on `^`, on a comma, or on spaces with the last word taken as the
  family name. `SplitRunTogetherNames` additionally reads `JohnDoe` as `John Doe`; it is
  **off** by default, because it is a guess and it is wrong for `McDonald` or `DaSilva`.
- **Study date** is the file's creation time, written to Study Date and Study Time via
  `PatientDemographics.StudyDateTime`. Content Date and Content Time remain the moment of
  conversion, so both facts are preserved rather than conflated.
- **The Patient ID is padded** with leading zeros to `PadPatientIdTo` digits, ten by
  default. A sending system routinely writes `77` where the hospital's record is
  `0000000077`; an archive matches patients on the string, so without this the document
  opens a second record for the same person. Because this changes the one value that
  decides whose record a document joins, it is deliberately narrow: **only a value that is
  entirely ASCII digits is padded**, since zeros in front of `AB12` would be invention
  rather than restoration, and a value already at or beyond the width is **never
  truncated**, because the right hand end of an identifier is what tells two people apart.
  Every padding, and every refusal to pad, is reported.

### Validation is relaxed, not bypassed

Date of Birth and Accession Number are mandatory for anything an operator types: the date
of birth is the second check a clinician makes against a patient, and without an accession
number the archive cannot reconcile the document with the order. On this path the file name
is all there is, and a document that cannot be imported at all is worse than one filed
without a date of birth. So `PatientDemographicsValidator.Validate` takes
`requireDateOfBirth` and `requireAccessionNumber`, which `ConversionOptions.UnattendedImport`
sets to false. Both attributes are Type 2 in the IODs this produces, so an empty value is
conformant; inventing one is not. **A value that is present is still checked as strictly as
ever.**

### It fills the form; it does not send

In the desktop application the fields are filled and the status bar says so. Confirmation
before sending is untouched. Anybody can rename a file, and the one step this application
must not short circuit is the one that decides whose record a document joins. A name that
matches nothing leaves the form exactly as every previous version left it.

The `--from-filename` path of the command line tool is the unattended one, and there it
**refuses** rather than falling back to values given on the command line: the flag says
where the identity comes from, and quietly converting with something else is how a document
reaches the wrong patient.

### Filing the source file

Once a document has been dealt with, the original must stop looking like new work.
`ImportedFileFiler` moves it into `Imported/` (the default), or appends `RenameSuffix`
**after** the extension so `report.pdf` becomes `report.pdf.imported` and no longer matches
`*.pdf`, or leaves it alone, which will cause it to be offered again.

It runs for any document the monitored folder produced, including one whose name said
nothing and whose details the operator typed in; what matters is that the document has been
dealt with, not where its identifiers came from. A file the operator browsed to is left
alone, because it is theirs. Filing is governed by the same `FilenameImport` settings as
reading the name and is off whenever those are off, so a site already using folder
monitoring sees no change until it asks for one.

Four properties, each covered by a test:

- **A failure is always moved** to `Failed/`, whatever the setting says, so a file that
  needs looking at is not left in the inbox. A name that matched no pattern at all counts
  as a failure for this purpose: it will not match on a later run either, so leaving it in
  place means retrying it for ever and nobody ever seeing it.
- **Nothing is ever deleted.** A collision appends ` (2)`, then a timestamp.
- **Nothing is ever overwritten.**
- **It never throws.** A file that has already been moved by somebody else is not an error.

### Configuring it

Every setting has a page of its own in the Settings dialog, **File names**, placed after
Folders because it acts on what the monitored folder produces. The patterns and the date
formats are edited as one per line: a list of strings has no sensible editor in a dialog of
this kind, and a grid for three short lines would be heavier than the thing it edits.

**A layout can be built by picking fields rather than typing braces.** Every field is a
button, and pressing them appends to the layout in the order pressed, which is how somebody
actually does it: read a real file name left to right and press the fields off as you go.
The separator is editable because it is whatever the sending system uses. **Add to the list**
compiles the layout before accepting it, so `FormatException` surfaces in the dialog rather
than when a document arrives.

The buttons come from `FilenameField.All` rather than a second list in the view, so a field
added to the reader appears in the picker without anybody remembering to add it; a label is
resolved as `Settings.Import.Field.<Token>`, and a missing one would show as that key.

The date format buttons show the format **and the same date written in it**, because
`ddMMyyyy` means nothing to most people and `21051970` is recognised instantly. They offer
the two month-first forms as well, which are deliberately absent from the *defaults*: with
both day-first and month-first in the default list, `03-04-1970` would match whichever came
first and nobody would know which, whereas picking one here is a statement about what the
sending system writes.

**Both boxes stay editable.** The pickers are for the common case, and a `regex:` layout or
anything else they cannot express is typed directly.

That page also carries a **test box**. Type a file name, press the button, and it shows
what would be read, including which pattern matched and every note the reader produced. It
reads the settings **as they are being edited** rather than as they were last saved, so a
layout can be got right without saving, closing and reopening. Nothing is converted,
nothing is transmitted, and the file need not exist: the name is text and only text. It is
`pdftodicom check-names` without the command line.

### Copying the configuration, and a bug worth remembering

The dialog edits a copy, so Cancel genuinely discards everything. That copy used to be a
hand written method in `SettingsViewModel` that assigned each property by name, and it
**silently stopped copying three settings** as they were added: `RenderPdfPagesAsImages`
and `PdfRenderDotsPerInch` from the image release, then the whole `FilenameImport` section.
Opening Settings and pressing Save, having changed nothing, reset them to their defaults.
No error, nothing in the log.

It is now `AppConfiguration.Clone()`, a serialize and deserialize round trip. That cannot
drift, because it enumerates the properties instead of repeating them, and the cost is
microseconds once per dialog. A test walks every settable property by reflection and
compares the copy by value rather than by reference, so a setting added later is covered
the moment it exists; the test was confirmed to fail, naming the property, when the copy
was deliberately broken.

### Trying it before trusting it

`pdftodicom check-names` compiles the patterns, prints which are usable and why the others
are not, and shows what each name would produce, without converting anything or touching
a PACS:

```bash
pdftodicom check-names --pattern '{PatientId}_{Name}_{AccessionNumber}' \
                       --name 9999999999_JohnDoe_TEST123451.pdf
ls inbox | pdftodicom check-names
```

A naming layout is agreed with another vendor and then discovered to be slightly different
in practice. This is where that is discovered, rather than in the archive.

---

## 9. Validation

Three independent layers.

| Layer | Class | Checks |
|---|---|---|
| **VR** | `DicomValueRules` | AE Title, UID syntax and length, illegal characters, repertoire |
| **Input** | `PatientDemographicsValidator`, `PdfFileValidator`, `PacsDestinationValidator` | Mandatory fields, plausible dates, VR limits, file readability, network configuration |
| **IOD** | `EncapsulatedPdfValidator` | Type 1 present and non empty, Type 2 present, meta group agrees with data set, identifiers distinct and valid, payload is a PDF of even length, date and time formats |

The IOD layer is defence in depth. The builder is written to produce conformant output;
the explicit check means a defect is caught locally with a precise message instead of
arriving as an opaque status code from the archive.

The input layer has two switches, `requireDateOfBirth` and `requireAccessionNumber`, both
true for anything typed and both false for an unattended import from a file name. See
section 8b for why, and for what stays strict regardless.

---

## 9a. The ImageSharp advisories

Five security advisories stand against every release of SixLabors.ImageSharp from 2.0.0 to
4.1.1. This product uses **2.1.13**, the last of the 2.x line, and suppresses those five
**individually by advisory URL** in `PdfToDicom.Core.csproj`.

Stated plainly, because a suppressed security warning that nobody can audit is worse than
the warning.

### Why not simply upgrade

The fix for all five is **4.1.2**. There is none in 2.x. Moving to 4.x is not a version
bump:

- ImageSharp 3.0 adopted the **Six Labors Split License**. It grants Apache-2.0 terms to an
  individual or company under 1M USD annual gross revenue, so the terms are most likely
  acceptable here.
- But **4.x refuses to build without a licence key.** Its build targets fail with
  `No Six Labors license found ... obtain a license from sixlabors.com/pricing`. That was
  tried: it is a hard error, not a warning.

So a patched ImageSharp requires obtaining a key from Six Labors first. Until that happens
the choice is 2.1.13 with these suppressions, or no image support.

### Why it is defensible here

Four of the five cannot be reached by this code, and the fifth is a slow decode of a file
the operator chose themselves. Each was checked against the source, not assumed.

| Advisory | What it needs | Reachable? |
|---|---|---|
| `GHSA-j3p4-wp97-rph4` | TIFF as `HalfVector4`, then `HistogramEqualization()` | **No.** Never called; decoding is non-generic, then cloned to `Rgb24` or `L8` |
| `GHSA-j9gm-c75j-xc9q` | TIFF CCITT Group 3 **encoder** | **No.** Nothing here encodes TIFF; `TiffEncoder` is not referenced |
| `GHSA-jjfr-hcj7-qf5w` | TIFF CCITT Group 4 **encoder** | **No.** Same |
| `GHSA-gwg2-r3hj-4w44` | ICC parser via `Convert` or `IccProfile.Entries` | **No.** Default is `Preserve`, which does not parse; no colour profile code |
| `GHSA-wmxv-xphr-5c9g` | BigTIFF EXIF IFD, decode occupying a thread for seconds | **Yes.** Availability only; the file is one the operator picked on their own workstation |

### Why the suppression is narrow

`NuGetAuditSuppress` suppresses one advisory each. A new advisory, against this package or
any other, still fails the audit. That was verified by deleting one of the five and
confirming only that one reappeared.

The blunt alternative, `NoWarn` on `NU1902;NU1903`, was rejected: it would hide every
future package vulnerability in the project, which is not an acceptable trade in software
that handles patient data.

`NuGetAuditSuppress` needs **NuGet 6.12 or later**, which means a .NET 9 or 10 SDK. The 8.0
SDK ignores the item in silence, so CI installs both: 8.0.x for the runtime the tests need,
10.0.x for a NuGet that honours the suppressions. Without that, the same repository
produces five warnings on a build agent and none on a developer's machine.

### When to revisit

Obtain a Six Labors licence key, move to 4.1.2 or later, and delete all five suppressions.
Re-check the reasoning above if this code ever starts **encoding TIFF**, **reading ICC
profiles**, or calling **`HistogramEqualization`** — three of the four "not reached"
answers would stop being true.

---

## 9b. Closing the application

A maximised or badly positioned window can put its own close button off the screen, so the
header carries a door icon bound to `MainViewModel.ExitCommand`.

**One closing path, not two.** The command does nothing more than
`Application.Current?.MainWindow?.Close()`. Everything that matters happens in
`MainWindow.OnClosing`, which calls `MainViewModel.ConfirmExit()` and sets `e.Cancel` when
the answer is no. The door button, the window's own close button and Alt+F4 therefore run
the same code; had the button confirmed and shut down by itself, there would have been two
behaviours to keep in step and one of them would eventually have drifted.

`ShutdownMode` is `OnMainWindowClose`, so the non-modal log viewer cannot keep the process
alive after the main window has gone.

**The command is deliberately always enabled.** Every command that changes the document or
the form carries `() => !IsBusy`. The exit command carries no guard at all: a door that
greys itself out during the one operation a person is most likely to want to abandon would
be the opposite of what it is for.

**Cancellation is requested, not awaited.** `ConfirmExit` calls `_cancellation?.Cancel()`
and returns. Waiting for a DICOM association to notice could hold the window open for as
long as the network timeout. The process exit ends the work regardless, and the archive's
own association handling deals with a half-finished transfer; nothing in the output folder
is removed, so the document can be sent again.

Two details in `ConfirmExit` are not decoration:

- `_exitConfirmed` makes a second call return true without asking again. Closing twice
  genuinely happens: pressing the door while the window is already closing, or Windows
  shutting down behind a confirmation that has already been given.
- `Cancel()` is wrapped against `ObjectDisposedException`. `MessageBox.Show` pumps
  messages, so the running task's `finally` block, which disposes and nulls the token
  source, can complete between the question and the answer.

`Dispose` then detaches the folder monitor and configuration listeners, drops
`_lastGeneratedFile`, which holds a whole DICOM object and can run to tens of megabytes,
clears the result and validation collections and flushes the logger. The process would
release all of this anyway; doing it in order is what makes the window closable while a
conversion is still running, and the flush is what makes the last log lines survive.

**The header icon needed its own style.** `HeaderButtonStyle` derives from
`LinkButtonStyle`, whose template puts `Content` straight into a `TextBlock.Text`. Content
that is not a string, a `Path` among other things, would simply not be drawn. The door
button uses `HeaderIconButtonStyle`, the same thing built around a `ContentPresenter`,
which hovers by washing the dark blue header with translucent white rather than by
underlining.

---

## 9c. Memory

A sixty page report is the case that matters. Everything below was measured on one, from
the command line tool, by sampling the process's own resident set rather than by reasoning
about allocations.

| | Peak resident |
|---|---|
| Before this work | 629 MB |
| Reading the rasterizer's own pixel layout | 247 MB |
| With the pixel data ceiling at 64 MB | 184 MB |

And across two conversions in one process, which is what a workstation actually does:

| | After one conversion | After two |
|---|---|---|
| Before | 246 MB | 311 MB |
| Releasing at the end of a conversion | 106 MB | 108 MB |

**Why a conversion needs the pages all at once.** Nothing is transmitted until every object
has passed validation, because half a document in an archive is worse than none. That is a
deliberate property of the workflow, not an accident of it, and the price is that a long
report's page images are live together. At 150 DPI an A4 page is about 2 MB in greyscale
and 6 MB in colour, so sixty pages is 124 MB of genuinely live data. The work below is
about everything that was resident *besides* that.

**The rasterizer's layout is read where it lies.** `PdfPageRenderer` used to convert every
page to one known pixel layout first, which allocated and copied a second full page, about
8 MB each at 150 DPI, for an image that was about to be read one pixel at a time anyway.
Both four byte layouts put the three colour channels first and the alpha last, so the only
difference is whether red sits at offset 0 or offset 2, which one integer expresses. That
alone took the sixty page document from 629 MB to 247 MB, and the generated pixel data is
byte for byte identical: verified by hashing all sixty page objects before and after.

The cost of that is caring which end of each pixel red lives at, and a mistake there would
be silent: the page still renders, the sizes still match, and a red stamp arrives blue.
`RedStaysRedAndBlueStaysBlue` renders a hand written PDF of a red half over a blue half and
checks the channels. It was confirmed to fail when the two offsets are swapped.

Rows are read by stride rather than as one span of the image. A bitmap's stride is allowed
to exceed its width, and an image read as though it were contiguous when it is not comes out
sheared.

**A ceiling, not a page limit.** `MaximumRenderedPixelMegabytes`, default 512, caps the
pixel data of one document's page images. Over it, the whole document is rendered at a lower
resolution; pages are never dropped. A reader shown twelve pages of a twenty page report has
no way of knowing that eight are missing, whereas twenty slightly softer pages is the whole
report, and the encapsulated document, which is the fidelity copy, is unaffected either way.
Below a floor of 72 DPI nothing is rendered at all and the log says why.

The estimate comes from rendering the first page at the requested resolution and measuring
it, because what a page costs depends on whether it came out colour, and that is decided
from its own pixels. A document whose pages differ is estimated imperfectly; the first page
of a report is the one with the letterhead on it, so the error is normally on the safe side.
The resolution that fits is the requested one scaled by the square root of the ratio, since
pixel count rises with the square of the resolution. `RenderedPdfPage` carries the
resolution it was actually rendered at, so the log reports what happened rather than what
was configured.

**The memory is handed back when the work is done.** Every rendered page is a multi megabyte
array, which lives on the large object heap: not compacted by default, and swept only on a
generation two collection. Left to the collector's own judgement the report that has just
been sent stays resident, which is exactly what somebody looking at Task Manager reports as
high memory usage. `MainViewModel.ReleaseMemoryAfterWork` runs one blocking, compacting
generation two collection at the end of a conversion, where the work is finished and nobody
is waiting. Measured at 17 to 35 milliseconds for documents from one to sixty pages.

**Two things that were measured and left alone.** `DicomFile.Open` on the generated object
was suspected of holding the whole thing in memory for the session, and of keeping a file
handle open. Neither is true: opening a 1 MB object grew the managed heap by 8 KB, the large
values are read on demand, and no descriptor is retained. The transmission result and
validation lists were suspected of growing across conversions; both are cleared at the start
of every run. Nothing was changed on either count.

---

## 10. Configuration

JSON, at `%APPDATA%\PdfToDicom\config.json` (`~/.config/PdfToDicom/` on macOS and Linux).
See `samples/config.sample.json` for an annotated example.

**Three robustness properties:**

- **Atomic writes.** Content goes to a temporary file which then replaces the original, so
  a crash mid save leaves the previous valid configuration rather than a truncated file.
- **Corrupt files are quarantined, not discarded.** The damaged file is renamed
  `config.json.corrupt-<timestamp>` with an explanatory note, and defaults take over. The
  application always starts.
- **Out of range values are repaired on load.** A hand edited file cannot put the
  application into an unusable state.

---

## 10a. Carrying settings between workstations

A department buys one licence of nobody's time to configure twenty identical machines.
`SettingsTransfer` in Core writes the archives and the worklist servers to a file, reads one
back, and applies it; the Backup page in Settings and the two command line verbs
`export-settings` and `import-settings` are both thin shells over it.

**Only the connection settings travel, and the omission is the point.** The file holds
destinations and worklist servers. It does not hold the **calling AE Title**, and that is
deliberate: copying it would give two workstations the same identity on the hospital
network, which is the one thing a PACS administrator will object to, and it would happen
silently. Institution, folders, language and templates are excluded for the same reason.
They are local to the machine, and a file that quietly changed them would be a trap.

Nothing secret is in the file. Neither entity holds a password; a DICOM association here is
identified by AE Title. The page is still behind the same password as Destinations and
Worklist, because the file does disclose the addresses of clinical systems and because the
page can replace them wholesale.

**Two servers are the same server when the AE Title, host and port match**, ignoring case
and surrounding spaces. Not the identifier: identifiers are generated per machine, so the
same archive typed by hand on two workstations has two of them, and matching on them would
add a duplicate on every import. Each imported entry is given a fresh identifier for the
same reason, and a name already in use gets a number rather than leaving two identical
entries in a list somebody picks from under pressure.

**Adding is idempotent.** Importing the same file twice changes nothing the second time,
which is what makes the command safe in a rollout script that may run again. Replacing
discards the configured entries of the kinds being imported, so the dialog asks first,
defaults to No, and says how many will go; the command line requires `--replace`.

**The dialog imports into what is on screen, not into what is on disk.** The settings
dialog edits two observable collections and copies them into the configuration only when
Save runs, so an import that went straight to the saved configuration would compare against
a stale list and be overwritten by Save a moment later. Import brings the collections up to
date and leaves the writing to Save, which means Cancel still discards it. The command line
has nobody to press Save, so it writes immediately and prints what it did.

**A file version check, not a guess.** Every file carries `FileKind` and a `FormatVersion`.
The marker is stamped on the way out rather than defaulted on the property, and that detail
is load bearing: a property left at its initialised value is indistinguishable after
deserialization from one the file never mentioned, so a default of the marker itself made
*any* JSON file look like one of ours. A test caught it. A file from a newer layout is
refused with its version named rather than read optimistically.

---

## 11. Logging and audit

Two separate channels, deliberately.

**Diagnostic log** — Serilog, rolling daily, `shared: true` so support can copy the current
file while the application runs. Configurable retention.

**Audit trail** — tab separated, one line per event, one file per day:

```
Timestamp  User  Machine  Event  Detail
```

Events: `APPLICATION_START`, `APPLICATION_CLOSE`, `PDF_SELECTED`,
`PATIENT_INFO_ENTERED`, `PATIENT_INFO_FROM_FILENAME`, `DICOM_GENERATED`,
`TRANSMISSION_SUCCESS`, `TRANSMISSION_FAILURE`, `GENERATED_FILE_DELETED`,
`CONFIGURATION_CHANGED`.

`PATIENT_INFO_FROM_FILENAME` records the file name and the pattern that interpreted it.
Where a document's identifiers came from is an audit question in its own right: if a report
is later found against the wrong patient, the first thing to establish is whether a person
read those identifiers off a wristband or whether a file name supplied them.

The format is stable and opens directly in a spreadsheet, so an information governance team
can review it without tooling. An audit write failure is reported to the diagnostic log but
never aborts a clinical workflow that has otherwise succeeded.

**Log export** produces a zip of both channels plus the configuration. It contains **no PDF
and no image data**.

---

## 12. Building

### Supported targets

The product is built for Windows only, natively for each of its three architectures:

| Runtime identifier | Target | Built by default |
|---|---|---|
| `win-x64` | The mainstream 64 bit workstation, which is what the sites run | Yes |
| `win-arm64` | Windows on ARM, natively rather than through x64 emulation | Yes |
| `win-x86` | 32 bit Windows, still present on older clinical workstations | No, ask for it |

**Both default architectures are built by every command**, because the machine a build is
made on is not the machine it runs on: the sites are 64 bit, and the testing is done on a
Windows on ARM virtual machine where a native build avoids the emulation layer. 32 bit is
left out because a third build is another 80 MB and another thing to pick the wrong one of;
`-Runtimes win-x86` or `RID=win-x86` builds it when a 32 bit machine turns up.

CI verifies the PE machine type of every published executable, so a build cannot silently
fall back to the runner's own architecture.

### What is not built unless asked for

**The command line tool.** It is a second self contained single file publish, about as
large and as slow as the application's, and a build about to be copied to a test machine
has no use for it. `-Cli` on Windows, `CLI=1` on a Mac, and the release pipeline passes it
so what reaches a site is still complete. A handover folder assembled without it simply has
no `Command line` folder, and both scripts say so as they go rather than leaving it to be
noticed.

A stale one is removed rather than packaged. An earlier build with the tool leaves its
output in `artifacts/<rid>/cli`, and the packaging step takes whatever it finds there:
shipping yesterday's command line tool beside today's application would be worse than
shipping none.

### Prerequisites

- **.NET 8 SDK**, or any later one. `global.json` sets `rollForward: latestMajor`, so a
  machine with only the .NET 9 or .NET 10 SDK builds the solution without complaint.
- **The .NET 8 runtime**, to run the tests on the runtime the projects actually target.
  It is not required: `RollForward` is `LatestMajor`, so on a machine without it the test
  host runs on the newest installed runtime instead, and both build scripts say so when
  that happens. Installing it is what makes a local run like for like with CI.
- Inno Setup **6.3 or later** — only needed to build the installer, and only available on
  Windows. The architecture identifiers used by the installer script require 6.3.

### On Windows

```powershell
.\build\build.ps1                          # 64 bit and ARM, tested, packaged, installers
.\build\build.ps1 -Task app                # the executables only: the fast loop
.\build\build.ps1 -Task app -Runtimes win-arm64
.\build\build.ps1 -Cli                     # with the command line tool
.\build\build.ps1 -Runtimes win-x86        # a third architecture, on its own
.\build\build.ps1 -Task test
.\build\build.ps1 -Task prune              # keep the deliverables, drop the intermediates
```

`-Runtime` is kept as an alias for `-Runtimes`, so a note or a script written against the
old single architecture parameter still works.

### From macOS or Linux

The build tooling cross compiles the Windows binaries, which is what lets the tests and the
interoperability check run on a Linux CI runner.

```bash
build/build.sh                       # restore, build, test, publish both, assemble both
build/build.sh app                   # the executables only: the fast loop
build/build.sh ship                  # both architectures in one folder to hand over
RID=win-arm64 build/build.sh app     # one architecture
RIDS="win-x64 win-x86" build/build.sh publish
CLI=1 build/build.sh                 # with the command line tool
build/build.sh test
build/build.sh prune                 # keep the deliverables, drop the intermediates
build/build.sh clean
```

This produces the Windows executables. It cannot produce the installer.

> **Building and running are separate problems**, and this is the one that catches people
> out. An SDK carries its own reference assemblies, so any recent SDK compiles a `net8.0`
> project. Starting one needs the .NET 8 runtime specifically, and the test host is itself
> a `net8.0` application. A machine with only the current SDK therefore built the whole
> solution and then failed to start a single test, reporting
> `You must install or update .NET to run this application` against `testhost.dll` without
> saying which of the two was missing. `RollForward` resolves it.

Both validators need Python, and both scripts treat it as **optional**: if `python3` is
absent, or present but unable to run, the build says so and carries on. That matters on a
macOS without the Command Line Tools installed, where `/usr/bin/python3` exists and
resolves and then refuses to run, so testing the name is not enough and the scripts test
whether it executes. CI runs both checks on every push, so nothing is lost by skipping
them locally.

> Cross compiling WPF from a non Windows host works because `EnableWindowsTargeting=true`
> is set in `Directory.Build.props`. The Windows reference assemblies are restored from
> NuGet.

### Via CI

`.github/workflows/build-windows.yml` tests on Linux (including the DCMTK interoperability
check) and then publishes and packages on Windows, once per architecture. It is the
simplest way to obtain all three installers from a single push.

### Output

Output is written per architecture, so all three can coexist.

| Path | Contents |
|---|---|
| `artifacts/<rid>/app/PdfToDicom.exe` | Self contained desktop application (~62 to 69 MB) |
| `artifacts/<rid>/cli/pdftodicom.exe` | Self contained command line tool |
| `artifacts/PdfToDicom-<version>-<rid>/` | The folder handed to a site |
| `artifacts/PdfToDicom-<version>-<rid>.zip` | The same, zipped, and only when asked for |
| `artifacts/PdfToDicom-<version>-<rid>.exe` | The application again, named for the build, for handing one file to somebody |
| `artifacts/PdfToDicom-Setup-<version>-<arch>.exe` | Per user installer (Windows build only) |

#### Disk, and why the duplicates are free

The published application ends up under three names: the publish staging directory, the
handover folder, and the versioned standalone copy. Three real copies per architecture is
a quarter of a gigabyte of identical bytes, and a full three architecture build occupied
**1.2 GB** because of it.

The two derived names are now **hard links** rather than copies, which brings the same
build down to a measured **640 MB** while leaving every path present and every file
complete.
It is safe because nothing rewrites these files in place: signing runs before the package
is assembled, deliberately, so that the copies handed out are the signed ones. Linking
falls back to a copy wherever it is refused, because a build must not fail over an
optimisation.

`prune` then removes what can be produced again and keeps what somebody asked for. It
drops the publish staging directories and the unpacked handover folder, whose whole
content is inside the zip, and keeps the zip, the standalone executable and the installer.
A hard link is an independent name, so the standalone executable survives the staging
directory being deleted. A package folder with no zip beside it is kept, because deleting
the only copy of something never archived would lose it.

Measured on a three architecture build: 640 MB before, 513 MB after, with every remaining
file verified byte identical and every zip tested.

It is deliberately **not** part of `publish` or `all`: on Windows the installer is built by
a separate `package` run that reads the staging directory, and the CI step that confirms
the executable is self contained reads it too.

Since the executables are no longer committed, what is in `artifacts/` after a build is
all there is until somebody asks for a build again, so both scripts now report the size
rather than leaving it to be discovered.

Inside the handover folder and under the installer's application directory, the command
line tool sits in a `Command line` subfolder rather than beside `PdfToDicom.exe`. The two
assembly names differ only in case, and a case insensitive filesystem cannot hold both in
one directory: the second write overwrites the first and keeps the first one's spelling.
`Invoke-Assemble` and its shell counterpart both assert, before the zip is made, that the
required files are present under their exact names and that no two entries in the package
differ only in case.

---

## 13. Testing and verification

### Unit and workflow tests

```bash
dotnet test tests/PdfToDicom.Core.Tests/PdfToDicom.Core.Tests.csproj
```

265 tests covering validation rules, VR limits, UID uniqueness (20,000 identifiers per
strategy), person name construction and shortening, the worklist date ranges, the
settings password verifier, every mandatory attribute, payload integrity and odd length
padding, UTF-8 round trips, configuration persistence and corruption recovery, file name
pattern compilation and matching, how the source file is filed afterwards, and the full
workflow with a stubbed network.

### XAML binding verification

```bash
python3 build/validate-xaml-bindings.py
```

WPF resolves bindings when a window loads, not when it compiles. A binding that cannot
work is therefore a clean build followed by a crash on startup, and on a clinical
workstation that means the application simply refuses to open.

The application itself cannot be run anywhere but Windows, so this check stands in for
launching it. It reports:

- a binding to a dependency property that is **two way by default** (`TextBox.Text`,
  `ComboBox.SelectedItem`, `CheckBox.IsChecked`, `DatePicker.SelectedDate`,
  `RangeBase.Value` and others) whose source property has no public setter;
- a `StaticResource` key that is never defined;
- a `StaticResource` used earlier in a file than it is defined.

> This check exists because exactly that defect shipped. `ProgressBar.Value` was bound to
> a property with a private setter, and because `RangeBase.Value` binds two way by
> default, the main window failed to load on a Windows ARM64 machine. The build was clean
> and every test of the day passed. Only running the application would have revealed it.

### View model command verification

```bash
python3 build/validate-viewmodel-commands.py
```

Fails the build on any `RaiseCanExecuteChanged()` in a view model that is not null
conditional.

These view models assign a selection in the constructor, and those setters refresh the
commands whose enabled state depends on the selection. The commands are created in the same
constructor, so a refresh that runs before they exist throws a `NullReferenceException` and
the window cannot be opened. Written `Command?.RaiseCanExecuteChanged()` the call is
harmless whenever it runs, and the ordering stops being something anybody has to reason
about. The cost is one character per call site.

> This check also exists because exactly that defect shipped, in 3.3.0. The new worklist
> selection refreshed two commands without the question marks, and Settings would not open
> for anybody who had a worklist server configured. **With none configured it worked
> perfectly**, because `FirstOrDefault()` returns null, the setter short circuits and
> nothing runs, which is why every check passed and the fault reached a release. The
> destination selection beside it had carried the guard since it was written.

### Interoperability verification

```bash
build/verify-against-dcmtk.sh [port]
```

Requires DCMTK (`brew install dcmtk` / `apt-get install dcmtk`).

This is the check that answers the question a PACS administrator actually cares about:
**does an independent, standards conformant implementation accept what this application
produces?** Unit tests cannot answer it, because they exercise the same library that wrote
the file.

The script starts a real `storescp`, drives the application's own CLI to convert and send
a PDF with an accented Portuguese name, then confirms the archive received it, that
`dcmdump` parses it, that every mandatory attribute survived, that the accented name is
intact, and that the extracted PDF is **byte identical** to the input. It also renders the
received page images back with `dcm2pnm` and counts dark samples, because a renderer that
produced blank pages would satisfy every other check and be worse than useless.

It then starts a real **`wlmscpfs`**, DCMTK's worklist SCP, serving one entry built with
`dump2dcm`, and drives `pdftodicom worklist` against it. Verifying the client against the
project's own test server would only prove the two agree with each other; `wlmscpfs` was
written by somebody else from the standard, which is what makes a pass mean something.
That section confirms the Patient ID, Accession Number and Study Instance UID come back
intact, that an accented name survives under `ISO_IR 192`, that a 73 character name is
shortened with the surname kept whole, and that the filters genuinely narrow the result:
a wildcard matches, an unknown Patient ID returns nothing, and a scheduled date range in
the past returns nothing, which together establish the filters are sent as match keys
rather than echoed as return keys.

`wlmscpfs` needs an empty file named `lockfile` in the AE directory before it will answer
anything; without it the response is a bare `a700 Out of Resources` that says nothing
about the cause. The script creates it.

The last section covers the file name import, because that is the one path where nobody
looks at the patient details before the object is created. A document called
`9999999999_JohnDoe_TEST123451.pdf` is converted with `--from-filename` and nothing else,
and the object the archive received is then read back: the Patient ID and Accession Number
came from the name, `JohnDoe` became `Doe^John`, Patient Sex is `O`, Patient Birth Date is
**present and empty** as Type 2 requires of an unknown value, and Study Date is the file's
own creation date while Content Date is separately the moment of conversion. It then
confirms the source file was moved into `Imported/` byte for byte rather than deleted, that
a document named `77_JaneRoe_TEST777777.pdf` arrives at the archive as Patient ID
`0000000077` with its leading zeros restored, and that a file name matching no pattern is
refused, reaches the archive as nothing at all, and ends up in `Failed/` where somebody will
see it. The last of those checks failed the first
time it ran: the refused file was being left in the watched folder, where it would have
been retried for ever.

The worklist section also covers what changes once there is more than one server: that
each is named in the output so a department knows whose list it is reading, that a server
which is down does not take a reachable server's results with it, that naming one server
queries only that one, that an unknown name is refused rather than quietly querying
everything, and that a single-server configuration from an older version still answers over
a real association and is rewritten as a list.

94 checks, all passing as of this release.

---

## 14. The command line tool

`pdftodicom` drives the same workflow as the desktop application. It is shipped for the
same three Windows architectures, and gives hospital IT a scriptable path for bulk or
unattended conversion. It is also what the interoperability verification script drives,
which is why it is kept free of any Windows only dependency.

```bash
pdftodicom echo
pdftodicom worklist
pdftodicom worklist --worklist Radiology
pdftodicom worklist --patient-id P0000004
pdftodicom worklist --surname 'SILVA*'
pdftodicom send --pdf report.pdf --patient-id 12345 --surname Silva \
                --given-name Maria --dob 1970-05-21 --accession ACC001
pdftodicom generate --pdf report.pdf ... --no-send
pdftodicom inspect --pdf out/12345_ACC001_1234.dcm
pdftodicom config
pdftodicom settings-password
pdftodicom export-settings --out \\server\share\pacs-settings.json
pdftodicom import-settings --file \\server\share\pacs-settings.json
pdftodicom import-settings --file pacs-settings.json --worklists-only --replace
pdftodicom check-names --pattern '{PatientId}_{Name}_{AccessionNumber}' \
                       --name 9999999999_JohnDoe_TEST123451.pdf
pdftodicom send --pdf 9999999999_JohnDoe_TEST123451.pdf --from-filename
```

`worklist` queries the configured worklist and prints what came back, including the name
as it would be written to the object whenever that differs from what the worklist sent.
It is written for commissioning a site rather than for daily use: the operator picks a
patient in the window, not here. Its value is answering "is the worklist reachable, and
does it return what we expect?" over a real association, from a machine with nothing on
it but this one file, without touching the clinical interface. It is also what the
interoperability script drives.

`settings-password` is handled before any configuration, log folder or DICOM stack is set
up, so it works on a machine where none of those exist yet. The older name
`worklist-password` is still accepted. See section 8a.

`export-settings` and `import-settings` carry the archives and worklist servers between
machines without the settings dialog, so commissioning twenty workstations is a line in a
rollout script rather than an afternoon of retyping. See section 10a.

`check-names` and `--from-filename` read the patient details out of the file name. Neither
is a convenience: `check-names` is how a naming layout is proved before anybody relies on
it, and `--from-filename` refuses to convert a name it cannot read rather than falling back
to the command line. See section 8b.

**Exit codes:** `0` success · `1` invalid arguments or patient information ·
`2` generation or validation failure · `3` generated but transmission failed.

---

## 15. Deployment

### Installer

Per user by design:

- `PrivilegesRequired=lowest` — no UAC prompt, so a clinical user can install it
  themselves.
- Installs to `{localappdata}\PdfToDicom`, not Program Files.
- Registry entries under `HKCU` only.

A site preferring machine wide deployment can set `PrivilegesRequired=admin` and
`DefaultDirName={autopf}`. The application still needs no elevation to run.

**Uninstalling deliberately leaves `%APPDATA%\PdfToDicom` in place.** The audit trail is a
hospital record and is not removed automatically; the uninstaller says so explicitly.

### Pre-configuring workstations

Deploy `config.json` to `%APPDATA%\PdfToDicom\` before first launch. See
`samples/config.sample.json`.

### Code signing

**Signing happens by default wherever it can.** Neither script asks. On Windows, a code
signing certificate in the current user's store is used; on a Mac or Linux, a certificate
at `~/.pdftodicom/codesign.pfx`, or wherever `SIGN_PFX` points. A machine with none builds
unsigned, says so in a few lines, and carries on, because an unsigned build is complete and
runs. `-Sign` on Windows makes a missing certificate an error instead, for a release build
where handing over an unsigned executable would be the wrong outcome, and `-NoSign` skips
signing altogether.

Signing runs **after publishing and before the handover folder is assembled**, and then
again after the installer exists, because both the folder and the installer take the
executable as they find it.

**The hard link defect, which is the reason that order is written down.** The handover
folder's `PdfToDicom.exe` is a hard link to the published one, so that an 80 MB file is not
stored three times. `osslsigncode` cannot sign in place: it writes a second file, and
`build.sh` used to move that over the original. A move unlinks the original and renames the
new file into its place, so the handover copy kept pointing at the old, unsigned bytes while
the published file beside it was signed. A build that was asked to sign therefore handed
over an unsigned executable, and nothing said so. The signed bytes are now written *through*
the existing file, which keeps every name for it in agreement whatever order the build does
things in. Both halves were demonstrated: a move leaves the two names on different inodes,
and writing through leaves them on one.

By hand, for reference, `signtool` on Windows:

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
    /f certificate.pfx /p $password artifacts\win-x64\app\PdfToDicom.exe
.\build\build.ps1 -Task package -Runtimes win-x64
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
    /f certificate.pfx /p $password artifacts\PdfToDicom-Setup-3.5.0-x64.exe
```

---

## 16. Security considerations

| Concern | Position |
|---|---|
| **Network exposure** | Outbound only, to configured destinations. No listener, no inbound port. |
| **Data at rest** | Generated objects sit in the user's profile, protected by NTFS permissions. Enable auto delete after successful transmission where policy requires it. |
| **Data in transit** | Plain DICOM by default. TLS is available per destination; enable it where the archive supports it. |
| **Credentials** | None are stored. DICOM associations authenticate by AE Title and IP, configured on the archive. |
| **Elevation** | Never required. `asInvoker` in the manifest. |
| **Worklist settings** | Guarded by a PBKDF2 verifier compiled into the binary, not by an encrypted password. It deters a colleague on a shared workstation; it does not resist a decompiler, and section 8a says so rather than overclaiming. |
| **Worklist traffic** | Outbound C-FIND only. Nothing is ever written back to the worklist server, and no patient data leaves the workstation when the window is opened. |
| **Third party code** | fo-dicom (MS-PL), PdfPig (MIT), Serilog (Apache 2.0), ImageSharp 2.x (Apache 2.0), PDFtoImage (MIT) over PDFium (Apache 2.0) and SkiaSharp (MIT). All permissive and suitable for hospital deployment. |
| **PDF handling** | The payload is embedded verbatim and never executed or re-encoded. PdfPig only parses structure. |
| **Audit** | Every clinically significant action is recorded with user, machine and timestamp. |

---

## 17. Extending the application

**Add a language:** drop `<culture>.json` into
`src/PdfToDicom.Core/Localization/Strings/` beside `en-US.json`. It is embedded and
discovered automatically; no code change and no registration. Missing keys fall back to
English.

**Change how an over-length name is shortened:** `PersonNameShortener.Fit` is the only
place that decides, and `PersonNameShortenerTests` is the only place that has to change
with it. Nothing else reads the 64 character limit.

**Add a validation rule:** add it to the relevant validator. Both front ends pick it up,
because both call `ConversionWorkflowService.ValidateInputs`.

**Add a DICOM attribute:** add it to the appropriate `Add…Module` method in
`EncapsulatedPdfBuilder`, and to `EncapsulatedPdfValidator` if it is Type 1 or 2.

**Support another document type:** the encapsulated CDA and STL SOP classes follow the same
IOD shape. Generalise `EncapsulatedPdfBuilder` over the SOP class and MIME type; the
networking, validation and workflow layers need no change.

---

## 18. Known limitations

Stated plainly, because a hospital deploying this needs to know what it does not do.

1. **The user interface is not covered by automated tests.** The 265 tests cover Core in
   depth, but WPF cannot run on a build agent without a desktop session, so no test opens
   a window. `build/validate-xaml-bindings.py` and
   `build/validate-viewmodel-commands.py` catch the two classes of fault that have
   actually stopped a window opening, and neither is a substitute for running it.
   **Launch the application once on a real Windows machine, and open every window,
   before releasing any build.** Two defects of this shape have reached releases, one in
   2.x and one in 3.3.0; both compiled cleanly and passed every test.
2. **No document preview in the window.** Pages are rendered for transmission, but the
   interface still shows a document icon and metadata rather than a thumbnail.
3. **A Person Name longer than 64 characters cannot be sent whole.** The limit is the
   standard's, not this application's, and no implementation can send more. The surname is
   kept and the given name shortened; see section 8a.
4. **No Modality Performed Procedure Step.** The worklist is read; an order is never
   marked started or completed. A site that closes orders through MPPS must continue to do
   so from the acquiring modality.
5. **Encrypted PDFs are refused.** Encapsulating one produces an object no clinician can
   open from the archive.
6. **Single instance per user session.** Two instances would both monitor the folder and
   double-send the same document.
7. **No storage commitment.** The application confirms the C-STORE was accepted, not that
   the archive has committed the object to permanent storage.
8. **`Modality` defaults to `OT`, not the standard's `DOC`.** As specified, configurable,
   and discussed in section 4.
9. **The worklist settings password cannot be changed by a site.** That was the
   requirement: it is compiled in, with no interface to alter it. Changing it means a
   rebuild by the author.
