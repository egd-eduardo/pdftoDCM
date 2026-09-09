# PDF to DICOM

**A Windows desktop application that converts PDF documents into DICOM Encapsulated PDF
objects and sends them to a PACS, so that a report produced by a third party system becomes
part of the patient's imaging record.**

Built for hospital staff with no technical background: one window, three steps, large
buttons, and error messages written in plain language.

---

## At a glance

| | |
|---|---|
| **Platform** | Windows 10 (1809+) and Windows 11, x64 |
| **Prerequisites on the workstation** | **None.** No .NET, Java, Python or Visual C++ runtime |
| **Deployment** | One self contained `.exe`, or a per user installer with no UAC prompt |
| **Administrator rights** | Not required, to install or to run |
| **Network use** | Outbound DICOM only, to configured destinations. Everything else is offline |
| **SOP class produced** | Encapsulated PDF Storage `1.2.840.10008.5.1.4.1.1.104.1` |
| **Languages** | English, Portuguese (Portugal) |
| **Verified against** | DCMTK, end to end, by an automated script in this repository |

---

## What it does

1. **Select a PDF** — drag and drop, a Browse button, or automatic pickup from a monitored
   folder.
2. **Enter the patient information** — Patient ID, name, date of birth and accession number
   are mandatory and validated as you type. Barcode scanners work with no configuration.
3. **Generate a DICOM object** — a standard conformant Encapsulated PDF instance, validated
   against its Information Object Definition before it is allowed anywhere near the network.
4. **Send it to the PACS** — C-ECHO verification, then C-STORE, to one or several
   destinations, with retries, live progress and a clear explanation of any failure.

---

## Quick start

### For a hospital user

Run the installer, open the application, and follow **[the user
manual](docs/UserManual.md)** ([versão portuguesa](docs/ManualDoUtilizador.pt-PT.md)).

Your IT department must first register this workstation on the PACS. See the
[commissioning checklist](docs/DicomConformanceStatement.md#93-commissioning-checklist).

### For a developer

Requires only the .NET 8 SDK. The solution builds on Windows, **macOS** and Linux.

```bash
git clone <repository-url> && cd pdftoDCM

build/build.sh            # restore, build, test and publish the Windows executable
build/build.sh test       # run the 107 tests
```

On Windows, `.\build\build.ps1` additionally produces the installer.

> **Working on a Mac?** The .NET SDK cross compiles the Windows executable natively, so
> `build/build.sh` gives you a shippable `.exe`. The Inno Setup installer needs Windows, so
> push the branch and let [the CI workflow](.github/workflows/build-windows.yml) build it
> on a `windows-latest` runner. The cross platform CLI below lets you exercise real DICOM
> transmission from macOS in the meantime.

### Testing against a real archive, from any platform

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
src/PdfToDicom.Core/     All logic. net8.0, no presentation code, fully testable
src/PdfToDicom.App/      WPF desktop application. net8.0-windows
src/PdfToDicom.Cli/      Cross platform command line front end
tests/                   107 xUnit tests
build/                   Build scripts, installer, interoperability verification
docs/                    User manual (EN + PT), technical docs, conformance statement
samples/                 Annotated configuration, example PDF, example DICOM object
```

The layering is strict: `Core` targets plain `net8.0` rather than `net8.0-windows`, which
is what allows the whole of the DICOM construction, validation and networking to be tested
from a Mac, and what makes the desktop application and the CLI provably identical in
behaviour.

---

## Documentation

| Document | Audience |
|---|---|
| [User Manual](docs/UserManual.md) | Hospital staff |
| [Manual do Utilizador](docs/ManualDoUtilizador.pt-PT.md) | Pessoal hospitalar |
| [Technical Documentation](docs/TechnicalDocumentation.md) | Developers and IT |
| [DICOM Conformance Statement](docs/DicomConformanceStatement.md) | PACS administrators |
| [`samples/config.sample.json`](samples/config.sample.json) | Annotated configuration reference |

---

## Verification status

**107 unit and workflow tests pass**, covering validation rules, VR limits, UID uniqueness
across 20,000 identifiers per strategy, every mandatory attribute, payload padding, UTF-8
round trips, configuration corruption recovery and the complete workflow.

**End to end interoperability against DCMTK passes.** An automated script starts a real
`storescp`, drives the application's own CLI to convert and transmit a PDF carrying an
accented Portuguese patient name, and then confirms that the archive accepted it, that
`dcmdump` parses it, that every mandatory attribute survived, that the name is intact, and
that **the extracted PDF is byte identical to the input**.

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

- **No Modality Worklist query.** Demographics are typed or scanned, not retrieved from the
  RIS. This is the most significant functional gap and the obvious next feature.
- **No PDF thumbnail preview** — a document icon and metadata instead, to preserve the
  single file deployment.
- **Encrypted PDFs are refused**, because nobody could open one from the archive.
- **x64 Windows only**; no ARM64 build is currently produced.
- **No storage commitment**; acceptance of the C-STORE is confirmed, not permanent
  archival.

---

## Third party components

| Component | Licence | Purpose |
|---|---|---|
| [fo-dicom](https://github.com/fo-dicom/fo-dicom) 5.2 | MS-PL | DICOM encoding and networking |
| [PdfPig](https://github.com/UglyToad/PdfPig) 0.1 | MIT | Managed PDF inspection |
| [Serilog](https://serilog.net/) 4 | Apache 2.0 | Structured logging |

All permissive and suitable for hospital deployment.

---

## `prototype/`

The original Node.js proof of concept that preceded this application is preserved under
`prototype/node-dcmjs/` for reference. It is superseded by the .NET implementation and is
not part of the deliverable.
