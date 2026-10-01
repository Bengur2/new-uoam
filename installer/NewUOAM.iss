; Installer for players (Inno Setup 6). Built by tools\Release\release.ps1, which passes:
;   /DAppVersion=x.y.z /DSourceDir=<publish\app\x.y.z\NewUOAM> /DOutputDir=<publish\app\x.y.z>
; Output: NewUOAM-Setup.exe (version-less name, so
; https://github.com/Bengur2/new-uoam/releases/latest/download/NewUOAM-Setup.exe always works).
;
; Per-user install into %LocalAppData%\Programs\NewUOAM, no admin needed: the map updates itself
; in place (docs/RELEASE.md), so its folder must stay writable for the player. Settings and
; markers live in %LocalAppData%\NewUOAM and are never touched, not even by the uninstaller.
; ASCII only: the Czech UI texts come from Inno's own Czech.isl.

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z (use tools\Release\release.ps1)
#endif

[Setup]
AppId={{EB22808A-50C0-4E3D-8163-743F81B5D31A}
AppName=new UOAM
AppVersion={#AppVersion}
AppVerName=new UOAM {#AppVersion}
AppPublisher=Bengur2
AppPublisherURL=https://bengur2.github.io/new-uoam/
AppSupportURL=https://github.com/Bengur2/new-uoam
DefaultDirName={localappdata}\Programs\NewUOAM
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=NewUOAM-Setup
SetupIconFile=..\src\NewUOAM.App\Assets\app.ico
UninstallDisplayIcon={app}\NewUOAM.App.exe
UninstallDisplayName=new UOAM
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Closes a running map (Restart Manager) before overwriting it.
CloseApplications=yes
VersionInfoVersion={#AppVersion}

[Languages]
Name: "cs"; MessagesFile: "compiler:Languages\Czech.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\new UOAM"; Filename: "{app}\NewUOAM.App.exe"
Name: "{autodesktop}\new UOAM"; Filename: "{app}\NewUOAM.App.exe"; Tasks: desktopicon

[Run]
; shellexec: the map asks for admin rights itself (UAC), which a plain CreateProcess can't show.
Filename: "{app}\NewUOAM.App.exe"; Description: "{cm:LaunchProgram,new UOAM}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallDelete]
; Files a self-update added after installation, and *.old-update leftovers.
Type: filesandordirs; Name: "{app}"
