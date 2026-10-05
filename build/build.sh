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
#   build/build.sh prune        keep the deliverables, drop the intermediates
#   build/build.sh clean        remove build output
#
# Set RID to choose the target architecture (win-x86, win-x64, win-arm64):
#   RID=win-arm64 build/build.sh publish

# Arrays, [[ ]] and here strings are used throughout, so this needs bash and not the
# system /bin/sh. Running it with "sh build/build.sh" fails with a syntax error whose
# message says nothing about the cause, which is worth one line to prevent.
if [ -z "${BASH_VERSION:-}" ]; then
    echo "This script needs bash. Run it as 'bash build/build.sh' or './build/build.sh'." >&2
    exit 1
fi

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

# Size of a directory, in a form a person reads.
#
# "du -sh" is everywhere. "du -shl", which counts a hard linked file once per name and so
# reports what the build would occupy without the linking, is a GNU and BSD extension with
# the same meaning on both; it is still probed rather than assumed, because a figure that
# is only of interest is no reason for a build to fail anywhere.
human_size() {
    du -sh "$1" 2>/dev/null | cut -f1 || true
}

human_size_counting_links() {
    if du -shl "$1" >/dev/null 2>&1; then
        du -shl "$1" 2>/dev/null | cut -f1 || true
    else
        human_size "$1"
    fi
}

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

# Places a second name on an existing file rather than a second copy of it.
#
# The published executable is around 70 MB and ends up under three names: the publish
# staging directory, the handover folder, and the versioned standalone copy. Three real
# copies per architecture is a quarter of a gigabyte of the same bytes, which is what made
# a full three architecture build occupy 1.2 GB.
#
# A hard link is safe here because nothing rewrites these files in place afterwards.
# Signing runs BEFORE the package is assembled, deliberately, so that the copies handed
# out are the signed ones; and a signature applied through one name would in any case be
# correct under all of them, since every name is meant to hold the same executable.
#
# Falls back to a copy when linking is refused, which happens across filesystems and on
# filesystems without hard link support. The build must not fail over an optimisation.
link_or_copy() {
    local source=$1
    local target=$2

    rm -f "$target"

    ln "$source" "$target" 2>/dev/null || cp "$source" "$target"
}

# Removes what can be produced again and keeps what cannot be produced quickly.
#
# Kept: the zip, the versioned standalone executable and the installer, which are the
# things somebody actually asked for. Removed: the publish staging directories and the
# unpacked handover folder, whose entire content is inside the zip.
#
# Deliberately a task of its own rather than part of "publish" or "all". On Windows the
# installer is built by a separate "package" run that reads the staging directory, and the
# CI step that confirms the executable is self contained reads it too, so pruning
# automatically would break both.
do_prune() {
    step "Pruning the intermediate build output"

    if [[ ! -d "$ARTIFACTS" ]]; then
        info "nothing to prune"
        return
    fi

    local before
    before=$(human_size "$ARTIFACTS")

    local path
    for path in "$ARTIFACTS"/win-x86 "$ARTIFACTS"/win-x64 "$ARTIFACTS"/win-arm64; do
        if [[ -d "$path" ]]; then
            rm -rf "$path"
            info "removed $(basename "$path")/ (publish staging)"
        fi
    done

    # Only a folder whose zip sits beside it. Deleting the one unpacked copy of a package
    # that was never archived would lose it.
    for path in "$ARTIFACTS"/PdfToDicom-*-win-*; do
        if [[ -d "$path" ]]; then
            if [[ -f "$path.zip" ]]; then
                rm -rf "$path"
                info "removed $(basename "$path")/ (its zip holds the same files)"
            else
                info "kept $(basename "$path")/ because it has no zip beside it"
            fi
        fi
    done

    info "artifacts: $before -> $(human_size "$ARTIFACTS")"
    info "$(find "$ARTIFACTS" -maxdepth 1 -type f \( -name '*.zip' -o -name '*.exe' \) | wc -l | tr -d ' ') deliverable file(s) kept"
}

