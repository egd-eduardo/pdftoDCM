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
#   build/build.sh              build, test, publish and assemble the handover folders
#   build/build.sh app          the executables only: no tests, no package, no installer
#   build/build.sh test         run the validators and the tests only
#   build/build.sh publish      publish and assemble the handover folders
#   build/build.sh ship         both architectures in one folder to hand over
#   build/build.sh sign         sign what has already been published
#   build/build.sh signing      explain what signing would do on this machine, and why
#   build/build.sh prune        keep the deliverables, drop the intermediates
#   build/build.sh clean        remove build output
#
# Both 64 bit and ARM are built by default. Every machine this is deployed to is 64 bit,
# and the ARM build exists because it is tested on a Windows on ARM virtual machine, where
# a native build avoids the emulation layer. To build one only:
#   RID=win-arm64 build/build.sh app
#
# The command line tool is NOT built unless asked for. It is a second self contained
# publish, as large and as slow as the application's, and most builds have no use for it:
#   CLI=1 build/build.sh
#
# No zip is produced unless asked for. The handover folder is the deliverable and can be
# copied to another machine as it stands; a zip is only of use for sending one file:
#   ZIP=1 build/build.sh publish
#
# Signing happens by default wherever it can. From macOS or Linux that needs osslsigncode
# and the certificate as a .pfx or .p12 file. Leave it at ~/.pdftodicom/codesign.pfx and
# every build signs without being asked; name it explicitly for a one off:
#   SIGN_PFX=~/keys/codesign.pfx build/build.sh ship
# See "build/build.sh sign" and docs/CodeSigning.md.

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

# Architectures every command builds: 64 bit and ARM.
#
# x86 is deliberately not among them. Every Windows 10 or 11 machine in service runs one
# of these two, and a third build is another 80 MB and another thing to pick the wrong one
# of. Build it explicitly if a 32 bit machine ever turns up.
#
# RID names one architecture, RIDS names several:
#   RID=win-arm64 build/build.sh app
#   RIDS="win-x86 win-x64" build/build.sh publish
# shellcheck disable=SC2206
RUNTIMES=(${RIDS:-${RID:-win-x64 win-arm64}})

# The architecture the per architecture functions are working on. Set by the loops below;
# never read before one of them has set it.
RUNTIME="${RUNTIMES[0]}"

# Kept under its old name because the signing step and the ship folder both read it.
SHIP_RUNTIMES=("${RUNTIMES[@]}")

# The command line tool, which is a second self contained publish of its own. Off unless
# asked for: a developer iterating on the window has no use for it, and the release
# pipeline passes CLI=1 so that what reaches a site is still complete.
BUILD_CLI="${CLI:-0}"

# A zip is produced only when asked for. The handover folder can be copied to another
# machine as it stands, so the zip is of use only for sending one file to somebody.
MAKE_ZIP="${ZIP:-0}"

# Signing, which happens by default wherever it can. SIGN_PFX is the certificate with its
# private key; the password comes from SIGN_PASSWORD_FILE, or is asked for, and is never
# put on a command line where the process list would show it.
#
# The default location is what makes "signed by default" true in practice. Put the
# certificate in ~/.pdftodicom, with its password in codesign.password beside it if you
# would rather not be asked, and every build signs itself. Nothing is created here and
# nothing is looked for anywhere else: a machine without one of these files builds
# unsigned and says so, naming the paths it looked at.
#
# Several names, because an export rarely comes out called what a script expects. macOS
# Keychain writes .p12, Windows writes .pfx, and both spellings mean the same container.
SIGN_DEFAULT_DIR="${SIGN_DEFAULT_DIR:-$HOME/.pdftodicom}"
SIGN_PFX="${SIGN_PFX:-}"
SIGN_PASSWORD_FILE="${SIGN_PASSWORD_FILE:-}"

SIGN_CANDIDATES=(
    "$SIGN_DEFAULT_DIR/codesign.pfx"
    "$SIGN_DEFAULT_DIR/codesign.p12"
    "$SIGN_DEFAULT_DIR/certificate.pfx"
    "$SIGN_DEFAULT_DIR/certificate.p12"
)

if [[ -z "$SIGN_PFX" ]]; then
    for candidate in "${SIGN_CANDIDATES[@]}"; do
        if [[ -f "$candidate" ]]; then
            SIGN_PFX="$candidate"
            break
        fi
    done
