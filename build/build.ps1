<#
.SYNOPSIS
    Builds, tests, publishes and packages PDF to DICOM on Windows.

.DESCRIPTION
    Produces the self contained single file executable and, when Inno Setup 6 is
    installed, the per user installer. This is the script the release pipeline runs.

    The equivalent for a developer working on macOS or Linux is build/build.sh, which
    can produce the executable but not the installer, because Inno Setup requires
    Windows.

.PARAMETER Runtime
    Target architecture: win-x86, win-x64 (default) or win-arm64. Windows on ARM can run
    the x64 build under emulation, but a native arm64 build avoids that layer entirely.

.PARAMETER Sign
    Sign the published executables and the installer after building. Requires a code
    signing certificate; see build/sign.ps1 and docs/CodeSigning.md.

.PARAMETER Task
    all      restore, build, test, publish and package (default)
    test     run the test suite only
    publish  publish the executables only
    package  build the installer only, from an existing publish
    clean    remove build output

.EXAMPLE
    .\build\build.ps1
    .\build\build.ps1 -Task test
    .\build\build.ps1 -Runtime win-arm64
    .\build\build.ps1 -Sign
#>

[CmdletBinding()]
param(
    [ValidateSet('all', 'test', 'publish', 'package', 'clean')]
    [string]$Task = 'all',

    [string]$Configuration = 'Release',

    [ValidateSet('win-x86', 'win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$Sign
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot  = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $RepoRoot 'artifacts'
$Solution  = Join-Path $RepoRoot 'PDF to DICOM.sln'

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Assert-Sdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'The .NET SDK was not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download'
    }

    Write-Host "    using .NET SDK $(dotnet --version)"
}

function Invoke-Clean {
    Write-Step 'Cleaning'

    if (Test-Path $Artifacts) {
        Remove-Item $Artifacts -Recurse -Force
    }

    Get-ChildItem -Path (Join-Path $RepoRoot 'src'), (Join-Path $RepoRoot 'tests') `
        -Include bin, obj -Recurse -Directory -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host '    build output removed'
}

function Invoke-Restore {
    Write-Step 'Restoring packages'
    dotnet restore $Solution --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
}

function Invoke-Build {
    Write-Step "Building ($Configuration)"
    dotnet build $Solution -c $Configuration --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

function Invoke-Validate {
    Write-Step 'Validating XAML bindings'

    # A broken WPF binding compiles cleanly and then crashes when the window loads, so
    # it is checked here rather than discovered by a user on a clinical workstation.
    $validator = Join-Path $RepoRoot 'build\validate-xaml-bindings.py'
    $python = Get-Command python3, python -ErrorAction SilentlyContinue | Select-Object -First 1

    if (-not $python) {
        Write-Warning 'Python was not found, so the XAML bindings were not validated.'
        return
    }

    & $python.Source $validator
    if ($LASTEXITCODE -ne 0) { throw 'The XAML bindings did not pass validation.' }
}

function Invoke-Test {
    Write-Step 'Running the test suite'
    dotnet test (Join-Path $RepoRoot 'tests\PdfToDicom.Core.Tests\PdfToDicom.Core.Tests.csproj') `
        -c $Configuration --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'The test suite failed. The build is not shippable.' }
}

