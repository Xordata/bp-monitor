; BpMonitor — установщик (PLAN bp.xor.world §15, п.7)
; Сборка: ISCC.exe /FBpMonitorSetup deploy\BpMonitor.iss   (из корня репо BPMonitor)
; Требуется собранный bin\Release\net481 (dotnet build -c Release).
; Данные пользователя (data\, config.json, backups\) НЕ трогаются ни при установке, ни при удалении.

#define MyAppName "BpMonitor — дневник давления"
#define MyAppVersion "2.0"
#define MyAppPublisher "xor.world"
#define MyAppURL "https://bp.xor.world"
#define MyAppExeName "BpMonitor.exe"

[Setup]
AppId={{8F3E2D71-5C4A-4B9E-9D2F-7A1C0B6E3F55}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\BpMonitor
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Z:\Temp\hermes-scratch
OutputBaseFilename=BpMonitorSetup
SetupIconFile=..\bin\Release\net481\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; GroupDescription: "Дополнительно:"

[Files]
; только программа: data/config.json/backups/logs в исходной папке не существует на машине пользователя —
; ставим явным списком, чтобы случайно не утащить чужой data
Source: "..\bin\Release\net481\BpMonitor.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net481\BpMonitor.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net481\index.html"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net481\alarm.mp3"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net481\app.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net481\*.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Открыть интерфейс в браузере"; Filename: "http://localhost:8090/"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить BpMonitor сейчас"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; явно ничего не удаляем из {app}: data\ и config.json — пользовательские, остаются после удаления

[Code]
// при обновлении: если процесс работает — Inno спросит закрывать (CloseApplications=yes)
