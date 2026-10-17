# Building It Yourself

**Audience: the copyright holder, assuming no programming background.**

You do not need to be able to program to build this. You need one free installer and one
double click.

---

## The short version

1. Install the **.NET 8 SDK**, or any later one, from
   [dotnet.microsoft.com/download/dotnet/8.0](https://dotnet.microsoft.com/download/dotnet/8.0).
   Choose the **SDK**, not the Runtime, matching your computer's architecture. It is an
   ordinary Windows installer; accept the defaults.
2. Double click **`BUILD.bat`** in the repository folder.

That is the whole procedure. The script checks what is installed, runs the tests, builds
the application for your computer's architecture, and tells you where the finished file
is. If anything is missing it says what to install and stops rather than failing
confusingly.

The result is a single self contained `PdfToDicom.exe` that runs on any Windows machine of
that architecture with nothing installed.

---

## Do I need an IDE?

**No, and I would not start with one.** An IDE is a large, complicated program aimed at
people who write code all day. To produce a build, it adds a great deal of surface area
and nothing you need.

If you want one anyway, here they are, honestly compared.

### Visual Studio 2022 Community — free, and the one to pick if you pick any

The full Microsoft development environment. Free for individuals and small companies.

- **Download:** [visualstudio.microsoft.com](https://visualstudio.microsoft.com/vs/community/)
- **During setup**, tick the **".NET desktop development"** workload. That single tick
  installs everything this project needs, the SDK included, so you can skip step 1 above.
- **To build:** open `PDF to DICOM.sln`, then **Build → Build Solution**.
- **To run and watch it work:** press **F5**.
- **Size:** roughly 8 to 10 GB installed.
- **On Windows on ARM:** version 17.10 and later run natively on ARM64. Older versions run
  under emulation and are slow.

Its real advantage is not building, which `BUILD.bat` does perfectly well. It is that you
can put a breakpoint on a line, run the application, and watch it stop there and show you
every value. That is genuinely useful the day something behaves oddly and nobody can see
why.

### Visual Studio Code — free, small, but not simpler

You already have this installed. It is a text editor with extensions rather than a full
development environment.

- Add the **C# Dev Kit** extension.
- **Size:** a few hundred megabytes.
- **Honest assessment:** lighter, but *more* fiddly for this purpose, not less. The build
  and debug configuration is exposed rather than hidden. For editing a documentation file
  or reading code it is ideal. For building, `BUILD.bat` is simpler.

### JetBrains Rider — paid, excellent, unnecessary here

Around 150 EUR a year for personal use, free for open source. Better than Visual Studio in
several respects, but nothing in this project needs it.

---

## Recommendation

| What you want to do | Use |
|---|---|
| Produce an executable to hand out | `BUILD.bat` |
| Read the code or edit documentation | VS Code, which you already have |
| Understand why something misbehaves, step by step | Visual Studio 2022 Community |
| Build all three architectures at once | Push to GitHub; the automated build does it |

**The last row is worth noting.** Every push builds all three architectures *and* the
installers, on Microsoft's machines, and attaches the results to the run. You never have to
build an architecture you do not own. The installers in particular can only be built on
Windows, so for anything other than your own machine that path is easier than doing it
locally.

---

## What `BUILD.bat` actually does

Nothing hidden, and nothing that touches your system outside this folder:

1. Checks that PowerShell and the .NET SDK are present, and reports the SDK version.
2. Detects whether this computer is x64, ARM64 or 32 bit, and targets that.
3. Reports whether Python and Inno Setup are present. **Neither is required**; without
   them the XAML checks and the installer are skipped, and it says so up front rather
   than leaving you to notice at the end.

   Python is checked by asking it for its version, not by looking for the name. Windows
   ships stub files that answer to `python` and `python3` but only advertise the Microsoft
   Store, and anything that merely checks the name is fooled by them.
4. Runs `build\build.ps1`, which restores, builds, validates, tests, publishes **both 64
   bit and ARM**, assembles a handover package for each and builds the installer.
5. **Refuses to continue if any of the 265 tests fails.** A build that fails its tests is
   not one to hand to a hospital.
6. Signs what it built, if a code signing certificate is installed for your user, and
   tells you how to create one if not. It is not asked about, because signing when it is
   possible is what you want every time.
7. Offers to open the finished folder.

The build itself lives in `build.ps1` rather than being duplicated in the batch file, so
the two cannot drift apart. `BUILD.bat` is the front door: prerequisites, plain language
and guidance when something is wrong.

It does not modify the source, install anything system wide, or need administrator rights.

---

## What you get

This appears under `artifacts\`:

```
artifacts\
  PdfToDicom-3.5.0-win-x64\        <- the folder to hand over to a site
  PdfToDicom-3.5.0-win-x64.exe     <- just the application, version in the name
  PdfToDicom-3.5.0-win-arm64\      <- the same, for Windows on ARM
  PdfToDicom-3.5.0-win-arm64.exe
```

**Both architectures, every time.** The machines in service are 64 bit, and the ARM build
exists because the testing is done on a Windows on ARM virtual machine, where a native
build avoids the emulation layer. One build, both answers.

**The command line tool is not built unless you ask.** It is a second self contained
publish, as large and as slow as the application's, and a build you are about to run on a
test machine has no use for it. The handover folder then has no `Command line` folder in
it, and the build says so as it goes. Add `-Cli` when the package is for a site:

```powershell
.\build\build.ps1 -Cli
```

**No zip is made.** The folder can be copied to the machine, or onto a stick, exactly as it
stands, so an archive is only of use for sending one file by email. Add `-Zip` if you want
one:

```powershell
.\build\build.ps1 -Zip
```

The folder contains everything a site needs and nothing it should not have:

```
  READ ME FIRST.txt          What to do, in plain language.
  PdfToDicom.exe             The application.
  LICENCE.txt                The terms.
  THIRD-PARTY-NOTICES.txt    Licences of the components used.

  Command line\
    pdftodicom.exe           Command line version.

  Documentation\
    User Manual (English)
    Manual do Utilizador (Portugues)
    DICOM Conformance Statement (for your PACS administrator)
    Technical Documentation (for IT)
    Worklist Setup (for IT)
    Changelog

  Samples\
    config.sample.json
    example-report.pdf
```

Three points about that list are deliberate.

**The command line tool is in a subfolder, not beside the application.** The two
executables are called `PdfToDicom.exe` and `pdftodicom.exe`, which differ only in case,
and a Windows filesystem cannot hold both in one folder. Copying them together does not
fail: the second silently overwrites the first and keeps the first one's spelling, so
what is left is a `PdfToDicom.exe` that is really the command line tool and opens a
console when double clicked. The build now refuses to produce a package in which any two
names differ only in case.

**The third party notices are not optional.** PdfPig is MIT licensed and Serilog is Apache
2.0, and both require their notice to accompany any redistribution. Leaving the file out
would put you in breach of them.

**Three documents are excluded on purpose**: the ones on building it yourself, code signing
and protecting against unauthorised distribution. They are written for you, not for a
customer, and the last one discusses the customer. Both the package and the installer copy
a named list rather than the whole `docs` folder, so a new document has to be considered
rather than shipped by accident.

The installer did not always do this. It used a wildcard, and so put all three of those
documents into every hospital's application folder. That is fixed, and the build now
fails outright if any of the three, or the test worklist server, turns up in an assembled
package.

---

## Reclaiming the disk afterwards

A default build, which is 64 bit and ARM, occupies about 216 MB. The same files under
their several names would be 427 MB; they are hard linked where the filesystem allows it,
so the executable exists once however many folders name it. Nothing is committed, so it
is all reclaimable: `prune` brings 216 MB down to 143 MB, and `clean` to nothing. Measured
on a build with no command line tool and no installer.

```powershell
.\build\build.ps1 -Task prune     # keep the deliverables, drop the working copies
.\build\build.ps1 -Task clean     # remove everything the build produced
```

`prune` keeps what is worth keeping, the standalone executable, the installer and any zip
you asked for, and removes the working copies: the publish staging folders, and the
unpacked handover folder when there is a zip of it to fall back on. A handover folder with
no zip beside it is kept and `prune` says so, because it would otherwise be the only copy
and it is the thing you build for. `clean` removes the lot, including the compiler's own
intermediate output.

Run `prune` after you have taken the files you wanted. Do not run it between
`-Task publish` and `-Task package`, because the installer is built from the staging
folder that `prune` removes.

---

## Setting the protected settings password

The **Worklist** and **Destinations** tabs in Settings are both locked behind one password
compiled into the executable. They are the settings that decide where patient data comes
from and where it goes, and the same person is trusted with both; unlocking either page
opens the other.

**The password currently set is recorded in the clear** in the comment on the `Verifier`
constant in `src\PdfToDicom.Core\Security\ProtectedSettingsLock.cs`, so that it cannot be
forgotten. That comment, and a test that asserts the same password, are the only records of
it in the repository, and they have to agree: the test fails until they do. The reasoning,
and what to do if this repository is ever shared or made public, are recorded beside the
constant rather than repeated here.

To change it, run from the repository:

```powershell
dotnet run --project src\PdfToDicom.Cli -- settings-password
```

It asks for the password twice without showing what you type, and prints one line:

```
    private const string Verifier = "PBKDF2-SHA256$210000$....$....";
```

Paste that line over the `Verifier` constant in
`src\PdfToDicom.Core\Security\ProtectedSettingsLock.cs`, **update the comment above it to
name the new password**, and **update the password in
`tests\PdfToDicom.Core.Tests\WorklistTests.cs`** so the test that pins it agrees. Then
rebuild. That is the whole procedure, and the test is what stops the first two steps being
done without the third.

**What is stored is not the password.** It is a PBKDF2-HMAC-SHA256 hash with a random
salt and 210,000 iterations. It can answer whether a password offered to it is correct,
and it cannot be turned back into the password, which is why a hash is used rather than
something encrypted: anything encrypted inside the binary would have to carry its key
inside the binary too.

There is **no interface to reset it**, by design. A password you no longer have means
generating a new verifier and rebuilding, which is why it is written beside the constant.

**To remove the lock**, set the constant to an empty string. The Worklist tab is then open
to anyone, and the dialog says so plainly rather than implying a protection that is not
there.

Two things still worth keeping to. **Do not put the password in a commit message, an
issue or an email**: the comment in the source is one controlled place, a commit history
is not. And **if this repository is ever shared or made public, change the password and
remove that comment**, because at that point it is published, and a published password is
no password.

---

## Testing the worklist without a real server

A site adopting the worklist usually has no server to point a test workstation at: the
real one holds live patient data and is not somewhere to try new software against. The
repository carries one.

```powershell
dotnet run --project tools\PdfToDicom.TestWorklistServer -- --port 1070 --aet TESTWL
```

It serves **seventeen** scheduled procedure steps by default, all for today: seven fixed
ones and ten drawn at random from a pool of five hundred Portuguese names. Point the
application at `TESTWL@127.0.0.1:1070` and press Search.

```powershell
dotnet run --project tools\PdfToDicom.TestWorklistServer -- --random 0     # the fixed seven only
dotnet run --project tools\PdfToDicom.TestWorklistServer -- --random 40    # a longer list
dotnet run --project tools\PdfToDicom.TestWorklistServer -- --seed 7       # repeat a draw exactly
```

**The seven fixed ones are always there, and are deliberately awkward:** accented names,
one patient with no given name, a different modality, a 73 character name whose surname
fits, and a 75 character name whose surname alone is over the 64 characters DICOM allows.
They exist to exercise the shortening rule, not to look tidy, and they are kept out of the
random pool on purpose. The pool is realistic but uniformly well behaved, with the longest
name composing to 31 characters, so a draw from it would exercise the shortening rule
roughly never.

**Everything about a pool entry is derived from its position**, so a given name always
carries the same Patient ID, accession number, birth date and scheduled time. Only which
ten are drawn changes. That is why `--seed` can reproduce a run exactly, and why
converting twice for the same patient files into the same study.

### Nothing it serves can be mistaken for a real patient

Every identifier and description carries **`TESTE`**, so a study generated from this list
is recognisable wherever it ends up:

| Attribute | Value |
|---|---|
| Patient ID | `TESTE0000001`, `TESTE1000259` … |
| Accession Number | `TESTEACC000001`, `TESTEACC1000259` … |
| Study and Procedure Description | `[TESTE] Relatório de Cardiologia` |
| Referring and Performing Physician | `TESTE^FICTÍCIO` |
| Study Instance UID | under `…9.6008.99.`, a branch used for nothing else |

The mark leads rather than trails, because a study description is routinely shown
truncated and a marker at the end is the part that gets cut off.

**The patient names are deliberately left realistic.** They are the one field whose
realism is the thing under test: the shortening rule turns on their exact length, and
prefixing them would both break that and make the accented characters harder to read.

None of which is a reason to be casual: **point the application at a test archive.** The
marking means a mistake is recoverable and obvious, not that it is harmless.

To check the whole path from the command line instead:

```powershell
dotnet run --project src\PdfToDicom.Cli -- worklist
```

**This server is never packaged.** It is not on the list of files the build copies, and
since this release the build fails if it ever appears in an assembled package.

---

## If something goes wrong

**"Python was not found; run without arguments to install from the Microsoft Store"** —
Windows ships stub files called `python.exe` and `python3.exe` that exist only to advertise
the Store. They sit on the PATH and look like Python to anything that merely checks whether
the name resolves.

**This is not a problem and does not stop the build.** Python is optional: it runs the XAML
binding check, which CI runs on every push anyway. The build skips it and carries on.

The same is true of `build/build.sh` on a Mac, where `/usr/bin/python3` exists but refuses
to run until the Command Line Tools are installed. Both scripts test whether Python
actually executes rather than whether the name resolves, because on both platforms the
name resolves to something that does not work.

If you want the check locally, install Python from
[python.org/downloads](https://www.python.org/downloads/) and tick **"Add python.exe to
PATH"** during setup. To silence the stubs instead, go to **Settings → Apps → Advanced app
settings → App execution aliases** and turn off the two Python entries.

**"The .NET SDK was not found"** — step 1 was skipped, or the installer needs a restart of
the command window. Close everything and try again.

**The first build fails with network errors** — the libraries are downloaded on first use.
Check the connection and run it again; the second attempt reuses whatever downloaded.

**The tests fail** — this means the code is wrong, not your computer. Nothing is produced.
Report what it said.

**It builds, but the application will not start on another machine** — check the
architecture matches. A `win-arm64` build will not run on an ordinary 64 bit PC, and the
build produces both, so make sure the file came from the `win-x64` folder.

---

## Building one architecture, or a third one

Both 64 bit and ARM are built by default. To build just one, or to build the 32 bit one
that is otherwise left out:

```powershell
.\build\build.ps1 -Runtimes win-x64
.\build\build.ps1 -Runtimes win-x86
.\build\build.ps1 -Runtimes win-x64,win-arm64,win-x86
```

Right click `build.ps1` and choose **Run with PowerShell**, or open PowerShell in the
folder and paste one of those lines. Add **Inno Setup 6** and the same script also builds
the installer.

`-Runtime` is still accepted for one architecture, so an older note or script keeps
working.

## The quickest trip to a running executable

For the loop of change something, build it, run it on the test machine. No tests, no
handover folder, no installer:

```powershell
.\build\build.ps1 -Task app
.\build\build.ps1 -Task app -Runtimes win-arm64
```

It leaves `artifacts\win-arm64\app\PdfToDicom.exe`, signed if it can be, and nothing
else. Run the tests separately when you want them:

```powershell
.\build\build.ps1 -Task test
```

---

## On a Mac or Linux

```bash
build/build.sh              # test, build both architectures, assemble both packages
build/build.sh app          # the executables only, as fast as it gets
build/build.sh ship         # both, in one folder to hand over
build/build.sh test         # the validators and the tests only
```

Both 64 bit and ARM, in every one of those. For one only, or for the 32 bit one that is
otherwise left out:

```bash
RID=win-arm64 build/build.sh app
RIDS="win-x64 win-x86" build/build.sh publish
```

The command line tool is left out unless asked for, because it is a second self contained
publish of its own:

```bash
CLI=1 build/build.sh
```

`ship` runs the tests and the validators first, and leaves one folder holding both
architectures:

```
artifacts/ship/
  WHICH FOLDER DO I USE.txt     Which one for which machine, and how to tell
  win-x64/                      Complete, ready to copy
  win-arm64/                    Complete, ready to copy
```

Copy the whole folder, or just the one that matches the machine. Each is a full package:
the application, the command line tool, the licence, the notices, the manuals and the
samples. Nothing needs installing on the target machine.

32 bit is deliberately not among them. Every Windows 10 or 11 machine in service runs x64
or ARM64, and a third build is another 80 MB and another thing to pick the wrong one of.

The installer is still Windows only, because Inno Setup is.

### Where to put the certificate

**One folder, both machines**, and the same file names:

| Machine | Folder |
|---|---|
| Mac or Linux | `~/.pdftodicom/` |
| Windows | `%USERPROFILE%\.pdftodicom\`, usually `C:\Users\<you>\.pdftodicom\` |

The names looked for, in order: `codesign.pfx`, `codesign.p12`, `certificate.pfx`,
`certificate.p12`. The password goes in `codesign.password` beside it, or you are asked
for it. **The name matters**: a file called anything else is not found, which is the usual
reason "I put it in the folder and nothing happened".

**Ask the build what it sees.** It builds nothing and takes a second:

```powershell
.\build\build.ps1 -Task signing
```

```bash
build/build.sh signing
```

It names every path it looked at, lists what is actually in the folder when the expected
name is not there, and on Windows shows what the certificate store holds as well.

On Windows the certificate store is still used when there is no file: a certificate you
created with `-CreateSelfSigned`, or one your IT department installed, is found without
any file at all. A file you put in the folder deliberately takes precedence over it, and
`-Thumbprint` insists on a particular store certificate.

### Signing from a Mac or Linux

Leave the certificate at `~/.pdftodicom/codesign.pfx` and **every build signs itself**,
with no option to remember:

```bash
mkdir -p ~/.pdftodicom
cp /somewhere/codesign.pfx ~/.pdftodicom/codesign.pfx
chmod 600 ~/.pdftodicom/codesign.pfx

# and, if you would rather not be asked for the password each time
printf '%s' 'the password' > ~/.pdftodicom/codesign.password
chmod 600 ~/.pdftodicom/codesign.password
```

A certificate somewhere else, for one build:

```bash
SIGN_PFX=~/keys/codesign.pfx build/build.sh ship
```

Either way it signs every architecture it built and timestamps against the same servers
`sign.ps1` uses, asking for the password once if there is no password file. It needs
`osslsigncode`:

```bash
brew install osslsigncode          # macOS
sudo apt-get install osslsigncode  # Linux
```

**This is not the same as signing on Windows, and the difference matters.** `sign.ps1`
takes the certificate from the Windows certificate store, where the private key can be
marked non exportable and never exists as a file. There is no equivalent here, so this
needs the key **as a file on the disk**. Keep it off shared drives, on an encrypted disk,
and out of the repository: `.gitignore` already covers `*.pfx`, and that is a safety net
rather than a plan.

If the key was created exportable, you can get a `.pfx` out of the Windows machine that
already signs:

```powershell
$c = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Select-Object -First 1
Export-PfxCertificate -Cert $c -FilePath codesign.pfx -Password (Read-Host -AsSecureString)
```

If it was not exportable, signing stays on the Windows machine, which is the safer place
for it anyway. Nothing is ever created or searched for beyond the one location: a machine
without that file builds unsigned, says so in a few lines, and carries on, because an
unsigned build is complete and runs.
