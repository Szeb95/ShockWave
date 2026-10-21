#ifndef PayloadDir
  #error PayloadDir must point to a clean installer payload
#endif
#ifndef SetupOutputDir
  #error SetupOutputDir must point to the installer output directory
#endif
#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif
#ifndef SetupAppId
  #define SetupAppId "A82D794F-972C-494B-8EF3-9D9FCB5D4032"
#endif
#ifndef SetupName
  #define SetupName "ShockWave-Setup-" + AppVersion + "-win-x64"
#endif
#ifndef VendorURL
  #define VendorURL "https://developer.lovense.com/LovenseBLE_Lib.dll"
#endif
#ifndef VendorSHA256
  #define VendorSHA256 "74424f9047078d0e02735dc9f7106897161e401de502d9f18597a3d9d3afa9cb"
#endif

[Setup]
AppId={#SetupAppId}
AppName=ShockWave
AppVersion={#AppVersion}
AppPublisher=ShockWave
DefaultDirName={localappdata}\Programs\ShockWave
DefaultGroupName=ShockWave
UsePreviousGroup=no
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
UninstallDisplayIcon={app}\ShockWave.exe
UninstallDisplayName=ShockWave
OutputDir={#SetupOutputDir}
OutputBaseFilename={#SetupName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic
DisableProgramGroupPage=yes
CloseApplications=no
RestartApplications=no
InfoBeforeFile=SETUP-INFO.txt
VersionInfoVersion={#AppVersion}
VersionInfoDescription=ShockWave installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Types]
Name: "full"; Description: "Complete installation"
Name: "compact"; Description: "Windows app and Bluetooth"
Name: "custom"; Description: "Choose components"; Flags: iscustom

[Components]
Name: "app"; Description: "Windows app, bridge and all app runtimes"; Types: full compact custom; Flags: fixed
Name: "bluetooth"; Description: "Lovense Bluetooth support (small official download)"; Types: full compact
Name: "avatar"; Description: "Optional Unity avatar tools (manual import)"; Types: full

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{tmp}\LovenseBLE_Lib.dll"; DestDir: "{app}\native\lovense"; Components: bluetooth; Flags: external ignoreversion
Source: "{#PayloadDir}\ShockWave.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\node.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\README.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\payload-manifest.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\bridge\*"; DestDir: "{app}\bridge"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\ToyControl\*"; DestDir: "{app}\ToyControl"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\Avatar-tools\*"; DestDir: "{app}\Avatar-tools"; Components: avatar; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Remove only the former installer-owned names after both running-app checks.
Type: files; Name: "{app}\SzebsToys.exe"
Type: files; Name: "{userprograms}\Szebs Toys\Szebs Toys.lnk"
Type: files; Name: "{userprograms}\Szebs Toys\Uninstall Szebs Toys.lnk"
Type: files; Name: "{userprograms}\Szebs Toys\Getting started.lnk"
Type: files; Name: "{userdesktop}\Szebs Toys.lnk"
Type: dirifempty; Name: "{userprograms}\Szebs Toys"

[Icons]
Name: "{group}\ShockWave"; Filename: "{app}\ShockWave.exe"; WorkingDir: "{app}"
Name: "{group}\Getting started"; Filename: "{app}\README.txt"
Name: "{group}\Uninstall ShockWave"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ShockWave"; Filename: "{app}\ShockWave.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ShockWave.exe"; WorkingDir: "{app}"; Description: "Open ShockWave"; Flags: nowait postinstall skipifsilent unchecked

[Code]
var
  BluetoothDownload: TDownloadWizardPage;

function OpenInstalledFile(const FileName: String; DesiredAccess, ShareMode,
  SecurityAttributes, CreationDisposition, Flags, Template: LongWord): LongWord;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseFileHandle(Handle: LongWord): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function AppFileCanBeUpdated(const FileName: String): Boolean;
var
  AppFile: String;
  Handle: LongWord;
begin
  AppFile := ExpandConstant('{app}\') + FileName;
  Result := True;
  if not FileExists(AppFile) then
    Exit;
  // Opening for write (without writing) fails while Windows maps the running
  // EXE, including its hidden worker. Never kill or restart either process.
  Handle := OpenInstalledFile(AppFile, $40000000, 7, 0, 3, $80, 0);
  Result := Handle <> $FFFFFFFF;
  if Result then
    CloseFileHandle(Handle);
end;

function InstalledAppCanBeUpdated: Boolean;
begin
  // Both names can exist during an upgrade. Never replace a running copy.
  Result := AppFileCanBeUpdated('ShockWave.exe') and
    AppFileCanBeUpdated('SzebsToys.exe');
end;

function InitializeUninstall: Boolean;
begin
  Result := InstalledAppCanBeUpdated;
  if not Result then
    SuppressibleMsgBox('Close ShockWave normally before uninstalling it, ' +
      'then run Uninstall again. The installed app file is currently in use ' +
      'or cannot be changed.', mbError, MB_OK, IDOK);
end;

procedure InitializeWizard;
begin
  BluetoothDownload := CreateDownloadPage('Bluetooth support',
    'Downloading the official Lovense Bluetooth library.', nil);
  BluetoothDownload.ShowBaseNameInsteadOfUrl := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExistingLibrary, TemporaryLibrary: String;
begin
  Result := '';
  if not InstalledAppCanBeUpdated then begin
    Result := 'Close ShockWave normally before updating it, then try again. ' +
      'The installed app file is currently in use or cannot be changed.';
    Exit;
  end;
  if not WizardIsComponentSelected('bluetooth') then
    Exit;

  ExistingLibrary := ExpandConstant('{app}\native\lovense\LovenseBLE_Lib.dll');
  TemporaryLibrary := ExpandConstant('{tmp}\LovenseBLE_Lib.dll');
  // Fetch and verify BEFORE changing installed files. A failed download leaves
  // an existing installation intact. An unchanged official DLL can be reused.
  try
    if FileExists(ExistingLibrary) then
      if CompareText(GetSHA256OfFile(ExistingLibrary), '{#VendorSHA256}') = 0 then
        if FileCopy(ExistingLibrary, TemporaryLibrary, False) then begin
          Log('Reusing verified installed Bluetooth library.');
          Exit;
        end;
    BluetoothDownload.Clear;
    BluetoothDownload.Add('{#VendorURL}', 'LovenseBLE_Lib.dll', '{#VendorSHA256}');
    BluetoothDownload.Show;
    try
      BluetoothDownload.Download;
    finally
      BluetoothDownload.Hide;
    end;
  except
    Log(GetExceptionMessage);
    Result := 'Bluetooth support could not be downloaded or verified. ' +
      'Check your Internet connection and try again. You can also go back ' +
      'and deselect Bluetooth support to install the other components.' + #13#10#13#10 +
      GetExceptionMessage;
  end;
end;

// Uninstall removes installer-owned files, shortcuts and the Windows Apps
// entry. It deliberately has no wildcard deletion of the user's settings,
// tokens, remembered toys, recordings or Unity projects.
