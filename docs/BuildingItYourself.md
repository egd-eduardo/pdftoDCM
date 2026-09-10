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

1. Checks the .NET SDK is installed and reports its version.
2. Detects whether this computer is x64, ARM64 or 32 bit, and targets that.
3. Downloads the libraries the project uses, once, into your user profile.
4. **Runs all 107 tests and refuses to build if any fails.** A build that fails its tests is
   not one you want to hand to a hospital.
5. Publishes the self contained executable into `artifacts\<architecture>\app\`.

It does not modify the source, install anything system wide, or need administrator rights.

---

## If something goes wrong

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
