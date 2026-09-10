#!/usr/bin/env bash
#
# Cross platform build script for macOS and Linux.
#
# The .NET SDK cross compiles for Windows from any host, so this script produces the
# shippable Windows executable from a developer's Mac. What it cannot produce is the
# Inno Setup installer, which requires Windows; use the GitHub Actions workflow in
# .github/workflows/build-windows.yml for that, or run build/build.ps1 on a Windows
# machine.
#
# Usage:
#   build/build.sh              build, test and publish
#   build/build.sh test         run the validators and the tests only
#   build/build.sh publish      publish only
#   build/build.sh clean        remove build output
#
# Set RID to choose the target architecture (win-x86, win-x64, win-arm64):
#   RID=win-arm64 build/build.sh publish

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS="$REPO_ROOT/artifacts"
CONFIGURATION="Release"

# Target architecture: win-x86, win-x64 (default) or win-arm64.
# Windows on ARM can run the x64 build under emulation, but a native arm64 build is faster
# and is what a Windows on ARM machine should really run.
#   RID=win-arm64 build/build.sh publish
RUNTIME="${RID:-win-x64}"

cd "$REPO_ROOT"

step() { printf '\n\033[1m==> %s\033[0m\n' "$1"; }
info() { printf '    %s\n' "$1"; }

require_sdk() {
    if ! command -v dotnet >/dev/null 2>&1; then
        cat >&2 <<'MESSAGE'
The .NET SDK was not found on the PATH.

  macOS   : brew install --cask dotnet-sdk
            (or download the .NET 8 SDK installer from Microsoft)
  Linux   : sudo apt-get install dotnet-sdk-8.0
MESSAGE
        exit 1
    fi

    info "using .NET SDK $(dotnet --version)"
}

do_clean() {
    step "Cleaning"
    rm -rf "$ARTIFACTS"
    find "$REPO_ROOT/src" "$REPO_ROOT/tests" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true
    info "build output removed"
}

do_restore() {
    step "Restoring packages"
    dotnet restore "$REPO_ROOT/PDF to DICOM.sln" --nologo
}

do_build() {
    step "Building ($CONFIGURATION)"
    # EnableWindowsTargeting lets the WPF project compile on a non Windows host.
    dotnet build "$REPO_ROOT/PDF to DICOM.sln" -c "$CONFIGURATION" --no-restore --nologo \
        -p:EnableWindowsTargeting=true
}

do_validate() {
    step "Validating XAML bindings and the installer script"

    # Both catch failures that only appear at runtime on Windows, so they are checked
    # here where they cost nothing rather than on a clinical workstation.
    python3 "$REPO_ROOT/build/validate-xaml-bindings.py"
    python3 "$REPO_ROOT/build/validate-installer-script.py"
}

do_test() {
    step "Running the test suite"
    dotnet test "$REPO_ROOT/tests/PdfToDicom.Core.Tests/PdfToDicom.Core.Tests.csproj" \
        -c "$CONFIGURATION" --nologo --verbosity minimal
}

