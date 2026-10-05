# PDF to DICOM 3.0.0 — where to get a build

**The executables are no longer committed here.** Each is around 70 MB, three are produced
per release, and git keeps every one of them for ever; the repository was growing faster
than that was worth. Nothing about the product changed, only where the files live.

---

## Getting one

**From CI.** Run the *Build Windows release* workflow by hand, or push a version tag. All
three architectures are built, and each run carries:

| Artifact | What it is |
|---|---|
| `PdfToDicom-exe-<rid>` | The standalone, self contained executable |
| `PdfToDicom-package-<rid>` | The folder to hand to a site: application, command line tool, documentation and samples, zipped |
| `PdfToDicom-installer-<rid>` | The per user installer, with Start menu shortcuts and an uninstaller |

Retention is short deliberately: seven days for the executable and the package, fourteen
for the installer. Download what you need from the run that produced it.

**Locally.** Any commit can be built from source with no development experience: double
click `BUILD.bat`, or run `build\build.ps1 -Runtime win-x64`. See
[Building It Yourself](../../docs/BuildingItYourself.md).

---

## Which architecture?

| Build | Runs on | Notes |
|---|---|---|
| `win-x86` | **Everything**: 32 bit, 64 bit, ARM64, Windows Server | Natively on 32 bit, under WOW64 on 64 bit, under x86 emulation on Windows on ARM |
| `win-x64` | 64 bit Windows and Windows Server; Windows 11 on ARM | The usual choice for a normal hospital PC |
| `win-arm64` | Windows 10 and 11 on ARM | Native, so no emulation layer |

**If you want one file that runs on anything, take `win-x86`.**

**Matching the architecture is worth it when you can.** This is a desktop application that
stays open, renders a window and holds documents in memory, so on an ARM machine the
native ARM64 build is noticeably smoother than the 32 bit build under emulation. Use
`win-x86` when you want a single file to hand out and do not know what it will land on;
use the matching native build when you do.

Windows 10 version 1809 or later, or Windows 11. Windows Server 2019 or later.

**Windows will warn that the publisher is unknown.** Choose **More info**, then **Run
anyway**. Buying a code signing certificate removes that warning and is the single thing
that most improves how the software is received by a hospital IT department.

---

## First run

1. Double click the file. There is no installer and nothing is written outside your own
   user profile.
2. Open **Settings** and fill in the **Institution** and **Destinations** tabs. Your PACS
   administrator must have registered this workstation's AE Title and IP address first.
3. Use **Test connection** on the destination before converting anything real.
4. If your department has a modality worklist, see
   [Worklist Setup](../../docs/WorklistSetup.md). It is off by default.

The [user manual](../../docs/UserManual.md) covers day to day use, and there is a
[Portuguese version](../../docs/ManualDoUtilizador.pt-PT.md).

---

## Checking you have the right file

Each build is checked in CI for its PE machine type, so a build cannot silently fall back
to the runner's own architecture. To confirm one by hand in PowerShell:

```powershell
(Get-Item .\PdfToDicom-3.0.0-win-x64.exe).VersionInfo.FileVersion
```

That should report `3.0.0.0`.

---

The release before this one is described in [`../previous/`](../previous/).