fi

if [[ -z "$SIGN_PASSWORD_FILE" ]]; then
    for candidate in "$SIGN_DEFAULT_DIR/codesign.password" "$SIGN_DEFAULT_DIR/password.txt"; do
        if [[ -f "$candidate" ]]; then
            SIGN_PASSWORD_FILE="$candidate"
            break
        fi
    done
fi
SIGN_NAME="${SIGN_NAME:-PDF to DICOM}"
SIGN_URL="${SIGN_URL:-}"

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
# Kept: the handover folder, the versioned standalone executable, the installer and a zip
# if one was asked for. Removed: the publish staging directories, and the handover folder
# only when a zip of it sits beside it, since its content is then in the zip.
#
# With no zip, which is now the default, the folder is always kept. That is the point of
# it: the folder IS the deliverable, and pruning away the thing somebody is about to copy
# to a machine would be an odd definition of tidying up.
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
    step "Validating the XAML, the view models, the installer script and the build scripts"

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
    # The root is passed rather than left to the validator's own guess, so that running
    # this script from anywhere checks the same tree it is building.
    "$python" "$REPO_ROOT/build/validate-viewmodel-commands.py" "$REPO_ROOT"
    "$python" "$REPO_ROOT/build/validate-installer-script.py"
    # Checked from here as well as from build.ps1, because this is the script that runs
    # on the machine where the fault it catches cannot happen. A Mac or a Linux box is
    # where these scripts are usually edited; Windows is where the edit fails.
    "$python" "$REPO_ROOT/build/validate-powershell-strict.py" "$REPO_ROOT"
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
    #
    # The signing material is listed for a different reason. The package is assembled from
    # a named list of files, so a certificate cannot reach it today, and one is kept in
    # this repository by a deliberate decision of its owner. Both of those are true until
    # somebody changes the list, and handing a private signing key to a hospital is not a
    # mistake that should depend on nobody ever writing a wildcard.
    local forbidden=("BuildingItYourself" "CodeSigning" "ProtectingAgainstUnauthorisedDistribution"
                     "testworklistserver" "hl7testserver" "mllp-receiver"
                     "codesign" ".pfx" ".p12" ".password" ".key")
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

# Publishes the executables for $RUNTIME. Nothing else: no handover folder, no installer.
do_publish_app() {
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

    info "produced PdfToDicom.exe ($(human_size "$ARTIFACTS/$RUNTIME/app/PdfToDicom.exe"))"

    if [[ "$BUILD_CLI" == "0" ]]; then
        info "the command line tool was not built; pass CLI=1 if you want it"

        # An earlier build with CLI=1 would have left one here, and the packaging step
        # takes whatever it finds. Shipping yesterday's command line tool beside today's
        # application is worse than shipping none, so the stale output goes.
        if [[ -d "$ARTIFACTS/$RUNTIME/cli" ]]; then
            rm -rf "$ARTIFACTS/$RUNTIME/cli"
            info "removed the command line tool left by an earlier build"
        fi

        return 0
    fi

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
}

# Assembles the folder that is handed to a site, from what do_publish_app produced.
#
# Called after signing, never before. The files here are hard links to the published ones,
# which is what keeps 80 MB from being copied three times, and signing is what decides the
# bytes those links point at.
do_assemble() {
    step "Assembling the package to hand over for $RUNTIME"

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

    # Only when asked for. The folder above is the deliverable: it can be copied to
    # another machine, or onto a stick, exactly as it stands.
    if [[ "$MAKE_ZIP" != "0" ]]; then
        if command -v zip >/dev/null 2>&1; then
            (cd "$ARTIFACTS" && zip -qr "PdfToDicom-$version-$RUNTIME.zip" "PdfToDicom-$version-$RUNTIME")
        else
            info "zip is not installed, so no archive was made. The folder is the deliverable."
        fi
    fi

    # A standalone copy whose name carries the version and the architecture. The file
    # inside the package stays PdfToDicom.exe, because the manuals, the shortcuts and the
    # installer all name it. This copy is for handing one file to somebody.
    link_or_copy "$ARTIFACTS/$RUNTIME/app/PdfToDicom.exe" "$ARTIFACTS/PdfToDicom-$version-$RUNTIME.exe"

    info "folder:  $package"

    if [[ -f "$ARTIFACTS/PdfToDicom-$version-$RUNTIME.zip" ]]; then
        info "zip:     $ARTIFACTS/PdfToDicom-$version-$RUNTIME.zip"
    fi

    info "exe:     $ARTIFACTS/PdfToDicom-$version-$RUNTIME.exe"
    info "$(find "$package" -type f | wc -l | tr -d ' ') file(s)"
    info "artifacts: $(human_size "$ARTIFACTS") on disk, $(human_size_counting_links "$ARTIFACTS") if every link were a copy"
    info "'build/build.sh prune' drops the staging and the unpacked folder"
}

