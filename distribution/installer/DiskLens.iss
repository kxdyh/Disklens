#ifndef AppVersion
  #define AppVersion "1.1.4"
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputPath
  #error OutputPath is required
#endif
#ifndef IconFile
  #define IconFile "..\..\src\DiskLens.App\Resources\Brand\DiskLens.ico"
#endif

[Setup]
AppId={{4C340A6D-C624-4E52-91AA-D17D26830485}
AppName={cm:AppDisplayName}
AppVersion={#AppVersion}
AppVerName=DiskLens {#AppVersion}
AppPublisher=kxdyh
AppPublisherURL=https://disklens.site/
AppSupportURL=https://github.com/kxdyh/Disklens/issues
AppUpdatesURL=https://disklens.site/#download
DefaultDirName={code:DefaultInstallDirectory}
UsePreviousAppDir=yes
UsePreviousPrivileges=yes
UninstallLogMode=append
DefaultGroupName=DiskLens
DisableProgramGroupPage=yes
DisableWelcomePage=no
LicenseFile=license.txt
DisableDirPage=no
OutputDir={#OutputPath}
OutputBaseFilename=DiskLens-Setup-{#AppVersion}-x64
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\DiskLens.App.exe
UninstallDisplayName=DiskLens {#AppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=115,110
WizardImageFile=brand-panel.bmp
WizardSmallImageFile=brand-small.bmp
WizardImageStretch=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=kxdyh
VersionInfoDescription=DiskLens installer
VersionInfoProductName=DiskLens
VersionInfoCopyright=Copyright (C) 2026 kxdyh
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"; InfoBeforeFile: "安装须知.txt"
Name: "english"; MessagesFile: "compiler:Default.isl"; InfoBeforeFile: "before-install-en.txt"

[CustomMessages]
chinesesimplified.AppDisplayName=DiskLens 磁盘透镜
english.AppDisplayName=DiskLens

[LangOptions]
chinesesimplified.DialogFontName=Microsoft YaHei UI
chinesesimplified.DialogFontSize=9
chinesesimplified.WelcomeFontName=Microsoft YaHei UI

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\DiskLens.App.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\使用说明.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\UserGuide.en.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\NOTICE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\DiskLens.install-manifest.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "Cleanup.ps1"; Flags: dontcopy
Source: "{#PublishDir}\DiskLens.install-manifest.json"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\DiskLens"; Filename: "{app}\DiskLens.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\DiskLens"; Filename: "{app}\DiskLens.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\DiskLens.App.exe"; WorkingDir: "{app}"; Verb: "runas"; Description: "{cm:LaunchProgram,DiskLens}"; Flags: shellexec nowait postinstall skipifsilent unchecked

[Code]
var
  CleanupPrepared: Boolean;

function QuoteArgument(Value: String): String;
begin
  Result := '"' + Value + '"';
end;

function CleanupParameters(Mode: String): String;
var
  Scope: String;
begin
  if IsAdminInstallMode then Scope := 'AllUsers' else Scope := 'CurrentUser';
  Result := '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File ' +
    QuoteArgument(ExpandConstant('{tmp}\Cleanup.ps1')) +
    ' -Mode ' + Mode +
    ' -InstallDirectory ' + QuoteArgument(RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) +
    ' -InstalledVersion {#AppVersion}' +
    ' -ManifestPath ' + QuoteArgument(ExpandConstant('{tmp}\DiskLens.install-manifest.json')) +
    ' -PlanPath ' + QuoteArgument(ExpandConstant('{tmp}\cleanup-plan.json')) +
    ' -SetupPath ' + QuoteArgument(ExpandConstant('{srcexe}')) +
    ' -InstallationMode ' + Scope +
    ' -ResultPath ' + QuoteArgument(ExpandConstant('{tmp}\cleanup-' + Mode + '-result.json'));
end;

procedure RunCleanup(Mode: String; AsOriginalUser: Boolean);
var
  Code: Integer;
  Started: Boolean;
  Details: AnsiString;
  PowerShell: String;
begin
  try
    PowerShell := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
    if AsOriginalUser then
      Started := ExecAsOriginalUser(PowerShell, CleanupParameters(Mode), '', SW_HIDE, ewWaitUntilTerminated, Code)
    else
      Started := Exec(PowerShell, CleanupParameters(Mode), '', SW_HIDE, ewWaitUntilTerminated, Code);
    if Started then Log(Format('DiskLens cleanup %s exited with %d.', [Mode, Code]))
    else Log('DiskLens cleanup could not start: ' + Mode);
    if LoadStringFromFile(ExpandConstant('{tmp}\cleanup-' + Mode + '-result.json'), Details) then
      Log('DiskLens cleanup: ' + UTF8Decode(Details));
  except
    Log('Optional DiskLens cleanup skipped: ' + GetExceptionMessage);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then begin
    try
      ExtractTemporaryFile('Cleanup.ps1');
      ExtractTemporaryFile('DiskLens.install-manifest.json');
      RunCleanup('Capture', False);
      CleanupPrepared := True;
    except
      Log('Cannot prepare optional DiskLens cleanup: ' + GetExceptionMessage);
    end;
  end;
  if (CurStep = ssPostInstall) and CleanupPrepared then begin
    RunCleanup('Install', False);
    RunCleanup('Downloads', True);
  end;
end;

procedure RegisterExtraCloseApplicationsResources;
var
  Directory: String;
  Root: Integer;
begin
  if IsAdminInstallMode then Root := HKLM64 else Root := HKCU64;
  if RegQueryStringValue(Root,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{4C340A6D-C624-4E52-91AA-D17D26830485}_is1',
    'InstallLocation', Directory) then
    RegisterExtraCloseApplicationsResource(True, AddBackslash(Directory) + 'DiskLens.App.exe');
end;

function DriveType(lpRootPathName: String): Cardinal;
  external 'GetDriveTypeW@kernel32.dll stdcall';

function DefaultInstallDirectory(Param: String): String;
begin
  { Prefer a non-system fixed drive when available; the directory remains editable. }
  if DirExists('E:\') and (DriveType('E:\') = 3) then
    Result := 'E:\Applications\DiskLens'
  else if DirExists('D:\') and (DriveType('D:\') = 3) then
    Result := 'D:\Applications\DiskLens'
  else
    Result := ExpandConstant('{localappdata}\Programs\DiskLens');
end;