do_clean() {
    step "Cleaning"
    rm -rf "$ARTIFACTS"
    find "$REPO_ROOT/src" "$REPO_ROOT/tests" "$REPO_ROOT/tools" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true
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

# The interpreter that actually runs the two validators, or nothing.
#
# "command -v python3" is not enough on either platform this script runs on. A macOS
# without the Command Line Tools installed has a /usr/bin/python3 that exists, resolves,
# and then refuses to run, putting up a dialog instead; Windows has equivalent stubs that
# only advertise the Store. Both answer "yes" to the name and "no" to running anything,
# so what is tested is whether it executes.
working_python() {
    local candidate
    for candidate in python3 python; do
        if command -v "$candidate" >/dev/null 2>&1 \
            && "$candidate" -c 'import sys' >/dev/null 2>&1; then
            printf '%s' "$candidate"
            return 0
        fi
    done

    return 1
}

do_validate() {
    step "Validating XAML bindings and the installer script"

    # Both catch failures that only appear at runtime on Windows, so they are checked
    # here where they cost nothing rather than on a clinical workstation.
    local python
    if ! python="$(working_python)"; then
        cat >&2 <<'MESSAGE'
    Python is not available, so the XAML bindings and the installer script were
    not checked. This is optional and the build continues without it.

    The checks exist to catch a binding that compiles but fails when a window
    opens, and an installer script mistake that only Inno Setup would report. CI
    runs both on every push, so nothing is lost as long as the branch is pushed.

    To run them here:
      macOS   : brew install python
                (or install the Command Line Tools: xcode-select --install)
      Linux   : sudo apt-get install python3
MESSAGE
        return
    fi

    "$python" "$REPO_ROOT/build/validate-xaml-bindings.py"
    "$python" "$REPO_ROOT/build/validate-installer-script.py"
}

do_test() {
    step "Running the test suite"

    # Says which runtime the tests are about to run on, when it is not the one they
    # target.
    #
    # Building and running are separate problems. Any recent SDK builds these projects,
    # because an SDK carries its own reference assemblies and global.json rolls forward.
    # Running a net8.0 application needs the .NET 8 runtime, and the test host is itself
    # a net8.0 application, so a machine with only the current SDK built everything and
    # then could not start a single test. RollForward in Directory.Build.props now lets
    # that machine run them on the runtime it has, which is better than refusing, but it
    # is not the runtime the product targets and so is worth one line rather than
    # happening quietly.
    local target
    target="$(sed -n 's|.*<TargetFramework>net\([0-9]*\)\.[0-9]*</TargetFramework>.*|\1|p' \
        "$REPO_ROOT/tests/PdfToDicom.Core.Tests/PdfToDicom.Core.Tests.csproj")"
    target="${target%%$'\n'*}"

    local runtimes
    runtimes="$(dotnet --list-runtimes 2>/dev/null || true)"

    if [[ -n "$target" && "$runtimes" != *"Microsoft.NETCore.App $target."* ]]; then
        info "no .NET $target runtime is installed, so the tests will run on a later one"
        info "install it for a like for like run: https://dotnet.microsoft.com/download/dotnet/$target.0"
    fi

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

    # Nothing written for the copyright holder may reach a hospital. These three discuss
    # signing keys, how to rebuild the product, and the customer. An earlier version
    # copied docs/* wholesale and shipped all three; naming them here means the next
    # document added to that folder has to be considered rather than inherited.
    local forbidden=("BuildingItYourself" "CodeSigning" "ProtectingAgainstUnauthorisedDistribution"
                     "testworklistserver")
    local leaked=()
    local secret
    for secret in "${forbidden[@]}"; do
        # An explicit if rather than "grep ... && leaked+=(...)". The one line form relies
        # on how a particular bash treats a failing AND list under set -e, and macOS still
        # ships bash 3.2, so it is not worth depending on.
        if grep -Fq -- "$secret" <<<"$present"; then
            leaked+=("$secret")
        fi
    done

    if (( ${#leaked[@]} > 0 )); then
        printf '\nThe package contains material that must never be handed to a site:\n\n' >&2
        printf '    %s\n' "${leaked[@]}" >&2
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
    Worklist Setup                           For IT, if your department has
                                             a modality worklist.
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
        -p:IncludeNativeLibrariesForSelfExtract=true \
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

    link_or_copy "$ARTIFACTS/$RUNTIME/app/PdfToDicom.exe" "$package/PdfToDicom.exe"

    # The command line tool goes into a subfolder rather than beside the application. The
    # two assembly names, PdfToDicom and pdftodicom, differ only in case, and a Windows
    # filesystem cannot hold both in one folder: the second copy overwrites the first and
    # keeps the first one's spelling, leaving a PdfToDicom.exe that is really the command
    # line tool. It is invisible here, where the names are distinct, which is exactly why
    # it was missed.
    local have_cli=no
    if [[ -f "$ARTIFACTS/$RUNTIME/cli/pdftodicom.exe" ]]; then
        mkdir -p "$package/Command line"
        link_or_copy "$ARTIFACTS/$RUNTIME/cli/pdftodicom.exe" "$package/Command line/pdftodicom.exe"
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
    cp "$REPO_ROOT/docs/WorklistSetup.md" \
       "$package/Documentation/Worklist Setup (for IT).md"

    cp "$REPO_ROOT/samples/config.sample.json" "$package/Samples/"
    cp "$REPO_ROOT/samples/example-report.pdf" "$package/Samples/"

    write_start_here "$package/READ ME FIRST.txt" "$version"

    assert_package_is_sound "$package" "$have_cli"

    (cd "$ARTIFACTS" && zip -qr "PdfToDicom-$version-$RUNTIME.zip" "PdfToDicom-$version-$RUNTIME")

    # A standalone copy whose name carries the version and the architecture. The file
    # inside the package stays PdfToDicom.exe, because the manuals, the shortcuts and the
    # installer all name it. This copy is for handing one file to somebody.
    link_or_copy "$ARTIFACTS/$RUNTIME/app/PdfToDicom.exe" "$ARTIFACTS/PdfToDicom-$version-$RUNTIME.exe"

    info "package: $package"
    info "zip:     $ARTIFACTS/PdfToDicom-$version-$RUNTIME.zip"
    info "exe:     $ARTIFACTS/PdfToDicom-$version-$RUNTIME.exe"
    info "$(find "$package" -type f | wc -l | tr -d ' ') file(s)"
    info "artifacts: $(human_size "$ARTIFACTS") on disk, $(human_size_counting_links "$ARTIFACTS") if every link were a copy"
    info "'build/build.sh prune' drops the staging and the unpacked folder"

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
    prune)   do_prune ;;
    test)    do_restore; do_build; do_validate; do_test ;;
    publish) do_restore; do_publish ;;
    all)     do_restore; do_build; do_validate; do_test; do_publish ;;
    *)
        echo "Usage: $0 [all|test|publish|prune|clean]" >&2
        exit 1
        ;;
esac

step "Done"
