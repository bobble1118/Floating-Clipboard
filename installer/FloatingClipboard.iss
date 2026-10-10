; Build: dotnet publish first (see README), then ISCC installer\FloatingClipboard.iss
#define Exe "..\bin\publish\FloatingClipboard.exe"
#define AppVersion GetVersionNumbersString(Exe)

[Setup]
AppId={{6F1C2B8E-4D7A-4E61-9B3C-2A5D8E0F7C14}
AppName=Floating Clipboard
AppVersion={#AppVersion}
AppPublisherURL=https://github.com/bobble1118/Floating-Clipboard
; Per-user install, no admin prompt: the app only touches HKCU and %APPDATA%
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\FloatingClipboard
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\FloatingClipboard.exe
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
InfoAfterFile=howto.txt
OutputDir=..\bin\installer
OutputBaseFilename=FloatingClipboard-Setup

[Files]
Source: "{#Exe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Floating Clipboard"; Filename: "{app}\FloatingClipboard.exe"

[Registry]
; The app writes its own "Start with Windows" entry; only make sure uninstalling removes it
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "FloatingClipboard"; ValueType: none; Flags: uninsdeletevalue

[Run]
Filename: "{app}\FloatingClipboard.exe"; Description: "Launch Floating Clipboard"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im FloatingClipboard.exe"; Flags: runhidden; RunOnceId: "StopApp"

[Code]
// The app ignores WM_CLOSE (closing only hides it), so stop it the hard way before replacing the exe
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im FloatingClipboard.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
