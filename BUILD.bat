@echo off
REM ===========================================================================
REM  PDF to DICOM - one click build
REM
REM  Double click this file. It checks what is needed, builds and tests the
REM  application for this computer's architecture, and assembles the folder
REM  you hand to a hospital: the executable together with the licence, the
REM  third party notices, the manuals and the samples.
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
set RID=win-x64
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set RID=win-arm64
if /i "%PROCESSOR_ARCHITECTURE%"=="x86"   set RID=win-x86

echo   Architecture  %PROCESSOR_ARCHITECTURE%  -^>  !RID!

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
"!PS!" -NoProfile -ExecutionPolicy Bypass -File "build\build.ps1" -Runtime !RID!
if errorlevel 1 goto :failed

REM ------------------------------------------------------------- Where it is
REM Found on disk rather than by asking for the version number. Nesting quotes
REM inside a for /f is a well known way to break a batch file, and the folder
REM is right there to be looked at.
set PKG=
for /d %%d in ("artifacts\PdfToDicom-*-!RID!") do set PKG=%CD%\%%d

if "!PKG!"=="" (
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
echo   The folder to hand over:
echo.
echo     !PKG!
echo.
echo   It contains the application, the licence, the third party notices,
echo   the manuals in English and Portuguese, the conformance statement for
echo   the PACS administrator, and sample files. A zip of the same is beside
echo   it, ready to send.
echo.

REM --------------------------------------------------------------- Signing
REM Offered rather than done. Creating a certificate is a one time decision
REM and should not happen as a side effect of pressing build.
"!PS!" -NoProfile -ExecutionPolicy Bypass -Command "if (Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }) }) { exit 0 } else { exit 1 }" >nul 2>&1
if errorlevel 1 goto :nocert

echo   A code signing certificate was found on this computer.
echo.
choice /c YN /n /m "   Sign the files now? [Y/N] "
if errorlevel 2 goto :opened
"!PS!" -NoProfile -ExecutionPolicy Bypass -File "build\sign.ps1" -Runtime !RID!
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
echo   See docs\CodeSigning.md.
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
