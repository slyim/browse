; Inno Setup 6 script for browse. Build from the repo root after `dotnet publish ... -o publish`:
;   iscc /DAppVersion=X.Y.Z packaging\browse.iss
; Output: artifacts\browse-X.Y.Z-win-x64-setup.exe

#ifndef AppVersion
  #error Pass /DAppVersion=X.Y.Z
#endif

[Setup]
; Never change AppId: upgrades and uninstall find the existing install by it.
AppId={{6F3C2A1E-8B4D-4E7A-9C51-2D0B7E9A4F13}
AppName=browse
AppVersion={#AppVersion}
AppPublisher=slyim
AppPublisherURL=https://github.com/slyim/browse
AppSupportURL=https://github.com/slyim/browse/issues
AppUpdatesURL=https://github.com/slyim/browse/releases
; Per-user, no admin: same folder the in-app updater can write to, same HKCU hive the browser registration uses.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\browse
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\browse.exe
UninstallDisplayName=browse
SourceDir=..
OutputDir=artifacts
OutputBaseFilename=browse-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: defaultbrowser; Description: "Make browse available as a default browser (choose it later in Windows Settings > Default apps)"
Name: desktopicon; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "publish\*"; Excludes: "*.pdb,*.xml"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\browse"; Filename: "{app}\browse.exe"
Name: "{autodesktop}\browse"; Filename: "{app}\browse.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\browse.exe"; Parameters: "--register"; Tasks: defaultbrowser; Flags: runhidden
Filename: "{app}\browse.exe"; Description: "Launch browse"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\browse.exe"; Parameters: "--unregister"; Flags: runhidden; RunOnceId: "Unregister"

[UninstallDelete]
; Left by the in-app updater. Not a recursive delete: /DIR= could point the install at a folder holding other files.
Type: files; Name: "{app}\browse.exe.old"
Type: dirifempty; Name: "{app}"

[Code]
// Browsing data lives in %LOCALAPPDATA%\SearchWindows, outside {app}, so upgrades never touch it.
// Uninstall keeps it unless the user says otherwise; silent uninstalls always keep it.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{localappdata}\SearchWindows');
  if UninstallSilent or not DirExists(DataDir) then Exit;
  if MsgBox('Also delete your browse profile (tabs, settings, cookies, history and site data)?' + #13#10#13#10 + DataDir,
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
