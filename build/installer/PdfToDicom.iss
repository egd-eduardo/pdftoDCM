; ============================================================================
;  PDF to DICOM - Inno Setup installer script
; ============================================================================
;
;  Produces a per user installer. That choice is deliberate and is what satisfies
;  the requirement that the application run without administrator rights:
;
;    - PrivilegesRequired=lowest means the installer never raises a UAC prompt,
;      so a clinical user can install it themselves without calling IT.
;    - The application is installed beneath the user's Local AppData folder
;      rather than Program Files, which a standard user cannot write to.
;    - Nothing is written to HKEY_LOCAL_MACHINE or to the Windows directory.
;
;  A site that prefers a machine wide deployment can set PrivilegesRequired to
;  admin and DefaultDirName to {autopf}, at the cost of needing an administrator
;  to run the installer once. The application itself still needs no elevation.
;
;  Build with:  ISCC.exe /DTargetRid=win-x64 build\installer\PdfToDicom.iss
;  Requires:    Inno Setup 6.3 or later, for the architecture identifiers used below
;               (https://jrsoftware.org/isdl.php)
;               artifacts\<rid>\app\PdfToDicom.exe produced by build.ps1 or build.sh
; ============================================================================

#define AppName        "PDF to DICOM"
#define AppShortName   "PdfToDicom"
#define AppVersion     "2.0.1"
#define AppPublisher   "Eduardo Dias"
#define AppExeName     "PdfToDicom.exe"
#define CliExeName     "pdftodicom.exe"

; Target architecture. Pass it on the command line to build an installer for one of the
; three supported Windows architectures, for example:
;     ISCC.exe /DTargetRid=win-arm64 build\installer\PdfToDicom.iss
; Defaults to win-x64, which is the mainstream clinical workstation.
#ifndef TargetRid
  #define TargetRid "win-x64"
#endif

; Maps the runtime identifier to the Inno Setup architecture identifiers.
;   x64compatible also matches ARM64 machines running x64 under emulation, so the x64
;   installer remains usable there; the arm64 installer is simply the better choice.
#if TargetRid == "win-x86"
  #define ArchAllowed      "x86compatible"
  #define ArchInstallIn64  ""
  #define ArchSuffix       "x86"
#elif TargetRid == "win-arm64"
  #define ArchAllowed      "arm64"
  #define ArchInstallIn64  "arm64"
  #define ArchSuffix       "arm64"
#else
  #define ArchAllowed      "x64compatible"
  #define ArchInstallIn64  "x64compatible"
  #define ArchSuffix       "x64"
#endif

[Setup]
AppId={{7B2E4C91-3D5A-4F8E-9C1B-2A6D8E4F0A31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} - Encapsulated PDF for PACS

; Per user installation: no elevation prompt, no administrator required.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={localappdata}\{#AppShortName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no

OutputBaseFilename=PdfToDicom-Setup-{#AppVersion}-{#ArchSuffix}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchInstallIn64}

; Windows 10 version 1809 is the oldest supported build.
MinVersion=10.0.17763

; The licence must be accepted before installation. It is free to use, so this is not
; about entitlement: acceptance is what makes the clinical and no warranty terms binding
; rather than merely displayed, and those are the terms that matter in a hospital.
LicenseFile=..\..\LICENSE

SetupIconFile=..\..\src\PdfToDicom.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} ({#ArchSuffix})

; Closes the application cleanly if it is running during an upgrade, rather than
; leaving files locked and producing a partial installation.
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

[Languages]
Name: "english";    MessagesFile: "compiler:Default.isl"
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "associatepdf"; Description: "Add PDF to DICOM to the ""Open with"" list for PDF files"; GroupDescription: "File associations"; Flags: unchecked

[Files]
; The application is a single self contained executable. The .NET runtime, WPF and
; every dependency are embedded, which is what removes the need to install any
; separate runtime on the workstation.
Source: "..\..\artifacts\{#TargetRid}\app\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

; The command line tool is optional and is used for scripted or bulk conversion.
;
; It is installed into a subfolder rather than beside the application, because the two
; assembly names, PdfToDicom and pdftodicom, differ only in case. A Windows filesystem
; cannot hold both in one directory: the second overwrites the first and keeps the first
; one's spelling, so {app}\PdfToDicom.exe would really be the command line tool and the
; Start Menu shortcut would open a console.
Source: "..\..\artifacts\{#TargetRid}\cli\{#CliExeName}"; DestDir: "{app}\Command line"; Flags: ignoreversion skipifsourcedoesntexist

; Documentation and samples, installed alongside the application so a user can
; reach the manual without a network connection.
Source: "..\..\LICENSE";                       DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\..\THIRD-PARTY-NOTICES.md";        DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; A named list, not docs\*. Three documents in that folder are written for the copyright
; holder rather than for a site, and one of them discusses the customer; a wildcard sent
; all three to every hospital. Naming them also means a new document has to be considered
; rather than shipped by accident. This list matches the one in build.ps1 and build.sh.
Source: "..\..\docs\UserManual.md";                 DestDir: "{app}\docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\docs\ManualDoUtilizador.pt-PT.md";   DestDir: "{app}\docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\docs\DicomConformanceStatement.md";  DestDir: "{app}\docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\docs\TechnicalDocumentation.md";     DestDir: "{app}\docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\CHANGELOG.md";                       DestDir: "{app}\docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\samples\config.sample.json";      DestDir: "{app}\samples"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\samples\example-report.pdf";      DestDir: "{app}\samples"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\samples\example-encapsulated.dcm"; DestDir: "{app}\samples"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}";                   Filename: "{app}\{#AppExeName}"
Name: "{group}\User manual";                  Filename: "{app}\docs\UserManual.md"; Flags: createonlyiffileexists
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";             Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Registered under HKCU only, so no administrator rights are needed and one user's
; choice never affects another user of a shared clinical workstation.
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; \
    ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; \
    Flags: uninsdeletekey; Tasks: associatepdf

Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; \
    ValueType: string; ValueName: ".pdf"; ValueData: ""; \
    Flags: uninsdeletekey; Tasks: associatepdf

Root: HKCU; Subkey: "Software\Classes\.pdf\OpenWithList\{#AppExeName}"; \
    ValueType: string; ValueName: ""; ValueData: ""; \
    Flags: uninsdeletekey; Tasks: associatepdf

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only the installed files are removed. The user's configuration, logs and any
; generated objects under %APPDATA%\PdfToDicom are deliberately left in place:
; the audit trail is a hospital record and must survive an uninstall.
Type: filesandordirs; Name: "{app}\docs"
Type: filesandordirs; Name: "{app}\samples"

[Code]

{ Warns an administrator that the audit trail is intentionally preserved, so that
  its presence after an uninstall is never mistaken for a defect. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataFolder: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataFolder := ExpandConstant('{userappdata}\PdfToDicom');

    if DirExists(DataFolder) then
    begin
      MsgBox('The configuration, logs and audit trail have been kept in:' + #13#10 + #13#10 +
             DataFolder + #13#10 + #13#10 +
             'The audit trail is a hospital record, so it is not removed automatically. ' +
             'Delete this folder manually only if your information governance policy allows it.',
             mbInformation, MB_OK);
    end;
  end;
end;
