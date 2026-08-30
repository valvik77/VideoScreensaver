#ifndef MyAppVersion
  #error MyAppVersion must be supplied by Build-Installer.ps1.
#endif

[Setup]
AppId={{4A155F69-8472-476A-9E59-548E9483C7BC}
AppName=Video Screensaver
AppVersion={#MyAppVersion}
AppPublisher=Video Screensaver
DefaultDirName={userpf}\Video Screensaver
DefaultGroupName=Video Screensaver
DisableProgramGroupPage=yes
OutputBaseFilename=VideoScreensaver-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\VideoScreensaver.scr
WizardStyle=modern

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\VideoScreensaver.exe"; DestDir: "{app}"; DestName: "VideoScreensaver.scr"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Control Panel\Desktop"; ValueType: string; ValueName: "SCRNSAVE.EXE"; ValueData: "{app}\VideoScreensaver.scr"

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
      and (CompareText(SelectedScreenSaver, ExpandConstant('{app}\VideoScreensaver.scr')) = 0) then
    begin
      RegDeleteValue(HKCU, 'Control Panel\Desktop', 'SCRNSAVE.EXE');
    end;
  end;
end;
