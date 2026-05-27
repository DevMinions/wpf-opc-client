; Dc.App 安装包脚本（Inno Setup 6.x）
;
; 用法（build.ps1 -Target installer 会自动包一层；手工调试时也可直接编译）：
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyAppVersion=1.0.0 installer\Dc.iss
;
; 注意：iscc 编译只在 Windows 上跑。开发期在 Linux 写脚本，构建在 Windows 触发。

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName "Dc"
#define MyAppExeName "Dc.App.exe"
#define MyAppPublisher "Dc"
#define MyAppURL "https://git.adamyu.top"

[Setup]
; AppId — 不要随版本变；版本号升级时用同一 AppId 实现"原地覆盖"
AppId={{9E5B7C3E-FA1C-4DAF-9F2F-AE2EA2B6F8C1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
LicenseFile=..\LICENSE
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=Dc-Setup-x64-{#MyAppVersion}
OutputDir=..\build\installer
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; 安装目录不允许带空格的运行时风险低 — 我们走 Program Files 默认
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Dc.App 主程序（self-contained publish 产物）
Source: "..\build\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; 运维脚本（OPC 注册同步 / 诊断）
Source: "..\scripts\*.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion

; wire 协议文档（broker 实现方参考）
Source: "..\docs\wire-format.md"; DestDir: "{app}\docs"; Flags: ignoreversion

; 法律文件
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

; 卸载时 Inno 自动清掉它装的所有文件；不在这里删用户产生的 sqlite.db / logs/ — 重装可恢复

[Code]
// 检测 OPC Core Components 是否安装；缺失就提示用户去装（不强制阻止）
// OpcEnum 是 32-bit COM 服务，永远在 SysWOW64（即使 64-bit 系统）
function OpcCoreComponentsInstalled(): Boolean;
begin
  Result := FileExists(ExpandConstant('{syswow64}\OpcEnum.exe')) or
            FileExists(ExpandConstant('{sys}\OpcEnum.exe'));
end;

function InitializeSetup(): Boolean;
var
  Msg: String;
  Response: Integer;
begin
  Result := True;
  if not OpcCoreComponentsInstalled() then
  begin
    Msg := '未检测到 OPC Foundation Core Components（缺 OpcEnum.exe）。' + #13#10 + #13#10 +
           '该组件提供 OPCEnum 服务，是 OPC DA/AE 客户端按 IP 扫描服务器的前置条件。' + #13#10 + #13#10 +
           '不装 Core Components 仍可使用：连接时手填 CLSID（"高级"区域）即可绕过 OPCEnum。' + #13#10 + #13#10 +
           '点"是"继续安装 Dc.App，点"否"取消（先去装 Core Components 再回来）。';
    Response := MsgBox(Msg, mbConfirmation, MB_YESNO);
    if Response = IDNO then
      Result := False;
  end;
end;
