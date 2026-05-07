; AirMirror Inno Setup installer script
; Build with:
;   iscc /DArch=x64   installer\AirMirror.iss
;   iscc /DArch=arm64 installer\AirMirror.iss
;
; Requires the published app at:
;   src\AirMirror\bin\Release\net8.0-windows10.0.19041.0\<rid>\publish\
; where <rid> is win-x64 or win-arm64.

#ifndef Arch
  #define Arch "x64"
#endif

#if Arch == "x64"
  #define Rid "win-x64"
  #define ArchAllowed "x64compatible"
  #define ArchInstall64 "x64compatible"
#elif Arch == "arm64"
  #define Rid "win-arm64"
  #define ArchAllowed "arm64"
  #define ArchInstall64 "arm64"
#else
  #error Unsupported Arch (must be x64 or arm64)
#endif

#define MyAppName "AirMirror"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "MuhannadYT"
#define MyAppURL "https://github.com/MuhannadYT"
#define MyAppExeName "AirMirror.exe"
#define PublishDir "..\src\AirMirror\bin\Release\net8.0-windows10.0.19041.0\" + Rid + "\publish"

[Setup]
AppId={{8F2A9B3C-5D4E-4F1A-9C7B-AIRMIRROR0001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=AirMirror-Setup-{#MyAppVersion}-{#Arch}
SetupIconFile=..\src\AirMirror\Assets\AirMirror.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchInstall64}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{userappdata}\..\Local\AirMirror\receiver.log"

[Code]
var
  AirPlayNamePage: TInputQueryWizardPage;

procedure InitializeWizard;
var
  DefaultName: String;
begin
  AirPlayNamePage := CreateInputQueryPage(wpSelectTasks,
    'AirPlay Server Name',
    'Choose the name your iPhone, iPad, or Mac will see when AirPlaying to this PC.',
    'You can change this later from inside AirMirror.');
  AirPlayNamePage.Add('AirPlay name:', False);

  DefaultName := GetUserNameString + '''s PC';
  AirPlayNamePage.Values[0] := DefaultName;
end;

function EscapeJsonString(const S: String): String;
var
  I: Integer;
  C: Char;
  R: String;
begin
  R := '';
  for I := 1 to Length(S) do
  begin
    C := S[I];
    case C of
      '\': R := R + '\\';
      '"': R := R + '\"';
    else
      if C = Chr(8) then R := R + '\b'
      else if C = Chr(9) then R := R + '\t'
      else if C = Chr(10) then R := R + '\n'
      else if C = Chr(12) then R := R + '\f'
      else if C = Chr(13) then R := R + '\r'
      else R := R + C;
    end;
  end;
  Result := R;
end;

procedure WriteAirPlayNameToSettings(const NewName: String);
var
  SettingsDir, SettingsPath: String;
  Lines: TArrayOfString;
  I: Integer;
  Replaced: Boolean;
  AnsiJson: AnsiString;
begin
  SettingsDir := ExpandConstant('{localappdata}') + '\AirMirror';
  if not DirExists(SettingsDir) then
    CreateDir(SettingsDir);
  SettingsPath := SettingsDir + '\settings.json';

  Replaced := False;
  if FileExists(SettingsPath) and LoadStringsFromFile(SettingsPath, Lines) then
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      if Pos('"AirPlayName"', Lines[I]) > 0 then
      begin
        Lines[I] := '  "AirPlayName": "' + EscapeJsonString(NewName) + '",';
        Replaced := True;
      end;
    end;
    if Replaced then
      SaveStringsToFile(SettingsPath, Lines, False);
  end;

  if not Replaced then
  begin
    AnsiJson := AnsiString('{' + Chr(13) + Chr(10) +
      '  "AirPlayName": "' + EscapeJsonString(NewName) + '"' + Chr(13) + Chr(10) +
      '}' + Chr(13) + Chr(10));
    SaveStringToFile(SettingsPath, AnsiJson, False);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ChosenName: String;
begin
  if CurStep = ssPostInstall then
  begin
    ChosenName := Trim(AirPlayNamePage.Values[0]);
    if ChosenName = '' then
      ChosenName := GetUserNameString + '''s PC';
    WriteAirPlayNameToSettings(ChosenName);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AirMirror');
  end;
end;

function IsAirMirrorRunning(): Boolean;
var
  ResultCode: Integer;
  TempFile: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := False;
  TempFile := ExpandConstant('{tmp}\airmirror-tasklist.txt');
  // Use cmd /c with redirection so the output goes to a file we can read.
  if Exec(ExpandConstant('{cmd}'),
          '/C tasklist /FI "IMAGENAME eq AirMirror.exe" /NH > "' + TempFile + '"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if FileExists(TempFile) and LoadStringsFromFile(TempFile, Lines) then
    begin
      for I := 0 to GetArrayLength(Lines) - 1 do
      begin
        if Pos('AirMirror.exe', Lines[I]) > 0 then
        begin
          Result := True;
          Break;
        end;
      end;
    end;
    DeleteFile(TempFile);
  end;
end;

procedure ForceCloseAirMirror();
var
  ResultCode: Integer;
begin
  // /F = force, /T = also kill any child processes (uxplay)
  Exec(ExpandConstant('{cmd}'),
       '/C taskkill /F /T /IM AirMirror.exe',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'),
       '/C taskkill /F /T /IM uxplay.exe',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Give Windows a moment to release file handles.
  Sleep(800);
end;

function InitializeUninstall(): Boolean;
var
  Response: Integer;
begin
  Result := True;
  while IsAirMirrorRunning() do
  begin
    Response := MsgBox(
      'AirMirror is currently running.' + #13#10 + #13#10 +
      'Please close it (right-click the system-tray icon and choose Exit) so it can be uninstalled cleanly.' + #13#10 + #13#10 +
      'Click "Retry" once you have closed it, or "Ignore" to force-close it now.',
      mbConfirmation, MB_ABORTRETRYIGNORE);
    case Response of
      IDABORT:
        begin
          Result := False;
          Exit;
        end;
      IDIGNORE:
        begin
          ForceCloseAirMirror();
          if IsAirMirrorRunning() then
          begin
            MsgBox('Failed to close AirMirror. Uninstall aborted.', mbError, MB_OK);
            Result := False;
            Exit;
          end;
        end;
      // IDRETRY falls through to re-check the loop.
    end;
  end;
end;
