#ifndef PublishDir
  #error 请通过 scripts/Build-Release.ps1 指定发布目录
#endif
#ifndef ReleaseDir
  #error 缺少安装包输出目录
#endif
#define AppVersion "1.0.0-rc.1"
#define AppExe "NovaPlayer.exe"

[Setup]
AppId={{88ACF80B-19A8-4F10-9C69-E8336AE516CF}
AppName=Nova Player
AppVersion={#AppVersion}
AppVerName=Nova Player {#AppVersion}（内部验收候选版）
AppPublisher=Nova Player
DefaultDirName={localappdata}\Programs\NovaPlayer
DefaultGroupName=Nova Player
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19044
OutputDir={#ReleaseDir}
OutputBaseFilename=NovaPlayer-1.0.0-rc.1-win-x64-setup
SetupIconFile=..\NovaPlayer.App\Assets\NovaPlayer.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
InfoBeforeFile={#PublishDir}\RELEASE-NOTES.txt
Uninstallable=yes
VersionInfoVersion=1.0.0.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked
Name: "fileassociations"; Description: "注册视频打开方式（默认播放器由您在 Windows 设置中选择）"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Nova Player"; Filename: "{app}\{#AppExe}"
Name: "{userdesktop}\Nova Player"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; 仅注册自己的 ProgID 与候选能力；不修改扩展名默认值或受保护的 UserChoice。
Root: HKCU; Subkey: "Software\Classes\NovaPlayer.Video"; ValueType: string; ValueData: "Nova Player 视频"; Tasks: fileassociations; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\NovaPlayer.Video\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExe}"",0"; Tasks: fileassociations
Root: HKCU; Subkey: "Software\Classes\NovaPlayer.Video\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fileassociations
Root: HKCU; Subkey: "Software\NovaPlayer\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Nova Player"; Tasks: fileassociations; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\NovaPlayer\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Nova Player 本地视频播放器"; Tasks: fileassociations
Root: HKCU; Subkey: "Software\NovaPlayer\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: fileassociations
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Nova Player"; ValueData: "Software\NovaPlayer\Capabilities"; Tasks: fileassociations; Flags: uninsdeletevalue
#dim Extension[12] {"mp4", "mkv", "avi", "mov", "webm", "wmv", "flv", "m4v", "ts", "m2ts", "mpg", "mpeg"}
#define i
#sub RegisterExtension
Root: HKCU; Subkey: "Software\Classes\.{#Extension[i]}\OpenWithProgids"; ValueType: string; ValueName: "NovaPlayer.Video"; ValueData: ""; Tasks: fileassociations; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\NovaPlayer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".{#Extension[i]}"; ValueData: "NovaPlayer.Video"; Tasks: fileassociations
#endsub
#for {i = 0; i < 12; i++} RegisterExtension

[Run]
Filename: "{app}\{#AppExe}"; Description: "启动 Nova Player"; Flags: nowait postinstall skipifsilent

; 卸载保留 LocalAppData\NovaPlayer 下的数据库、进度、设置和日志，不执行递归用户数据删除。
