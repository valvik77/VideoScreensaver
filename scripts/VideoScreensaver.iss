#ifndef MyAppVersion
  #error MyAppVersion must be supplied by Build-Installer.ps1.
#endif

[Setup]
AppId={{4A155F69-8472-476A-9E59-548E9483C7BC}
AppName=Video Screensaver
AppVersion={#MyAppVersion}
AppPublisher=Video Screensaver
DefaultDirName={autopf}\Video Screensaver
DefaultGroupName=Video Screensaver
DisableProgramGroupPage=yes
OutputBaseFilename=VideoScreensaver-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\VideoScreensaver.exe
WizardStyle=modern

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
; Windows' classic "Change screen saver" dialog only lists .scr files placed directly in
; System32/SysWOW64 - it never looks inside Program Files. The full self-contained app (~400
; files) is installed under {app} as usual; only the tiny stub launcher goes into System32 under
; the .scr name so it shows up in that dialog and relays to the real app via the registry below.
Source: "..\artifacts\publish-stub\VideoScreensaverStub.exe"; DestDir: "{sys}"; DestName: "VideoScreensaver.scr"; Flags: ignoreversion

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Video Screensaver"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Control Panel\Desktop"; ValueType: string; ValueName: "SCRNSAVE.EXE"; ValueData: "{sys}\VideoScreensaver.scr"

[Icons]
Name: "{group}\Configurar Video Screensaver"; Filename: "{app}\VideoScreensaver.exe"; Parameters: "/c"
Name: "{group}\Desinstalar Video Screensaver"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\VideoScreensaver.exe"; Parameters: "/c"; Description: "Configurar Video Screensaver"; Flags: postinstall nowait skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SelectedScreenSaver: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU, 'Control Panel\Desktop', 'SCRNSAVE.EXE', SelectedScreenSaver)
      and (CompareText(RemoveQuotes(SelectedScreenSaver), ExpandConstant('{sys}\VideoScreensaver.scr')) = 0) then
    begin
      RegDeleteValue(HKCU, 'Control Panel\Desktop', 'SCRNSAVE.EXE');
    end;
  end;
end;
