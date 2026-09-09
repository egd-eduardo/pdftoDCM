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
#   build/build.sh test         run the tests only
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

do_test() {
    step "Running the test suite"
    dotnet test "$REPO_ROOT/tests/PdfToDicom.Core.Tests/PdfToDicom.Core.Tests.csproj" \
        -c "$CONFIGURATION" --nologo --verbosity minimal
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

    step "Assembling the portable package"

    local package="$ARTIFACTS/portable-$RUNTIME"
    rm -rf "$package"
    mkdir -p "$package"

    cp "$ARTIFACTS/$RUNTIME/app/PdfToDicom.exe" "$package/"
    cp "$ARTIFACTS/$RUNTIME/cli/pdftodicom.exe" "$package/" 2>/dev/null || true
    cp "$REPO_ROOT/samples/config.sample.json" "$package/"
    cp "$REPO_ROOT/samples/example-report.pdf" "$package/"
    cp -r "$REPO_ROOT/docs" "$package/docs"

    (cd "$ARTIFACTS" && zip -qr "PdfToDicom-portable-$RUNTIME.zip" "portable-$RUNTIME")

    info "portable package: $ARTIFACTS/PdfToDicom-portable-$RUNTIME.zip"

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
    test)    do_restore; do_build; do_test ;;
    publish) do_restore; do_publish ;;
    all)     do_restore; do_build; do_test; do_publish ;;
    *)
        echo "Usage: $0 [all|test|publish|clean]" >&2
        exit 1
        ;;
esac

step "Done"
