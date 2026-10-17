@echo off
REM ===========================================================================
REM  PDF to DICOM - one click build
REM
REM  Double click this file. It checks what is needed, builds and tests the
REM  application for 64 bit and for ARM, and assembles the folder you hand to
REM  a hospital: the executable together with the licence, the third party
REM  notices, the manuals and the samples.
REM
REM  Both architectures, because the machine this is built on is not always
REM  the machine it runs on: the sites are 64 bit and the testing is done on a
REM  Windows on ARM virtual machine.
REM
REM  The build itself is done by build\build.ps1, so there is one
REM  implementation rather than two that drift apart. This file is the front
REM  door: it checks prerequisites and explains anything that goes wrong.
REM ===========================================================================

setlocal enabledelayedexpansion
cd /d "%~dp0"

echo.
echo  ===========================================================
echo   PDF to DICOM - build
echo  ===========================================================
echo.

REM --------------------------------------------------------------- PowerShell
REM Windows PowerShell is present on every supported Windows. PowerShell 7 is
REM accepted too, for anyone who has installed it.
set PS=
where powershell.exe >nul 2>&1 && set PS=powershell.exe
if "!PS!"=="" (
    where pwsh.exe >nul 2>&1 && set PS=pwsh.exe
)
if "!PS!"=="" goto :nopowershell

REM --------------------------------------------------------------- .NET SDK
where dotnet >nul 2>&1
if errorlevel 1 goto :nosdk

for /f "tokens=*" %%v in ('dotnet --version 2^>nul') do set SDKVER=%%v
if "!SDKVER!"=="" goto :nosdk

echo   .NET SDK      !SDKVER!
echo !SDKVER! | findstr /b "8. 9. 10." >nul
if errorlevel 1 (
    echo.
    echo   NOTE: this project targets .NET 8 and version !SDKVER! was found.
    echo   The build may still work. If it does not, install the .NET 8 SDK.
    echo.
)

REM ------------------------------------------------------- Which architecture
REM Both are built. This only decides which of the two finished folders is
REM pointed at as the one for THIS computer.
set RID=win-x64
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set RID=win-arm64
if /i "%PROCESSOR_ARCHITECTURE%"=="x86"   set RID=win-x86

echo   Architecture  %PROCESSOR_ARCHITECTURE%  -^>  !RID!  ^(this computer^)
echo   Building      win-x64 and win-arm64

REM ------------------------------------------------------------ Optional bits
REM Neither is required. Saying which are present avoids surprise at the end
REM when the installer or the binding check is quietly absent.
REM Asked to report its version rather than merely looked for. Windows ships App
REM execution aliases for python.exe and python3.exe: stubs that are found on the
REM PATH but only print a message about the Microsoft Store and exit non zero.
REM Checking presence alone reports Python as available and then the build fails.
set HAVEPY=no
for %%p in (python3.exe python.exe py.exe) do (
    if "!HAVEPY!"=="no" (
        %%p --version >nul 2>&1 && set HAVEPY=yes
    )
)

set HAVEINNO=no
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set HAVEINNO=yes
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set HAVEINNO=yes

if "!HAVEPY!"=="yes"   (echo   Python        found, so the XAML checks will run) else (echo   Python        not available, so the XAML checks are skipped. Optional)
if "!HAVEINNO!"=="yes" (echo   Inno Setup    found, so an installer will be built) else (echo   Inno Setup    not found, so no installer will be built)

echo.
echo   Building. The first run takes a few minutes because the libraries have
echo   to be downloaded once. Later runs are much faster.
echo.

REM ------------------------------------------------------------------- Build
REM ExecutionPolicy Bypass applies to this one invocation only. It does not
REM change any setting on this computer.
REM
REM No -Runtime: build.ps1 builds 64 bit and ARM by default. It also signs
REM what it builds when a code signing certificate is installed for this
REM user, so there is nothing to ask about afterwards.
"!PS!" -NoProfile -ExecutionPolicy Bypass -File "build\build.ps1"
if errorlevel 1 goto :failed

