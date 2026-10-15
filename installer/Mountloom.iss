; Compile through scripts/build-installer.ps1. Dependencies are embedded for offline installation.
#ifndef AppSource
  #error AppSource is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef WinFspMsi
  #error WinFspMsi is required
#endif
#ifndef WinFspSha256
  #error WinFspSha256 is required
#endif
#ifndef InstallerManifest
  #error InstallerManifest is required
#endif

[Setup]
AppId={{80A5B506-7581-4486-B215-51DCFC9BF88C}
AppName=Mountloom
AppVersion={#AppVersion}
AppPublisher=Mountloom
DefaultDirName={autopf}\Mountloom
DefaultGroupName=Mountloom
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputBaseFilename=Mountloom-Setup-x64
UninstallDisplayIcon={app}\Mountloom.App.exe
SetupMutex=Global\Mountly-Setup
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.Dependencies=安装内容包含 Mountloom、.NET 运行时和 rclone。缺少 WinFsp 时会自动安装随包驱动，无需手动配置程序路径。%n%nWinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos%nhttps://github.com/winfsp/winfsp
english.Dependencies=Setup includes Mountloom, the .NET runtime and rclone. The bundled WinFsp driver is installed automatically when missing. No executable path configuration is required.%n%nWinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos%nhttps://github.com/winfsp/winfsp
chinesesimplified.ActiveApplication=Mountloom、旧版 Mountly 或其挂载进程仍在运行。请在通知区域选择“退出并卸载”，等待上传完成后重试。
english.ActiveApplication=Mountloom, an older Mountly version or one of its mount processes is still running. Choose Exit and unmount from the notification area and wait for pending uploads before retrying.
chinesesimplified.ProcessCheckFailed=无法检查正在运行的挂载进程。为保护待上传文件，本次操作已停止。请恢复 Windows Management Instrumentation 服务后重试。
english.ProcessCheckFailed=Running mount processes could not be checked. Setup stopped to protect pending uploads. Restore the Windows Management Instrumentation service and retry.
chinesesimplified.InstallingWinFsp=正在安装挂载所需的 WinFsp 驱动，请稍候…
english.InstallingWinFsp=Installing the WinFsp driver required for mounting. Please wait...
chinesesimplified.WinFspFailed=WinFsp 安装未完成（错误代码 %1）。日志：%2。解决该错误后重新运行 Mountloom 安装器。
english.WinFspFailed=WinFsp installation did not complete (error %1). Log: %2. Resolve this error and run Mountloom Setup again.
chinesesimplified.WinFspMissing=WinFsp 安装程序已结束，但仍未检测到所需的 x64 运行库。请查看日志后重试：%1
english.WinFspMissing=The WinFsp installer completed, but the required x64 runtime was not detected. Review the log and retry: %1
chinesesimplified.WinFspCorrupt=安装包内的 WinFsp 校验失败。请重新获取 Mountloom 安装包。
english.WinFspCorrupt=The embedded WinFsp package failed verification. Obtain a fresh Mountloom installer.
chinesesimplified.Launch=打开 Mountloom
english.Launch=Open Mountloom
chinesesimplified.DesktopShortcut=创建桌面快捷方式
english.DesktopShortcut=Create a desktop shortcut

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: unchecked

[Files]
Source: "{#WinFspMsi}"; DestName: "winfsp-bundled.msi"; Flags: dontcopy
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#InstallerManifest}"; DestDir: "{app}"; Flags: ignoreversion
Source: "third-party\winfsp\*"; DestDir: "{app}\third-party\winfsp"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Mountloom"; Filename: "{app}\Mountloom.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Mountloom"; Filename: "{app}\Mountloom.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

; WinFsp is shared with other applications. Uninstall only Mountloom's recorded files and
; shortcuts, leaving WinFsp and all per-user configuration, credentials and VFS caches intact.
[Code]
var
  DriverRestartRequired: Boolean;

function IsUsableWinFspDirectory(RootKey: Integer; ValueName: String): Boolean;
var
  Directory: String;
begin
  Result := RegQueryStringValue(RootKey, 'SOFTWARE\WinFsp', ValueName, Directory) and
    (Directory <> '') and FileExists(AddBackslash(Directory) + 'bin\winfsp-x64.dll');
end;

function WinFspInstalled: Boolean;
begin
  Result := IsUsableWinFspDirectory(HKLM64, 'SxsDir') or
    IsUsableWinFspDirectory(HKLM64, 'InstallDir') or
    IsUsableWinFspDirectory(HKLM32, 'SxsDir') or
    IsUsableWinFspDirectory(HKLM32, 'InstallDir');
end;

function CheckRunningProcesses: String;
var
  Locator, Services, Processes, Process: Variant;
  Index: Integer;
  ExecutablePath, AppPrefix: String;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''Mountloom.App.exe'' OR Name = ''Mountly.App.exe''');
    if Processes.Count > 0 then begin
      Result := CustomMessage('ActiveApplication');
      Exit;
    end;
    AppPrefix := Lowercase(AddBackslash(ExpandConstant('{app}')));
    Processes := Services.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name = ''rclone.exe''');
    for Index := 0 to Processes.Count - 1 do begin
      Process := Processes.ItemIndex(Index);
      if VarIsNull(Process.ExecutablePath) then begin
        Result := CustomMessage('ProcessCheckFailed');
        Exit;
      end;
      ExecutablePath := Lowercase(Process.ExecutablePath);
      if (ExecutablePath = '') or (Pos(AppPrefix, ExecutablePath) = 1) then begin
        Result := CustomMessage('ActiveApplication');
        Exit;
      end;
    end;
  except
    Log('Process inspection failed: ' + GetExceptionMessage);
    Result := CustomMessage('ProcessCheckFailed');
  end;
end;

procedure InitializeWizard;
begin
  WizardForm.WelcomeLabel2.Caption := WizardForm.WelcomeLabel2.Caption + #13#10#13#10 + CustomMessage('Dependencies');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  MsiPath, LogDirectory, LogPath, Parameters: String;
  ResultCode: Integer;
begin
  Result := CheckRunningProcesses;
  if Result <> '' then Exit;
  if WinFspInstalled then begin
    Log('An existing x64 WinFsp runtime was found. Keeping the installed version.');
    Exit;
  end;

  WizardForm.StatusLabel.Caption := CustomMessage('InstallingWinFsp');
  ExtractTemporaryFile('winfsp-bundled.msi');
  MsiPath := ExpandConstant('{tmp}\winfsp-bundled.msi');
  if CompareText(GetSHA256OfFile(MsiPath), '{#WinFspSha256}') <> 0 then begin
    Result := CustomMessage('WinFspCorrupt');
    Exit;
  end;
  LogDirectory := ExpandConstant('{commonappdata}\Mountly\InstallerLogs');
  if not ForceDirectories(LogDirectory) then begin
    Result := 'Cannot create installer log directory: ' + LogDirectory;
    Exit;
  end;
  LogPath := AddBackslash(LogDirectory) + 'winfsp-' + GetDateTimeString('yyyymmdd-hhnnss', '-', ':') + '.log';
  // Default WinFsp features include its runtime. No developer components are needed.
  // Disable MSI Restart Manager shutdown as well as all automatic restarts.
  Parameters := '/i "' + MsiPath + '" /qn /norestart REBOOT=ReallySuppress ' +
    'MSIRESTARTMANAGERCONTROL=Disable /L*v "' + LogPath + '"';
  Log('Installing bundled WinFsp. Log: ' + LogPath);
  if not Exec(ExpandConstant('{sys}\msiexec.exe'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then begin
    Result := FmtMessage(CustomMessage('WinFspFailed'), [IntToStr(ResultCode), LogPath]);
    Exit;
  end;
  case ResultCode of
    0: Log('WinFsp installation succeeded.');
    3010, 1641: begin
      DriverRestartRequired := True;
      Log('WinFsp installation succeeded and requires a restart.');
    end;
  else
    Result := FmtMessage(CustomMessage('WinFspFailed'), [IntToStr(ResultCode), LogPath]);
    Exit;
  end;
  if (not DriverRestartRequired) and (not WinFspInstalled) then
    Result := FmtMessage(CustomMessage('WinFspMissing'), [LogPath]);
end;

procedure RemoveLegacyShortcut(LinkPath: String);
var
  Shell, Shortcut: Variant;
  TargetPath: String;
begin
  if not FileExists(LinkPath) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(LinkPath);
    TargetPath := Shortcut.TargetPath;
    if CompareText(TargetPath, ExpandConstant('{app}\Mountly.App.exe')) = 0 then
      if not DeleteFile(LinkPath) then Log('Legacy shortcut was retained: ' + LinkPath);
  except
    Log('Legacy shortcut was retained because its target could not be verified: ' + LinkPath);
  end;
end;

procedure RemoveLegacyApplicationFiles;
var
  LegacyFiles: TArrayOfString;
  Index: Integer;
  LegacyPath: String;
begin
  // Run only after the replacement is installed; remove exact legacy program
  // filenames in this installation, never configuration, logs or cache folders.
  if not FileExists(ExpandConstant('{app}\Mountloom.App.exe')) then Exit;
  SetArrayLength(LegacyFiles, 7);
  LegacyFiles[0] := 'Mountly.App.exe';
  LegacyFiles[1] := 'Mountly.App.dll';
  LegacyFiles[2] := 'Mountly.App.deps.json';
  LegacyFiles[3] := 'Mountly.App.runtimeconfig.json';
  LegacyFiles[4] := 'Mountly.Core.dll';
  LegacyFiles[5] := 'Mountly.App.pdb';
  LegacyFiles[6] := 'Mountly.Core.pdb';
  for Index := 0 to GetArrayLength(LegacyFiles) - 1 do begin
    LegacyPath := AddBackslash(ExpandConstant('{app}')) + LegacyFiles[Index];
    if FileExists(LegacyPath) then
      if not DeleteFile(LegacyPath) then Log('Legacy application file was retained: ' + LegacyPath);
  end;
  RemoveLegacyShortcut(ExpandConstant('{autoprograms}\Mountly.lnk'));
  RemoveLegacyShortcut(ExpandConstant('{autodesktop}\Mountly.lnk'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ProcessError: String;
begin
  if CurStep = ssInstall then begin
    ProcessError := CheckRunningProcesses;
    if ProcessError <> '' then RaiseException(ProcessError);
  end;
  if CurStep = ssPostInstall then RemoveLegacyApplicationFiles;
end;

function NeedRestart: Boolean;
begin
  Result := DriverRestartRequired;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if DriverRestartRequired then Result := 3010;
end;

function InitializeUninstall: Boolean;
var
  ProcessError: String;
begin
  // Block newly launched app instances for the entire uninstall, too.
  if CheckForMutexes('Global\Mountly-Setup') then begin
    Result := False;
    SuppressibleMsgBox(SetupMessage(msgSetupAlreadyRunning), mbError, MB_OK, IDOK);
    Exit;
  end;
  CreateMutex('Global\Mountly-Setup');
  ProcessError := CheckRunningProcesses;
  Result := ProcessError = '';
  if not Result then SuppressibleMsgBox(ProcessError, mbError, MB_OK, IDOK);
end;
