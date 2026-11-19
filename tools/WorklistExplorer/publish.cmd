@echo off
rem Builds both executables as self contained single files for win-x64.
rem Output: publish\WorklistExplorer.exe (window) and publish\WorklistExplorer.Cli.exe (console).
dotnet publish src\WorklistExplorer.Wpf\WorklistExplorer.Wpf.csproj -c Release -r win-x64 -o publish || exit /b 1
dotnet publish src\WorklistExplorer.Cli\WorklistExplorer.Cli.csproj -c Release -r win-x64 -o publish || exit /b 1
echo.
echo Done. Copy the two .exe files from the publish folder; no runtime installation is needed.
