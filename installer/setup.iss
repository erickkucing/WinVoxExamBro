; ExamBro Inno Setup Installer Script
; Windows 10/11 64-bit Kiosk Exam Browser & Configuration Tool

#define MyAppName "ExamBro"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "ExamBro Security Team"
#define MyAppURL "https://github.com/exambro"
#define MyAppExamExeName "ExamBro.Exam.exe"
#define MyAppConfigExeName "ExamBro.Config.exe"

[Setup]
AppId={{9B7E3E5D-4F12-4C1F-9E45-8B41D8291F09}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=
OutputDir=..\dist\installer
OutputBaseFilename=ExamBro-Setup-v{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExamExeName}

[Languages]
Name: "indonesian"; MessagesFile: "compiler:Languages\Indonesian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
; Set ACL on ProgramData folder: Administrators = Full Control, Users = Read & Execute
Name: "{commonappdata}\ExamBro"; Permissions: users-readexec admins-full
Name: "{commonappdata}\ExamBro\logs"; Permissions: users-modify admins-full

[Files]
; Published single-file binaries
Source: "..\dist\ExamBro.Exam\{#MyAppExamExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\ExamBro.Config\{#MyAppConfigExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Optional WebView2 Bootstrapper if bundled in installer folder
Source: "MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist

[Icons]
; Main Exam Browser shortcut for students
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExamExeName}"; Comment: "Exam Browser Kiosk"
; Admin / Proctor Configuration shortcut
Name: "{group}\Pengaturan ExamBro (Admin)"; Filename: "{app}\{#MyAppConfigExeName}"; Comment: "Konfigurasi ExamBro untuk Pengawas/Admin"
; Desktop icon
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExamExeName}"; Tasks: desktopicon; Comment: "Exam Browser Kiosk"

[Run]
; Option to launch config app after initial installation
Filename: "{app}\{#MyAppConfigExeName}"; Description: "Jalankan Pengaturan ExamBro (Buat kata sandi & Start URL)"; Flags: postinstall runascurrentuser skipifsilent

[Code]
// Helper function to check if WebView2 Evergreen Runtime is installed
function IsWebView2Installed(): Boolean;
var
  InstalledVersion: String;
begin
  Result := False;
  // Check 64-bit registry path
  if RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BD6-D65022BBC543}', 'pv', InstalledVersion) then
  begin
    if (InstalledVersion <> '') and (InstalledVersion <> '0.0.0.0') then
      Result := True;
  end;
  // Check 32-bit (WOW6432Node) registry path
  if not Result then
  begin
    if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BD6-D65022BBC543}', 'pv', InstalledVersion) then
    begin
      if (InstalledVersion <> '') and (InstalledVersion <> '0.0.0.0') then
        Result := True;
    end;
  end;
  // Check Current User registry path
  if not Result then
  begin
    if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BD6-D65022BBC543}', 'pv', InstalledVersion) then
    begin
      if (InstalledVersion <> '') and (InstalledVersion <> '0.0.0.0') then
        Result := True;
    end;
  end;
end;

// Bootstrap / Download WebView2 Runtime if missing
function InstallWebView2(): Boolean;
var
  ResultCode: Integer;
  BootstrapperPath: String;
begin
  Result := True;
  if not IsWebView2Installed() then
  begin
    BootstrapperPath := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
    if FileExists(BootstrapperPath) then
    begin
      // Execute bundled bootstrapper silently
      Exec(BootstrapperPath, '/silent /install', '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
      Result := (ResultCode = 0) and IsWebView2Installed();
    end
    else
    begin
      // Notify admin that WebView2 Runtime is required
      MsgBox('Microsoft Edge WebView2 Runtime tidak ditemukan pada komputer ini.' + #13#10 +
             'Silakan unduh dan pasang WebView2 Runtime dari situs resmi Microsoft sebelum menjalankan ExamBro.',
             mbInformation, MB_OK);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    InstallWebView2();
  end;
end;

// Clean up recovery files and session profile during uninstall
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  LocalAppExamBroDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    LocalAppExamBroDir := ExpandConstant('{localappdata}\ExamBro');
    if DirExists(LocalAppExamBroDir) then
    begin
      DelTree(LocalAppExamBroDir, True, True, True);
    end;
  end;
end;
