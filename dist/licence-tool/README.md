# Licence generator, prebuilt

**For the copyright holder only. Never send any of these files to a customer.**

These executables sign licence codes. Everything they can do depends on the private key
you create with `keygen`, so the security of the whole scheme rests on that key file, not
on these binaries being secret. Keep the key safe and never commit it.

---

## Which file do I run?

**If you want one file that runs everywhere, use
[`pdftodicom-licence-win-x86.exe`](pdftodicom-licence-win-x86.exe).**

A 32 bit Windows executable runs on every Windows this software targets: natively on 32
bit Windows, through WOW64 on 64 bit Windows and Windows Server, and through the built in
x86 emulation on Windows 10 and 11 on ARM. There is no configuration and nothing to
install.

The native builds are also here if you prefer one:

| File | Runs on |
|---|---|
| `pdftodicom-licence-win-x86.exe` | **Everything.** 32 bit, 64 bit, ARM64, Windows Server |
| `pdftodicom-licence-win-x64.exe` | 64 bit Windows and Windows Server, and Windows 11 ARM64 |
| `pdftodicom-licence-win-arm64.exe` | Windows 10 and 11 on ARM, natively |

The tool runs for a few milliseconds, so the native builds are no faster in any way you
could notice. Choose on convenience.

**Nothing needs to be installed.** No .NET, no Visual C++ runtime, no administrator
rights. Each file contains everything it needs. Windows may warn that the publisher is
unknown, because these are not code signed; choose **More info**, then **Run anyway**.

---

## Using it

Open a terminal in the folder containing the file. In File Explorer, type `cmd` in the
address bar and press Enter.

### Once, ever: create your key pair

```
pdftodicom-licence-win-x86.exe keygen
```

This writes `licence-private.key` beside the executable and prints your public key.

> **The private key is the entire system.** Anyone holding it can issue licences your
> application will accept. If you lose it, every licence already issued has to be reissued
> under a new key, which means a new build for every customer. Back it up in at least two
> places, for example a password manager and an offline copy. Never email it, and never
> commit it.

### Then: switch licensing on

The public key that `keygen` printed has to be compiled into the application. Send me that
line, or paste it yourself into
`src/PdfToDicom.Core/Licensing/LicenceService.cs` and rebuild.

The public key is not secret. It is safe to email, and it is meant to be embedded in
software you hand to customers.

Until that is done, the application saves licence codes but does not check them, and
nothing is restricted. The Licence tab in Settings says so plainly.

### Issuing a code for a customer

```
pdftodicom-licence-win-x86.exe issue --licensee "Hospital de Exemplo" --seats 20
pdftodicom-licence-win-x86.exe issue --licensee "Clinica Norte" --days 365
pdftodicom-licence-win-x86.exe issue --licensee "Clinica Sul" --machine A1B2-C3D4-E5F6-7890
```

The output is one line beginning `PDFDCM1.`. Email it to the customer, who pastes it into
**Settings → Licence**.

Omit `--expires` and `--days` for a perpetual licence. Use `--machine` with the identifier
the customer reads out from that same Settings tab to tie the licence to one workstation.

### Checking a code

```
pdftodicom-licence-win-x86.exe verify --code-file code.txt
```

This runs exactly the code path the application runs, so what it says is what the customer
will see. Use it before sending a code, and whenever someone reports a code that does not
work.

Run the tool with no arguments for the full list of options.

---

## Notes

- These are rebuilt from source whenever the licensing code changes. The source of truth
  is `src/PdfToDicom.LicenceTool/`.
- They are deliberately excluded from the installer and the portable package. No build
  script ships them.
- They are about 10 MB each because they carry their own copy of the .NET runtime, which
  is what lets them run with nothing installed.

Full detail: [`docs/LicensingWorkflow.md`](../../docs/LicensingWorkflow.md).
