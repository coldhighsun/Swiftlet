; Swiftlet Inno Setup script (per-user install, no elevation).
;
; Build from the repository root after publishing the application:
;   dotnet publish src/Swiftlet.Desktop -c Release -r win-x64 --self-contained -o artifacts/installer-input
;   ISCC.exe /DAppVersion=1.2.3 installer\Swiftlet.iss
; The result is artifacts\installer\Swiftlet-Setup-v1.2.3.exe. The application's updater looks for
; that file name, and for the uninstaller (unins000.exe) beside the application.

; The defaults allow compiling locally without arguments.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\artifacts\installer-input"
#endif

; Must match InstallerMutex.Name in src/Swiftlet.Desktop/Hosting.
#define AppMutexName "Swiftlet-Running-5D0E3B7A-2C64-4E0B-9C1F-8A6D7B3E4F21"
#define AppExeName "Swiftlet.exe"

[Setup]
; Never change the AppId: it is how an update finds the installation it replaces.
AppId={{C4A1F6D2-7B93-4E58-A0D6-3F2B9E815C74}
AppName=Swiftlet
AppVersion={#AppVersion}
AppPublisher=Swiftlet
AppPublisherURL=https://github.com/coldhighsun/Swiftlet
PrivilegesRequired=lowest
; With the lowest privileges {autopf} is %LocalAppData%\Programs. The mail data is kept elsewhere
; (%LocalAppData%\Swiftlet) and is neither touched by an update nor removed by uninstalling.
DefaultDirName={autopf}\Swiftlet
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=Swiftlet-Setup-v{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex={#AppMutexName}
SetupIconFile=..\src\Swiftlet.Desktop\Assets\swiftlet.ico
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile=..\LICENSE
WizardStyle=modern dynamic

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; The program was called Swiftlet.Desktop up to 0.1.0-alpha.1; an update would leave those files behind.
Type: files; Name: "{app}\Swiftlet.Desktop.exe"
Type: files; Name: "{app}\Swiftlet.Desktop.dll"
Type: files; Name: "{app}\Swiftlet.Desktop.deps.json"
Type: files; Name: "{app}\Swiftlet.Desktop.runtimeconfig.json"
Type: files; Name: "{app}\zh-Hans\Swiftlet.Desktop.resources.dll"
; Native symbol files that earlier packages shipped.
Type: files; Name: "{app}\libHarfBuzzSharp.pdb"
Type: files; Name: "{app}\libSkiaSharp.pdb"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Swiftlet"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\Swiftlet"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; The application writes these when the user lets it start at sign-in (WindowsAutoStart); setup creates nothing,
; and uninstalling removes them.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Swiftlet"; Flags: dontcreatekey uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: none; ValueName: "Swiftlet"; Flags: dontcreatekey uninsdeletevalue

[Run]
; No "skipifsilent": a silent run is the application updating itself, and it must start again.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,Swiftlet}"; Flags: nowait postinstall

[Code]
const
  ExitTimeoutMs = 30000;
  ExitPollIntervalMs = 500;

// A silent run is started by the application just before it quits, so it is given time to do so.
// Runs before Setup's own AppMutex check, which then handles an application that is still running
// (in an interactive run it asks the user to close it at once).
function InitializeSetup(): Boolean;
var
  Elapsed: Integer;
begin
  Result := True;
  if not WizardSilent() then
    exit;

  Elapsed := 0;
  while CheckForMutexes('{#AppMutexName}') and (Elapsed < ExitTimeoutMs) do
  begin
    Sleep(ExitPollIntervalMs);
    Elapsed := Elapsed + ExitPollIntervalMs;
  end;
end;