# Printed once at the end rather than once per architecture, which is what it became when
# the build started producing two of them.
say_where_the_installer_comes_from() {
    step "Result"

    local runtime
    for runtime in "${RUNTIMES[@]}"; do
        info "$runtime: $ARTIFACTS/$runtime/app/PdfToDicom.exe ($(human_size "$ARTIFACTS/$runtime/app/PdfToDicom.exe"))"
    done

    cat <<'MESSAGE'

    The installer is not produced here because Inno Setup requires Windows.
    To build it, either:
      - push the branch and let .github/workflows/build-windows.yml build it, or
      - run build\build.ps1 on a Windows machine with Inno Setup 6 installed.
MESSAGE
}

# ---------------------------------------------------------------- signing

# Mirrors what build/sign.ps1 does on Windows, for a build made on macOS or Linux.
#
# The two take their certificate from different places, and they have to. sign.ps1 uses
# PowerShell's Set-AuthenticodeSignature with a certificate from the Windows certificate
# store, where the private key can be marked non exportable and never exists as a file.
# There is no equivalent store here, so this needs the certificate AS a file, which is a
# private key sitting on a disk. That is a real difference in exposure, and it is why the
# certificate is never created or looked for beyond the one location: a build signs when
# ~/.pdftodicom/codesign.pfx is there, or when SIGN_PFX names a file, and otherwise says
# so and carries on unsigned.
#
# Keep that file out of the repository (.gitignore already covers *.pfx), off shared
# drives, and on an encrypted disk.
#
# The password is never put on a command line. osslsigncode's -pass would show it in the
# process list to every other user on the machine; -readpass takes a file instead, so the
# password goes to a file this script creates with owner only permissions and deletes
# immediately afterwards.
# Runs a command, discarding its output, and without the shell announcing a crash.
#
# osslsigncode does not merely fail when it cannot reach a timestamp server: it segfaults.
# A command's own redirection cannot hide that, because the message is printed by the shell
# that reaped the child, not by the child. Running it through a nested shell moves the
# announcement into that shell, whose stderr is discarded with it, and the outer shell then
# sees an ordinary non zero exit code. The caller handles the failure; the operator is not
# shown a segmentation fault that is not their problem.
run_quietly() {
    bash -c '"$@" >/dev/null 2>&1' _ "$@" 2>/dev/null
}

# Replaces a file's contents without replacing the file.
#
# osslsigncode cannot sign in place, so it writes a second file which has to go back over
# the first. "mv" would NOT do: it unlinks the original and renames the new file into its
# place, so any hard link to the original keeps pointing at the old, unsigned bytes. The
# handover folder is built from hard links to exactly these files, and signing it after it
# had been assembled left the copy that is actually handed to a site unsigned while the
# staging copy next to it was signed. Writing through the existing inode keeps every name
# for this file in agreement, whatever order the build does things in.
write_back_in_place() {
    local from=$1
    local to=$2

    cat "$from" > "$to"
    rm -f "$from"
}

