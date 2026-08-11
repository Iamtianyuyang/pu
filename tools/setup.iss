; pu~ installer script (Inno Setup 6, per-user, no admin).
; Build: ISCC.exe tools/setup.iss   (publish.ps1 does this automatically when ISCC is found)
#define AppBrand "噗~噗噗~~噗噗噗噗~~~~"
#define AppVersion "0.0.520"

[Setup]
AppId={{8F3A7C2E-5B1D-4E9F-A6C8-2D7B9E1F4A35}
AppName={#AppBrand}
AppVersion={#AppVersion}
AppPublisher=pu~
DefaultDirName={localappdata}\Programs\pu~
DefaultGroupName={#AppBrand}
PrivilegesRequired=lowest
OutputDir=..\publish
#ifdef Full
OutputBaseFilename=pu-setup-full
#else
OutputBaseFilename=pu-setup
#endif
SetupIconFile=..\assets\pu.ico
Compression=lzma2/ultra64
WizardStyle=modern
UninstallDisplayIcon={app}\pu.exe
VersionInfoVersion=0.0.520.0
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "..\publish\pu\pu.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\pu\使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion
#ifdef Full
Source: "vendor\ffmpeg\ffmpeg.exe"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion
Source: "vendor\ffmpeg\ffprobe.exe"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion
#endif

[Icons]
Name: "{group}\{#AppBrand}"; Filename: "{app}\pu.exe"
Name: "{group}\卸载 {#AppBrand}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\pu.exe"; Parameters: "--register"; Flags: runhidden waituntilterminated; StatusMsg: "Registering context menu..."

[UninstallRun]
Filename: "{app}\pu.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterMenu"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Pu"
