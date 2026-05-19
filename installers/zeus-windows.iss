; Openhpsdr Zeus Installer Script for Inno Setup
; Requires Inno Setup 6.2 or later: https://jrsoftware.org/isinfo.php
;
; One installer, one set of files, two Start Menu shortcuts. The shipped
; binary (OpenhpsdrZeus.exe) serves both launch modes — service mode by
; default (LAN HTTP on :6060, browser-driven UI) and desktop mode when
; invoked with --desktop (Photino native window, loopback only).

#define MyAppName "Openhpsdr Zeus"
#define MyAppShortName "Zeus"
#define MyAppPublisher "Brian Keating (EI6LF) and contributors"
#define MyAppURL "https://github.com/brianbruff/openhpsdr-zeus"
#define MyAppExeName "OpenhpsdrZeus.exe"

; Version will be passed via /DMyAppVersion="x.y.z" command line parameter
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

; Target architecture: pass /DArch=arm64 from CI to build the Windows-on-ARM
; installer. Defaults to x64 so local iscc runs without explicit args still
; produce the historical x64 installer. Inno Setup 6.3+ accepts both "x64"
; and "arm64" as ArchitecturesAllowed identifiers.
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
; AppId pinned to the historical service-mode AppId so this installer
; upgrades existing service-mode installs in place. Operators who had the
; separate "Zeus Desktop" edition (AppId B23E7F4A-...) keep that install
; alongside the new combined product until they uninstall it manually from
; Settings → Apps; the release notes call this out.
AppId={{8F2E3B1C-9A4D-4E6F-B7C3-1D5A9E8F2B4C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppShortName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=.\output
OutputBaseFilename=openhpsdr-zeus-{#MyAppVersion}-win-{#Arch}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64 arm64
ArchitecturesAllowed={#Arch}
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon";       Description: "{cm:CreateDesktopIcon} (Zeus)";       GroupDescription: "{cm:AdditionalIcons}"
Name: "desktopiconserver"; Description: "Create a &server-mode desktop icon (Zeus Server)"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\OpenhpsdrZeus\bin\Release\net10.0\win-{#Arch}\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Two Start Menu shortcuts under the "Openhpsdr Zeus" group:
;   1. "Openhpsdr Zeus"       — desktop edition (Photino window, --desktop flag)
;   2. "Openhpsdr Zeus Server" — server edition (LAN bind, Photino status
;                                window with URLs + Stop button, --server flag)
; Both desktop icons are tasks the operator opts into. The desktop-mode
; checkbox is on by default; server-mode is unchecked so the typical
; first-time installer doesn't auto-create both icons.
Name: "{group}\{#MyAppName}";        Filename: "{app}\{#MyAppExeName}"; Parameters: "--desktop"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} Server"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--server";  IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppShortName}";        Filename: "{app}\{#MyAppExeName}"; Parameters: "--desktop"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{autodesktop}\{#MyAppShortName} Server"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--server";  IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopiconserver

[Run]
; Post-install launch lands the operator in the desktop window — no console,
; no browser dance. Server mode is one Start Menu click away for the LAN /
; remote operator.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--desktop"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure InitializeWizard;
begin
  WizardForm.LicenseAcceptedRadio.Checked := True;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    MsgBox('This application requires Windows 64-bit.', mbError, MB_OK);
    Result := False;
  end;
end;
