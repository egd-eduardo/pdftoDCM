# PDF to DICOM — Technical Documentation

**Version 1.0 · For developers, PACS administrators and hospital IT**

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
(`1.2.840.10008.5.1.4.1.1.104.1`) carrying operator supplied patient demographics, and
transmits it to one or more DICOM Storage SCPs.

Everything except that transmission happens offline.

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
│   │   ├── Networking/              Verification and Storage SCU
│   │   ├── Pdf/                     PdfPig based inspection
│   │   ├── Logging/                 Serilog logger, audit trail, log export
│   │   ├── Monitoring/              FileSystemWatcher based folder monitor
│   │   ├── Localization/            Localizer + embedded en-US.json, pt-PT.json
│   │   ├── Licensing/               Licence codec, signing, machine fingerprint
│   │   └── Services/                ConversionWorkflowService, ApplicationServices
│   │
│   ├── PdfToDicom.App/              WPF desktop application. net8.0-windows
│   │   ├── Views/                   MainWindow, Settings, TagViewer, LogViewer
│   │   ├── ViewModels/              MVVM view models
│   │   ├── Infrastructure/          ObservableObject, commands, theming, localization
│   │   ├── Converters/              Value converters
│   │   ├── Themes/                  Light.xaml, Dark.xaml, Controls.xaml
│   │   └── app.manifest             asInvoker, per monitor DPI
│   │
│   ├── PdfToDicom.Cli/              Headless front end for scripted conversion
│   │
│   └── PdfToDicom.LicenceTool/      Licence generator. PUBLISHER ONLY, never shipped
│
├── tests/PdfToDicom.Core.Tests/     139 xUnit tests
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

### Licensing is dormant by default and never blocks

The licence check is compiled into every build, and every published build has a blank
`LicenceService.PublisherPublicKey`, which makes the whole subsystem inert: `IsEnforced` is
false, `Check` returns `NotEnforced` for any input, the Licence tab is collapsed, and the
workflow gate is a no-op. A test asserts the constant is blank so it cannot change by
accident.

The alternative, a separate "licensed" build configuration, was rejected: it produces two
binaries that differ in the code path that matters, and the untested one is the one that
ships.

When the feature is switched on, three properties are deliberate:

- **Only transmission is gated.** Generation, validation and local saving always work.
  A clinician can always produce the document.
- **Failure is permissive.** If the licence check throws, if the embedded key is
  unreadable, or if no licence service was supplied, transmission proceeds and the fault
  is logged. A defect in the licensing code must never stop a report reaching a record.
- **Expiry has a fourteen day grace period.** A licence that lapses at midnight in a
  hospital is a clinical incident, not a commercial success.

The retry action is gated separately in `RetryTransmissionAsync`, because it does not pass
through `RunAsync` and would otherwise be a way around the check.

Full detail is in [the licensing workflow](LicensingWorkflow.md).

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

## 7. UID generation

Two strategies, both guaranteeing uniqueness without central coordination.

### UUID derived (default)

A version 4 UUID is read as a big endian 128 bit unsigned integer and appended to `2.25.`,
as permitted by **PS3.5 Annex B.2**. Requires no registered root and cannot collide with
any other system's identifiers.

### Registered organisation root

`<root>.<discriminator>.<yyyyMMddHHmmssfff>.<random>`

The random component is cryptographic and bounded to twelve digits, giving a known maximum
width. The maximum root length is **derived** from the component widths:

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
```

### From macOS or Linux

The build tooling cross compiles the Windows binaries, which is what lets the tests and the
interoperability check run on a Linux CI runner.

```bash
build/build.sh                       # restore, build, test, publish (win-x64)
RID=win-arm64 build/build.sh publish # a different architecture
build/build.sh test
build/build.sh clean
```

This produces the Windows executables. It cannot produce the installer.

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
| `artifacts/<rid>/app/PdfToDicom.exe` | Self contained desktop application (~65 to 69 MB) |
| `artifacts/<rid>/cli/pdftodicom.exe` | Self contained command line tool |
| `artifacts/PdfToDicom-portable-<rid>.zip` | Portable package with docs and samples |
| `artifacts/PdfToDicom-Setup-1.0.0-<arch>.exe` | Per user installer (Windows build only) |

---

## 13. Testing and verification

### Unit and workflow tests

```bash
dotnet test tests/PdfToDicom.Core.Tests/PdfToDicom.Core.Tests.csproj
```

139 tests covering validation rules, VR limits, UID uniqueness (20,000 identifiers per
strategy), person name construction, every mandatory attribute, payload integrity and odd
length padding, UTF-8 round trips, configuration persistence and corruption recovery,
licence signing, tampering, expiry, grace period and machine binding, and the full workflow
with a stubbed network.

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
> and all 107 tests passed. Only running the application would have revealed it.

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
intact, and that the extracted PDF is **byte identical** to the input.

All checks pass as of this release.

---

## 14. The command line tool

`pdftodicom` drives the same workflow as the desktop application. It is shipped for the
same three Windows architectures, and gives hospital IT a scriptable path for bulk or
unattended conversion. It is also what the interoperability verification script drives,
which is why it is kept free of any Windows only dependency.

```bash
pdftodicom echo
pdftodicom send --pdf report.pdf --patient-id 12345 --surname Silva \
                --given-name Maria --dob 1970-05-21 --accession ACC001
pdftodicom generate --pdf report.pdf ... --no-send
pdftodicom inspect --pdf out/12345_ACC001_1234.dcm
pdftodicom config
```

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
    /f certificate.pfx /p $password artifacts\PdfToDicom-Setup-1.0.0-x64.exe
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
| **Third party code** | fo-dicom (MS-PL), PdfPig (MIT), Serilog (Apache 2.0). All permissive and suitable for hospital deployment. |
| **PDF handling** | The payload is embedded verbatim and never executed or re-encoded. PdfPig only parses structure. |
| **Audit** | Every clinically significant action is recorded with user, machine and timestamp. |

---

## 17. Extending the application

**Add a language:** drop `<culture>.json` into
`src/PdfToDicom.Core/Localization/Strings/` beside `en-US.json`. It is embedded and
discovered automatically; no code change and no registration. Missing keys fall back to
English.

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

1. **The user interface is not covered by automated tests.** The 107 tests cover Core in
   depth, but WPF cannot run on a build agent without a desktop session, so no test opens
   a window. `build/validate-xaml-bindings.py` catches the binding errors that stop the
   application starting, and is not a substitute for running it. **Launch the application
   once on a real Windows machine before releasing any build.**
2. **No PDF page rendering.** The interface shows a document icon and metadata, not a
   thumbnail. Rendering would require a native component and would defeat the single file
   deployment.
3. **No Modality Worklist query.** Demographics are typed or scanned, not retrieved from
   the RIS. A worklist SCU is the obvious next feature and would eliminate the transcription
   risk that section 4 of the user manual warns about.
4. **Encrypted PDFs are refused.** Encapsulating one produces an object no clinician can
   open from the archive.
6. **Single instance per user session.** Two instances would both monitor the folder and
   double-send the same document.
7. **No storage commitment.** The application confirms the C-STORE was accepted, not that
   the archive has committed the object to permanent storage.
8. **`Modality` defaults to `OT`, not the standard's `DOC`.** As specified, configurable,
   and discussed in section 4.
