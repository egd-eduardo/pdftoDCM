# PDF to DICOM

**A Windows desktop application that converts PDF documents and images into DICOM objects
and sends them to a PACS, so that a report or a scan produced by a third party system
becomes part of the patient's imaging record.**

Built for hospital staff with no technical background: one window, three steps, large
buttons, and error messages written in plain language.

---

## At a glance

| | |
|---|---|
| **Platform** | Windows 10 (1809+) and Windows 11 |
| **Architectures** | 32 bit (x86), 64 bit (x64) and ARM64, each a native build |
| **Prerequisites on the workstation** | **None.** No .NET, Java, Python or Visual C++ runtime |
| **Deployment** | One self contained `.exe`, or a per user installer with no UAC prompt |
| **Administrator rights** | Not required, to install or to run |
| **Network use** | Outbound DICOM only, to configured destinations. Everything else is offline |
| **Accepts** | PDF, and JPEG, PNG, TIFF, BMP, GIF, WebP, TGA images |
| **SOP classes produced** | Encapsulated PDF Storage `1.2.840.10008.5.1.4.1.1.104.1`, Secondary Capture Image Storage `1.2.840.10008.5.1.4.1.1.7` |
| **SOP classes queried** | Modality Worklist Information Model — FIND `1.2.840.10008.5.1.4.31` |
| **Languages** | English, Portuguese (Portugal) |
| **Author** | Eduardo Dias |
| **Licence** | Free to use, commercially and privately. Not open source; copyright retained |
| **Verified against** | DCMTK, end to end, by an automated script in this repository |

---

## What it does

1. **Select a PDF** — drag and drop, a Browse button, or automatic pickup from a monitored
   folder.
2. **Take the patient from the modality worklist**, or from the file's own name, or enter
   the details — Patient ID, name, date of birth and accession number are mandatory and
   validated as you type. Barcode scanners work with no configuration. Where a worklist is
   configured, none of it has to be typed, and the order's Study Instance UID files the
   document beside the images rather than in a study of its own. Where documents arrive
   from another system with the identifiers in the file name, a configurable naming layout
   reads them, and the file's creation time becomes the study date.
3. **Generate a DICOM object** — a standard conformant Encapsulated PDF instance, validated
   against its Information Object Definition before it is allowed anywhere near the network.
4. **Send it to the PACS** — C-ECHO verification, then C-STORE, to one or several
   destinations, with retries, live progress and a clear explanation of any failure.

---

## Quick start

### Just want to run it?

Prebuilt executables for **all three architectures** are committed at
[`dist/app/`](dist/app/). They are self contained, so download one and double click. If you
want a single file that runs anywhere, take the `win-x86` build; on an ARM machine the
native `win-arm64` build is noticeably smoother. Installers for all three architectures are
attached to every [CI run](../../actions).

### For a hospital user

Run the installer, open the application, and follow **[the user
manual](docs/UserManual.md)** ([versão portuguesa](docs/ManualDoUtilizador.pt-PT.md)).

Your IT department must first register this workstation on the PACS. See the
[commissioning checklist](docs/DicomConformanceStatement.md#93-commissioning-checklist).

### Just want to build it, with no programming background?

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), then double
click **`BUILD.bat`**. It checks what is needed, runs the tests, builds for your machine's
architecture and tells you where the result is. No IDE required. See
[Building It Yourself](docs/BuildingItYourself.md).

### For a developer

Requires only the .NET 8 SDK, or any later one: `global.json` rolls forward, and
`RollForward` lets the tests run on a newer runtime where the .NET 8 one is absent.

```powershell
git clone <repository-url>
cd pdftoDCM

.\build\build.ps1                          # test, publish and build the installer (x64)
.\build\build.ps1 -Runtime win-arm64       # native ARM64 build
.\build\build.ps1 -Runtime win-x86         # 32 bit build
.\build\build.ps1 -Task test               # run the 260 tests only
```

The product is Windows only. The build tooling itself is not: `build/build.sh` cross
compiles the same Windows binaries from macOS or Linux, which is what lets the test suite
and the DICOM interoperability check run on a Linux CI runner. Only the Inno Setup
installer genuinely requires Windows.

```bash
RID=win-arm64 build/build.sh publish        # from macOS or Linux
```

Every push builds all three architectures and attaches the installers to
[the CI run](.github/workflows/build-windows.yml).

