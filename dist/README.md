# Prebuilt executable

`PdfToDicom-win-arm64.exe` is a ready to run build for **Windows on ARM64**.

It is self contained: the .NET runtime, WPF and every dependency are embedded, so it needs
no .NET, no Java and no Visual C++ redistributable. Download it, double click it, and it
runs.

## Running it

1. Download `PdfToDicom-win-arm64.exe`.
2. Double click it.
3. Windows SmartScreen will warn that the publisher is unrecognised. This is expected: the
   file is not code signed yet. Choose **More info**, then **Run anyway**.

## Testing without a PACS

The default configuration points at `127.0.0.1:104`, so **Generate and send** will fail
until a real destination is configured in Settings.

To exercise the conversion on its own, use **Generate only**. It produces a DICOM file in
`%APPDATA%\PdfToDicom\Output` without opening any network connection. `samples/example-report.pdf`
in this repository is a suitable test document.

## Other architectures

Only the ARM64 build is committed here, to limit the size of the repository. Builds for
**x64** and **x86**, the matching command line tool, and the per user installers for all
three architectures are produced on every push and are attached to the
[GitHub Actions run](../../actions).

## A note on keeping this file here

This binary is committed for convenience during development. Once the application is being
sold or deployed, **remove it and distribute through a channel you control**, as described
in [docs/ProtectingAgainstUnauthorisedDistribution.md](../docs/ProtectingAgainstUnauthorisedDistribution.md).
A public repository containing a ready to run executable is the easiest possible route to
unauthorised distribution.