# Copies a password file, turning what an editor wrote into what a tool can read.
#
# A password file written on Windows is usually NOT plain ASCII. PowerShell's "> file"
# writes UTF-16 with a byte order mark, and Notepad's "Unicode" does the same, so handing
# those bytes to osslsigncode offers it a password that begins with 0xFF 0xFE and contains
# a NUL between every character. It answers "invalid password", which sends somebody
# looking for a wrong password rather than a wrong encoding. That happened with a real
# file, which is why this exists.
#
# Trailing newlines go too: a file saved by an editor ends with one and it is not part of
# the password.
normalise_password_file() {
    local from=$1
    local to=$2

    # The first two bytes say whether this is UTF-16. "od" is in POSIX and is on macOS;
    # "file" and "hexdump" are not guaranteed to agree between the two.
    local marker
    marker="$(od -An -N2 -tx1 "$from" 2>/dev/null | tr -d ' \n')"

    case "$marker" in
        fffe|feff)
            if command -v iconv >/dev/null 2>&1; then
                iconv -f UTF-16 -t UTF-8 "$from" 2>/dev/null | tr -d '\r\n' > "$to"
                info "the password file is UTF-16; converted it for signing"
                return 0
            fi

            info "the password file looks like UTF-16 and iconv is not available, so it is"
            info "  being used as it is; if signing reports a wrong password, save it as"
            info "  plain text and try again"
            ;;
    esac

    # A UTF-8 byte order mark is invisible in an editor and is not part of the password.
    # Stripped by bytes rather than with sed, whose handling of \xEF differs between GNU
    # and BSD.
    tr -d '\r\n' < "$from" |
        if [[ "${marker}" == "efbb" ]]; then tail -c +4; else cat; fi > "$to"
}

sign_one() {
    local file=$1
    local label=$2
    local password_file=$3

    if [[ ! -f "$file" ]]; then
        return 0
    fi

    local signed="$file.signed"

    # Built as an array. Putting an optional argument inside ${VAR:+...} and letting the
    # shell split it is how a URL with a space in it turns into two arguments.
    local -a base=(
        sign
        -pkcs12 "$SIGN_PFX"
        -readpass "$password_file"
        -h sha256
        -n "$SIGN_NAME"
    )

    if [[ -n "$SIGN_URL" ]]; then
        base+=(-i "$SIGN_URL")
    fi

    # The same servers build/sign.ps1 tries, in the same order. A timestamp is what lets a
    # signature outlive the certificate, so it is worth trying more than one.
    local server
    for server in http://timestamp.digicert.com \
                  http://timestamp.sectigo.com \
                  http://timestamp.globalsign.com/tsa/r6advanced1; do

        if run_quietly osslsigncode "${base[@]}" -ts "$server" -in "$file" -out "$signed"; then
            write_back_in_place "$signed" "$file"
            info "signed $label (timestamped by ${server#http://})"
            return 0
        fi

        rm -f "$signed"
    done

    # Every timestamp server failed. Worth signing anyway and saying so: a signature
    # without a timestamp is still tamper evidence, it simply stops being verifiable once
    # the certificate expires.
    if run_quietly osslsigncode "${base[@]}" -in "$file" -out "$signed"; then
        write_back_in_place "$signed" "$file"
        info "signed $label WITHOUT a timestamp: no timestamp server could be reached"
        info "  the signature will stop being verifiable when the certificate expires"
        return 0
    fi

    rm -f "$signed"
    echo "    ERROR: $label could not be signed." >&2
    return 1
}

# Reports what signing would do, and why, without building anything.
#
# Exists because "I put the certificate in the folder and nothing happened" is not a
# question a person should have to read a script to answer. It names every path that was
# examined, says what is in the folder if the expected name is not, and checks the tool
# that applies the signature.
do_describe_signing() {
    step "Signing: what this machine would do"

    info "certificate folder : $SIGN_DEFAULT_DIR"

    if [[ -n "${SIGN_PFX:-}" ]]; then
        info "certificate        : $SIGN_PFX"

        if [[ -n "${SIGN_PASSWORD_FILE:-}" ]]; then
            info "password           : read from $SIGN_PASSWORD_FILE"
        else
            info "password           : it will be asked for, without being shown"
        fi

        if command -v osslsigncode >/dev/null 2>&1; then
            info "osslsigncode       : $(command -v osslsigncode)"
            info ""
            info "Every build signs. Nothing further to do."
        else
            info "osslsigncode       : NOT INSTALLED, so nothing can be signed"
            info ""
            info "  macOS : brew install osslsigncode"
            info "  Linux : sudo apt-get install osslsigncode"
        fi

        return 0
    fi

    info "certificate        : none found"
    info ""
    info "These are the names looked for, in this order:"

    local candidate
    for candidate in "${SIGN_CANDIDATES[@]}"; do
        info "    ${candidate/#$HOME/~}"
    done

    if [[ -d "$SIGN_DEFAULT_DIR" ]]; then
        info ""
        info "The folder exists. What is in it:"

        local entry
        local found=0
        while IFS= read -r entry; do
            info "    $entry"
            found=1
        done < <(ls -A "$SIGN_DEFAULT_DIR" 2>/dev/null)

        if (( found == 0 )); then
            info "    (empty)"
        else
            info ""
            info "If the certificate is one of those, rename it to codesign.pfx, or point at"
            info "it directly:"
            info ""
            info "    SIGN_PFX=$SIGN_DEFAULT_DIR/<the file> build/build.sh app"
        fi
    else
        info ""
        info "That folder does not exist on this machine. To create it:"
        info ""
        info "    mkdir -p $SIGN_DEFAULT_DIR"
        info "    cp /somewhere/your-certificate.pfx $SIGN_DEFAULT_DIR/codesign.pfx"
        info "    chmod 600 $SIGN_DEFAULT_DIR/codesign.pfx"
    fi

    info ""
    info "A certificate in the macOS Keychain or the Windows certificate store is not used"
    info "here: this needs the exported file, because osslsigncode reads a file. On Windows,"
    info "build\\build.ps1 reads the certificate store as well as this folder."

    return 0
}