function Invoke-Publish {
    Write-Step "Publishing the desktop application for $Runtime"

    # Self contained and single file: the runtime, WPF and every dependency are embedded,
    # so the result runs on a clean Windows 10 or 11 machine with nothing pre-installed.
    dotnet publish (Join-Path $RepoRoot 'src\PdfToDicom.App\PdfToDicom.App.csproj') `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o (Join-Path $Artifacts "$Runtime\app") `
        --nologo -v quiet

    if ($LASTEXITCODE -ne 0) { throw 'Publishing the desktop application failed.' }

    Write-Step "Publishing the command line tool for $Runtime"

    dotnet publish (Join-Path $RepoRoot 'src\PdfToDicom.Cli\PdfToDicom.Cli.csproj') `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -o (Join-Path $Artifacts "$Runtime\cli") `
        --nologo -v quiet

    if ($LASTEXITCODE -ne 0) { throw 'Publishing the command line tool failed.' }

    $exe = Join-Path $Artifacts "$Runtime\app\PdfToDicom.exe"
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "    produced PdfToDicom.exe ($size MB)"

    Write-Step 'Assembling the portable package'

    $portable = Join-Path $Artifacts "portable-$Runtime"
    if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
    New-Item -ItemType Directory -Path $portable | Out-Null

    Copy-Item $exe $portable
    Copy-Item (Join-Path $Artifacts "$Runtime\cli\pdftodicom.exe") $portable -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $RepoRoot 'samples\config.sample.json') $portable
    Copy-Item (Join-Path $RepoRoot 'samples\example-report.pdf') $portable
    Copy-Item (Join-Path $RepoRoot 'docs') (Join-Path $portable 'docs') -Recurse

    Compress-Archive -Path "$portable\*" `
        -DestinationPath (Join-Path $Artifacts "PdfToDicom-portable-$Runtime.zip") -Force

    Write-Host "    portable package: $(Join-Path $Artifacts "PdfToDicom-portable-$Runtime.zip")"
}

function Invoke-Package {
    Write-Step 'Building the installer'

    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        Write-Warning @'
Inno Setup 6 was not found, so the installer was not built.
Install it from https://jrsoftware.org/isdl.php, or use winget:
    winget install JRSoftware.InnoSetup
The self contained executable in artifacts\<rid>\app is fully functional without it.
'@
        return
    }

    # The installer script uses the architecture identifiers introduced in Inno Setup 6.3.
    # An older compiler reports their absence as an obscure parse error, so the version is
    # checked here to produce a message that says what is actually wrong.
    #
    # ISCC.exe does not populate its ProductVersion resource: it reports 0.0.0.0. The
    # version is therefore taken from the compiler's own banner, which it prints on
    # startup, and the file version resource is used only as a fallback.
    #
    # This check must never block a build it cannot evaluate. An undetermined version is
    # a warning, not a failure, because letting the compiler speak for itself is always
    # better than refusing to run it.
    $isccVersion = $null

    try {
        $banner = & $iscc /? 2>&1 | Select-Object -First 5

        # Anchored to the product name so that a copyright year or any other number in
        # the banner cannot be mistaken for a version and wrongly block the build.
        $match = [regex]::Match(($banner -join ' '), 'Inno Setup\s+(\d+\.\d+(?:\.\d+)?)')
        if ($match.Success) { $isccVersion = $match.Groups[1].Value }
    }
    catch {
        # The banner could not be read. The file version is tried next.
    }

    if (-not $isccVersion) {
        $fileVersion = (Get-Item $iscc).VersionInfo.FileVersion
        if ($fileVersion -and $fileVersion -notmatch '^0\.0') { $isccVersion = $fileVersion }
    }

    if ($isccVersion -match '^(\d+)\.(\d+)') {
        $major = [int]$Matches[1]
        $minor = [int]$Matches[2]

        Write-Host "    using Inno Setup $isccVersion"

        if ($major -lt 6 -or ($major -eq 6 -and $minor -lt 3)) {
            throw "Inno Setup 6.3 or later is required for the architecture identifiers used by the installer script, but $isccVersion was found."
        }
    }
    else {
        Write-Host '    Inno Setup version could not be determined; proceeding and letting the compiler report any incompatibility.'
    }

    # A static check of the installer script, when Python is available. It catches the
    # common mistakes far more clearly than the compiler does.
    $validator = Join-Path $RepoRoot 'build\validate-installer-script.py'
    $python = Get-Command python3, python -ErrorAction SilentlyContinue | Select-Object -First 1

    if ($python -and (Test-Path $validator)) {
        & $python.Source $validator
        if ($LASTEXITCODE -ne 0) { throw 'The installer script did not pass validation.' }
    }

    $script = Join-Path $RepoRoot 'build\installer\PdfToDicom.iss'
    & $iscc "/DTargetRid=$Runtime" "/O$Artifacts" $script

    if ($LASTEXITCODE -ne 0) { throw 'The installer build failed.' }

    Get-ChildItem $Artifacts -Filter '*.exe' |
        Where-Object { $_.Name -like 'PdfToDicom-Setup*' } |
        ForEach-Object { Write-Host "    installer: $($_.FullName)" }
}

function Invoke-Sign {
    Write-Step 'Signing'

    # Delegated rather than inlined, so signing can also be run on its own against
    # an existing build without repeating it.
    & (Join-Path $PSScriptRoot 'sign.ps1') -Runtime $Runtime
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
}

Assert-Sdk

switch ($Task) {
    'clean'   { Invoke-Clean }
    'test'    { Invoke-Restore; Invoke-Build; Invoke-Validate; Invoke-Test }
    'publish' { Invoke-Restore; Invoke-Publish; if ($Sign) { Invoke-Sign } }
    'package' { Invoke-Package; if ($Sign) { Invoke-Sign } }
    'all'     { Invoke-Restore; Invoke-Build; Invoke-Validate; Invoke-Test; Invoke-Publish; Invoke-Package; if ($Sign) { Invoke-Sign } }
}

Write-Step 'Done'
