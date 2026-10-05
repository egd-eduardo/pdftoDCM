# Changelog

## 3.0.0

**Patient details can now be taken from a Modality Worklist, so the step where a document
is most likely to be filed against the wrong patient disappears.**

Typing a patient number by hand is the single likeliest way a report ends up in someone
else's record. Where a department has a modality worklist, the operator now presses
**From worklist...**, picks the scheduled procedure, and the Patient ID, name, date of
birth, sex, accession number and referring physician come from the order itself.

This is a major version because the worklist changes where identity comes from, not
because anything was removed. Nothing in the previous behaviour has gone: a site with no
worklist uses the application exactly as before, and the feature is disabled by default.

### What it does

- **Modality Worklist Information Model — FIND** (`1.2.840.10008.5.1.4.31`) as an SCU.
  Configured separately from the destinations, because a worklist server is usually a
  different system from the archive.
- **The order's Study Instance UID is carried into the document.** This is the most
  valuable thing the response holds: the order already created the study the images will
  land in, so the report is filed beside them rather than in a study of its own. It
  overrides study grouping exactly as a Study Instance UID typed by the operator does.
- **Configurable match keys**: Called AE Title, host, port, Calling AE Title override,
  modality and scheduled station, each of which may be left blank. Blank is not an
  incomplete setting; it is DICOM universal matching, which is what "any modality" should
  mean.
- **Date intervals**: today (the default), yesterday and today, the last seven days, any
  date, or an explicit range. A range entered backwards is put the right way round rather
  than refused.
- **Any character set.** The query declares `ISO_IR 192`, so accented and non Latin names
  come back intact rather than transliterated.
- **Search by patient number or surname**, wildcards included, or leave both blank for
  the whole list.
- **Read only.** Nothing is ever written back to the worklist, nothing is cached on disk,
  and a worklist that is unreachable never blocks anything: the operator types the
  details in as before.

### Long names

DICOM allows 64 characters for a Person Name component group, which is the whole
`Family^Given` pair and not 64 each. Some Portuguese names are longer, and no
implementation can send them whole; the limit is the standard's.

The surname is kept complete, because that is what an archive indexes and what a
clinician searches by, and the given name is shortened at a word boundary so it still
reads as a name. Where the surname alone is over the limit, it is cut at a word boundary
and the given name dropped.

This happens without interrupting anyone, as asked. It is not hidden: the worklist window
shows the name that will actually be used underneath the selected row, and every
shortening is written to the audit log with the original length, so a later question
about why a name differs from the RIS has an answer.

Middle names are not carried across. The department does not use them, and leaving them
out means they cannot consume characters the first and last name need.

### The worklist settings are locked

Pointing a workstation at the wrong worklist server does not produce an error. It
produces a list of the wrong patients, presented exactly as convincingly as the right
ones. The Worklist tab in Settings is therefore protected by a password compiled into the
executable, with no interface to change it.

What is stored is a **PBKDF2-HMAC-SHA256 verifier**, 210,000 iterations with a random
salt, not an encrypted password. A hash is strictly stronger here: anything encrypted
inside a binary must carry its key inside the binary too, and could be decrypted back to
the password. This cannot.

Stated honestly: the check runs on the workstation, so it deters a colleague who should
not be in there. It is not a defence against someone with a decompiler, and no password a
desktop application checks by itself could be.

### A test worklist server, for sites that have none

`tools/PdfToDicom.TestWorklistServer` serves seven invented scheduled procedure steps
over a real DICOM association, so the whole path can be exercised without touching
anything clinical. Its fixtures are deliberately awkward: accented names, a patient with
no given name, a 73 character name whose surname fits, and a 75 character name whose
surname alone is over the limit.

**It is never packaged**, and the build now fails outright if it, or any of the three
documents written for the copyright holder, turns up in an assembled package.

### Also in this release

- **`pdftodicom worklist`** queries the configured worklist and prints what came back,
  including the name as it would be written to the object whenever that differs. Written
  for commissioning a site: it answers "is the worklist reachable, and does it return
  what we expect?" from a machine with nothing on it but one file.
- **`pdftodicom worklist-password`** turns a password into the verifier to compile in,
  reading it without echoing so it never has to be told to anyone.
- **The interoperability script now verifies the worklist against DCMTK `wlmscpfs`**, not
  against this project's own test server. Verifying a client against our own server would
  only prove the two agree with each other.
- **The previous release is kept** under `dist/previous/2.1.0/`.

### Verified

173 automated tests, and the full interoperability script against DCMTK `storescp` and
`wlmscpfs`: the Patient ID, Accession Number and Study Instance UID come back intact, an
accented name survives the round trip, a 73 character name is shortened with the surname
kept whole, and the filters genuinely narrow the result — a wildcard matches, an unknown
Patient ID returns nothing, and a date range in the past returns nothing.

