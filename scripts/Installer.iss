#ifndef BuildDir
  #error BuildDir must identify the verified portable core
#endif
#ifndef ReleaseDir
  #error ReleaseDir must identify the release destination
#endif
[Setup]
AppId={{AA39B37E-BF56-433B-BEDD-AB2000B20002}
AppName=AI BLACK HOLE
AppVersion=2.0.0-beta.2
AppVerName=AI BLACK HOLE 2.0.0-beta.2
AppPublisher=chatgptopenaiagi
AppPublisherURL=https://github.com/chatgptopenaiagi/AI-BLACK-HOLE
AppSupportURL=https://github.com/chatgptopenaiagi/AI-BLACK-HOLE/issues
DefaultDirName={localappdata}\Programs\AI BLACK HOLE 2.0.0-beta.2
DefaultGroupName=AI BLACK HOLE
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
OutputDir={#ReleaseDir}
OutputBaseFilename=AI-BLACK-HOLE-2.0.0-beta.2-SETUP
Compression=lzma2/fast
SolidCompression=yes
UninstallDisplayIcon={app}\AI-BLACK-HOLE.exe
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupLogging=yes
[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked
[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "data\*,vault\*,models\*,test-models\*"
[InstallDelete]
[Icons]
Name: "{group}\AI BLACK HOLE"; Filename: "{app}\AI-BLACK-HOLE.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\AI BLACK HOLE"; Filename: "{app}\AI-BLACK-HOLE.exe"; WorkingDir: "{app}"; Tasks: desktopicon
[Run]
Filename: "{app}\AI-BLACK-HOLE.exe"; Description: "Launch AI BLACK HOLE"; Flags: nowait postinstall skipifsilent
[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var FindRec: TFindRec;
begin
  Result := '';
  if FindFirst(ExpandConstant('{app}\*'), FindRec) then begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
          Result := 'Choose a new empty installation folder. This beta never overwrites an existing installation.';
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\installed.marker'), 'ABH installed edition: writable state uses LocalAppData.', False);
end;
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    DeleteFile(ExpandConstant('{app}\installed.marker'));
end;
