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
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[CustomMessages]
desktopiconDesc=Ярлык на рабочем столе
english.desktopiconDesc=Desktop shortcut
german.desktopiconDesc=Verknüpfung auf dem Desktop
french.desktopiconDesc=Raccourci sur le bureau
spanish.desktopiconDesc=Acceso directo en el escritorio
portuguese.desktopiconDesc=Atalho na área de trabalho
brazilianportuguese.desktopiconDesc=Atalho na área de trabalho
chinesesimplified.desktopiconDesc=桌面快捷方式
japanese.desktopiconDesc=デスクトップショートカット
groupAdditional=Дополнительно:
english.groupAdditional=Additional:
german.groupAdditional=Zusätzlich:
french.groupAdditional=Supplément :
spanish.groupAdditional=Adicional:
portuguese.groupAdditional=Adicional:
brazilianportuguese.groupAdditional=Adicional:
chinesesimplified.groupAdditional=附加：
japanese.groupAdditional=追加：
openbrowserName=Открыть интерфейс в браузере
english.openbrowserName=Open interface in browser
german.openbrowserName=Oberfläche im Browser öffnen
french.openbrowserName=Ouvrir l'interface dans le navigateur
spanish.openbrowserName=Abrir la interfaz en el navegador
portuguese.openbrowserName=Abrir a interface no navegador
brazilianportuguese.openbrowserName=Abrir a interface no navegador
chinesesimplified.openbrowserName=在浏览器中打开界面
japanese.openbrowserName=ブラウザで画面を開く
runnowDesc=Запустить BpMonitor сейчас
english.runnowDesc=Launch BpMonitor now
german.runnowDesc=BpMonitor jetzt starten
french.runnowDesc=Lancer BpMonitor maintenant
spanish.runnowDesc=Iniciar BpMonitor ahora
portuguese.runnowDesc=Iniciar o BpMonitor agora
brazilianportuguese.runnowDesc=Iniciar o BpMonitor agora
chinesesimplified.runnowDesc=立即启动 BpMonitor
japanese.runnowDesc=今すぐ BpMonitor を起動

[Tasks]
Name: "desktopicon"; Description: "{cm:desktopiconDesc}"; GroupDescription: "{cm:groupAdditional}"

[Files]
; только программа: data/config.json/backups/logs в исходной папке не существует на машине пользователя —
; ставим явным списком, чтобы случайно не утащить чужой data
Source: "..\bin\Release\net481\BpMonitor.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net481\BpMonitor.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net481\index.html"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net481\help.html"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net481\i18n.js"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net481\lang\*"; DestDir: "{app}\lang"; Flags: ignoreversion
Source: "..\bin\Release\net481\alarm.mp3"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net481\app.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net481\*.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\{cm:openbrowserName}"; Filename: "http://localhost:8090/"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:runnowDesc}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; явно ничего не удаляем из {app}: data\ и config.json — пользовательские, остаются после удаления

[Code]
// при обновлении: если процесс работает — Inno спросит закрывать (CloseApplications=yes)
