# Changelog

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
- **DICOM Implementation Version Name** is now `PDFTODICOM_1_5`, which is how a PACS
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
