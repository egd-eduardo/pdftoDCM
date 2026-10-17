<#
.SYNOPSIS
    Signs the built executables with an Authenticode signature.

.DESCRIPTION
    Signs the application, the command line tool and the installer using a certificate
    from the current user's certificate store.

    NOTHING NEEDS TO BE INSTALLED. Signing is done with PowerShell's own
    Set-AuthenticodeSignature, which is part of Windows. signtool.exe from the Windows
    SDK is not required and is not used.

    On a first run with -CreateSelfSigned it creates a self signed code signing
    certificate, uses it, and exports the public half so it can be given to anyone who
    wants to verify a file.

    WHAT A SELF SIGNED CERTIFICATE DOES AND DOES NOT DO

    It does NOT stop Windows warning that the publisher is unrecognised. Windows trusts a
    certificate only if it chains to a root in its trusted store, and a self signed
    certificate does not. SmartScreen will still ask for "More info", then "Run anyway",
    exactly as it does for an unsigned file.

    What it does buy is worth having:

      - a consistent publisher identity across every release,
      - tamper evidence: alter one byte of a signed file and the signature breaks,
      - verifiability: anyone holding the exported .cer can confirm a file is byte for
        byte what was signed,
      - timestamping, so signatures stay valid after the certificate expires.

    See docs/CodeSigning.md for what would remove the warning.

.PARAMETER Runtime
    Which architecture's output to sign. Default win-x64.

.PARAMETER CreateSelfSigned
    Create a self signed certificate if no suitable one exists. Run once.

.PARAMETER SubjectName
    Subject for a newly created certificate. Default "CN=Eduardo Dias".

.PARAMETER Thumbprint
    Use the certificate with this thumbprint instead of searching.

.PARAMETER NoTimestamp
    Sign without a timestamp. Only for when no timestamp server can be reached; the
    signature then stops being valid when the certificate expires.

.PARAMETER IfAvailable
    Sign when a certificate can be found, and say so and stop cleanly when none can.
    This is how the build signs by default: a machine that can sign does, and a machine
    that cannot still produces a complete build rather than failing.

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

    [switch]$NoTimestamp,

    [switch]$IfAvailable
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot  = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $RepoRoot 'artifacts'

# Tried in order. All are free and public. Several are listed because a signing run
# should not fail because one company's server is having a bad afternoon.
$TimestampServers = @(
    'http://timestamp.digicert.com',
    'http://timestamp.sectigo.com',
    'http://timestamp.globalsign.com/tsa/r6advanced1',
    'http://tsa.starfieldtech.com'
)

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# ---------------------------------------------------------------- preconditions

function Assert-Windows {
    <#
        The Authenticode cmdlets live in Microsoft.PowerShell.Security and exist only on
        Windows. Saying so plainly beats a "command not found" from inside a loop.
    #>
    if (-not (Get-Command Set-AuthenticodeSignature -ErrorAction SilentlyContinue)) {
        throw @'
Set-AuthenticodeSignature is not available.

Code signing is a Windows feature. This script must be run on Windows, in either
Windows PowerShell or PowerShell 7. Nothing needs to be installed beyond that.
'@
    }
}

# ---------------------------------------------------------------- certificate