### Upgrading

Nothing to do. Existing configuration files load unchanged; the `Worklist` section is
added with the worklist disabled. Objects produced by this version carry Implementation
Version Name `PDFTODICOM_3_0`.

---

## 2.1.0

**A converted PDF now also arrives as page images, so a viewer that needs an image will
open it.**

Many PACS viewers will only launch on a study that contains an image. An Encapsulated PDF
is not an image SOP Class, so at such a site the viewer refused to open and the report
could not be read at all, however conformant the object was.

Converting one PDF now produces two series in a single study: the encapsulated document,
and one Secondary Capture image per page. The viewer opens, the report is legible on
screen, and the document remains authoritative with selectable text.

### Why the pages are rendered rather than a blank image sent

A blank placeholder would have satisfied the viewer with far less work. It was rejected:
a blank image in a patient's record is actively misleading. A clinician scanning a worklist
sees an empty thumbnail and reasonably concludes the study is empty or the acquisition
failed, so the study holding the actual report gets skipped. It would satisfy the viewer
while defeating the purpose of sending the report.

### Details

- **150 DPI by default**, which keeps body text comfortably readable and is what this was
  verified at. Configurable through `PdfRenderDotsPerInch`.
- **Greyscale when the page has no colour**, decided from the pixels rather than assumed.
  A report is usually black text on white, and storing that as RGB would triple every
  object for nothing.
- **Every page, never a subset.** Rendering only the first page would let a reader believe
  they had seen a report whose findings continue on page three.
- **Own series, same study.** The pages are numbered so a viewer pages through them in
  order, and stay distinguishable from the document.
- **Derivation Description** records that the pixels were rendered from the document in
  the same study, so an auditor need not guess why one study holds the report twice.
- **Switchable.** `RenderPdfPagesAsImages`, on the DICOM tab in Settings, restores the
  single object behaviour for a site whose viewer already displays encapsulated documents.
- **Rendering never blocks the report.** If the renderer cannot load or a document will not
  render, the failure is logged and the encapsulated PDF is produced and sent regardless.

### The first native dependency

Rendering needs PDFium, the PDF engine in Chrome, reached through PDFtoImage. PDFium is
Apache-2.0 and PDFtoImage is MIT, matching the existing licence set, and all the pieces
ship for win-x86, win-x64 and win-arm64. The alternatives were Ghostscript and MuPDF, both
AGPL, which would place conditions on every hospital receiving this software.

This is the first native code in the product and costs roughly 11 to 13 MB per executable
before compression. The command line tool now publishes with
`IncludeNativeLibrariesForSelfExtract`, which it never needed before; without that change
its single file promise would have quietly stopped being true.

### Fixed along the way

- **The interoperability check could test the wrong object.** It took the first file the
  archive stored, which with several objects arriving was no longer reliably the document.
- **Two of its checks could pass without reading anything.** They matched on the DICOM
  keyword, which `dcmdump` prints *after* the value, so the reads returned empty strings
  and comparing two empty strings succeeded. Attributes are now read by tag number.


## 2.0.1

- **Dragging an image onto the window did nothing.** The drop handler still required a
  `.pdf` extension, so every image was refused even though the window invited one and the
  Browse button accepted one. It now takes any single file and decides what it is from the
  content, as everything else in the application does.
- **A file that is neither a PDF nor an image** was reported as not having a PDF header,
  which is true and unhelpful. The message now names what can be converted.

## 2.0.0

**Images can now be converted, and several files can share one study.** The major version
reflects a new SOP Class and a new way of forming studies, not a rewrite: the PDF
conversion is unchanged and its objects are byte for byte what 1.5.5 produced.

### Images

JPEG, PNG, TIFF, BMP, GIF, WebP and TGA files are converted to **Secondary Capture Image
Storage** objects (`1.2.840.10008.5.1.4.1.1.7`), transmitted the same way documents are.

- **A multi page source, typically a scanned TIFF, becomes one DICOM image per page**, all
  sharing one series and numbered in order, so a viewer pages through them as one document.
  Single frame Secondary Capture is used rather than the multi frame SOP Classes because it
  is the most widely supported image class in the field, including on older archives.
- **Greyscale stays greyscale, decided from the pixels rather than the file format.** A
  flatbed scanner emitting an RGB JPEG of a black and white page produces a `MONOCHROME2`
  object: two thirds smaller, and nothing is lost, because if red, green and blue are equal
  in every pixel there is no colour to discard.
- **What a file is comes from its content, not its name.** A scanner that writes a TIFF and
  calls it `.jpg`, or a renamed file, converts correctly. This applies to PDFs too.
- **Lossy history is recorded.** A JPEG source sets Lossy Image Compression to `01` and the
  method to `ISO_10918_1`, because the pixels have already lost information that no later
  step recovers and a clinician judging fine detail is entitled to know it.
