<#
.SYNOPSIS
    Signs the built executables with an Authenticode signature.

.DESCRIPTION
    Signs the application and, when present, the installer, using a certificate
    from the current user's certificate store.

    On a first run with -CreateSelfSigned it creates a self signed code signing
    certificate, uses it, and exports the public half so it can be given to anyone
    who wants to verify a file.

    WHAT A SELF SIGNED CERTIFICATE DOES AND DOES NOT DO

    It does NOT stop Windows warning that the publisher is unrecognised. Windows
    trusts a certificate only if it chains to a root in its trusted store, and a
    self signed certificate does not. SmartScreen will still ask for
    "More info", then "Run anyway", exactly as it does for an unsigned file.

    What it does buy is worth having:

      - a consistent publisher identity across every release,
      - tamper evidence: alter one byte of a signed file and the signature breaks,
      - verifiability: anyone holding the exported .cer can confirm a file is
        byte for byte what was signed, which matters if a copy of unknown
        provenance ever turns up,
      - timestamping, so signatures stay valid after the certificate expires.

    To remove the warning entirely, either the certificate must be installed into
    Trusted Publishers on each machine, or a publicly trusted certificate must be
    bought. See docs/CodeSigning.md.

.PARAMETER Runtime
    Which architecture's output to sign. Default win-x64.

.PARAMETER CreateSelfSigned
    Create a self signed certificate if no suitable one exists. Run once.

.PARAMETER SubjectName
    Subject for a newly created certificate. Default "CN=Eduardo Dias".

.PARAMETER Thumbprint
    Use the certificate with this thumbprint instead of searching.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server. Default DigiCert's, which is free and public.

.EXAMPLE
    .\build\sign.ps1 -CreateSelfSigned
    .\build\sign.ps1 -Runtime win-arm64
#>

