; Service Monitor setup (Inno Setup 6). Built by build\publish.ps1 -Installer, which passes /DAppVersion=x.y.z,
; /DSourceDir=<folder with the published exe> and /O<output folder>.
;
; Installs the program, its Start menu shortcut and an uninstaller. The background monitor (the ServiceMonitor
; service) is still set up from the app's Overview page once services and channels are chosen, exactly as before.
; On an upgrade the running monitor is stopped, updated and started again.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist"
#endif
#define AppExe "ServiceMonitor.exe"
#define ServiceName "ServiceMonitor"
; Until 1.0.8 the product was called "Windows Service Monitor" ("Windows" is Microsoft's trademark) and installed here.
#define LegacyDir "{autopf}\Windows Service Monitor"
; Until 1.0.9 the service and the settings folder had these names. "--install" (run on an upgrade, see [Code]) replaces
; the service and moves the folder.
#define LegacyServiceName "WinServiceMonitor"
#define DataDir "{commonappdata}\ServiceMonitor"
#define LegacyDataDir "{commonappdata}\WindowsServiceMonitor"

[Setup]
; Never change: upgrades find the existing install by it.
AppId={{23C4B8D9-B06C-4995-AD49-FD19476DBB40}
AppName=Service Monitor
AppVersion={#AppVersion}
AppVerName=Service Monitor {#AppVersion}
AppPublisher=Service Monitor
AppCopyright=Copyright (c) 2026 David Majchrowski
AppComments=Sends email, chat and push notifications when Windows services stop, crash or hang.
; Open source under the MIT License: shown on the License Agreement page, which must be accepted to continue.
LicenseFile=..\LICENSE
; Fixed folder: the app installs and updates the service from C:\Program Files\Service Monitor
; (AppInfo.InstallDirectory), and the service must run from a folder non-administrators cannot change. Not the previous
; folder either: before the rename it was {#LegacyDir}.
DefaultDirName={autopf}\Service Monitor
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=ServiceMonitor-Setup-{#AppVersion}
SetupIconFile=..\src\ServiceMonitor\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=Service Monitor
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; [Code] stops the monitor and closes open windows itself (PrepareToInstall); this is a fallback for anything else.
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
; Written by build\publish.ps1: the licenses of the .NET runtime and packages bundled into the exe.
Source: "{#SourceDir}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; An install from before the rename: its folder (program and uninstaller; the uninstall entry now points here) and
; its shortcuts. PrepareToInstall has stopped the monitor and closed the old program first.
Type: filesandordirs; Name: "{#LegacyDir}"
Type: files; Name: "{autoprograms}\Windows Service Monitor.lnk"
Type: files; Name: "{autodesktop}\Windows Service Monitor.lnk"

[Icons]
; The same shortcut the app creates when it installs the monitor (MonitorServiceInstaller), so there is only ever one.
Name: "{autoprograms}\Service Monitor"; Filename: "{app}\{#AppExe}"; Comment: "Watches Windows services and sends notifications when they stop, crash or hang."
Name: "{autodesktop}\Service Monitor"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; The app always runs as administrator (its manifest asks for it). runascurrentuser is needed: postinstall entries
; otherwise run as the original (unelevated) user, and CreateProcess then fails with error 740.
Filename: "{app}\{#AppExe}"; Description: "Open Service Monitor"; Flags: postinstall nowait skipifsilent runascurrentuser

[UninstallRun]
; Stops and removes the monitor service and the app's Start menu shortcut. Settings are kept unless the user asks
; for them to be deleted ([Code] below).
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"
; Close a window still running from this folder, and wait for the service's process to exit, so the file can be removed.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -Command ""$exe = '{app}\{#AppExe}'; function Running {{ Get-CimInstance Win32_Process | Where-Object {{ $_.ExecutablePath -eq $exe } }; Running | Where-Object {{ $_.SessionId -ne 0 } | Invoke-CimMethod -MethodName Terminate | Out-Null; for ($i = 0; $i -lt 50 -and (Running); $i++) {{ Start-Sleep -Milliseconds 200 }"""; Flags: runhidden waituntilterminated; RunOnceId: "CloseRunningCopies"

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
var
  ServiceExisted: Boolean;
  DeleteSettings: Boolean;

function Run(const FileName, Params: String; Show: Integer): Integer;
var
  Code: Integer;
begin
  if not Exec(FileName, Params, '', Show, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

// Remembered once: the service is stopped during the upgrade, and "sc query" answers 1060 when it does not exist.
function InitializeSetup: Boolean;
begin
  ServiceExisted := (Run(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', SW_HIDE) <> 1060) or
    (Run(ExpandConstant('{sys}\sc.exe'), 'query {#LegacyServiceName}', SW_HIDE) <> 1060);
  Result := True;
end;

function QuotedForPowerShell(const Path: String): String;
begin
  Result := Path;
  StringChangeEx(Result, '''', '''''', True);
  Result := '''' + Result + '''';
end;

// Stops the monitor service (it sends its "Monitoring stopped" notices first, within 25 s) and closes desktop windows
// running from the install folder or the folder used before the rename. Restart Manager cannot close windows in other
// users' sessions, and closes none while the stopped service's process is still exiting, so this waits until every
// copy has exited.
procedure StopMonitorAndCloseRunningCopies;
begin
  Run(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -NonInteractive -Command "' +
    '$exes = @(' + QuotedForPowerShell(ExpandConstant('{app}\{#AppExe}')) + ', ' +
    QuotedForPowerShell(ExpandConstant('{#LegacyDir}\{#AppExe}')) + '); ' +
    'foreach ($svc in @(Get-Service {#ServiceName}, {#LegacyServiceName} -ErrorAction SilentlyContinue)) { ' +
    'if ($svc.Status -ne ''Stopped'') { ' +
    'try { $svc.Stop() } catch { }; try { $svc.WaitForStatus(''Stopped'', [TimeSpan]::FromSeconds(45)) } catch { } } }; ' +
    'function Running { Get-CimInstance Win32_Process | Where-Object { $exes -contains $_.ExecutablePath } }; ' +
    'Running | Where-Object { $_.SessionId -ne 0 } | Invoke-CimMethod -MethodName Terminate | Out-Null; ' +
    'for ($i = 0; $i -lt 50 -and (Running); $i++) { Start-Sleep -Milliseconds 200 }"', SW_HIDE);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if ServiceExisted or FileExists(ExpandConstant('{app}\{#AppExe}')) or FileExists(ExpandConstant('{#LegacyDir}\{#AppExe}')) then
    StopMonitorAndCloseRunningCopies;
end;

// On an upgrade, "--install" updates the service registration and starts the monitor again (the program is already
// in place, so it copies nothing). From before 1.0.9 it also replaces the WinServiceMonitor service and moves the
// settings folder. New installs leave all this to the app's "Install & start monitoring" button.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Code, I: Integer;
  Output: TExecOutput;
  Details: String;
begin
  if (CurStep <> ssPostInstall) or not ServiceExisted then Exit;
  WizardForm.StatusLabel.Caption := 'Starting the background monitor again...';
  if not ExecAndCaptureOutput(ExpandConstant('{app}\{#AppExe}'), '--install', '', SW_HIDE, ewWaitUntilTerminated, Code, Output) then
    Code := -1;
  if Code <> 0 then
  begin
    Details := '';
    for I := 0 to GetArrayLength(Output.StdErr) - 1 do
      Details := Details + #13#10 + Output.StdErr[I];
    SuppressibleMsgBox('Service Monitor is updated, but its background monitor could not be started again ' +
      '(code ' + IntToStr(Code) + ').' + Details + #13#10#13#10 +
      'Open Service Monitor and click Install & start monitoring on the Overview page.', mbError, MB_OK, IDOK);
  end;
end;

// Settings hold the watched services, the alert channels and their saved passwords and tokens. Keep them unless the
// user says otherwise; silent uninstalls keep them.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data, LegacyData: String;
begin
  Data := ExpandConstant('{#DataDir}');
  LegacyData := ExpandConstant('{#LegacyDataDir}');
  if (CurUninstallStep = usUninstall) and (DirExists(Data) or DirExists(LegacyData)) then
    DeleteSettings := SuppressibleMsgBox('Also delete the settings?' + #13#10#13#10 +
      'This removes the watched services, the alert channels and their saved passwords and tokens from ' + Data + '.' + #13#10#13#10 +
      'Choose No to keep them for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
  if (CurUninstallStep = usPostUninstall) and DeleteSettings then
  begin
    DelTree(Data, True, True, True);
    DelTree(LegacyData, True, True, True);
    // The app's event log sources too (the one from before the rename as well); when settings are kept they stay, so
    // past events remain readable.
    RegDeleteKeyIncludingSubkeys(HKLM, 'SYSTEM\CurrentControlSet\Services\EventLog\Application\Service Monitor');
    RegDeleteKeyIncludingSubkeys(HKLM, 'SYSTEM\CurrentControlSet\Services\EventLog\Application\Windows Service Monitor');
  end;
end;