- **Modality for images is configured separately** from the document modality, so a site
  using `DOC` for documents does not end up labelling photographs as encapsulated documents.

Pixel data is stored uncompressed, as the documents are, so every conformant archive
accepts it and no codec library is needed. The cost is size: a twelve megapixel photograph
becomes an object of roughly 36 MB. The operator is shown the pixel dimensions before
converting, and a page beyond roughly 1.2 GB raises a warning.

### Study grouping

A new setting decides how converted files are gathered into studies:

| Mode | Behaviour |
|---|---|
| Each file in its own study | Every conversion creates a new study. **This is the default** and is what every earlier version did |
| Files for the same patient share one study | Conversions for the same Patient ID reuse one Study Instance UID |

**Each file always gets its own Series Instance UID, in both modes.** That is what keeps
the files distinguishable inside a study, and it is why grouping loses nothing: a viewer
still shows each document or image separately, it simply shows them together.

The association is held in memory and **never written to disk**. A patient returning in
three months is having a new examination, and their new report must not silently join the
study created for the old one. Changing the Patient ID, or closing the application, starts
a new study. Patient identifiers are compared exactly, including case, because they are
opaque strings from another system and treating `ab1` and `AB1` as one patient is a guess.

A Study Instance UID typed on the form, or passed as `--study-uid`, overrides grouping.

### Command line

- `--file` converts anything; `--pdf` and `--image` are accepted as the same option, since
  the content decides what the file is.
- **`--file` may be repeated** to convert several files in one run. That is what makes
  grouping usable from a script, because the association lives for one run of the tool.
- Multi page results are reported page by page.

### Executables carry their version

The standalone builds are now named `PdfToDicom-2.0.0-win-x64.exe` and so on, so a
downloaded file says which build it is without anyone opening its properties. The
executable **inside** the package and the installer stays `PdfToDicom.exe`, because the
manuals, the Start Menu shortcut and the file associations all name it; those already sit
inside a folder and an installer that carry the version.

### Under the hood

- The modules shared by both kinds of object, Patient, General Study, General Series,
  General Equipment, SC Equipment, SOP Common and the file meta information, are written in
  one place rather than two. A correction applied to one and forgotten in the other would
  land on the attributes that decide which patient a document is filed against.
- `SopClassDicomValidator` applies the rules for whichever SOP Class an object declares, so
  the workflow does not decide what kind of object it is holding in a second place.
- The image validator checks that the pixel data is exactly the length the dimensions
  promise. A viewer handed a buffer of the wrong length renders a torn image rather than
  refusing it, which is the worst outcome for a clinical image.

### Not changed

The PDF conversion path. Its objects were compared tag for tag against 1.5.5 output and are
identical, and the DCMTK interoperability check still proves the embedded PDF byte
identical to the input.

### Verification

136 tests, up from 113. The DCMTK check now also converts a hand built three page greyscale
TIFF, transmits it to a real archive, and confirms the archive stored three instances in
one series and one study with **every pixel of every page identical** after rendering back.

---

## 1.5.5

### A registered UID root

The software now owns a UID root registered with Medical Connections Ltd,
`1.2.826.0.1.3680043.9.6008`, and the "Registered UID root" field in Settings is filled in
from it rather than left for a site to complete.

- **The field is fixed and disabled.** A registered root identifies its registrant, so it
  is not a site's to set: a hospital generating identifiers beneath it would be claiming an
  arc it does not own. Anything left in `OrganizationUidRoot` by an earlier version is read
  and discarded rather than obeyed.
- **It is shown partly masked**, as `1.2.826.0.1.3680043.*.****`, and the field is disabled
  rather than merely read only, so the text cannot be selected and copied either. This
  stops the prefix being taken off a screen. It is **not** secrecy: every DICOM object the
  software produces carries UIDs built from the root, so anyone holding one can read it,
  and it is present in the executable.
- **The uniqueness undertaking is tested, not asserted.** The registration requires that
  UIDs beneath the prefix cannot be duplicated. The random component of each identifier is
  widened from twelve digits to fifteen, a thousand million million values within any one
  millisecond, and a new test generates 20,000 identifiers in a tight loop where most share
  a timestamp, so uniqueness rests entirely on that component. A generated UID is 62
  characters, two inside the 64 character limit.
- **The choice of strategy is unchanged.** UUID derived (`2.25.`) remains the default and
  is unique by construction. Select the registered root if your archive prefers identifiers
  under a registered arc.

### Two packaging faults, both of which only appeared on Windows

