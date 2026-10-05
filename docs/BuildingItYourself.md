# Building It Yourself

**Audience: the copyright holder, assuming no programming background.**

You do not need to be able to program to build this. You need one free installer and one
double click.

---

## The short version

1. Install the **.NET 8 SDK** from
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
4. Runs `build\build.ps1`, which restores, builds, validates, tests, publishes, assembles
   the handover package and builds the installer.
5. **Refuses to continue if any of the 173 tests fails.** A build that fails its tests is
   not one to hand to a hospital.
6. Offers to sign the files if a code signing certificate exists, and tells you how to
   create one if not.
7. Offers to open the finished folder.

The build itself lives in `build.ps1` rather than being duplicated in the batch file, so
the two cannot drift apart. `BUILD.bat` is the front door: prerequisites, plain language
and guidance when something is wrong.

It does not modify the source, install anything system wide, or need administrator rights.

---

## What you get

Two things appear under `artifacts\`:

```
artifacts\
  PdfToDicom-3.0.0-win-x64\        <- the folder to hand over
  PdfToDicom-3.0.0-win-x64.zip     <- the same, ready to email
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

A build for one architecture occupies about 200 MB, and all three about 640 MB. Nothing
is committed, so it is all reclaimable: `prune` brings that 640 MB down to 513 MB, and
`clean` to nothing.

```powershell
.\build\build.ps1 -Task prune     # keep the zip, the exe and the installer
.\build\build.ps1 -Task clean     # remove everything the build produced
```

`prune` keeps the three things worth keeping, the zip, the standalone executable and the
installer, and removes the working copies: the publish staging folders and the unpacked
handover folder, whose entire content is already inside the zip. `clean` removes the lot,
including the compiler's own intermediate output.

Run `prune` after you have taken the files you wanted. Do not run it between
`-Task publish` and `-Task package`, because the installer is built from the staging
folder that `prune` removes.

---

## Setting the worklist settings password

The Worklist tab in Settings can be locked behind a password that is compiled into the
executable. **Nobody but you ever needs to know it**, and nothing you do here writes it
down.

Run, from the repository:

```powershell
dotnet run --project src\PdfToDicom.Cli -- worklist-password
```

It asks for the password twice without showing what you type, and prints one line:

```
    private const string Verifier = "PBKDF2-SHA256$210000$....$....";
```

Paste that line into `src\PdfToDicom.Core\Security\WorklistSettingsLock.cs`, replacing
the empty `Verifier` constant, and rebuild. That is the whole procedure.

**What is stored is not the password.** It is a PBKDF2-HMAC-SHA256 hash with a random
salt and 210,000 iterations. It can answer whether a password offered to it is correct,
and it cannot be turned back into the password, which is why a hash is used rather than
something encrypted: anything encrypted inside the binary would have to carry its key
inside the binary too.

**Write the password down somewhere safe**, in a password manager rather than in this
repository. It cannot be recovered from the line above, and there is no interface to
reset it: a forgotten password means generating a new verifier and rebuilding.

**Never type the password into a chat window, an issue, a commit message or an email.**
The tool exists so it never has to leave your own machine.

Leaving the constant empty is allowed and means the Worklist tab is open to anyone. The
application says so in the interface rather than implying a protection that is not there.

---

## Testing the worklist without a real server

A site adopting the worklist usually has no server to point a test workstation at: the
real one holds live patient data and is not somewhere to try new software against. The
repository carries one.

```powershell
dotnet run --project tools\PdfToDicom.TestWorklistServer -- --port 1070 --aet TESTWL
```

It serves seven invented scheduled procedure steps and nothing else. Point the
application at `TESTWL@127.0.0.1:1070` with the date range set to **Any date**, since the
fixtures are scheduled for today but a range of today is the stricter test to make
second.

The fixtures are deliberately awkward: accented names, one patient with no given name, a
73 character name whose surname fits, and a 75 character name whose surname alone is over
the 64 characters DICOM allows. They exist to exercise the shortening rule, not to look
tidy.

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
architecture matches. A `win-arm64` build will not run on an ordinary 64 bit PC. Build with
`build\build.ps1 -Runtime win-x64` for that, or take the file from the automated build.

---

## Building a different architecture than your own

`BUILD.bat` builds for the machine it runs on. For the others:

```powershell
.\build\build.ps1 -Runtime win-x64
.\build\build.ps1 -Runtime win-x86
.\build\build.ps1 -Runtime win-arm64
```

Right click `build.ps1` and choose **Run with PowerShell**, or open PowerShell in the
folder and paste one of those lines. Add **Inno Setup 6** and the same script also builds
the installer.
