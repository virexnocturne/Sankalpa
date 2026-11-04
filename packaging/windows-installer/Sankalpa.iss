; =====================================================================
; Sankalpa Windows Installer Script (Inno Setup 6+)
; Tamper-Resistant Productivity & Focus Blocker
; =====================================================================

#define MyAppName "Sankalpa"
#ifndef MyAppVersion
#define MyAppVersion "1.0.1"
#endif
#define MyAppPublisher "Sankalpa"
#define MyAppURL "https://github.com/virex/Sankalpa"
#define MyAppExeName "Sankalpa.exe"
#define MyServiceExeName "Blocker.Service.exe"
#define MyHostExeName "Blocker.NativeHost.exe"
#define MyCliExeName "sankalpa-cli.exe"

[Setup]
; Unique Application ID for version tracking and updates
AppId={{8B1D4F3C-7A45-42E1-98F1-2B4A1D9C3E55}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\dist
OutputBaseFilename=Sankalpa-Setup-{#MyAppVersion}
SetupIconFile=..\..\src\Blocker.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

; Elevation is REQUIRED to install and manage the Windows Service and system hosts/DNS blocking
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Launch Sankalpa automatically on Windows logon (Recommended)"; GroupDescription: "Startup Options:"

[Files]
; Primary Release Binaries from Staging or Release directory
Source: "..\..\dist\Sankalpa-Release\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.old,*.old2,*.tmp,logs\*,data\*"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\app.ico"
Name: "{group}\Sankalpa (CLI)"; Filename: "{cmd}"; Parameters: "/k """"{app}\{#MyCliExeName}"""" --help"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\app.ico"; Tasks: desktopicon

[Registry]
; Run at Windows Logon for current user
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

; AppCompatFlags: Prevent redundant UAC prompts when user launches the desktop dashboard
Root: HKLM; Subkey: "Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"; ValueType: string; ValueName: "{app}\{#MyAppExeName}"; ValueData: "~ RUNASINVOKER"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"; ValueType: string; ValueName: "{app}\{#MyAppExeName}"; ValueData: "~ RUNASINVOKER"; Flags: uninsdeletevalue

; AppUserModelId Registration for Windows Shell Action Center / Toast Notifications
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#MyAppName}"; ValueType: string; ValueName: "DisplayName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#MyAppName}"; ValueType: string; ValueName: "IconUri"; ValueData: "{app}\Assets\app.ico"; Flags: uninsdeletevalue

[Run]
; 1. Stop existing service if running prior to configuration
Filename: "{sys}\sc.exe"; Parameters: "stop SankalpaService"; Flags: runhidden; StatusMsg: "Preparing service..."; Check: ServiceExists('SankalpaService')

; 2. Delete existing service definition if present to ensure fresh parameters
Filename: "{sys}\sc.exe"; Parameters: "delete SankalpaService"; Flags: runhidden; StatusMsg: "Refreshing service registration..."; Check: ServiceExists('SankalpaService')

; 3. Create Windows Service with auto-start
Filename: "{sys}\sc.exe"; Parameters: "create SankalpaService binPath= """"{app}\{#MyServiceExeName}"""" start= auto DisplayName= ""Sankalpa Enforcer Service"""; Flags: runhidden; StatusMsg: "Registering Sankalpa Enforcer Service..."

; 4. Service Description
Filename: "{sys}\sc.exe"; Parameters: "description SankalpaService ""Tamper-resistant enforcer service for Sankalpa focus blocker."""; Flags: runhidden

; 5. Service Recovery Watchdog (Auto-restart instantly on crash / abnormal kill)
Filename: "{sys}\sc.exe"; Parameters: "failure SankalpaService reset= 0 actions= restart/0/restart/0/restart/0"; Flags: runhidden

; 6. Service Security Descriptor (allow interactive users to query/control service)
Filename: "{sys}\sc.exe"; Parameters: "sdset SankalpaService ""D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;IU)(A;;CCLCSWRPWPDTLOCRRC;;;SU)(A;;CCLCSWRPWPDTLOCRRC;;;AU)"""; Flags: runhidden

; 7. Start the Service immediately
Filename: "{sys}\sc.exe"; Parameters: "start SankalpaService"; Flags: runhidden; StatusMsg: "Starting Sankalpa Enforcer Service..."

; 8. Register Native Messaging Host for Chrome, Edge, Brave, and Firefox
Filename: "{app}\{#MyHostExeName}"; Parameters: "--register"; Flags: runhidden; StatusMsg: "Registering browser extension bridge..."

; 9. Launch Desktop UI upon completion
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Gracefully terminate UI and service on uninstallation
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "stop SankalpaService"; Flags: runhidden; Check: ServiceExists('SankalpaService')
Filename: "{sys}\sc.exe"; Parameters: "delete SankalpaService"; Flags: runhidden; Check: ServiceExists('SankalpaService')
Filename: "{app}\{#MyHostExeName}"; Parameters: "--unregister"; Flags: runhidden

[Code]
// Helper function to check if a Windows service is registered
function ServiceExists(ServiceName: string): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('sc.exe', 'query ' + ServiceName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// Pre-installation cleanup
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  // Force terminate any running UI process before staging files
  Exec('taskkill.exe', '/F /IM Sankalpa.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM Blocker.NativeHost.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Post-uninstall: Ask user if they wish to remove custom user data and profiles
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, LegacyDataDir, CommonDataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Sankalpa');
    LegacyDataDir := ExpandConstant('{localappdata}\Blocker');
    CommonDataDir := ExpandConstant('{commonappdata}\Sankalpa');

    if DirExists(DataDir) or DirExists(LegacyDataDir) or DirExists(CommonDataDir) then
    begin
      if MsgBox('Do you want to delete your Sankalpa block history, statistics, and profile data?' + #13#10#13#10 +
                'Select "Yes" to remove all data and logs completely, or "No" to keep your profiles for future installations.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        if DirExists(DataDir) then DelTree(DataDir, True, True, True);
        if DirExists(LegacyDataDir) then DelTree(LegacyDataDir, True, True, True);
        if DirExists(CommonDataDir) then DelTree(CommonDataDir, True, True, True);
      end;
    end;
  end;
end;
