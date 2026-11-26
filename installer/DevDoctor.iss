#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#ifndef PayloadDir
  #error PayloadDir must name the complete self-contained Release payload.
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{4D627B25-2DCB-4CFA-AF04-3C57F9E267BC}
AppName=DevDoctor
AppVersion={#AppVersion}
AppVerName=DevDoctor {#AppVersion}
AppPublisher=DevDoctor
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=DevDoctor Windows 开发环境诊断助手
DefaultDirName={localappdata}\Programs\DevDoctor
DefaultGroupName=DevDoctor
DisableDirPage=no
DisableProgramGroupPage=no
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=DevDoctor-v{#AppVersion}-win-x64-Setup
SetupIconFile=assets\DevDoctor.ico
UninstallDisplayIcon={app}\DevDoctor.exe
UninstallDisplayName=DevDoctor {#AppVersion}
UninstallFilesDir={app}\.devdoctor-uninstall
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern dynamic
SetupLogging=yes
SetupMutex=DevDoctor.Setup
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
CloseApplications=yes
RestartApplications=no
InfoBeforeFile=install-info.txt
ShowLanguageDialog=no
UsePreviousLanguage=no

[Languages]
Name: chinesesimplified; MessagesFile: languages\ChineseSimplified.isl

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "portable.mode"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Remove only the legacy installer-created English shortcut from this group.
Type: files; Name: "{group}\Uninstall DevDoctor.lnk"

[Icons]
Name: "{group}\DevDoctor"; Filename: "{app}\DevDoctor.exe"; WorkingDir: "{app}"; AppUserModelID: DevDoctor.Desktop
Name: "{group}\卸载 DevDoctor"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{groupname}"; Filename: "{app}\DevDoctor.exe"; WorkingDir: "{app}"; AppUserModelID: DevDoctor.Desktop; Tasks: desktopicon

[Run]
Filename: "{app}\DevDoctor.exe"; Description: "运行 DevDoctor"; Flags: nowait postinstall skipifsilent unchecked

[Messages]
FinishedLabel=DevDoctor 已安装到您的计算机。%n%n设置、外观、SQLite 诊断历史与备份单独保存在本地应用数据目录。卸载和重新安装均保留这些数据。需要启动时，请主动勾选“运行 DevDoctor”或使用快捷方式。
ConfirmUninstall=您确定要卸载 %1 及其程序组件吗？%n%n设置、外观、SQLite 诊断历史、报告与备份将保留，不会删除用户数据。
UninstalledAll=%1 已成功从您的计算机卸载。%n%n用户设置、外观、SQLite 诊断历史、报告与备份均已保留。以后重新安装可继续使用。
UninstalledMost=已完成 %1 卸载，但部分程序文件无法删除。请先关闭占用程序后再检查安装目录。%n%n用户数据已保留，请勿删除保存设置、历史与备份的数据目录。

[UninstallDelete]
; This reserved directory contains only DevDoctor installer-owned files,
; including incomplete uninstall files left by an interrupted setup.
; User data is outside {app} and is never targeted.
Type: filesandordirs; Name: "{app}\.devdoctor-uninstall"

[Code]
#include "InterruptedInstall.iss"

var
  UpgradePage: TWizardPage;
  UpgradeMemo: TNewMemo;

procedure InitializeWizard;
var
  PreviousVersion, PreviousDirectory, Detail: String;
begin
  { Read the actual stable product registration; do not invent an old version. }
  if RegQueryStringValue(HKCU64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{4D627B25-2DCB-4CFA-AF04-3C57F9E267BC}_is1',
    'DisplayVersion', PreviousVersion) then
  begin
    RegQueryStringValue(HKCU64,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{4D627B25-2DCB-4CFA-AF04-3C57F9E267BC}_is1',
      'InstallLocation', PreviousDirectory);
    Detail := '检测到当前用户已安装 DevDoctor ' + PreviousVersion + '。' + #13#10#13#10 +
      '本次将安装 DevDoctor {#AppVersion}，无需先卸载旧版本。' + #13#10 +
      '原安装位置：' + PreviousDirectory + #13#10#13#10 +
      '程序文件将更新；设置、外观、SQLite 诊断历史、报告与备份继续保留。' + #13#10 +
      '建议先在应用中创建备份并关闭 DevDoctor，再继续安装。' + #13#10#13#10 +
      '如果安装被中断，请保留数据并重新运行此安装程序进行修复。' + #13#10 +
      '若遇错误，请保存安装日志；不保证强制终止后的自动回滚。';
    UpgradePage := CreateCustomPage(wpWelcome, '已有版本与升级说明',
      '检测到 DevDoctor ' + PreviousVersion + ' → {#AppVersion}');
    { The same native memo type as ReadyMemo preserves Unicode and follows
      the installer's light/dark style without hard-coded foreground colors. }
    UpgradeMemo := TNewMemo.Create(UpgradePage);
    UpgradeMemo.Parent := UpgradePage.Surface;
    UpgradeMemo.SetBounds(0, 0, UpgradePage.SurfaceWidth, UpgradePage.SurfaceHeight);
    UpgradeMemo.Anchors := [akLeft, akTop, akRight, akBottom];
    UpgradeMemo.Font.Assign(WizardForm.ReadyMemo.Font);
    UpgradeMemo.Color := WizardForm.ReadyMemo.Color;
    UpgradeMemo.ReadOnly := True;
    UpgradeMemo.WordWrap := True;
    UpgradeMemo.ScrollBars := ssVertical;
    UpgradeMemo.WantReturns := False;
    UpgradeMemo.WantTabs := False;
    UpgradeMemo.Text := '以下说明可以选择复制，并可滚动查看。' + #13#10#13#10 + Detail;
    Log('Detected existing DevDoctor version: ' + PreviousVersion);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\portable.mode')) then
    Result := '此文件夹包含 DevDoctor 便携版。请选择单独的安装文件夹；如需迁移数据，请使用应用中的“备份与恢复”功能。用户数据未被更改。'
  else
    Result := PreserveRecognizedInstallTemps;
end;
