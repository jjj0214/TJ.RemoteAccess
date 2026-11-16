; TJ.RemoteAccess 0.5.4-preview compact per-user installer
; Copyright 2026 TJ.RemoteAccess contributors, GPL-2.0-or-later.
; Inno Setup creates a NATIVE single EXE. The only .NET runtime in its
; payload is the Microsoft WindowsDesktop 10 LTS runtime under {app}\Runtime.
; Server/Viewer/Setup use AppHostRelativeDotNet=../Runtime, never global .NET.
#ifndef RepoRoot
  #error RepoRoot must be supplied via ISCC /DRepoRoot=<absolute checkout>
#endif
#define AppVersion "0.5.4-preview"

[Setup]
AppId={{F13E9AC0-7D9A-43EC-9D04-82EADE1DB315}
AppName=TJ.RemoteAccess
AppVerName=TJ.RemoteAccess {#AppVersion} (Compact Preview)
AppVersion={#AppVersion}
AppPublisher=TJ.RemoteAccess contributors
AppPublisherURL=https://github.com/jjj0214/TJ.RemoteAccess
AppSupportURL=https://github.com/jjj0214/TJ.RemoteAccess/issues
AppUpdatesURL=https://github.com/jjj0214/TJ.RemoteAccess/releases
AppCopyright=GPL-2.0-or-later; see LICENSE and GPL-Source
DefaultDirName={localappdata}\Programs\TJ.RemoteAccess
DefaultGroupName=TJ.RemoteAccess
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
UsePreviousAppDir=yes
UsePreviousLanguage=yes
AllowNoIcons=yes
MinVersion=10.0
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
WizardResizable=yes
ShowComponentSizes=yes
Compression=lzma2
SolidCompression=yes
InternalCompressLevel=ultra
OutputDir={#RepoRoot}\out\compact-dist
OutputBaseFilename=TJ.RemoteAccess-{#AppVersion}-Compact-Setup-win-x64
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=TJ.RemoteAccess {#AppVersion}
UninstallDisplayIcon={app}\Server\TJ.RemoteAccess.Server.exe
LicenseFile={#RepoRoot}\LICENSE
VersionInfoVersion=0.5.4.0
VersionInfoDescription=TJ.RemoteAccess Compact Preview Setup
VersionInfoProductName=TJ.RemoteAccess

[Types]
Name: "both"; Description: "Server + Viewer + Setup (recommended)"
Name: "server"; Description: "Server + Setup"
Name: "viewer"; Description: "Viewer + Setup"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "runtime"; Description: ".NET 10 Windows Desktop Runtime (shared, offline)"; Types: both server viewer custom; Flags: fixed
Name: "setup"; Description: "Setup / certificate management"; Types: both server viewer custom; Flags: fixed
Name: "server"; Description: "TJ.RemoteAccess Server (tray)"; Types: both server
Name: "viewer"; Description: "TJ.RemoteAccess Viewer"; Types: both viewer

[Files]
; This runtime includes Microsoft.NETCore.App and WindowsDesktop.App exactly once.
; Never copy the runtime into individual Server/Viewer/Setup directories.
Source: "{#RepoRoot}\out\compact-publish\Runtime\*"; DestDir: "{app}\Runtime"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: runtime
Source: "{#RepoRoot}\out\compact-publish\Server\*"; DestDir: "{app}\Server"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: server
Source: "{#RepoRoot}\out\compact-publish\Viewer\*"; DestDir: "{app}\Viewer"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: viewer
Source: "{#RepoRoot}\out\compact-publish\Setup\*"; DestDir: "{app}\Setup"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: setup
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}\Licenses"; Flags: ignoreversion
Source: "{#RepoRoot}\THIRD_PARTY_NOTICES.md"; DestDir: "{app}\Licenses"; Flags: ignoreversion
Source: "{#RepoRoot}\out\compact-dist\source.zip"; DestDir: "{app}\GPL-Source"; Flags: ignoreversion
Source: "{#RepoRoot}\out\compact-dist\runtime-provenance.json"; DestDir: "{app}\Licenses"; Flags: ignoreversion

[Icons]
Name: "{group}\TJ.RemoteAccess Server"; Filename: "{app}\Server\TJ.RemoteAccess.Server.exe"; Components: server
Name: "{group}\TJ.RemoteAccess Viewer"; Filename: "{app}\Viewer\TJ.RemoteAccess.Viewer.exe"; Components: viewer
Name: "{group}\Uninstall TJ.RemoteAccess"; Filename: "{uninstallexe}"
Name: "{userdesktop}\TJ.RemoteAccess Viewer"; Filename: "{app}\Viewer\TJ.RemoteAccess.Viewer.exe"; Components: viewer; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a Viewer desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Code]
function InitializeSetup(): Boolean;
begin
  { Never auto-initialize or overwrite server/user TLS pins, passwords, PFX,
    viewer profiles, Windows Service policy, firewall or TightVNC. }
  Result := True;
end;
