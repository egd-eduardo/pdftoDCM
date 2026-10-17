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

.PARAMETER Pfx
    A certificate file, with its private key, instead of one from the certificate store.
    Also read from the SIGN_PFX environment variable. Without either, a file in
    %USERPROFILE%\.pdftodicom is used when one is there: codesign.pfx, codesign.p12,
    certificate.pfx or certificate.p12, in that order. That is the same folder and the
    same names build/build.sh reads on a Mac, so one instruction covers both machines.

.PARAMETER PfxPassword
    The password for that file. Prefer leaving it in codesign.password beside the
    certificate, or being asked for it; a password on a command line is visible to
    anything that can list processes.

.PARAMETER Describe
    Report what signing would do on this machine, and why, then stop. Nothing is signed
    and nothing is created. This is what answers "I put the certificate in the folder and
    nothing happened".

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

    [switch]$IfAvailable,

    [string]$Pfx,

    [string]$PfxPassword,

    [switch]$Describe
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot  = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $RepoRoot 'artifacts'

<#
    Where a certificate FILE is looked for, and under which names.

    The same folder and the same names build/build.sh reads on a Mac, so "put the
    certificate in ~/.pdftodicom" is one instruction that covers both machines. It had
    better be, because the first version of this read only the Windows certificate store:
    a .pfx placed in that folder on a Windows machine was ignored in silence, which is
    exactly the kind of thing somebody spends an evening on.

    Several names, because an export rarely comes out called what a script expects. macOS
    Keychain writes .p12, Windows writes .pfx, and both spellings mean the same container.
#>
$SigningFolder = if ($env:SIGN_DEFAULT_DIR) { $env:SIGN_DEFAULT_DIR } else { Join-Path $HOME '.pdftodicom' }

$CertificateCandidates = @(
    (Join-Path $SigningFolder 'codesign.pfx'),
    (Join-Path $SigningFolder 'codesign.p12'),
    (Join-Path $SigningFolder 'certificate.pfx'),
    (Join-Path $SigningFolder 'certificate.p12')
)

$PasswordCandidates = @(
    (Join-Path $SigningFolder 'codesign.password'),
    (Join-Path $SigningFolder 'password.txt')
)

<#
    The certificate file this run will use, or $null.

    An explicit -Pfx, then SIGN_PFX, then the folder. A file the person put there on
    purpose outranks whatever happens to be in the certificate store, which may have been
    left by a self signed certificate created months ago; the report says which was used
    and whether the other exists, so neither is a silent choice.
