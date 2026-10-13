#ifndef PackageDirectory
  #error PackageDirectory is required.
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required.
#endif
#ifndef Architecture
  #define Architecture "x64"
#endif
#define ProductVersion GetFileVersion(PackageDirectory + "\QingFDM.exe")

[Setup]
AppId={{7A37761F-1B55-4D8D-A2D6-304D62935BEC}
AppName=Qing Free Download Manager
AppVersion={#ProductVersion}
AppPublisher=Qing Free Download Manager contributors
AppPublisherURL=https://github.com/qingkongwanli0l/qing-free-download-manager
DefaultDirName={localappdata}\Programs\QingFDM
DefaultGroupName=Qing Free Download Manager
PrivilegesRequired=lowest
MinVersion=6.1sp1
OutputDir={#OutputDirectory}
OutputBaseFilename=QingFDM-{#ProductVersion}-{#Architecture}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=assets\qing.ico
UninstallDisplayIcon={app}\QingFDM.exe
LicenseFile=LICENSE
CloseApplications=yes
RestartApplications=no
#if Architecture == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "installer\ChineseSimplified.isl"

[CustomMessages]
english.FullInstallation=Full installation
chinesesimplified.FullInstallation=完整安装
english.CustomInstallation=Custom installation
chinesesimplified.CustomInstallation=自定义安装
english.DesktopShortcut=Create a desktop shortcut
chinesesimplified.DesktopShortcut=创建桌面快捷方式
english.RegisterFileTypes=Register Qing as a choice for torrent files and magnet links
chinesesimplified.RegisterFileTypes=将 Qing 注册为种子文件和磁力链接的可选处理程序
english.CoreComponents=Qing and download components
chinesesimplified.CoreComponents=Qing 和下载组件
english.BrowserExtensions=Browser extensions
chinesesimplified.BrowserExtensions=浏览器扩展
english.LaunchQing=Launch Qing Free Download Manager
chinesesimplified.LaunchQing=运行 Qing Free Download Manager
english.NetFrameworkRequired=Install Microsoft .NET Framework 4.8 before installing Qing Free Download Manager.
chinesesimplified.NetFrameworkRequired=请先安装 Microsoft .NET Framework 4.8，再安装 Qing Free Download Manager。
english.AppLanguage=en
chinesesimplified.AppLanguage=zh-CN

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: unchecked
Name: "filetypes"; Description: "{cm:RegisterFileTypes}"; Flags: unchecked

[Types]
Name: "full"; Description: "{cm:FullInstallation}"
Name: "custom"; Description: "{cm:CustomInstallation}"; Flags: iscustom

[Components]
Name: "core"; Description: "{cm:CoreComponents}"; Types: full custom; Flags: fixed
Name: "browser"; Description: "{cm:BrowserExtensions}"; Types: full
Name: "browser\chromium"; Description: "Chrome / Edge / Chromium"; Types: full
Name: "browser\firefox"; Description: "Firefox"; Types: full

[Files]
Source: "{#PackageDirectory}\QingFDM.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDirectory}\QingFDM.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDirectory}\QingFDM.NativeHost.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDirectory}\QingFDM.NativeHost.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDirectory}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDirectory}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDirectory}\Mono.Nat.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDirectory}\Mono.Nat-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "installer\ChineseSimplified-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDirectory}\engines\*"; DestDir: "{app}\engines"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDirectory}\plugins\qing\yt_dlp_plugins\postprocessor\*.py"; DestDir: "{app}\plugins\qing\yt_dlp_plugins\postprocessor"; Flags: ignoreversion
Source: "{#PackageDirectory}\browser\chromium\*"; DestDir: "{app}\browser\chromium"; Components: browser\chromium; Flags: ignoreversion recursesubdirs
Source: "{#PackageDirectory}\browser\firefox\*"; DestDir: "{app}\browser\firefox"; Components: browser\firefox; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\Qing Free Download Manager"; Filename: "{app}\QingFDM.exe"
Name: "{autodesktop}\Qing Free Download Manager"; Filename: "{app}\QingFDM.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\QingFDM.exe"; Parameters: "--register-file-types"; Tasks: filetypes; Flags: runhidden waituntilterminated
Filename: "{app}\QingFDM.exe"; Description: "{cm:LaunchQing}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\QingFDM.exe"; Parameters: "--unregister-integration"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveQingIntegration"

[UninstallDelete]
Type: files; Name: "{app}\initial-language.txt"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\initial-language.txt'), CustomMessage('AppLanguage'), False);
end;

function InitializeSetup(): Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM32, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
  if not Result then
    MsgBox(CustomMessage('NetFrameworkRequired') + #13#10 + 'https://dotnet.microsoft.com/download/dotnet-framework/net48', mbError, MB_OK);
end;