### Testing against a real archive

```bash
# Start a reference DICOM archive
storescp -aet TESTPACS -od /tmp/received --fork 11112 &

# Convert and send
dotnet run --project src/PdfToDicom.Cli -- send \
    --pdf samples/example-report.pdf \
    --patient-id 000123456 --surname Silva --given-name Maria \
    --dob 1970-05-21 --accession ACC0001
```

Or run the full automated interoperability verification:

```bash
build/verify-against-dcmtk.sh
```

---

## Repository layout

```
"PDF to DICOM.sln"       Visual Studio solution
BUILD.bat                One click build, for anyone without a development setup
src/PdfToDicom.Core/     All logic. net8.0, no presentation code, fully testable
src/PdfToDicom.App/      WPF desktop application. net8.0-windows
src/PdfToDicom.Cli/      Command line front end for scripted conversion
tests/                   260 xUnit tests
tools/                   A Modality Worklist SCP for testing. Never packaged
build/                   Build scripts, installer, interoperability verification
dist/app/                The application, prebuilt for x86, x64 and ARM64
dist/previous/           The release before this one, kept for a site that needs to go back
docs/                    User manual (EN + PT), technical docs, conformance statement
samples/                 Annotated configuration, example PDF, example DICOM object
```

The layering is strict. `Core` targets plain `net8.0` rather than `net8.0-windows`. That is
a build time decision, not a product one: the shipped application is Windows only, but
keeping the logic free of a Windows target is what allows the DICOM construction,
validation and networking to be tested on a CI runner, and what makes the desktop
application and the CLI provably identical in behaviour.

---

## Documentation

| Document | Audience |
|---|---|
| [User Manual](docs/UserManual.md) | Hospital staff |
| [Manual do Utilizador](docs/ManualDoUtilizador.pt-PT.md) | Pessoal hospitalar |
| [Technical Documentation](docs/TechnicalDocumentation.md) | Developers and IT |
| [DICOM Conformance Statement](docs/DicomConformanceStatement.md) | PACS administrators |
| [Modality Worklist Setup](docs/WorklistSetup.md) | Hospital IT, commissioning the worklist |
| [Building It Yourself](docs/BuildingItYourself.md) | The copyright holder, no programming assumed |
| [Code Signing and Tamper Resistance](docs/CodeSigning.md) | The copyright holder |
| [Protecting Against Unauthorised Distribution](docs/ProtectingAgainstUnauthorisedDistribution.md) | The copyright holder |
| [`samples/config.sample.json`](samples/config.sample.json) | Annotated configuration reference |
| [`LICENSE`](LICENSE) | Licence terms. Free to use, commercial and private |
| [`CHANGELOG.md`](CHANGELOG.md) | What changed in each version |
| [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) | Third party component licences |

---

## Verification status

**260 unit and workflow tests pass**, covering validation rules, VR limits, UID uniqueness
across 20,000 identifiers per strategy, every mandatory attribute, payload padding, UTF-8
round trips, Person Name shortening, the worklist date ranges, the settings password
verifier, configuration corruption recovery, reading patient details out of a file name
and the complete workflow.

**All three architectures are verified as native builds.** CI checks the PE machine type
of each published executable, so a build cannot silently fall back to the runner's own
architecture.

**End to end interoperability against DCMTK passes.** An automated script starts a real
`storescp`, drives the application's own CLI to convert and transmit a PDF carrying an
accented Portuguese patient name, and then confirms that the archive accepted it, that
`dcmdump` parses it, that every mandatory attribute survived, that the name is intact, and
that **the extracted PDF is byte identical to the input**.

The same script then starts a real **`wlmscpfs`**, DCMTK's worklist SCP, and drives the
worklist client against it: the Patient ID, Accession Number and Study Instance UID come
back intact, an accented name survives, a 73 character name is shortened with the surname
kept whole, and the filters genuinely narrow the result. Verifying the client against
this project's own test server would only prove the two agree with each other.

---

## Notable engineering decisions