#>
function Find-CertificateFile {
    if ($Pfx) {
        if (-not (Test-Path $Pfx)) { throw "The certificate file '$Pfx' does not exist." }
        return (Resolve-Path $Pfx).Path
    }

    if ($env:SIGN_PFX) {
        if (-not (Test-Path $env:SIGN_PFX)) {
            throw "SIGN_PFX is set to '$($env:SIGN_PFX)', which is not a file."
        }
        return (Resolve-Path $env:SIGN_PFX).Path
    }

    foreach ($candidate in $CertificateCandidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    return $null
}

<#
    The password for a certificate file, as a SecureString.

    From -PfxPassword, then SIGN_PASSWORD_FILE, then a file beside the certificate, then
    the keyboard without being echoed. A password on a command line is visible to anything
    that can list processes, which is why it is the least preferred of the four.
#>
function Get-CertificatePassword {
    if ($PfxPassword) {
        return (ConvertTo-SecureString $PfxPassword -AsPlainText -Force)
    }

    $file = $null

    if ($env:SIGN_PASSWORD_FILE) {
        if (-not (Test-Path $env:SIGN_PASSWORD_FILE)) {
            throw "SIGN_PASSWORD_FILE is set to '$($env:SIGN_PASSWORD_FILE)', which is not a file."
        }
        $file = $env:SIGN_PASSWORD_FILE
    }
    else {
        foreach ($candidate in $PasswordCandidates) {
            if (Test-Path $candidate) { $file = $candidate; break }
        }
    }

    if ($file) {
        Write-Host "    password read from $file"

        # Trimmed, because a password file written by an editor ends with a newline and
        # the newline is not part of the password.
        $text = (Get-Content $file -Raw).Trim()
        return (ConvertTo-SecureString $text -AsPlainText -Force)
    }

    return (Read-Host -Prompt '    Certificate password (not shown)' -AsSecureString)
}

<#
    Loads a certificate from a file.

    Not with EphemeralKeySet, deliberately. An ephemeral key never reaches a key
    container, and Set-AuthenticodeSignature needs one to sign with, so the signature
    would fail on a certificate that had loaded perfectly.
#>
function Import-CertificateFile {
    param([string]$Path)

    $password = Get-CertificatePassword

    try {
        return [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($Path, $password)
    }
    catch {
        throw "The certificate '$Path' could not be opened: $($_.Exception.Message)"
    }
}

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

<#
    Code signing certificates in this user's personal store that could actually be used:
    able to sign code, holding a private key, and not expired.

    Returns nothing rather than failing where there is no such store, so that the report
    below can run on a Mac and still say something useful about the certificate file.
#>
function Get-StoreCertificates {
    if (-not (Test-Path 'Cert:\CurrentUser\My')) { return @() }

    return @(
        Get-ChildItem Cert:\CurrentUser\My |
            Where-Object {
                $_.HasPrivateKey -and
                $_.NotAfter -gt (Get-Date) -and
                ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' })
            } |
            Sort-Object NotAfter -Descending
    )
}

function Get-SigningCertificate {
    if ($Thumbprint) {
        $certificate = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
        if (-not $certificate) { throw "No certificate with thumbprint $Thumbprint was found." }
        return $certificate
    }

    # A file first. Somebody who put a certificate in the folder meant it to be used, and
    # the store may hold a self signed certificate created months ago and forgotten.
    $file = Find-CertificateFile

    if ($file) {
        Write-Host "    certificate file $file"
        return Import-CertificateFile -Path $file
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
No code signing certificate was found.

Looked in the certificate store of the current user, and for these files:

$($CertificateCandidates -join "`n")

'.\build\sign.ps1 -Describe' explains this machine's state in full.

To use a certificate you already have, put it in $SigningFolder as codesign.pfx, or
name it directly:

    .\build\sign.ps1 -Pfx C:\path\to\certificate.pfx

To create a self signed one instead:

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

<#
    Reports what signing would do on this machine, and why, without signing anything.

    Exists because "I put the certificate in the folder and nothing happened" is not a
    question a person should have to read a script to answer. It names every path that
    was examined, lists what is in the folder when the expected name is not there, and
    shows what the certificate store holds.
#>
function Show-SigningState {
    Write-Step 'Signing: what this machine would do'

    if (-not (Get-Command Set-AuthenticodeSignature -ErrorAction SilentlyContinue)) {
        Write-Host '    NOTE: this is not Windows, so nothing here could sign anything. The'
        Write-Host '    certificate file is still reported, because that part is the same.'
        Write-Host '    On a Mac, use "build/build.sh signing" instead.'
        Write-Host ''
    }

    Write-Host "    certificate folder : $SigningFolder"

    if ($env:SIGN_PFX)           { Write-Host "    SIGN_PFX           : $($env:SIGN_PFX)" }
    if ($env:SIGN_PASSWORD_FILE) { Write-Host "    SIGN_PASSWORD_FILE : $($env:SIGN_PASSWORD_FILE)" }

    $file = $null

    try { $file = Find-CertificateFile }
    catch { Write-Host "    certificate file   : $($_.Exception.Message)" -ForegroundColor Yellow }

    $store = @(Get-StoreCertificates)

    if ($file) {
        Write-Host "    certificate file   : $file  <- this is what would be used"
    }
    else {
        Write-Host '    certificate file   : none found'
        Write-Host ''
        Write-Host '    These are the names looked for, in this order:'
        $CertificateCandidates | ForEach-Object { Write-Host "        $_" }

        if (Test-Path $SigningFolder) {
            $entries = @(Get-ChildItem $SigningFolder -Force -ErrorAction SilentlyContinue)

            Write-Host ''
            Write-Host '    The folder exists. What is in it:'

            if ($entries) {
                $entries | ForEach-Object { Write-Host "        $($_.Name)" }
                Write-Host ''
                Write-Host '    If the certificate is one of those, rename it to codesign.pfx, or name it:'
                Write-Host ''
                Write-Host "        .\build\sign.ps1 -Pfx $SigningFolder\<the file>"
            }
            else {
                Write-Host '        (empty)'
            }
        }
        else {
            Write-Host ''
            Write-Host '    That folder does not exist on this machine. To create it:'
            Write-Host ''
            Write-Host "        mkdir $SigningFolder"
            Write-Host "        copy C:\path\to\certificate.pfx $SigningFolder\codesign.pfx"
        }
    }

    Write-Host ''

    if ($store) {
        Write-Host "    certificate store  : $($store.Count) usable code signing certificate(s)"
        $store | ForEach-Object {
            Write-Host "        $($_.Subject), expires $($_.NotAfter.ToString('yyyy-MM-dd')), $($_.Thumbprint)"
        }

        if ($file) {
            Write-Host ''
            Write-Host '    The file above wins: a certificate put in the folder on purpose outranks'
            Write-Host '    whatever is in the store. Use -Thumbprint to insist on a store certificate.'
        }
        else {
            Write-Host '        the first of those is what would be used'
        }
    }
    else {
        Write-Host '    certificate store  : no usable code signing certificate'
    }

    if (-not $file -and -not $store) {
        Write-Host ''
        Write-Host '    Nothing would be signed. The build still completes; an unsigned executable'
        Write-Host '    runs. See docs/CodeSigning.md.'
    }

    Write-Host ''
}

# Before the Windows check on purpose: a diagnostic that refuses to run where signing is
# unavailable is not much of a diagnostic, and the file half of the answer is the same
# everywhere.
if ($Describe) {
    Show-SigningState
    exit 0
}

Assert-Windows

$certificate = Get-SigningCertificate

if (-not $certificate) {
    # Reached only with -IfAvailable. One line rather than a page: a build that cannot
    # sign is still a build, and docs/CodeSigning.md says what a signature does buy.
    Write-Host '    nothing was signed: no code signing certificate was found'
    Write-Host "    not in the store, and not as a file in $SigningFolder"
    Write-Host '    ".\build\sign.ps1 -Describe" says exactly what was looked for'
    exit 0
}

Write-Host "    signing as $($certificate.Subject), expires $($certificate.NotAfter.ToString('yyyy-MM-dd'))"

# The application and the command line tool for this architecture, plus any installer
# that has been built. Everything else is embedded inside these, so there is nothing
# further to sign.
#
# The whole pipeline is wrapped, not just the list being filtered: a pipeline that emits
# one object assigns that object, not a list of one, and Windows PowerShell refuses to
# read .Count from anything that is not really a collection once Set-StrictMode is on.
# An application only build with no installer yet leaves exactly one file here, which is
# the ordinary case, so the wrapping is what keeps the count below from failing the run.
$targets = @(
    @(
        (Join-Path $Artifacts "$Runtime\app\PdfToDicom.exe"),
        (Join-Path $Artifacts "$Runtime\cli\pdftodicom.exe")
    ) | Where-Object { Test-Path $_ }
)

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