# Checks the assembled folder before anything is zipped or sent.
#
# Two assertions, because neither catches the failure on both kinds of filesystem.
#
# The first compares what the operating system actually lists against what was meant to
# be there, matching case exactly. On Windows a file lost to a case insensitive
# overwrite vanishes from the listing, so its absence is detected. A plain existence
# test would not do: on Windows it answers "yes" for a name that is really the other
# file.
#
# The second looks for two entries differing only in case. That pair cannot coexist on
# Windows but assembles quite happily here, so this is the check that catches the fault
# on a developer machine rather than after a hospital has the folder.
assert_package_is_sound() {
    local package="$1"
    local have_cli="$2"

    local present
    present="$(cd "$package" && find . -type f | sed 's|^\./||' | sort)"

    local required=("PdfToDicom.exe" "LICENCE.txt" "THIRD-PARTY-NOTICES.txt" "READ ME FIRST.txt")
    [[ "$have_cli" == "yes" ]] && required+=("Command line/pdftodicom.exe")

    local missing=()
    local name
    for name in "${required[@]}"; do
        grep -Fqx -- "$name" <<<"$present" || missing+=("$name")
    done

    if (( ${#missing[@]} > 0 )); then
        printf '\n' >&2
        printf 'The package is incomplete. These files were meant to be in it and are not:\n\n' >&2
        printf '    %s\n' "${missing[@]}" >&2
        printf '\nIf a file was copied but is missing under its own name, another file whose\n' >&2
        printf 'name differs only in case has overwritten it. Windows cannot hold both.\n' >&2
        exit 1
    fi

    local collisions
    collisions="$(tr 'A-Z' 'a-z' <<<"$present" | sort | uniq -d)"

    if [[ -n "$collisions" ]]; then
        printf '\nThe package holds names that differ only in case and so cannot both exist\n' >&2
        printf 'on Windows:\n\n%s\n' "$collisions" >&2
        exit 1
    fi
}

# Writes the note a recipient sees first. CRLF and ASCII, because it has to open
# correctly in Notepad on a Windows machine with nothing installed.
write_start_here() {
    local path="$1"
    local version="$2"

    cat > "$path" <<EOF
PDF to DICOM $version
Copyright (c) 2026 Eduardo Dias

Converts a PDF document or an image into a DICOM object and sends it to a
PACS, so a report or a scan from another system becomes part of the patient's
imaging record.


TO RUN IT
---------

Double click PdfToDicom.exe. There is no installer and nothing needs to be
installed first: no .NET, no Java, no Visual C++ runtime, and no administrator
rights.

Windows will warn that the publisher is unrecognised. Choose "More info", then
"Run anyway". The file is digitally signed, but with a certificate Windows does
not recognise by default.


BEFORE THE FIRST REAL DOCUMENT
------------------------------

1. Your PACS administrator must register this workstation on the PACS. The
   details they need are in Documentation, DICOM Conformance Statement.

2. Open Settings and fill in the Institution and Destinations tabs.

3. Use "Test connection" on the destination and confirm it succeeds.

4. Convert the sample in Samples with "Generate only" and check the result
   before sending anything belonging to a patient.


WHAT IT CONVERTS
----------------

  PDF documents           Become DICOM Encapsulated PDF objects.
  JPEG, PNG, TIFF, BMP,   Become DICOM Secondary Capture images. A scan with
  GIF, WebP, TGA          several pages becomes one image per page, kept
                          together as a single item in the archive.


WHAT IS IN THIS FOLDER
----------------------

  PdfToDicom.exe           The application. This is the one to double click.
  LICENCE.txt              The terms. Free to use, commercial and private.
  THIRD-PARTY-NOTICES.txt  Licences of the components used.

  Command line\\
    pdftodicom.exe           For scripted or bulk conversion. Not needed
                             for ordinary use.

  Documentation\\
    User Manual (English)                    For the people using it daily.
    Manual do Utilizador (Portugues)         The same, in Portuguese.
    DICOM Conformance Statement              For your PACS administrator.
    Technical Documentation                  For IT.
    Changelog                                What changed in each version.

  Samples\\
    config.sample.json       Annotated settings reference.
    example-report.pdf       A document to test with.


IMPORTANT
---------

This is NOT a medical device. It has not been assessed under Regulation
(EU) 2017/745 or any equivalent regime, and no clinical decision should be
based on it. It is supplied free of charge, as is, without warranty and
without any promise of support.

The operator is responsible for confirming that the patient details entered
match the document being converted. Filing a document against the wrong
patient is a risk no software can remove.

Read LICENCE.txt before use.
EOF

    # build.ps1 writes this file with no trailing newline. Removing the one the here
    # document adds keeps the two byte identical, which is what assemble() promises.
    printf '%s' "$(cat "$path")" > "$path.tmp" && mv "$path.tmp" "$path"

    # Convert to CRLF in place, without depending on unix2dos being installed.
    perl -pi -e 's/\n/\r\n/' "$path" 2>/dev/null || \
        { sed -i 's/$/\r/' "$path" 2>/dev/null || sed -i '' -e 's/$/\r/' "$path"; }
}

do_publish() {
    step "Publishing the desktop application for $RUNTIME"

    # Self contained and single file: the runtime, WPF and every dependency are embedded,
    # so the result runs on a clean Windows 10 or 11 machine with nothing pre-installed.
    dotnet publish "$REPO_ROOT/src/PdfToDicom.App/PdfToDicom.App.csproj" \
        -c "$CONFIGURATION" \
        -r "$RUNTIME" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:EnableWindowsTargeting=true \
        -o "$ARTIFACTS/$RUNTIME/app" \
        --nologo -v quiet

    step "Publishing the command line tool for $RUNTIME"

    dotnet publish "$REPO_ROOT/src/PdfToDicom.Cli/PdfToDicom.Cli.csproj" \
        -c "$CONFIGURATION" \
        -r "$RUNTIME" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:EnableCompressionInSingleFile=true \
        -o "$ARTIFACTS/$RUNTIME/cli" \
        --nologo -v quiet

    step "Assembling the package to hand over"

    # Mirrors Invoke-Assemble in build.ps1. The two must agree: a package assembled on a
    # Mac and one assembled on Windows should be the same thing.
    local version
    version="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$REPO_ROOT/Directory.Build.props" | head -1)"

    local package="$ARTIFACTS/PdfToDicom-$version-$RUNTIME"
    rm -rf "$package"
    mkdir -p "$package/Documentation" "$package/Samples"

    cp "$ARTIFACTS/$RUNTIME/app/PdfToDicom.exe" "$package/"

    # The command line tool goes into a subfolder rather than beside the application. The
    # two assembly names, PdfToDicom and pdftodicom, differ only in case, and a Windows
    # filesystem cannot hold both in one folder: the second copy overwrites the first and
    # keeps the first one's spelling, leaving a PdfToDicom.exe that is really the command
    # line tool. It is invisible here, where the names are distinct, which is exactly why
    # it was missed.
    local have_cli=no
    if [[ -f "$ARTIFACTS/$RUNTIME/cli/pdftodicom.exe" ]]; then
        mkdir -p "$package/Command line"
        cp "$ARTIFACTS/$RUNTIME/cli/pdftodicom.exe" "$package/Command line/"
        have_cli=yes
    fi

    # Not optional: PdfPig is MIT and Serilog is Apache 2.0, and both require their notice
    # to accompany redistribution.
    cp "$REPO_ROOT/LICENSE"                "$package/LICENCE.txt"
    cp "$REPO_ROOT/THIRD-PARTY-NOTICES.md" "$package/THIRD-PARTY-NOTICES.txt"
    cp "$REPO_ROOT/CHANGELOG.md"           "$package/Documentation/Changelog.md"

    # A named list, not the whole docs folder. Three of those documents are written for
    # the copyright holder, and one of them discusses the customer.
    cp "$REPO_ROOT/docs/UserManual.md" \
       "$package/Documentation/User Manual (English).md"
    cp "$REPO_ROOT/docs/ManualDoUtilizador.pt-PT.md" \
       "$package/Documentation/Manual do Utilizador (Portugues).md"
    cp "$REPO_ROOT/docs/DicomConformanceStatement.md" \
       "$package/Documentation/DICOM Conformance Statement (for your PACS administrator).md"
    cp "$REPO_ROOT/docs/TechnicalDocumentation.md" \
       "$package/Documentation/Technical Documentation (for IT).md"

    cp "$REPO_ROOT/samples/config.sample.json" "$package/Samples/"
    cp "$REPO_ROOT/samples/example-report.pdf" "$package/Samples/"

    write_start_here "$package/READ ME FIRST.txt" "$version"

    assert_package_is_sound "$package" "$have_cli"

    (cd "$ARTIFACTS" && zip -qr "PdfToDicom-$version-$RUNTIME.zip" "PdfToDicom-$version-$RUNTIME")

    # A standalone copy whose name carries the version and the architecture. The file
    # inside the package stays PdfToDicom.exe, because the manuals, the shortcuts and the
    # installer all name it. This copy is for handing one file to somebody.
    cp "$ARTIFACTS/$RUNTIME/app/PdfToDicom.exe" "$ARTIFACTS/PdfToDicom-$version-$RUNTIME.exe"

    info "package: $package"
    info "zip:     $ARTIFACTS/PdfToDicom-$version-$RUNTIME.zip"
    info "exe:     $ARTIFACTS/PdfToDicom-$version-$RUNTIME.exe"
    info "$(find "$package" -type f | wc -l | tr -d ' ') file(s)"

    step "Result"
    ls -lh "$ARTIFACTS/$RUNTIME/app/PdfToDicom.exe" | awk '{print "    desktop application: " $9 " (" $5 ")"}'

    cat <<'MESSAGE'

    The installer is not produced here because Inno Setup requires Windows.
    To build it, either:
      - push the branch and let .github/workflows/build-windows.yml build it, or
      - run build\build.ps1 on a Windows machine with Inno Setup 6 installed.
MESSAGE
}

require_sdk

case "${1:-all}" in
    clean)   do_clean ;;
    test)    do_restore; do_build; do_validate; do_test ;;
    publish) do_restore; do_publish ;;
    all)     do_restore; do_build; do_validate; do_test; do_publish ;;
    *)
        echo "Usage: $0 [all|test|publish|clean]" >&2
        exit 1
        ;;
esac

step "Done"