do_sign() {
    step "Signing the published executables"

    if [[ -z "$SIGN_PFX" ]]; then
        info "Nothing was signed, because no certificate was found."
        info ""
        info "Looked for, in order:"

        local candidate
        for candidate in "${SIGN_CANDIDATES[@]}"; do
            info "    $candidate"
        done

        if [[ -d "$SIGN_DEFAULT_DIR" ]]; then
            local others
            others="$(ls -A "$SIGN_DEFAULT_DIR" 2>/dev/null | tr '\n' ' ')"

            if [[ -n "$others" ]]; then
                info ""
                info "That folder exists and holds: $others"
                info "If one of those is the certificate, rename it to codesign.pfx."
            fi
        fi

        cat <<'MESSAGE'

    'build/build.sh signing' explains this machine's state in full.

    Signing here needs the certificate as a .pfx or .p12 file, including its private
    key, and osslsigncode to apply it:

      macOS   : brew install osslsigncode
      Linux   : sudo apt-get install osslsigncode

    To export one from the Windows machine that already signs, in PowerShell:

      $c = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Select-Object -First 1
      Export-PfxCertificate -Cert $c -FilePath codesign.pfx -Password (Read-Host -AsSecureString)

    That only works if the key was created exportable. If it was not, signing stays on
    the Windows machine, which is the safer place for it anyway.

    This is not an error. An unsigned build is complete and runs; see docs/CodeSigning.md
    for what a signature does and does not buy.
MESSAGE
        return 0
    fi

    info "certificate: $SIGN_PFX"

    if [[ ! -f "$SIGN_PFX" ]]; then
        echo "    ERROR: SIGN_PFX is set to '$SIGN_PFX', which is not a file." >&2
        exit 1
    fi

    if ! command -v osslsigncode >/dev/null 2>&1; then
        cat <<'MESSAGE'
    ERROR: a certificate was given but osslsigncode is not installed, so nothing
    could be signed. Refusing to continue rather than quietly handing over an
    unsigned build that was meant to be signed.

      macOS   : brew install osslsigncode
      Linux   : sudo apt-get install osslsigncode
MESSAGE
        exit 1
    fi

    # The password, obtained once for the whole run and held in a file only this user can
    # read, for as long as the signing takes.
    local password_file
    password_file="$(mktemp "${TMPDIR:-/tmp}/pdftodicom-sign.XXXXXX")"
    chmod 600 "$password_file"

    # Removed on the way out of this function, and on an interrupt while it is signing.
    #
    # This used a RETURN trap, which was wrong in a way that took a restructure to show.
    # A RETURN trap set inside a function is not lifted when that function returns: it
    # stays installed and fires again the next time ANY function returns. While signing
    # was the last thing a build did, nothing returned afterwards and it never showed. The
    # moment signing moved before the packaging step, the trap fired again with its
    # variable out of scope and "set -u" failed the whole build after every file had been
    # produced and signed. Explicit cleanup at each way out, and no RETURN trap.
    #
    # A global EXIT trap is deliberately not set either, because it would silently replace
    # any other EXIT handler this script grows later.
    trap 'finish_signing; exit 130' INT
    trap 'finish_signing; exit 143' TERM

    if [[ -n "$SIGN_PASSWORD_FILE" ]]; then
        if [[ ! -f "$SIGN_PASSWORD_FILE" ]]; then
            echo "    ERROR: SIGN_PASSWORD_FILE is set to '$SIGN_PASSWORD_FILE', which is not a file." >&2
            exit 1
        fi

        normalise_password_file "$SIGN_PASSWORD_FILE" "$password_file"
    elif [[ -n "${SIGN_PASSWORD:-}" ]]; then
        # Accepted because a build server has to get it from somewhere, and noted because
        # an environment variable is readable by anything running as this user.
        printf '%s' "$SIGN_PASSWORD" > "$password_file"
        info "the password came from SIGN_PASSWORD; prefer SIGN_PASSWORD_FILE where you can"
    else
        printf '    Certificate password (not shown): '
        local password
        IFS= read -rs password
        printf '\n'
        printf '%s' "$password" > "$password_file"
        unset password
    fi

    local failures=0
    local runtime

    # Each architecture once. "ship" sets RUNTIME as it goes, so the current one is
    # usually already in SHIP_RUNTIMES, and osslsigncode on an already signed file is at
    # best wasted work.
    local -a targets=()
    for runtime in "${SHIP_RUNTIMES[@]}" "$RUNTIME"; do
        if [[ -d "$ARTIFACTS/$runtime" ]] && [[ " ${targets[*]-} " != *" $runtime "* ]]; then
            targets+=("$runtime")
        fi
    done

    if (( ${#targets[@]} == 0 )); then
        info "nothing has been published yet, so there was nothing to sign"
        finish_signing
        return 0
    fi

    for runtime in "${targets[@]}"; do
        # The executable inside the handover folder is a hard link to the staging copy, so
        # signing the staging copy signs the one that is handed over as well. That holds
        # only because sign_one writes the signed bytes through the existing file rather
        # than replacing it; see write_back_in_place for what went wrong when it did not.
        sign_one "$ARTIFACTS/$runtime/app/PdfToDicom.exe" "$runtime desktop application" "$password_file" || failures=$((failures + 1))
        sign_one "$ARTIFACTS/$runtime/cli/pdftodicom.exe" "$runtime command line tool" "$password_file" || failures=$((failures + 1))
    done

    finish_signing

    if (( failures > 0 )); then
        echo "    ERROR: $failures file(s) could not be signed." >&2
        exit 1
    fi

    info "a signature does not stop Windows warning about an unrecognised publisher;"
    info "see docs/CodeSigning.md for what it does buy"
}

# Removes the password file and lifts the interrupt handlers.
#
# Reads do_sign's local variable, which bash allows: locals are visible to everything the
# function calls. Written once here rather than at each of do_sign's four ways out, where
# the one that was forgotten would leave a password on the disk.
finish_signing() {
    rm -f "${password_file:-}"
    trap - INT TERM
}

# ---------------------------------------------------------------- ship

# Mirrors a directory, hard linking each file where the filesystem allows it.
#
# The executables are around 80 MB each, and the handover copy is already a link to the
# staging copy. Copying here would undo that for no benefit, so each file is linked and
# only falls back to a copy when linking is refused.
link_tree() {
    local source=$1
    local target=$2

    local relative
    while IFS= read -r relative; do
        mkdir -p "$target/$(dirname "$relative")"
        link_or_copy "$source/$relative" "$target/$relative"
    done < <(cd "$source" && find . -type f | sed 's|^\./||')
}

# Builds both architectures into one folder that can be handed over or copied as it is.
#
# One folder with a subfolder per architecture, rather than the executables loose together.
# The application inside each is called PdfToDicom.exe, which the manuals, the shortcuts
# and the installer all name, and two of those cannot share a folder. A subfolder each also
# means what gets copied to a machine is a complete package, documentation and samples
# included, rather than a bare executable.
do_ship() {
    local version
    version="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$REPO_ROOT/Directory.Build.props" | head -1)"

    local ship="$ARTIFACTS/ship"
    rm -rf "$ship"
    mkdir -p "$ship"

    local runtime
    for runtime in "${RUNTIMES[@]}"; do
        RUNTIME="$runtime"
        do_publish_app
    done

    # Signed once, after everything is published, so the password is asked for once, and
    # before the packages are assembled, because those are hard links to what is signed
    # here.
    do_sign

    for runtime in "${RUNTIMES[@]}"; do
        RUNTIME="$runtime"
        do_assemble
    done

    step "Assembling the folder to hand over"

    for runtime in "${RUNTIMES[@]}"; do
        link_tree "$ARTIFACTS/PdfToDicom-$version-$runtime" "$ship/$runtime"
        info "$ship/$runtime"
    done

    write_ship_readme "$ship/WHICH FOLDER DO I USE.txt" "$version"

    step "Result"
    info "$ship"
    info "copy that folder, or the one architecture folder inside it, to the machine"
    info "$(find "$ship" -type f | wc -l | tr -d ' ') file(s), $(human_size "$ship") on disk"
    info "no zip was made; pass ZIP=1 if you want one as well"
}

# A note at the top of the ship folder, because "win-x64 or win-arm64?" is the one question
# somebody copying this folder will have, and the answer is not obvious from the names.
write_ship_readme() {
    local path=$1
    local version=$2

    local title="PDF to DICOM $version"
    local rule
    rule="$(printf '%*s' "${#title}" '' | tr ' ' '=')"

    cat > "$path" <<TEXT
$title
$rule

This folder holds one complete copy of the application per processor type. Copy the
folder that matches the machine, or copy all of it and pick there.

  win-x64     Almost every Windows desktop and laptop. Start here if unsure.
  win-arm64   Windows on ARM: Surface Pro X, Dev Kit, Snapdragon laptops.

Not sure which? On the machine, press the Windows key, type "About your PC", and read
"System type". It says either "x64-based processor" or "ARM-based processor".

Picking the wrong one is not dangerous. Windows on ARM runs the x64 build under
emulation, so it will work but more slowly; an x64 machine simply refuses to run the
ARM build.

Inside each folder:

  PdfToDicom.exe       The application. Nothing to install; double click it.
  READ ME FIRST.txt    Start here.
  Documentation\\       The manuals, in English and Portuguese.
  Samples\\             An example configuration and an example report.
  Command line\\        pdftodicom.exe, for scripted or unattended use.

Nothing else needs to be installed on the machine: no .NET, no Java, no Visual C++
runtime. Administrator rights are not required to run it.
TEXT
}

# Publishes every requested architecture, signs them, then assembles each package.
#
# In that order, and not interleaved per architecture: the password is asked for once, and
# nothing is assembled from an executable that has not been signed yet.
publish_and_assemble() {
    local runtime

    for runtime in "${RUNTIMES[@]}"; do
        RUNTIME="$runtime"
        do_publish_app
    done

    do_sign

    for runtime in "${RUNTIMES[@]}"; do
        RUNTIME="$runtime"
        do_assemble
    done

    say_where_the_installer_comes_from
}

# The shortest trip to a running executable: no tests, no handover folder, no installer.
# For the loop of change something, build it, copy it to the test machine, run it.
publish_only() {
    local runtime

    for runtime in "${RUNTIMES[@]}"; do
        RUNTIME="$runtime"
        do_publish_app
    done

    do_sign

    step "Result"

    for runtime in "${RUNTIMES[@]}"; do
        info "$ARTIFACTS/$runtime/app/PdfToDicom.exe"
    done
}

require_sdk
info "building for ${RUNTIMES[*]}$( [[ "$BUILD_CLI" == "0" ]] || printf ', with the command line tool' )"

case "${1:-all}" in
    clean)   do_clean ;;
    prune)   do_prune ;;
    test)    do_restore; do_build; do_validate; do_test ;;
    signing) do_describe_signing ;;
    app)     do_restore; publish_only ;;
    publish) do_restore; publish_and_assemble ;;
    sign)    do_sign ;;
    ship)    do_restore; do_build; do_validate; do_test; do_ship ;;
    all)     do_restore; do_build; do_validate; do_test; publish_and_assemble ;;
    *)
        echo "Usage: $0 [all|app|test|publish|ship|sign|signing|prune|clean]" >&2
        exit 1
        ;;
esac

step "Done"
