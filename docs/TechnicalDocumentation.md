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
9. [Validation](#9-validation)
10. [Configuration](#10-configuration)
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
│   │   ├── Security/                Worklist settings password verifier
│   │   ├── Imaging/                 Image decoding and PDF page rendering
│   │   ├── Logging/                 Serilog logger, audit trail, log export
│   │   ├── Monitoring/              FileSystemWatcher based folder monitor
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
├── tests/PdfToDicom.Core.Tests/     173 xUnit tests
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

### Password protection

`WorklistSettingsLock` guards the Worklist tab in Settings behind a password compiled
into the binary.

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
pdftodicom worklist-password
```

reads it without echoing, derives the verifier locally, and prints the single line to
paste into `WorklistSettingsLock.Verifier` before rebuilding. The password itself is
never written to a file, a log, a commit or a message.

An empty `Verifier` constant means no password has been set, and the Worklist tab is then
open. That is deliberate: shipping with a placeholder nobody knows would lock the author
out of their own software, which is a worse failure than an unset lock. The interface
reports the setting as unprotected rather than implying a protection that is not there.

### The test worklist server

`tools/PdfToDicom.TestWorklistServer` is a Modality Worklist SCP that serves seven
invented scheduled procedure steps. It exists because a site adopting this feature
usually has no worklist server it can point a test workstation at: the real one holds
live patient data and is not somewhere to try new software against.

```bash
dotnet run --project tools/PdfToDicom.TestWorklistServer -- --port 1070 --aet TESTWL
```

Its fixtures are chosen to be awkward rather than convenient: accented names, a patient
with no given name, a 73 character name whose surname fits, and a 75 character name whose
surname alone is over the limit. It builds its data sets with `NotValidated()` precisely
so it can serve names fo-dicom would otherwise refuse to construct, which is the only way
to exercise the shortening rule against a real association.

**It is never packaged.** The build scripts copy a named list of files into the customer
package and it is not on that list; `assert_package_is_sound` checks the list rather than
the folder, so it cannot be added by accident.

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

## 11. Logging and audit

Two separate channels, deliberately.

**Diagnostic log** — Serilog, rolling daily, `shared: true` so support can copy the current
file while the application runs. Configurable retention.

**Audit trail** — tab separated, one line per event, one file per day:

```
Timestamp  User  Machine  Event  Detail
```

Events: `APPLICATION_START`, `APPLICATION_CLOSE`, `PDF_SELECTED`,
`PATIENT_INFO_ENTERED`, `DICOM_GENERATED`, `TRANSMISSION_SUCCESS`,
`TRANSMISSION_FAILURE`, `GENERATED_FILE_DELETED`, `CONFIGURATION_CHANGED`.

The format is stable and opens directly in a spreadsheet, so an information governance team
can review it without tooling. An audit write failure is reported to the diagnostic log but
never aborts a clinical workflow that has otherwise succeeded.

**Log export** produces a zip of both channels plus the configuration. It contains **no PDF
and no image data**.

---

## 12. Building

### Supported targets

The product is built for Windows only, natively for each of its three architectures:

| Runtime identifier | Target |
|---|---|
| `win-x86` | 32 bit Windows, still present on older clinical workstations |
| `win-x64` | The mainstream 64 bit workstation |
| `win-arm64` | Windows on ARM, natively rather than through x64 emulation |

CI verifies the PE machine type of every published executable, so a build cannot silently
fall back to the runner's own architecture.

### Prerequisites

- .NET 8 SDK
- Inno Setup **6.3 or later** — only needed to build the installer, and only available on
  Windows. The architecture identifiers used by the installer script require 6.3.

### On Windows

```powershell
.\build\build.ps1                        # everything, including the installer
.\build\build.ps1 -Runtime win-arm64     # native ARM64
.\build\build.ps1 -Runtime win-x86       # 32 bit
.\build\build.ps1 -Task publish
.\build\build.ps1 -Task package
.\build\build.ps1 -Task prune          # keep the deliverables, drop the intermediates
```

### From macOS or Linux

The build tooling cross compiles the Windows binaries, which is what lets the tests and the
interoperability check run on a Linux CI runner.

```bash
build/build.sh                       # restore, build, test, publish (win-x64)
RID=win-arm64 build/build.sh publish # a different architecture
build/build.sh test
build/build.sh prune                 # keep the deliverables, drop the intermediates
build/build.sh clean
```

This produces the Windows executables. It cannot produce the installer.

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
| `artifacts/PdfToDicom-<version>-<rid>.zip` | The same, zipped |
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

173 tests covering validation rules, VR limits, UID uniqueness (20,000 identifiers per
strategy), person name construction and shortening, the worklist date ranges, the
settings password verifier, every mandatory attribute, payload integrity and odd length
padding, UTF-8 round trips, configuration persistence and corruption recovery, and the
full workflow with a stubbed network.

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

All checks pass as of this release.

---

## 14. The command line tool

`pdftodicom` drives the same workflow as the desktop application. It is shipped for the
same three Windows architectures, and gives hospital IT a scriptable path for bulk or
unattended conversion. It is also what the interoperability verification script drives,
which is why it is kept free of any Windows only dependency.

```bash
pdftodicom echo
pdftodicom worklist
pdftodicom worklist --patient-id P0000004
pdftodicom worklist --surname 'SILVA*'
pdftodicom send --pdf report.pdf --patient-id 12345 --surname Silva \
                --given-name Maria --dob 1970-05-21 --accession ACC001
pdftodicom generate --pdf report.pdf ... --no-send
pdftodicom inspect --pdf out/12345_ACC001_1234.dcm
pdftodicom config
pdftodicom worklist-password
```

`worklist` queries the configured worklist and prints what came back, including the name
as it would be written to the object whenever that differs from what the worklist sent.
It is written for commissioning a site rather than for daily use: the operator picks a
patient in the window, not here. Its value is answering "is the worklist reachable, and
does it return what we expect?" over a real association, from a machine with nothing on
it but this one file, without touching the clinical interface. It is also what the
interoperability script drives.

`worklist-password` is handled before any configuration, log folder or DICOM stack is set
up, so it works on a machine where none of those exist yet. See section 8a.

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

The application is designed to be signed. Sign `artifacts/<rid>/app/PdfToDicom.exe` **before**
building the installer for that architecture, then sign the installer. Repeat per
architecture:

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
    /f certificate.pfx /p $password artifacts\win-x64\app\PdfToDicom.exe
.\build\build.ps1 -Task package -Runtime win-x64
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
    /f certificate.pfx /p $password artifacts\PdfToDicom-Setup-1.5.0-x64.exe
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

1. **The user interface is not covered by automated tests.** The 173 tests cover Core in
   depth, but WPF cannot run on a build agent without a desktop session, so no test opens
   a window. `build/validate-xaml-bindings.py` catches the binding errors that stop the
   application starting, and is not a substitute for running it. **Launch the application
   once on a real Windows machine before releasing any build.**
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
