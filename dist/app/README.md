# PDF to DICOM 1.1 — ready to run

These are the application itself, built for each Windows architecture. Nothing needs to be
installed: no .NET, no Java, no Python, no Visual C++ runtime, and no administrator rights,
either to run them or to copy them onto a workstation.

---

## Which file?

**If you want one file that runs on anything, use
[`PdfToDicom-win-x86.exe`](PdfToDicom-win-x86.exe).**

| File | Runs on | Notes |
|---|---|---|
| `PdfToDicom-win-x86.exe` | **Everything**: 32 bit, 64 bit, ARM64, Windows Server | Runs natively on 32 bit, under WOW64 on 64 bit, and under x86 emulation on Windows on ARM |
| `PdfToDicom-win-x64.exe` | 64 bit Windows and Windows Server; Windows 11 on ARM | The usual choice for a normal hospital PC |
| `PdfToDicom-win-arm64.exe` | Windows 10 and 11 on ARM | Native, so no emulation layer |

Unlike the licence generator, this one **does** benefit from matching the architecture. It
is a desktop application that stays open, renders a window and holds documents in memory,
so on an ARM machine the native ARM64 build is noticeably smoother than the 32 bit build
running under emulation. Use `win-x86` when you want a single file to hand out and do not
know what it will land on; use the matching native build when you do.

Windows 10 version 1809 or later, or Windows 11. Windows Server 2019 or later.

**Windows will warn that the publisher is unknown**, because these are not code signed.
Choose **More info**, then **Run anyway**. Buying a code signing certificate removes that
warning and is the single thing that most improves how the software is received by a
hospital IT department.

---

## First run

1. Double click the file. There is no installer and nothing is written outside your own
   user profile.
2. Open **Settings** and fill in the **Institution** and **Destinations** tabs. Your PACS
   administrator must have registered this workstation's AE Title and IP address first.
3. Open **Settings → Licence** and paste the licence code for this site.
4. Use **Test connection** on the destination before converting anything real.

The [user manual](../../docs/UserManual.md) covers day to day use, and there is a
[Portuguese version](../../docs/ManualDoUtilizador.pt-PT.md).

---

## Licensing, from version 1.1

**This version requires a licence code before it will send anything to a PACS.**

Version 1.0 builds are unaffected: only a build carrying the publisher key checks
anything. So if you upgrade a workstation to 1.1 without giving it a code, it will stop
transmitting. **Issue the code first, then upgrade.**

If a code is missing, wrong, or long expired, the application still opens, still converts
and still saves the DICOM file locally. Only the transmission is withheld, and the reason
is stated in plain language. An expired licence keeps transmitting for a further fourteen
days with a warning, so nobody is ever cut off without notice.

Codes are issued with the generator in [`../licence-tool/`](../licence-tool/), which is
for the publisher only and is never given to a customer.

---

## Verifying you have the right file

Each build is checked in CI for its PE machine type, so a build cannot silently fall back
to the runner's own architecture. To confirm one by hand in PowerShell:

```powershell
(Get-Item .\PdfToDicom-win-x64.exe).VersionInfo.FileVersion
```

That should report `1.1.0.0`.

---

These files are rebuilt whenever the application changes. Installers for all three
architectures, which put shortcuts in the Start menu and register an uninstaller, are
attached to every CI run.
