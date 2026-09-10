# Changelog

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
