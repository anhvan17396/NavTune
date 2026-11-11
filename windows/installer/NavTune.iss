; NavTune for Windows: per-user installer (no administrator rights needed).
; Built by build.ps1:  ISCC.exe /DAppVersion=1.3.0 /DSourceExe=<published NavTune.exe> /DOutputDir=<dist> NavTune.iss

#ifndef AppVersion
  #define AppVersion "1.3.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\dist\NavTune-" + AppVersion + "-win-x64\NavTune.exe"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{6F0B7C43-2F7B-4F0E-9A51-4E61766E5475}
AppName=NavTune
AppVersion={#AppVersion}
AppVerName=NavTune {#AppVersion}
AppPublisher=navtools.vn
AppPublisherURL=https://navtools.vn
AppSupportURL=https://navtools.vn
AppCopyright=© navtools.vn
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\NavTune
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=NavTune-Setup-{#AppVersion}
SetupIconFile=..\src\NavTune\Assets\NavTune.ico
UninstallDisplayIcon={app}\NavTune.exe
UninstallDisplayName=NavTune
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto
CloseApplications=no

[Languages]
Name: "vi"; MessagesFile: "compiler:Default.isl,Vietnamese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "{cm:AutoStartProgram,NavTune}"; GroupDescription: "{cm:AutoStartProgramGroupDescription}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\NavTune"; Filename: "{app}\NavTune.exe"; Comment: "NavTune"
Name: "{userdesktop}\NavTune"; Filename: "{app}\NavTune.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NavTune"; ValueData: """{app}\NavTune.exe"" --background"; Tasks: autostart

[Run]
Filename: "{app}\NavTune.exe"; Description: "{cm:LaunchProgram,NavTune}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

{ Asks a running NavTune to quit cleanly, so it gives Windows its default speaker back before files change. }
procedure QuitNavTune(const Exe: String);
var
  Code: Integer;
begin
  if FileExists(Exe) then
  begin
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(2500);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    QuitNavTune(ExpandConstant('{app}\NavTune.exe'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\NavTune.exe');
  if CurUninstallStep = usUninstall then
  begin
    QuitNavTune(Exe);
    { In case NavTune had crashed earlier: put the default device and app volumes back. }
    if FileExists(Exe) then
      Exec(Exe, '--restore-audio', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    { NavTune's own "launch at sign-in" switch writes this value too, and its power-cut safety net a RunOnce one. }
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'NavTune');
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\RunOnce', 'NavTuneRestoreAudio');
    if SuppressibleMsgBox(CustomMessage('DeleteSettings'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\NavTune'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\NavTune'), True, True, True);
    end;
  end;
end;

[CustomMessages]
vi.DeleteSettings=Xoá luôn cài đặt và preset của NavTune?%n%nChọn Không nếu sau này anh có thể cài lại.
en.DeleteSettings=Also delete NavTune's settings and presets?%n%nChoose No if you may install it again later.
