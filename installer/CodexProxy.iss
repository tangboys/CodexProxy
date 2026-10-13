#ifndef AppVersion
  #define AppVersion "0.12.0"
#endif
#ifndef ReleaseDir
  #define ReleaseDir SourcePath + "..\dist\" + AppVersion
#endif

[Setup]
AppId={{62D79D98-5724-4F95-9D62-18D63504CAB5}
AppName=Codex Proxy
AppVersion={#AppVersion}
AppVerName=Codex Proxy {#AppVersion}
AppPublisher=Codex Proxy
DefaultDirName={localappdata}\Programs\CodexProxy
DefaultGroupName=Codex Proxy
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
ShowLanguageDialog=yes
DisableProgramGroupPage=auto
OutputDir=..
OutputBaseFilename=CodexProxy-{#AppVersion}-setup-win-x64
SetupIconFile=..\src\CodexProxy\Assets\app.ico
UninstallDisplayIcon={app}\CodexProxy.exe
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.CreateDesktopIcon=创建桌面快捷方式
chinesesimplified.LaunchProxy=启动 Codex Proxy
chinesesimplified.UninstallProxy=卸载 Codex Proxy
chinesesimplified.RunningProxy=此安装目录中的 Codex Proxy 或其代理内核仍在运行。请在代理管理器中点击“退出代理”，然后重试。您的 Codex 不会被关闭。
chinesesimplified.ProcessCheckFailed=无法检查代理程序是否仍在运行。请稍后重试。
english.CreateDesktopIcon=Create a desktop shortcut
english.LaunchProxy=Launch Codex Proxy
english.UninstallProxy=Uninstall Codex Proxy
english.RunningProxy=Codex Proxy or its proxy core is still running from this installation directory. Click Exit Proxy in the manager, then try again. Your Codex will stay open.
english.ProcessCheckFailed=Could not check whether the proxy program is still running. Please try again later.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
#include "files.iss"

[Icons]
Name: "{group}\Codex Proxy"; Filename: "{app}\CodexProxy.exe"; WorkingDir: "{app}"; AppUserModelID: "CodexProxy.Desktop"
Name: "{group}\{cm:UninstallProxy}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Codex Proxy"; Filename: "{app}\CodexProxy.exe"; WorkingDir: "{app}"; AppUserModelID: "CodexProxy.Desktop"; Tasks: desktopicon

[Run]
Filename: "{app}\CodexProxy.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProxy}"; Flags: nowait postinstall skipifsilent

[Code]
function RunningProxyMessage: String;
var
  Locator, Services, Processes, Process: Variant;
  Index: Integer;
  GuiPath, CorePath, ProcessPath: String;
begin
  Result := '';
  GuiPath := ExpandConstant('{app}\CodexProxy.exe');
  CorePath := ExpandConstant('{app}\bin\mihomo.exe');
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name = ''CodexProxy.exe'' OR Name = ''mihomo.exe''');
    for Index := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(Index);
      if not VarIsNull(Process.ExecutablePath) then
      begin
        ProcessPath := Process.ExecutablePath;
        if (CompareText(ProcessPath, GuiPath) = 0) or (CompareText(ProcessPath, CorePath) = 0) then
        begin
          Result := CustomMessage('RunningProxy');
          Exit;
        end;
      end;
    end;
  except
    Result := CustomMessage('ProcessCheckFailed');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := RunningProxyMessage;
end;

function InitializeUninstall: Boolean;
var
  Message: String;
begin
  Message := RunningProxyMessage;
  Result := Message = '';
  if not Result then
    SuppressibleMsgBox(Message, mbError, MB_OK, IDOK);
end;
