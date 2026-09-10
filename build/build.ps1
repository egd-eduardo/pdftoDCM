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

}

<#
    Assembles the folder that is actually handed to a site: the executable together with
    the documents that should travel with it.

    The document list is deliberately explicit rather than copying the docs folder
    wholesale. Two of those documents, on protecting against unauthorised distribution and
    on code signing, are written for the copyright holder and discuss the customer. Sending
    them to a hospital would be at best odd. An explicit list also means a new document has
    to be considered rather than shipped by accident.
#>
function Invoke-Assemble {
    Write-Step 'Assembling the package to hand over'

    $ApplicationPath = Join-Path $Artifacts "$Runtime\app\PdfToDicom.exe"

    if (-not (Test-Path $ApplicationPath)) {
        throw "Nothing to package. Publish first: .\build\build.ps1 -Task publish -Runtime $Runtime"
    }

    $version = (Select-Xml -Path (Join-Path $RepoRoot 'Directory.Build.props') `
        -XPath '/Project/PropertyGroup/Version').Node.InnerText

    $package = Join-Path $Artifacts "PdfToDicom-$version-$Runtime"
    if (Test-Path $package) { Remove-Item $package -Recurse -Force }

    $documents = Join-Path $package 'Documentation'
    $samples   = Join-Path $package 'Samples'

    New-Item -ItemType Directory -Path $package, $documents, $samples -Force | Out-Null

    # ---------------------------------------------------------------- the software
    Copy-Item $ApplicationPath $package

    $cli = Join-Path $Artifacts "$Runtime\cli\pdftodicom.exe"
    if (Test-Path $cli) { Copy-Item $cli $package }

    # ---------------------------------------------------------------- legal
    # LICENCE and the third party notices are not optional courtesies. The MIT and Apache
    # 2.0 licences covering PdfPig and Serilog both require their notice to accompany any
    # redistribution, so omitting them would put the recipient, and us, in breach.
    Copy-Item (Join-Path $RepoRoot 'LICENSE')                  (Join-Path $package 'LICENCE.txt')
    Copy-Item (Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.md')   (Join-Path $package 'THIRD-PARTY-NOTICES.txt')
    Copy-Item (Join-Path $RepoRoot 'CHANGELOG.md')             (Join-Path $documents 'Changelog.md')

    # ---------------------------------------------------------------- documentation
    $shipped = @{
        'UserManual.md'                = 'User Manual (English).md'
        'ManualDoUtilizador.pt-PT.md'  = 'Manual do Utilizador (Portugues).md'
        'DicomConformanceStatement.md' = 'DICOM Conformance Statement (for your PACS administrator).md'
        'TechnicalDocumentation.md'    = 'Technical Documentation (for IT).md'
    }

    foreach ($source in $shipped.Keys) {
        $from = Join-Path $RepoRoot "docs\$source"

        if (Test-Path $from) {
            Copy-Item $from (Join-Path $documents $shipped[$source])
        }
        else {
            Write-Warning "The document '$source' was not found and is missing from the package."
        }
    }

    # ---------------------------------------------------------------- samples
    Copy-Item (Join-Path $RepoRoot 'samples\config.sample.json') $samples
    Copy-Item (Join-Path $RepoRoot 'samples\example-report.pdf') $samples

    # ---------------------------------------------------------------- start here
    New-StartHereFile -Path (Join-Path $package 'READ ME FIRST.txt') -Version $version

    # ---------------------------------------------------------------- archive
    $zip = Join-Path $Artifacts "PdfToDicom-$version-$Runtime.zip"
    Compress-Archive -Path "$package\*" -DestinationPath $zip -Force

    $fileCount = (Get-ChildItem $package -Recurse -File).Count

    Write-Host "    folder : $package"
    Write-Host "    zip    : $zip"
    Write-Host "    $fileCount file(s): the application, the licence and third party"
    Write-Host "    notices, four documents and two samples"
}

<#
    Writes the note a recipient sees first. Plain text, because it must open in Notepad on
    a machine with nothing installed.
#>
function New-StartHereFile {
    param([string]$Path, [string]$Version)

    $content = @"
PDF to DICOM $Version
Copyright (c) 2026 Eduardo Dias

Converts a PDF document into a DICOM Encapsulated PDF object and sends it to a
PACS, so a report from another system becomes part of the patient's imaging
record.


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


WHAT IS IN THIS FOLDER
----------------------

  PdfToDicom.exe           The application.
  pdftodicom.exe           Command line version, for scripted conversion.
  LICENCE.txt              The terms. Free to use, commercial and private.
  THIRD-PARTY-NOTICES.txt  Licences of the components used.

  Documentation    User Manual (English)                    For the people using it daily.
    Manual do Utilizador (Portugues)         The same, in Portuguese.
    DICOM Conformance Statement              For your PACS administrator.
    Technical Documentation                  For IT.
    Changelog                                What changed in each version.

  Samples    config.sample.json       Annotated settings reference.
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
"@

    # ASCII, and CRLF, so the file opens correctly in Notepad on any Windows.
    $content = $content -replace "`r?`n", "`r`n"
    [System.IO.File]::WriteAllText($Path, $content, [System.Text.Encoding]::ASCII)
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
    'publish' {
        Invoke-Restore
        Invoke-Publish
        if ($Sign) { Invoke-Sign }
        Invoke-Assemble
    }
    'package' {
        if ($Sign) { Invoke-Sign }
        Invoke-Assemble
        Invoke-Package
        if ($Sign) { Invoke-Sign }
    }
    'all' {
        Invoke-Restore
        Invoke-Build
        Invoke-Validate
        Invoke-Test
        Invoke-Publish

        # Signing has to happen here, between publishing and packaging, not at the end.
        # The package and the installer both take copies of the executable, so signing
        # afterwards would sign the original and leave every copy that is actually handed
        # out unsigned.
        if ($Sign) { Invoke-Sign }

        Invoke-Assemble
        Invoke-Package

        # Again, because the installer did not exist the first time round.
        if ($Sign) { Invoke-Sign }
    }
}

Write-Step 'Done'
