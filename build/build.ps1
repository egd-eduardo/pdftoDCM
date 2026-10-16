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
    prune    keep the deliverables, drop the intermediate build output
    clean    remove build output

.EXAMPLE
    .\build\build.ps1
    .\build\build.ps1 -Task test
    .\build\build.ps1 -Runtime win-arm64
    .\build\build.ps1 -Sign
#>

[CmdletBinding()]
param(
    [ValidateSet('all', 'test', 'publish', 'package', 'prune', 'clean')]
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

<#
    Total size of everything under artifacts, in bytes.

    Windows does not report a hard link's size as shared, so this is the figure as if
    every link were a full copy. On a filesystem that does share them, the real
    occupancy is lower, which is the point of linking in the first place.
#>
function Get-ArtifactBytes {
    # Measure-Object emits nothing at all for an empty pipeline rather than an object
    # whose Sum is null, so the result is captured before any property is read. Reaching
    # straight for .Sum is fatal under Set-StrictMode -Version Latest, which is set at the
    # top of this script, and only on an artifacts folder that holds no files.
    $measured = Get-ChildItem $Artifacts -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum

    if ($null -eq $measured -or $null -eq $measured.Sum) { return 0 }

    return $measured.Sum
}

<#
    Places a second name on an existing file rather than a second copy of it.

    The published executable is around 70 MB and ends up under three names: the publish
    staging directory, the handover folder, and the versioned standalone copy. Three real
    copies per architecture is a quarter of a gigabyte of the same bytes, which is what
    made a full three architecture build occupy 1.2 GB.

    A hard link is safe here because nothing rewrites these files in place afterwards.
    Signing runs BEFORE the package is assembled, deliberately, so that the copies handed
    out are the signed ones; and a signature applied through one name would in any case be
    correct under all of them, since every name is meant to hold the same executable.

    Hard links need no elevation on Windows, unlike symbolic links, and both paths here
    are inside the artifacts folder and so on one volume. It still falls back to a copy if
    linking is refused: the build must not fail over an optimisation.
#>
function Copy-OrLink {
    param([string]$Source, [string]$Target)

    if (Test-Path $Target) { Remove-Item $Target -Force }

    try {
        New-Item -ItemType HardLink -Path $Target -Target $Source -ErrorAction Stop | Out-Null
    }
    catch {
        Write-Verbose "Hard linking was refused ($($_.Exception.Message)); copying instead."
        Copy-Item $Source $Target -Force
    }
}

<#
    Removes what can be produced again and keeps what cannot be produced quickly.

    Kept: the zip, the versioned standalone executable and the installer, which are the
    things somebody actually asked for. Removed: the publish staging directories and the
    unpacked handover folder, whose entire content is inside the zip.

    Deliberately a task of its own rather than part of publish or all. The installer is
    built by a separate "package" run that reads the staging directory, and the CI step
    that confirms the executable is self contained reads it too, so pruning automatically
    would break both.
#>
function Invoke-Prune {
    Write-Step 'Pruning the intermediate build output'

    if (-not (Test-Path $Artifacts)) {
        Write-Host '    nothing to prune'
        return
    }

    $before = Get-ArtifactBytes

    foreach ($rid in 'win-x86', 'win-x64', 'win-arm64') {
        $staging = Join-Path $Artifacts $rid

        if (Test-Path $staging) {
            Remove-Item $staging -Recurse -Force
            Write-Host "    removed $rid\ (publish staging)"
        }
    }

    # Only a folder whose zip sits beside it. Deleting the one unpacked copy of a package
    # that was never archived would lose it.
    Get-ChildItem $Artifacts -Directory -Filter 'PdfToDicom-*-win-*' | ForEach-Object {
        if (Test-Path "$($_.FullName).zip") {
            Remove-Item $_.FullName -Recurse -Force
            Write-Host "    removed $($_.Name)\ (its zip holds the same files)"
        }
        else {
            Write-Host "    kept $($_.Name)\ because it has no zip beside it"
        }
    }

    $after = Get-ArtifactBytes
    $kept  = @(Get-ChildItem $Artifacts -File | Where-Object { $_.Extension -in '.zip', '.exe' }).Count

    Write-Host ("    artifacts: {0:N0} MB -> {1:N0} MB" -f ($before / 1MB), ($after / 1MB))
    Write-Host "    $kept deliverable file(s) kept"
}

function Invoke-Clean {
    Write-Step 'Cleaning'

    if (Test-Path $Artifacts) {
        Remove-Item $Artifacts -Recurse -Force
    }

    Get-ChildItem -Path (Join-Path $RepoRoot 'src'), (Join-Path $RepoRoot 'tests'), (Join-Path $RepoRoot 'tools') `
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

<#
    Finds a Python that actually runs, or returns null.

    Merely being on the PATH is not enough on Windows. It ships "App execution aliases"
    for python.exe and python3.exe: zero length stubs that exist, resolve, and satisfy
    Get-Command, but do nothing except print

        Python was not found; run without arguments to install from the Microsoft Store

    and exit non zero. A build that trusts Get-Command therefore reports Python as present
    and then fails, which is exactly what happened.

    So the candidate is asked for its version and has to answer properly.
#>
function Get-WorkingPython {
    foreach ($name in 'python3', 'python', 'py') {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if (-not $command) { continue }

        try {
            # 2>&1 because some builds report the version on standard error.
            $reported = & $command.Source '--version' 2>&1

            if ($LASTEXITCODE -eq 0 -and "$reported" -match 'Python\s+\d') {
                return $command.Source
            }
        }
        catch {
            # Unrunnable. Try the next candidate.
        }
    }

    return $null
}

function Invoke-Validate {
    Write-Step 'Validating XAML bindings'

    # A broken WPF binding compiles cleanly and then crashes when the window loads, so
    # it is checked here rather than discovered by a user on a clinical workstation.
    $validator = Join-Path $RepoRoot 'build\validate-xaml-bindings.py'
    $python = Get-WorkingPython

    if (-not $python) {
        Write-Warning @'
Python is not available, so the XAML bindings were not checked.

This is optional and the build continues without it. The check exists to catch a
binding that compiles but fails when a window opens. CI runs it on every push, so
nothing is lost as long as the branch is pushed.

To run it here, install Python from https://www.python.org/downloads/ and tick
"Add python.exe to PATH".
'@
        return
    }

    & $python $validator

    # Only reached with a working Python, so a non zero code now means the validator
    # genuinely found a problem rather than that Python could not start.
    if ($LASTEXITCODE -ne 0) { throw 'The XAML bindings did not pass validation.' }

    # A command refreshed before it has been created throws in the view model's
    # constructor, so the window never opens. It compiles cleanly, and it only shows at a
    # site that has the feature configured.
    & $python (Join-Path $RepoRoot 'build\validate-viewmodel-commands.py')

    if ($LASTEXITCODE -ne 0) { throw 'The view model command refreshes did not pass validation.' }
}

function Invoke-Test {
    Write-Step 'Running the test suite'

    # Says which runtime the tests are about to run on, when it is not the one they
    # target.
    #
    # Building and running are separate problems. Any recent SDK builds these projects,
    # because an SDK carries its own reference assemblies and global.json rolls forward.
    # Running a net8.0 application needs the .NET 8 runtime, and the test host is itself a
    # net8.0 application, so a machine with only the current SDK built everything and then
    # could not start a single test. RollForward in Directory.Build.props now lets that
    # machine run them on the runtime it has, which is better than refusing, but it is not
    # the runtime the product targets and so is worth one line rather than happening
    # quietly.
    $project = Join-Path $RepoRoot 'tests\PdfToDicom.Core.Tests\PdfToDicom.Core.Tests.csproj'
    $target = ([regex]::Match((Get-Content $project -Raw), '<TargetFramework>net(\d+)\.')).Groups[1].Value

    if ($target) {
        $runtimes = @(dotnet --list-runtimes 2>$null)

        if (-not ($runtimes | Where-Object { $_ -like "Microsoft.NETCore.App $target.*" })) {
            Write-Host "    no .NET $target runtime is installed, so the tests will run on a later one"
            Write-Host "    install it for a like for like run: https://dotnet.microsoft.com/download/dotnet/$target.0"
        }
    }

    dotnet test $project -c $Configuration --nologo --verbosity minimal
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
        -p:IncludeNativeLibrariesForSelfExtract=true `
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
    Copy-OrLink -Source $ApplicationPath -Target (Join-Path $package 'PdfToDicom.exe')

    # The command line tool goes into a subfolder rather than beside the application,
    # because the two assembly names differ only in case: PdfToDicom and pdftodicom.
    #
    # Windows filesystems are case insensitive, so copying both into one folder does not
    # produce two files. The second copy matches the first by name, overwrites its
    # contents and leaves the first one's spelling in place. The handover folder then
    # holds a single PdfToDicom.exe which is really the command line tool, so double
    # clicking it opens a console instead of the application.
    #
    # None of that is visible on Linux or macOS, where the two names are distinct, which
    # is why an assembly verified there looked correct. The Windows CI job reported 11
    # files in the package where the same code on Linux reported 12.
    $cli = Join-Path $Artifacts "$Runtime\cli\pdftodicom.exe"

    if (Test-Path $cli) {
        $commandLine = Join-Path $package 'Command line'
        New-Item -ItemType Directory -Path $commandLine -Force | Out-Null
        Copy-OrLink -Source $cli -Target (Join-Path $commandLine 'pdftodicom.exe')
    }

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
        'WorklistSetup.md'             = 'Worklist Setup (for IT).md'
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

    # Checked before the zip is made, so a defective package is never handed over.
    Assert-PackageIsSound -Package $package -ExpectCli ([bool](Test-Path $cli))

    # ---------------------------------------------------------------- archive
    $zip = Join-Path $Artifacts "PdfToDicom-$version-$Runtime.zip"
    Compress-Archive -Path "$package\*" -DestinationPath $zip -Force

    # A standalone copy whose name carries the version and the architecture. The file
    # inside the package stays PdfToDicom.exe, because the manuals, the shortcuts and the
    # installer all name it and a version in that name would break them. This copy is for
    # handing one file to somebody, where "which build is this?" has to be answerable
    # without opening its properties.
    $standalone = Join-Path $Artifacts "PdfToDicom-$version-$Runtime.exe"
    Copy-OrLink -Source $ApplicationPath -Target $standalone

    $fileCount = (Get-ChildItem $package -Recurse -File).Count

    Write-Host "    folder : $package"
    Write-Host "    zip    : $zip"
    Write-Host "    exe    : $standalone"
    Write-Host "    $fileCount file(s): the application and the command line tool, the"
    Write-Host "    licence and third party notices, five documents and two samples"

    # Reported rather than left to be discovered. A full three architecture build is the
    # largest thing this repository produces, and the executables are no longer committed,
    # so what is on disk here is all there is until somebody asks for it again.
    Write-Host ("    artifacts: {0:N0} MB if every link were a copy" -f (Get-ArtifactBytes / 1MB))
    Write-Host "    '.\build\build.ps1 -Task prune' drops the staging and the unpacked folder"
}

<#
    Checks the assembled folder before anything is zipped or sent.

    Two complementary assertions, because neither one catches the failure on both kinds
    of filesystem.

    The first compares the names the operating system actually reports against the names
    that were meant to be there, matching case exactly. On Windows a file lost to a case
    insensitive overwrite disappears from the listing, so the missing name is detected.
    Test-Path cannot be used for this: it matches case insensitively and answers "yes"
    for a name that is really the other file.

    The second looks for two entries in the package that differ only in case. That pair
    cannot coexist on Windows, but it assembles perfectly happily on Linux and macOS, so
    this is the check that catches the fault on a developer machine rather than after a
    hospital has the folder.
#>
function Assert-PackageIsSound {
    param([string]$Package, [bool]$ExpectCli)

    $files = Get-ChildItem $Package -Recurse -File
    $present = $files | ForEach-Object { $_.FullName.Substring($Package.Length + 1) }

    $required = @('PdfToDicom.exe', 'LICENCE.txt', 'THIRD-PARTY-NOTICES.txt', 'READ ME FIRST.txt')
    if ($ExpectCli) { $required += (Join-Path 'Command line' 'pdftodicom.exe') }

    # -cne and -notcontains are case insensitive in PowerShell. The case sensitive
    # operator is the point of the check, so it has to be spelled out.
    $missing = $required | Where-Object { $present -cnotcontains $_ }

    if ($missing) {
        throw @"
The package is incomplete. These files were meant to be in it and are not:

    $($missing -join "`n    ")

If a file was copied but is missing under its own name, another file whose name
differs only in case has overwritten it. Windows cannot hold both.
"@
    }

    $collisions = $present |
        Group-Object { $_.ToLowerInvariant() } |
        Where-Object { $_.Count -gt 1 }

    if ($collisions) {
        $detail = ($collisions | ForEach-Object { $_.Group -join ' and ' }) -join '; '
        throw "The package holds names that differ only in case and so cannot both exist on Windows: $detail"
    }

    # Nothing written for the copyright holder may reach a hospital. These three discuss
    # signing keys, how to rebuild the product, and the customer. An earlier version
    # copied docs\* wholesale and shipped all three; naming them here means the next
    # document added to that folder has to be considered rather than inherited.
    $forbidden = @('BuildingItYourself', 'CodeSigning', 'ProtectingAgainstUnauthorisedDistribution',
                   'testworklistserver')

    $leaked = $forbidden | Where-Object { $name = $_; $present | Where-Object { $_ -like "*$name*" } }

    if ($leaked) {
        throw @"
The package contains material that must never be handed to a site:

    $($leaked -join "`n    ")
"@
    }
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

  Command line\
    pdftodicom.exe           For scripted or bulk conversion. Not needed
                             for ordinary use.

  Documentation\
    User Manual (English)                    For the people using it daily.
    Manual do Utilizador (Portugues)         The same, in Portuguese.
    DICOM Conformance Statement              For your PACS administrator.
    Technical Documentation                  For IT.
    Worklist Setup                           For IT, if your department has
                                             a modality worklist.
    Changelog                                What changed in each version.

  Samples\
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

    # A static check of the installer script, when a working Python is available. It
    # catches the common mistakes far more clearly than the compiler does.
    $validator = Join-Path $RepoRoot 'build\validate-installer-script.py'
    $python = Get-WorkingPython

    if ($python -and (Test-Path $validator)) {
        & $python $validator
        if ($LASTEXITCODE -ne 0) { throw 'The installer script did not pass validation.' }
    }
    elseif (-not $python) {
        Write-Host '    Python is not available, so the installer script was not checked first.'
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
    'prune'   { Invoke-Prune }
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
