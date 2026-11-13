; Vibershot Windows installer (Inno Setup 6.2 or newer).
; Built by tools\build-installer.ps1, which publishes the app and passes the defines below.
; See docs\RELEASING.md for the release and update flow.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\installer"
#endif
#define AppName "Vibershot"
#define AppExe "Vibershot.exe"
#define Publisher "Vibershot"
#define RepoUrl "https://github.com/CommonDocsOrg/vibershot"

[Setup]
; Never change AppId: it is how a newer installer finds and upgrades an existing copy in place.
AppId={{B7E1C2D4-5A6F-4E8B-9C3D-2F1A0E7B6C59}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
; Per-user by default (no UAC, silent updates need no elevation); the dialog lets the user pick all users.
; {autopf} is %LOCALAPPDATA%\Programs for a per-user install and Program Files for all users.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Upgrades keep the previous folder, task choices and per-user/all-users mode without asking again.
UsePreviousAppDir=yes
UsePreviousTasks=yes
UsePreviousPrivileges=yes
LicenseFile=..\..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=VibershotSetup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
WizardSizePercent=110
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
ChangesAssociations=yes
; Close a running Vibershot through Restart Manager (the app also exits itself before a silent update).
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
; A log in %TEMP% (Setup Log <date> #<n>.txt) for silent updates that go wrong.
SetupLogging=yes
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows (runs in the tray)"; GroupDescription: "Startup:"
Name: "fileassoc"; Description: "Open .vibeshot project files with {#AppName}"; GroupDescription: "File types:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; The self-contained publish output: Vibershot.exe, the .NET runtime, and ffmpeg\ with its LICENSE.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Screenshots, recordings and a capture library"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The same per-user Run value the app's Settings > General writes, so the checkbox there reflects this choice.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --tray"; Flags: uninsdeletevalue; Tasks: startup
; .vibeshot association, per-user or per-machine to match the install (HKA).
Root: HKA; Subkey: "Software\Classes\.vibeshot"; ValueType: string; ValueName: ""; ValueData: "Vibershot.Project"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Vibershot.Project"; ValueType: string; ValueName: ""; ValueData: "Vibershot project"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Vibershot.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Vibershot.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" --edit ""%1"""; Tasks: fileassoc

[Run]
; Interactive install: the finish page offers to launch.
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Silent update started by the app (/UPDATE=1): bring it straight back.
Filename: "{app}\{#AppExe}"; Parameters: "--updated"; Flags: nowait runasoriginaluser; Check: IsUpdateRun

[UninstallDelete]
; Caches only. Settings are asked about in code; the library is never touched.
Type: filesandordirs; Name: "{localappdata}\{#AppName}\Updates"
Type: filesandordirs; Name: "{localappdata}\{#AppName}\Thumbs"

[Code]
var
  LibraryPage: TInputDirWizardPage;

function IsUpdateRun: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

function ConfigPath: String;
begin
  Result := ExpandConstant('{userappdata}\Vibershot\config.json');
end;

{ An existing config means an upgrade or reinstall: the library is already chosen in the app. }
function ConfigExists: Boolean;
begin
  Result := FileExists(ConfigPath);
end;

{ Same default as GallerySettings.DefaultLibraryRoot in the app: next to the Windows Screenshots folder. }
function DefaultLibraryRoot: String;
var
  Pictures: String;
begin
  Pictures := GetShellFolderByCSIDL($27 { CSIDL_MYPICTURES }, False);
  if Pictures = '' then
    Pictures := ExpandConstant('{userdocs}\..\Pictures');
  Result := AddBackslash(Pictures) + 'Screenshots\Vibershot Gallery';
end;

procedure InitializeWizard;
begin
  LibraryPage := CreateInputDirPage(wpSelectDir,
    'Choose your library folder',
    'Where should Vibershot keep your captures?',
    'Screenshots, recordings and editor projects are saved here and shown in the gallery. ' +
    'The default sits next to the Windows Screenshots folder in Pictures. ' +
    'You can move the library later from Settings > Gallery.' + #13#10#13#10 +
    'To continue, click Next.',
    False, 'Vibershot Gallery');
  LibraryPage.Add('Library folder:');
  { /LIBRARY="D:\Shots" presets (or, in a silent install, decides) the folder; else the previous or default one. }
  LibraryPage.Values[0] := ExpandConstant('{param:LIBRARY|' + GetPreviousData('LibraryRoot', DefaultLibraryRoot) + '}');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = LibraryPage.ID then
    Result := ConfigExists;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Root: String;
begin
  Result := True;
  if CurPageID = LibraryPage.ID then
  begin
    Root := Trim(LibraryPage.Values[0]);
    { Accept a drive path (C:\...) or a UNC path (\\server\share\...). }
    if (Length(Root) < 3) or ((Pos(':\', Root) <> 2) and (Pos('\\', Root) <> 1)) then
    begin
      MsgBox('Enter a full folder path for the library, such as C:\Users\you\Pictures\Vibershot.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'LibraryRoot', LibraryPage.Values[0]);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  if not ConfigExists then
    Result := Result + 'Library folder:' + NewLine + Space + LibraryPage.Values[0] + NewLine + NewLine;
  if MemoTasksInfo <> '' then
    Result := Result + MemoTasksInfo + NewLine + NewLine;
  Result := Trim(Result);
end;

function JsonEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

{ Hands the library choice to the app. The app reads this on its first launch, saves a fresh
  config.json from it and deletes the file; config.json itself is never written here because the
  app treats any existing one as a pre-installer config. }
procedure WriteInstallChoices;
var
  Dir: String;
  Lines: TArrayOfString;
begin
  Dir := ExpandConstant('{userappdata}\Vibershot');
  ForceDirectories(Dir);
  SetArrayLength(Lines, 5);
  Lines[0] := '{';
  Lines[1] := '  "libraryRoot": "' + JsonEscape(Trim(LibraryPage.Values[0])) + '",';
  Lines[2] := '  "installer": "inno",';
  Lines[3] := '  "version": "{#AppVersion}"';
  Lines[4] := '}';
  if not SaveStringsToUTF8File(Dir + '\install-choices.json', Lines, False) then
    Log('Could not write install-choices.json; the app will ask for the library on first run.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not ConfigExists) then
    WriteInstallChoices;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if (not UninstallSilent) and FileExists(ConfigPath) then
    begin
      if MsgBox('Remove your Vibershot settings as well?' + #13#10#13#10 +
                'Your library (screenshots, recordings and projects) is kept either way.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{userappdata}\Vibershot'), True, True, True);
    end;
  end;
end;
