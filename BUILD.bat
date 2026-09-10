@echo off
REM ===========================================================================
REM  PDF to DICOM - one click build
REM
REM  Double click this file. It checks what is needed, builds the application
REM  for this computer's architecture, and tells you where the result is.
REM
REM  Nothing here needs programming knowledge. If something is missing, it says
REM  what to install and where to get it, then stops.
REM ===========================================================================

setlocal enabledelayedexpansion
cd /d "%~dp0"

echo.
echo  ===========================================================
echo   PDF to DICOM - building the application
echo  ===========================================================
echo.

REM --------------------------------------------------------------- .NET SDK
where dotnet >nul 2>&1
if errorlevel 1 goto :nosdk

for /f "tokens=*" %%v in ('dotnet --version 2^>nul') do set SDKVER=%%v
if "!SDKVER!"=="" goto :nosdk

echo   Using .NET SDK !SDKVER!
echo !SDKVER! | findstr /b "8. 9. 10." >nul
if errorlevel 1 (
    echo.
    echo   WARNING: this project targets .NET 8. Version !SDKVER! was found.
    echo   The build may still work. If it does not, install the .NET 8 SDK.
    echo.
)

REM ------------------------------------------------------- Which architecture
set RID=win-x64
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set RID=win-arm64
if /i "%PROCESSOR_ARCHITECTURE%"=="x86"   set RID=win-x86

echo   Building for %PROCESSOR_ARCHITECTURE%  (%RID%)
echo.
echo   This takes a few minutes the first time, because it downloads the
echo   libraries it needs. Later builds are much faster.
echo.

REM ------------------------------------------------------------------- Build
echo   [1 of 3]  Restoring libraries...
dotnet restore "PDF to DICOM.sln" --nologo -v quiet
if errorlevel 1 goto :failed

echo   [2 of 3]  Running the tests...
dotnet test tests\PdfToDicom.Core.Tests\PdfToDicom.Core.Tests.csproj -c Release --nologo -v quiet
if errorlevel 1 goto :testsfailed

echo   [3 of 3]  Building the application...
dotnet publish src\PdfToDicom.App\PdfToDicom.App.csproj -c Release -r %RID% -o "artifacts\%RID%\app" --nologo -v quiet
if errorlevel 1 goto :failed

echo.
echo  ===========================================================
echo   Done.
echo  ===========================================================
echo.
echo   The application is here:
echo.
echo     %CD%\artifacts\%RID%\app\PdfToDicom.exe
echo.
echo   It is self contained. You can copy that one file to any
echo   Windows machine of this architecture and run it. Nothing
echo   needs to be installed on that machine.
echo.
choice /c YN /n /m "   Open the folder now? [Y/N] "
if errorlevel 2 goto :end
start "" "%CD%\artifacts\%RID%\app"
goto :end

REM --------------------------------------------------------------- Failures
:nosdk
echo.
echo   The .NET SDK was not found on this computer.
echo.
echo   Install the .NET 8 SDK, then run this file again:
echo.
echo       https://dotnet.microsoft.com/download/dotnet/8.0
echo.
echo   Choose the SDK, not the Runtime, and pick the installer that
echo   matches this computer: %PROCESSOR_ARCHITECTURE%.
echo.
echo   It is a normal Windows installer. Accept the defaults.
echo.
pause
exit /b 1

:testsfailed
echo.
echo   The tests did not pass, so the build was stopped.
echo.
echo   This means something is wrong with the code, not with your
echo   computer. Nothing was produced. It is safe to try again.
echo.
pause
exit /b 1

:failed
echo.
echo   The build failed. The messages above say why.
echo.
echo   The most common cause is no internet connection the first
echo   time, because the libraries have to be downloaded once.
echo.
pause
exit /b 1

:end
echo.
pause
endlocal