Recorded in full in the [technical documentation](docs/TechnicalDocumentation.md#4-key-design-decisions):

- **Self contained single file publish**, so no runtime is ever installed on a workstation.
  Trimming is deliberately disabled because WPF fails at runtime, not build time, when
  trimmed.
- **fo-dicom** for encoding and networking. DICOM encoding is never implemented by hand.
- **The PDF payload is embedded verbatim** and never re-encoded. Only even length padding
  is added, as VR OB requires.
- **`Modality` defaults to `OT`** as specified, with `DOC` selectable. The current standard
  defines `DOC` for encapsulated document series; the trade-off is documented rather than
  decided silently on a site's behalf.
- **The page count goes in a correctly reserved private block**, because the standard
  defines no attribute for it, and can be disabled.
- **A non conformant object is never transmitted.** IOD validation gates the network step.
- **The audit trail survives uninstallation**, because it is a hospital record.

---

## Known limitations

Stated openly; the full list is in the
[technical documentation](docs/TechnicalDocumentation.md#18-known-limitations).

- **The user interface has no automated test coverage.** WPF cannot run on a build agent,
  so `build/validate-xaml-bindings.py` and `build/validate-viewmodel-commands.py` catch
  the two classes of fault that have actually stopped a window opening, and nothing else
  does. **Launch any build once on real Windows, and open every window, before releasing
  it.** Two such defects have reached releases; both compiled cleanly and passed every
  test.
- **No Modality Performed Procedure Step.** The worklist is read; an order is never marked
  started or completed. A site that closes orders through MPPS must keep doing so from the
  acquiring modality.
- **A Person Name over 64 characters cannot be sent whole.** That limit is DICOM's, not
  this application's. The surname is kept and the given name shortened at a word boundary.
- **No PDF thumbnail preview** — a document icon and metadata instead, to preserve the
  single file deployment.
- **Encrypted PDFs are refused**, because nobody could open one from the archive.
- **No storage commitment**; acceptance of the C-STORE is confirmed, not permanent
  archival.

---

## Distribution protection

**There is no licence key mechanism.** One was built and then removed; nothing in the
application checks entitlement, and nothing withholds any feature. From 1.5.0 the software
is free to use commercially and privately, so there is nothing to enforce.

What remains is the licence agreement, which reserves redistribution and modification, and
the repository being private.

Builds are **signed with a self signed certificate** (`build\sign.ps1`, or
`build.ps1 -Sign`). This gives tamper evidence and a consistent publisher identity. It does
**not** remove the Windows "unrecognised publisher" warning, because the certificate is not
deployed to client machines; that is a deliberate trade-off. Obfuscation is not used, and
[Code Signing and Tamper Resistance](docs/CodeSigning.md) explains why, along with what it
would cost if that changes.

---

## Licence

**Free to use, commercially and privately. Copyright (c) 2026 Eduardo Dias.**

From version 1.5.0 there is no licence key, no activation, no expiry and no seat count. No
payment is due and no agreement need be signed to install and use it, on any number of
workstations and at any number of sites.

Copyright is retained, so **free to use is not the same as open source.** Redistributing it
outside your own organisation, modifying it, rebranding it or charging for it still need
permission, which is generally given on request. The purpose of that is knowing where it is
running, not restricting who may run it.

The clinical and no-warranty terms are deliberately strong: supplying software free of
charge into a hospital increases the need for them. It is not a medical device, it has not
been assessed under EU MDR, and no support is promised.

See [`LICENSE`](LICENSE) for the terms and [`CHANGELOG.md`](CHANGELOG.md) for what changed.

---

## Third party components

| Component | Licence | Purpose |
|---|---|---|
| [fo-dicom](https://github.com/fo-dicom/fo-dicom) 5.2 | MS-PL | DICOM encoding and networking |
| [PdfPig](https://github.com/UglyToad/PdfPig) 0.1 | MIT | Managed PDF inspection |
| [Serilog](https://serilog.net/) 4 | Apache 2.0 | Structured logging |
| [ImageSharp](https://github.com/SixLabors/ImageSharp) 2.1 | Apache 2.0 | Image decoding. Pinned to 2.x; 3.0 changed licence |
| [PDFtoImage](https://github.com/sungaila/PDFtoImage) 4.1 | MIT | PDF page rendering, over PDFium (Apache 2.0) and SkiaSharp (MIT) |

All permissive and suitable for hospital deployment.

---

## `prototype/`

The original Node.js proof of concept that preceded this application is preserved under
`prototype/node-dcmjs/` for reference. It is superseded by the .NET implementation and is
not part of the deliverable.