function Get-SigningCertificate {
    if ($Thumbprint) {
        $certificate = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
        if (-not $certificate) { throw "No certificate with thumbprint $Thumbprint was found." }
        return $certificate
    }

    # Any certificate in the personal store that can actually sign code, has a private
    # key, and has not expired.
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

    if ($IfAvailable) {
        # Not an error. The caller asked to sign if it were possible, and it is not.
        return $null
    }

    if (-not $CreateSelfSigned) {
        throw @"
No code signing certificate was found in the current user's store.

To create a self signed one, run:

    .\build\sign.ps1 -CreateSelfSigned

Read docs/CodeSigning.md first: a self signed certificate does not remove the Windows
"unrecognised publisher" warning. It provides identity and tamper evidence, which are
useful for different reasons.
"@
    }

    Write-Step "Creating a self signed code signing certificate for $SubjectName"

    # Five years, because reissuing is a chore and a timestamped signature stays valid
    # after expiry anyway.
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $SubjectName `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(15)

    Write-Host "    created, thumbprint $($certificate.Thumbprint)"

    $publicPath = Join-Path $RepoRoot 'code-signing-public.cer'
    Export-Certificate -Cert $certificate -FilePath $publicPath -Force | Out-Null

    Write-Host "    public half exported to $publicPath"
    Write-Host ''
    Write-Host '    BACK THIS UP. Losing the certificate means future releases are' -ForegroundColor Yellow
    Write-Host '    signed by a different identity, which to anyone checking looks' -ForegroundColor Yellow
    Write-Host '    like a different publisher. To export it with its private key:' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "        Export-PfxCertificate -Cert Cert:\CurrentUser\My\$($certificate.Thumbprint) ``" -ForegroundColor Yellow
    Write-Host '            -FilePath code-signing-backup.pfx -Password (Read-Host -AsSecureString)' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '    Never commit the .pfx. The repository ignores *.pfx and *.cer already.' -ForegroundColor Yellow

    return $certificate
}

# ---------------------------------------------------------------- signing

function Invoke-SignFile {
    param(
        [string]$Path,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $name = Split-Path -Leaf $Path

    if ($NoTimestamp) {
        Write-Host "    signing $name (no timestamp)"

        return Set-AuthenticodeSignature `
            -FilePath $Path `
            -Certificate $Certificate `
            -HashAlgorithm SHA256
    }

    # A timestamp is what lets a signature outlive the certificate, so it is worth
    # trying every server before giving up on it.
    #
    # Success is judged on TimeStamperCertificate being populated, not on the call
    # returning without error. Set-AuthenticodeSignature can sign a file successfully and
    # still fail to timestamp it, which would otherwise be mistaken for a good result and
    # produce a signature that quietly dies with the certificate.
    foreach ($server in $TimestampServers) {
        try {
            $result = Set-AuthenticodeSignature `
                -FilePath $Path `
                -Certificate $Certificate `
                -HashAlgorithm SHA256 `
                -TimestampServer $server `
                -ErrorAction Stop

            if ($result.TimeStamperCertificate) {
                Write-Host "    signing $name (timestamped by $server)"
                return $result
            }

            Write-Host "    $server signed but did not timestamp, trying the next one" -ForegroundColor DarkGray
        }
        catch {
            Write-Host "    $server could not be reached, trying the next one" -ForegroundColor DarkGray
        }
    }

    throw @"
No timestamp server could be reached, so '$name' was not signed.

Check the internet connection and run this again. A timestamp is what keeps a signature
valid after the certificate expires, so it is worth getting.

To sign without one anyway, understanding that the signature will stop being valid when
the certificate expires:

    .\build\sign.ps1 -Runtime $Runtime -NoTimestamp
"@
}

# ---------------------------------------------------------------- main

Assert-Windows

$certificate = Get-SigningCertificate

if (-not $certificate) {
    # Reached only with -IfAvailable. One line rather than a page: a build that cannot
    # sign is still a build, and docs/CodeSigning.md says what a signature does buy.
    Write-Host '    nothing was signed: no code signing certificate is installed for this user'
    Write-Host '    create one with ".\build\sign.ps1 -CreateSelfSigned"; see docs/CodeSigning.md'
    exit 0
}

Write-Host "    signing as $($certificate.Subject), expires $($certificate.NotAfter.ToString('yyyy-MM-dd'))"

# The application and the command line tool for this architecture, plus any installer
# that has been built. Everything else is embedded inside these, so there is nothing
# further to sign.
$targets = @(
    (Join-Path $Artifacts "$Runtime\app\PdfToDicom.exe"),
    (Join-Path $Artifacts "$Runtime\cli\pdftodicom.exe")
) | Where-Object { Test-Path $_ }

$installers = Get-ChildItem $Artifacts -Filter 'PdfToDicom-Setup*.exe' -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty FullName

if ($installers) { $targets += $installers }

if (-not $targets) {
    if ($IfAvailable) {
        Write-Host "    nothing has been published for $Runtime yet, so there was nothing to sign"
        exit 0
    }

    throw @"
Nothing to sign. Build first:

    .\build\build.ps1 -Runtimes $Runtime
"@
}

Write-Step "Signing $($targets.Count) file(s)"

foreach ($target in $targets) {
    $result = Invoke-SignFile -Path $target -Certificate $certificate

    # A self signed certificate that is not in the trusted store reports UnknownError.
    # The signature itself is intact; Windows simply does not trust the issuer. That is
    # the documented trade-off for this configuration, so it is not a failure.
    if ($result.Status -notin @('Valid', 'UnknownError')) {
        throw "Signing '$(Split-Path -Leaf $target)' failed: $($result.Status) - $($result.StatusMessage)"
    }
}

# ---------------------------------------------------------------- verify

Write-Step 'Verifying the signatures'

foreach ($target in $targets) {
    $signature = Get-AuthenticodeSignature -FilePath $target
    $name = Split-Path -Leaf $target

    $timestamp = if ($signature.TimeStamperCertificate) { 'timestamped' } else { 'not timestamped' }

    switch ($signature.Status) {
        'Valid' {
            Write-Host "    [OK]   $name  signed and trusted by this machine, $timestamp" -ForegroundColor Green
        }
        'UnknownError' {
            Write-Host "    [OK]   $name  signed, $timestamp; issuer not trusted by this machine" -ForegroundColor Yellow
        }
        'NotSigned' {
            Write-Host "    [FAIL] $name  carries no signature" -ForegroundColor Red
            throw "'$name' was not signed."
        }
        default {
            Write-Host "    [FAIL] $name  $($signature.Status)" -ForegroundColor Red
            throw "The signature on '$name' did not verify: $($signature.StatusMessage)"
        }
    }
}

Write-Step 'Done'
Write-Host ''
Write-Host '  Every file is signed.'
Write-Host ''
Write-Host '  Windows will still warn that the publisher is unrecognised, because a self'
Write-Host '  signed certificate is not trusted by default. That is expected and was the'
Write-Host '  choice made. The signature proves the file is unmodified and identifies you'
Write-Host '  as its author to anyone who checks its properties.'
Write-Host ''
Write-Host '  To check a file by hand:'
Write-Host ''
Write-Host '      Get-AuthenticodeSignature .\PdfToDicom.exe | Format-List'
Write-Host ''
Write-Host '  See docs/CodeSigning.md for what would remove the warning.'
Write-Host ''
