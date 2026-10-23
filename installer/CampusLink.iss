; CampusLink 安装程序（Inno Setup 6）
; 构建：tools\package.ps1 会传入 /DAppVersion 与 /DSourceDir

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\CampusLink-" + AppVersion
#endif

#define AppName "CampusLink 校园网认证助手"
#define AppExe "CampusLink.exe"
#define AppPublisher "Thomas455"
#define AppUrl "https://github.com/Thomas455/CampusLink"

[Setup]
AppId={{8F3B7C2A-5D4E-4C1B-9E7A-2F6D8C1B4A90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\CampusLink
DefaultGroupName=CampusLink
DisableProgramGroupPage=yes
OutputDir={#SourcePath}\..\dist
OutputBaseFilename=CampusLink-Setup-{#AppVersion}
SetupIconFile={#SourcePath}\..\CampusLink\Resources\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl"

; Inno 官方安装包不带中文语言文件，这里基于英文版覆盖常用文案（未覆盖的保持英文）
[Messages]
SetupAppTitle=安装程序
SetupWindowTitle=安装 - %1
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=现在将把 [name/ver] 安装到你的电脑上。%n%n继续之前，建议关闭其他正在运行的程序。
SelectDirDesc=选择安装位置
SelectDirLabel3=安装程序将把 [name] 安装到下面的文件夹。
SelectDirBrowseLabel=点击“下一步”继续；如需更换文件夹，点击“浏览”。
SelectTasksDesc=选择附加任务
SelectTasksLabel2=请选择安装 [name] 时需要的附加任务，然后点击“下一步”。
ReadyLabel1=准备安装
ReadyLabel2a=点击“安装”开始安装，或点击“上一步”检查设置。
PreparingDesc=正在准备安装，请稍候…
InstallingLabel=正在安装，请稍候…
FinishedHeadingLabel=完成 [name] 安装
FinishedLabelNoIcons=安装完成。
FinishedLabel=安装完成。
ClickFinish=点击“完成”退出安装程序。
CreateDesktopIcon=创建桌面快捷方式
AdditionalIcons=附加图标：
LaunchProgram=启动 [name]
UninstallProgram=卸载 [name]

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\CampusLink"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,CampusLink}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\CampusLink"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,CampusLink}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
