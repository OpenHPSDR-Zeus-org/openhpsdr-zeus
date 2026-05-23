; Openhpsdr Zeus Installer Script for Inno Setup
; Requires Inno Setup 6.2 or later: https://jrsoftware.org/isinfo.php
;
; One installer, one set of files, two Start Menu shortcuts. The shipped
; binary (OpenhpsdrZeus.exe) serves both launch modes — service mode by
; default (LAN HTTP on :6060, browser-driven UI) and desktop mode when
; invoked with --desktop (Photino native window, loopback only).

#define MyAppName "OpenHPSDR-Zeus"
#define MyAppShortName "OpenHPSDR-Zeus"
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
; separate "Zeus Desktop" edition (AppId B23E7F4A-...) or one of the
; legacy stray AppIds (8F2E3B1C-...4D) used by a short-lived prerelease are
; silently uninstalled by the [Code] section below before install proceeds,
; so the upgrader is not left with two Zeus entries in Settings → Apps.
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
; Microsoft Visual C++ 2015-2022 Redistributable — required for wdsp.dll and
; miniaudio.dll to load. Fresh Windows installs do not include this runtime,
; and without it Zeus silently falls back to its synthetic DSP engine
; (blank panadapter, no audio, no transmit power). The release CI downloads
; the matching vc_redist.{#Arch}.exe from Microsoft (https://aka.ms/vs/17/release/)
; into this directory before iscc runs. The file is then bundled with the
; installer and run during install via the [Run] section below, skipped via
; VCRedistInstalled() if a compatible runtime is already present.
;
; For LOCAL iscc builds (developers), download the matching redist into the
; installers/ folder manually:
;   x64:   curl -L -o installers/vc_redist.x64.exe   https://aka.ms/vs/17/release/vc_redist.x64.exe
;   arm64: curl -L -o installers/vc_redist.arm64.exe https://aka.ms/vs/17/release/vc_redist.arm64.exe
Source: "vc_redist.{#Arch}.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: VCRedistNeeded

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
; Install the Microsoft Visual C++ 2015-2022 Redistributable BEFORE launching
; Zeus. wdsp.dll and miniaudio.dll are MSVC-linked and will not load without
; vcruntime140.dll / msvcp140.dll present in the system. Skipped via
; VCRedistNeeded() if a compatible runtime is already installed (avoids the
; ~10-second silent reinstall and the UAC prompt for operators who already
; have it). See issue #452 for the symptoms this fix prevents.
Filename: "{tmp}\vc_redist.{#Arch}.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Microsoft Visual C++ Runtime..."; Check: VCRedistNeeded; Flags: waituntilterminated

; Post-install launch lands the operator in the desktop window — no console,
; no browser dance. Server mode is one Start Menu click away for the LAN /
; remote operator.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--desktop"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Legacy AppIds shipped by earlier Zeus installers. We silently uninstall
// any present before the new files are laid down so the operator does not
// end up with multiple "Zeus" / "Zeus Desktop" entries in Settings → Apps.
//   B23E7F4A-1C8D-4DB6-9E5F-3A8C2B7D4E91 — old standalone "Zeus Desktop" build
//   8F2E3B1C-9A4D-4E6F-B7C3-1D5A9E8F2B4D — typo'd AppId used by one prerelease
// The current AppId (...B4C) is owned by THIS installer — never uninstall it,
// or Inno will undo what it just installed.
const
  LegacyAppId_Desktop = '{B23E7F4A-1C8D-4DB6-9E5F-3A8C2B7D4E91}_is1';
  LegacyAppId_Typo    = '{8F2E3B1C-9A4D-4E6F-B7C3-1D5A9E8F2B4D}_is1';

function GetUninstallString(const AppId: String): String;
var
  RegPath: String;
  Value: String;
begin
  Result := '';
  RegPath := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + AppId;
  // Per-user installs land under HKCU; per-machine under HKLM (both 32- and
  // 64-bit views). PrivilegesRequired=lowest means most installs are HKCU,
  // but we check both so an operator who once ran the installer elevated
  // still gets cleaned up.
  if RegQueryStringValue(HKCU, RegPath, 'QuietUninstallString', Value) then
    Result := Value
  else if RegQueryStringValue(HKCU, RegPath, 'UninstallString', Value) then
    Result := Value
  else if RegQueryStringValue(HKLM, RegPath, 'QuietUninstallString', Value) then
    Result := Value
  else if RegQueryStringValue(HKLM, RegPath, 'UninstallString', Value) then
    Result := Value;
end;

procedure UninstallLegacy(const AppId: String);
var
  UninstallCmd: String;
  ResultCode: Integer;
begin
  UninstallCmd := GetUninstallString(AppId);
  if UninstallCmd = '' then
    Exit;
  // Inno's UninstallString quotes the exe; pass the flags as the parameter
  // half. /VERYSILENT suppresses UI, /SUPPRESSMSGBOXES eats the "really?"
  // prompt, /NORESTART keeps us from rebooting the operator mid-install.
  UninstallCmd := RemoveQuotes(UninstallCmd);
  Exec(UninstallCmd, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

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

// VCRedistNeeded — returns True when the Microsoft Visual C++ 2015-2022
// Redistributable is NOT already installed on this machine. The [Files]
// entry for vc_redist.{#Arch}.exe and the [Run] step that invokes it are
// both gated on this so we don't waste ~10 seconds reinstalling the runtime
// and triggering a needless UAC prompt for operators who already have it.
//
// Detection: registry presence under HKLM\SOFTWARE\Microsoft\VisualStudio\
// 14.0\VC\Runtimes\<arch> is the Microsoft-blessed signal. The 14.0 line
// covers the entire 2015..2022 ABI-compatible family — any 14.x install
// satisfies our DLL dependencies.
//
// Per-arch keys: x64 is checked under the 64-bit registry view; arm64 ships
// to a sibling key with the same shape. The "Installed" DWORD = 1 indicates
// presence.
function VCRedistInstalled: Boolean;
var
  InstalledFlag: Cardinal;
begin
  // The {#Arch} preprocessor token expands to "x64" or "arm64" at iscc time,
  // so the resulting Pascal string is the literal registry path for whichever
  // installer flavour we're building.
  Result := False;
  if RegQueryDWordValue(HKLM64,
       'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{#Arch}',
       'Installed', InstalledFlag) and (InstalledFlag = 1) then
    Result := True;
end;

function VCRedistNeeded: Boolean;
begin
  Result := not VCRedistInstalled;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // ssInstall fires after the wizard has confirmed install but before files
  // are copied — the right window to evict prior installs so their files
  // don't linger on disk next to ours.
  if CurStep = ssInstall then
  begin
    UninstallLegacy(LegacyAppId_Desktop);
    UninstallLegacy(LegacyAppId_Typo);
  end;
end;
