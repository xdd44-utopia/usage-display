; Built by scripts\build.ps1 once per architecture: ISCC /DArch=x64|arm64 UsageMonitor.iss
#ifndef Arch
  #define Arch "x64"
#endif
#define SourceExe "..\build\" + Arch + "\UsageMonitor.exe"
#define AppVersion GetVersionNumbersString(SourceExe)

[Setup]
AppId={{57F2AF8D-8784-4AEA-9955-E50B34342779}
AppName=Usage Monitor
AppVersion={#AppVersion}
AppVerName=Usage Monitor {#AppVersion}
VersionInfoVersion={#AppVersion}
; Per user, so installing and updating never ask for administrator approval. The folder matches where
; earlier versions moved themselves, so updating replaces those copies in place.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\UsageMonitor
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0
; The app holds this mutex while running, so setup and uninstall ask to quit it first.
AppMutex=UsageMonitor
UninstallDisplayIcon={app}\UsageMonitor.exe
UninstallDisplayName=Usage Monitor
WizardStyle=modern
OutputDir=..\build
OutputBaseFilename=UsageMonitor-Setup-{#Arch}

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Usage Monitor"; Filename: "{app}\UsageMonitor.exe"; Comment: "Codex and Claude Code usage in the notification area"

[Registry]
Root: HKCU; Subkey: "Software\UsageMonitor"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "UsageMonitor"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\UsageMonitor.exe"; Description: "Open Usage Monitor"; Flags: nowait postinstall skipifsilent
Filename: "{app}\UsageMonitor.exe"; Flags: nowait skipifnotsilent

[UninstallRun]
Filename: "{app}\UsageMonitor.exe"; Parameters: "--remove-claude-status-line"; RunOnceId: "RemoveClaudeStatusLine"; Flags: runhidden waituntilterminated

[Code]
function InitializeSetup(): Boolean;
var
  FindRec: TFindRec;
  ErrorCode: Integer;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
#if Arch == "x64"
  // On an ARM PC the x64 runtime lives in its own subfolder.
  if not Result and IsArm64 then
    Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\x64\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
#endif
  if Result then
    FindClose(FindRec)
  else if SuppressibleMsgBox('Usage Monitor needs the .NET 10 Desktop Runtime, which isn''t installed yet.' + #13#10#13#10 +
      'Open the download page? Run this setup again once it''s installed.', mbConfirmation, MB_YESNO, IDNO) = IDYES then
    ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;