[CmdletBinding()]
param(
    [ValidateSet('win-x86', 'win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$CreateSelfSigned,

    [string]$SubjectName = 'CN=Eduardo Dias',

    [string]$Thumbprint,

    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot  = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $RepoRoot 'artifacts'

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# ---------------------------------------------------------------- signtool

function Find-SignTool {
    <#
        signtool.exe ships with the Windows SDK, which is installed alongside
        Visual Studio and separately. The newest version found wins, since older
        ones lack the RFC 3161 timestamp switches used below.
    #>
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }

    $candidates = foreach ($root in $roots) {
        Get-ChildItem -Path $root -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -match '\\(x64|x86|arm64)$' }
    }

    $chosen = $candidates | Sort-Object -Property FullName -Descending | Select-Object -First 1

    if (-not $chosen) {
        throw @'
signtool.exe was not found.

It ships with the Windows SDK. Install either:
  - Visual Studio 2022 with the ".NET desktop development" workload, or
  - the Windows SDK on its own, from
    https://developer.microsoft.com/windows/downloads/windows-sdk/
'@
    }

    return $chosen.FullName
}

# ---------------------------------------------------------------- certificate

function Get-SigningCertificate {
    if ($Thumbprint) {
        $certificate = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
        if (-not $certificate) { throw "No certificate with thumbprint $Thumbprint was found." }
        return $certificate
    }

    # Any certificate in the personal store that can actually sign code, with a
    # private key, and that has not expired.
    $existing = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object {
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date) -and
            ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' })
        } |
        Sort-Object NotAfter -Descending

    if ($existing) {
        return $existing[0]
    }

    if (-not $CreateSelfSigned) {
        throw @"
No code signing certificate was found in the current user's store.

To create a self signed one, run:

    .\build\sign.ps1 -CreateSelfSigned

Read docs/CodeSigning.md first: a self signed certificate does not remove the
Windows "unrecognised publisher" warning. It provides identity and tamper
evidence, which are useful for different reasons.
"@
    }

    Write-Step "Creating a self signed code signing certificate for $SubjectName"

    # Five years, because reissuing is a chore and a timestamped signature stays
    # valid after expiry anyway.
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $SubjectName `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(5)

    Write-Host "    created, thumbprint $($certificate.Thumbprint)"

    $publicPath = Join-Path $RepoRoot 'code-signing-public.cer'
    Export-Certificate -Cert $certificate -FilePath $publicPath -Force | Out-Null

    Write-Host "    public half exported to $publicPath"
    Write-Host ''
    Write-Host '    Back up the certificate. Losing it means future releases are' -ForegroundColor Yellow
    Write-Host '    signed by a different identity, which looks like a different' -ForegroundColor Yellow
    Write-Host '    publisher. To export it with its private key:' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "        Export-PfxCertificate -Cert Cert:\CurrentUser\My\$($certificate.Thumbprint) ``" -ForegroundColor Yellow
    Write-Host '            -FilePath code-signing-backup.pfx -Password (Read-Host -AsSecureString)' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '    Never commit the .pfx. The repository ignores *.pfx already.' -ForegroundColor Yellow

    return $certificate
}

# ---------------------------------------------------------------- signing

function Invoke-Sign {
    param(
        [string]$SignTool,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string[]]$Files
    )

    foreach ($file in $Files) {
        Write-Host "    signing $(Split-Path -Leaf $file)"

        # /fd and /td both SHA256: SHA-1 signatures are no longer accepted.
        # /tr timestamps, so the signature outlives the certificate.
        & $SignTool sign `
            /sha1 $Certificate.Thumbprint `
            /fd SHA256 `
            /tr $TimestampUrl `
            /td SHA256 `
            /d 'PDF to DICOM' `
            $file

        if ($LASTEXITCODE -ne 0) {
            throw "Signing '$file' failed. If the timestamp server was unreachable, try again."
        }
    }
}

# ---------------------------------------------------------------- main

$signTool = Find-SignTool
Write-Host "    using $signTool"

$certificate = Get-SigningCertificate
Write-Host "    signing as $($certificate.Subject), expires $($certificate.NotAfter.ToString('yyyy-MM-dd'))"

# The application, and the installer when one has been built.
$targets = @(
    (Join-Path $Artifacts "$Runtime\app\PdfToDicom.exe"),
    (Join-Path $Artifacts "$Runtime\cli\pdftodicom.exe")
) | Where-Object { Test-Path $_ }

$installers = Get-ChildItem $Artifacts -Filter 'PdfToDicom-Setup*.exe' -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty FullName

$targets += $installers

if (-not $targets) {
    throw @"
Nothing to sign. Build first:

    .\build\build.ps1 -Runtime $Runtime
"@
}

Write-Step "Signing $($targets.Count) file(s)"
Invoke-Sign -SignTool $signTool -Certificate $certificate -Files $targets

# ---------------------------------------------------------------- verify

Write-Step 'Verifying the signatures'

foreach ($file in $targets) {
    $signature = Get-AuthenticodeSignature -FilePath $file
    $name = Split-Path -Leaf $file

    # "UnknownError" is the expected status for a self signed certificate that is
    # not in the trusted store. The signature itself is intact; Windows simply
    # does not trust the issuer. That is the documented trade-off, so it is
    # reported as a note rather than a failure.
    switch ($signature.Status) {
        'Valid' {
            Write-Host "    [OK]   $name  signed and trusted by this machine" -ForegroundColor Green
        }
        'UnknownError' {
            Write-Host "    [OK]   $name  signed; issuer not trusted by this machine" -ForegroundColor Yellow
        }
        default {
            Write-Host "    [FAIL] $name  $($signature.Status)" -ForegroundColor Red
            throw "The signature on '$name' did not verify."
        }
    }
}

Write-Step 'Done'
Write-Host ''
Write-Host '  Every file is signed and timestamped.'
Write-Host ''
Write-Host '  Windows will still warn that the publisher is unrecognised, because a'
Write-Host '  self signed certificate is not trusted by default. That is expected.'
Write-Host '  The signature proves the file is unmodified and identifies you as its'
Write-Host '  author to anyone who checks its properties.'
Write-Host ''
Write-Host '  See docs/CodeSigning.md for what would remove the warning.'
Write-Host ''