- **The handover folder and the installer lost the desktop application.** The two
  executables are named `PdfToDicom.exe` and `pdftodicom.exe`, which differ only in case.
  Windows filesystems are case insensitive, so copying both into one folder did not
  produce two files: the second overwrote the first and kept the first one's spelling.
  What was handed over was a `PdfToDicom.exe` that was really the command line tool, and
  double clicking it opened a console rather than the application. The installer put both
  into its application directory too, so the Start Menu shortcut had the same problem.

  The command line tool now goes into a `Command line` subfolder in both.

  This was invisible on Linux and macOS, where the two names are distinct, which is why an
  assembly verified there looked correct. The Windows build reported 11 files in the
  package where the same code on Linux reported 12.

- **The installer shipped three documents written for the copyright holder**, on building
  the software, code signing and protecting against unauthorised distribution, the last of
  which discusses the customer. It copied `docs\*` with a wildcard. It now copies the same
  named list the handover package uses.

- **The build refuses to produce either fault again.** Before the package is zipped it
  asserts that the required files are present under their exact names, and that no two
  entries differ only in case. The first catches the loss as Windows shows it, a missing
  name; the second catches it as Linux shows it, two names that cannot coexist. Both
  checks are in `build.ps1` and `build.sh`.

### Also

- **The XAML validator now checks localization keys.** A mistyped `{loc:Localize}` key does
  not throw; the localizer returns the key, so the label renders as
  `Settings.OrganizationUidRoot.Caption` on a clinical workstation. Nothing caught that
  before a user was looking at it. All 114 keys in the interface are now checked against
  the English string table on every build.

### Not changed

The conversion, validation and transmission behaviour is as in 1.5.0. 113 tests pass, six
more than 1.5.0, all six covering the registered root.

## 1.5.0

**Licensing dropped. The software is free to use, commercially and privately.**

There is no licence key, no activation, no expiry, no seat count and no feature is
withheld. No payment is due and no agreement need be signed to install and use it, on any
number of workstations, at any number of sites.

Copyright is retained. Free to use is not the same as open source: redistributing outside
your own organisation, modifying, rebranding or charging for it still need permission,
which is generally given on request. The point of that clause is knowing where the software
is running, not restricting who may run it. See [`LICENSE`](LICENSE).

### What changed

- **Licence key enforcement removed entirely.** The codec, signing, machine fingerprint,
  transmission gate, Settings tab and generator tool are gone from the code, not merely
  disabled. Nothing checks entitlement.
- **`LICENSE` rewritten** to grant free commercial and private use, with the clinical and
  no-warranty terms strengthened rather than relaxed. Supplying software free of charge
  into a clinical setting increases the need for those terms; it does not reduce it.
- **Code signing added.** `build/sign.ps1` signs the application, the command line tool and
  the installer with a self signed certificate, and `build.ps1 -Sign` runs it as part of a
  release. Every signature is timestamped, so it outlives the certificate.

  **Nothing needs to be installed.** It uses PowerShell's `Set-AuthenticodeSignature`,
  which is part of Windows. `signtool.exe` and the Windows SDK are not required.

  **Windows will still warn that the publisher is unrecognised.** A self signed certificate
  is not trusted by default and is deliberately not being deployed to client machines. What
  signing provides is tamper evidence, a consistent publisher identity, and the ability for
  anyone to verify a file is exactly what was signed. See
  [`docs/CodeSigning.md`](docs/CodeSigning.md).
- **`BUILD.bat` added**, so the application can be built by anyone with the free .NET SDK
  and no development environment. It checks prerequisites, runs the tests, refuses to build
  if any fails, and reports where the result is. See
  [`docs/BuildingItYourself.md`](docs/BuildingItYourself.md).
- **DICOM Implementation Version Name** is now `PDFTODICOM_2_0`, which is how a PACS
  administrator identifies which build produced an object.

### Upgrading

Nothing to do. A 1.5.0 build never asks for anything. Configuration files from earlier
versions are read unchanged; the obsolete `LicenceCode` setting, if present, is ignored.

### Not changed

The conversion, validation and transmission behaviour is identical to 1.1.0. The same 107
tests pass and the same end to end interoperability check against DCMTK passes, with a byte
identical PDF round trip.

---

## 1.1.0

Licence key enforcement, since removed in 1.5.0. Not recommended; use 1.5.0.

- Signed offline licence codes, entered in Settings, gating transmission only.
- Application published for x86, x64 and ARM64.

## 1.0.0

First release.

- Converts PDF documents to DICOM Encapsulated PDF (`1.2.840.10008.5.1.4.1.1.104.1`) and
  transmits them to a PACS by C-STORE, with C-ECHO verification first.
- Single window WPF interface, English and Portuguese (Portugal).
- Self contained single file deployment; no runtime installed on the workstation, no
  administrator rights to install or run.
- Per user installer with no elevation prompt.
- Folder monitoring, barcode scanner input, patient form templates, DICOM tag viewer,
  audit trail and exportable logs.
- Verified end to end against DCMTK, with the embedded PDF proven byte identical to the
  input.
