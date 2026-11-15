; Inno Setup 6 script for the TaskScope installer.
; Build: iscc /DAppVersion=1.0.0 /DSourceExe=path\to\TaskScope.exe installer\TaskScope.iss
; SourceExe should be the self-contained single-file build (see .github/workflows/release.yml).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\TaskScope.exe"
#endif
#ifndef OutputDir
  #define OutputDir "..\publish"
#endif

[Setup]
AppId={{A80A4CD9-1965-4E1A-A1C4-3FB82112E503}
AppName=TaskScope
AppVersion={#AppVersion}
AppVerName=TaskScope {#AppVersion}
AppPublisher=King0fgames
AppPublisherURL=https://github.com/King0fgames/TaskScope
AppSupportURL=https://github.com/King0fgames/TaskScope/issues
DefaultDirName={autopf}\TaskScope
DefaultGroupName=TaskScope
DisableProgramGroupPage=yes
; Install for the current user by default (no admin prompt); the user can choose all users.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=TaskScope-{#AppVersion}-setup
UninstallDisplayIcon={app}\TaskScope.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\TaskScope"; Filename: "{app}\TaskScope.exe"
Name: "{autodesktop}\TaskScope"; Filename: "{app}\TaskScope.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TaskScope.exe"; Description: "{cm:LaunchProgram,TaskScope}"; Flags: nowait postinstall skipifsilent

; Settings in %AppData%\TaskScope are left in place on uninstall so a reinstall keeps them.