REM ------------------------------------------------------------- Where it is
REM Found on disk rather than by asking for the version number. Nesting quotes
REM inside a for /f is a well known way to break a batch file, and the folders
REM are right there to be looked at.
set PKG=
set PKG64=
set PKGARM=
for /d %%d in ("artifacts\PdfToDicom-*-win-x64")   do set PKG64=%CD%\%%d
for /d %%d in ("artifacts\PdfToDicom-*-win-arm64") do set PKGARM=%CD%\%%d
for /d %%d in ("artifacts\PdfToDicom-*-!RID!")     do set PKG=%CD%\%%d

REM An x86 computer is the one case where this computer's architecture is not
REM among the two that were built. The 64 bit folder is the one to point at,
REM and 32 bit Windows is rare enough to be worth a line rather than a build.
if "!PKG!"=="" set PKG=!PKG64!

if "!PKG64!"=="" (
    echo.
    echo   The build reported success but no package folder was found under
    echo   artifacts. Something is wrong; the messages above may say what.
    echo.
    pause
    exit /b 1
)

echo.
echo  ===========================================================
echo   Done.
echo  ===========================================================
echo.
echo   The folder to hand over, for a 64 bit machine, which is what the
echo   sites run:
echo.
echo     !PKG64!
echo.
echo   And the one for a Windows on ARM machine, such as the virtual machine
echo   this is tested on:
echo.
echo     !PKGARM!
echo.
echo   This computer is !RID!, so to run it here use:
echo.
echo     !PKG!
echo.
echo   Each folder contains the application, the licence, the third party
echo   notices, the manuals in English and Portuguese, the conformance
echo   statement for the PACS administrator, and sample files. No zip is made:
echo   copy the folder to the machine, or onto a stick, as it stands.
echo.

REM --------------------------------------------------------------- Signing
REM Reported rather than offered. The build signs by default when there is a
REM certificate to sign with, so asking again here would either repeat work
REM that is already done or imply it had not been.
REM
REM Creating a certificate is still a one time decision and still does not
REM happen as a side effect of pressing build.
"!PS!" -NoProfile -ExecutionPolicy Bypass -Command "if (Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }) }) { exit 0 } else { exit 1 }" >nul 2>&1
if errorlevel 1 goto :nocert

echo   The files were signed with the code signing certificate installed for
echo   this user. Right click the executable, Properties, Digital Signatures
echo   to see it.
echo.
goto :opened

:nocert
echo   The files are not signed. No code signing certificate was found.
echo.
echo   Signing is optional. It will not stop Windows warning that the
echo   publisher is unrecognised, but it does prove a file is unaltered and
echo   identifies you as its author. To set it up once:
echo.
echo       powershell -ExecutionPolicy Bypass -File build\sign.ps1 -CreateSelfSigned
echo.
echo   Every build after that signs itself. See docs\CodeSigning.md.
echo.

:opened
choice /c YN /n /m "   Open the folder now? [Y/N] "
if errorlevel 2 goto :end
if exist "!PKG!" start "" "!PKG!"
goto :end

REM --------------------------------------------------------------- Failures
:nopowershell
echo.
echo   PowerShell was not found on this computer, which is unexpected: it is
echo   part of Windows. This file cannot continue without it.
echo.
pause
exit /b 1

:nosdk
echo.
echo   The .NET SDK was not found on this computer.
echo.
echo   Install the .NET 8 SDK, then run this file again:
echo.
echo       https://dotnet.microsoft.com/download/dotnet/8.0
echo.
echo   Choose the SDK, not the Runtime, and pick the installer matching
echo   this computer: %PROCESSOR_ARCHITECTURE%.
echo.
echo   It is an ordinary Windows installer. Accept the defaults, then close
echo   this window and run this file again.
echo.
pause
exit /b 1

:failed
echo.
echo   The build did not finish. The messages above say why.
echo.
echo   Common causes:
echo.
echo     - No internet connection on the first run, because the libraries
echo       have to be downloaded once.
echo     - A test failed, which means something is wrong with the code
echo       rather than with this computer. Nothing was produced, and it is
echo       safe to try again.
echo.
echo   Python not being installed is NOT a cause. It is optional, and the
echo   build skips the XAML checks and carries on without it.
echo.
pause
exit /b 1

:end
echo.
pause
endlocal
