; ═══════════════════════════════════════════════════════════════════════
;  渡口 Ferry · Windows 安装程序（Inno Setup 6）
;
;  架构（v0.8 起）：
;    渡口助手.exe   ← C# WinForms，.NET Framework 4.8（Win10/11 自带），44 KB
;    ssh-tunnel-keeper.ps1  ← 隧道保活（复用，没重写）
;    dual-proxy.mjs         ← 本地 HTTP↔SOCKS 双协议代理（复用，没重写）
;    config.json            ← 安装时写入；**token 留空，等用户在软件里激活**
;
;  设计要点（都是踩过坑才定下来的）：
;
;  1. 为什么用 Inno Setup 而不是 NSIS
;     中文向导开箱即用，原生支持 [Tasks] 三个独立勾选项和 [Run] 的 postinstall。
;
;  2. 为什么一份 exe 能服务所有用户
;     账号 IP 从**自身文件名**解析（门户按 Ferry-Setup-10.141.59.235.exe 发），
;     再拿这个 IP 向门户要配置。二进制完全一样，门户只改文件名，不用重编译。
;     用户重命名了 exe 就弹一页让他手填 IP。
;
;  3. 为什么不再调 client-setup.ps1
;     v0.7 是用 PowerShell 脚本当安装向导（两层向导，很难看）。现在安装向导
;     由 Inno 自己出，配置由下面的 [Code] 段直接写 —— 少一层、少一坨代码。
;
;  4. 卸载必须调 渡口助手.exe /uninstall
;     Inno 的卸载器只能删文件，删不掉**系统代理设置**和正在跑的 node/ssh 进程。
;     不清理的话用户卸载完就「全网上不去」。而且我们的卸载还要**还原用户原来
;     的代理**（state.json 里备份着），这个只有程序自己知道。
;
;  5. 密钥不在安装时输入
;     config.json 里的 token 留空装进去，用户第一次打开软件在面板里填密钥。
;     这样门户发出去的安装包不含任何凭据，转发给别人也用不了。
; ═══════════════════════════════════════════════════════════════════════

#define AppName "渡口 Ferry"
#define AppVersion "0.8.0"
#define AppExeName "渡口助手.exe"
#define PortalBase "http://10.13.24.1:8080"

[Setup]
AppId={{8F3A2B1C-4D5E-4F60-9A7B-1C2D3E4F5A6B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Ferry
DefaultDirName={localappdata}\Ferry
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=Ferry-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
; 覆盖安装时先让旧版自己退出，否则 exe 被占用写不进去
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon";   Description: "创建桌面快捷方式";                 GroupDescription: "快捷方式："; Flags: unchecked
Name: "startmenuicon"; Description: "创建开始菜单快捷方式";             GroupDescription: "快捷方式："
Name: "autostart";     Description: "开机自动启动（断网时自动接管）";   GroupDescription: "启动选项："; Flags: unchecked

[Files]
; 只有一个文件：SOCKS5+HTTP 双协议代理和隧道保活都已用 C# 写进 exe，
; 目标机器只需要 Windows 自带的 ssh.exe，不再需要 Node.js / PowerShell 脚本。
Source: "..\dist\渡口助手.exe";              DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\渡口助手"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "在借网与直连之间切换"; Tasks: startmenuicon
Name: "{autodesktop}\渡口助手";  Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "在借网与直连之间切换"; Tasks: desktopicon
Name: "{userstartup}\渡口自动启动"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: autostart

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动渡口助手"; \
  Flags: postinstall nowait skipifsilent

[UninstallRun]
; 交给程序自己清：还原原代理 → 杀隧道/代理进程 → 删自启
Filename: "{app}\{#AppExeName}"; Parameters: "/uninstall"; \
  Flags: runhidden waituntilterminated; RunOnceId: "FerryCleanup"

[UninstallDelete]
Type: files;          Name: "{app}\config.json"
Type: files;          Name: "{app}\state.json"
Type: filesandordirs; Name: "{app}"

[Code]
var
  IpPage: TInputQueryWizardPage;
  NeedIpPage: Boolean;
  AccountIP: string;
  FetchedCfg: string;
  FetchError: string;

{ ── 从自身文件名解析 IP：Ferry-Setup-10.141.59.235.exe ──────────── }
{ 从文件名解析 IPv4。Ferry-Setup-10.141.59.235.exe → 10.141.59.235

  为什么要重写这一版：
    旧实现把「由数字/点/横线组成的连续串」当候选，但 `Ferry-Setup-10.141.59.235.exe`
    里的 `-` 会被吸收、`.exe` 的 `.` 也会被吸收，候选变成 `.10.141.59.235.`
    —— 五个点、首尾都是点，Dots=3 的判断永远不成立，于是解析失败、
    安装程序白弹一页「请输入你的局域网 IP」。用户明确的文件名就是这么长，
    所以这是必现 bug，不是边界情况。

  新做法：
    ① 先取纯文件名（去掉路径）
    ② 把 - _ 空格 统一替换成点
    ③ 扫所有 [0-9.] 连续段，去掉首尾点后校验「恰好 4 段、每段 1–3 位数字」
}
function ParseIPFromExeName(const FileName: string): string;
var
  S, Cur: string;
  I, J, Dots, SegLen: Integer;
  C: Char;
  Valid: Boolean;
begin
  Result := '';
  S := ExtractFileName(FileName);
  for I := 1 to Length(S) do
    if (S[I] = '-') or (S[I] = '_') or (S[I] = ' ') then S[I] := '.';

  Cur := '';
  for I := 1 to Length(S) + 1 do
  begin
    if I <= Length(S) then C := S[I] else C := ' ';
    if (C >= '0') and (C <= '9') or (C = '.') then
      Cur := Cur + C
    else
    begin
      { 去掉首尾的点 —— 这是旧版栽的地方 }
      while (Cur <> '') and (Cur[1] = '.') do Delete(Cur, 1, 1);
      while (Cur <> '') and (Cur[Length(Cur)] = '.') do Delete(Cur, Length(Cur), 1);
      if Cur <> '' then
      begin
        Dots := 0;
        SegLen := 0;
        Valid := True;
        for J := 1 to Length(Cur) + 1 do
        begin
          if (J > Length(Cur)) or (Cur[J] = '.') then
          begin
            if (SegLen < 1) or (SegLen > 3) then Valid := False;
            SegLen := 0;
            if J <= Length(Cur) then Dots := Dots + 1;
          end
          else
          begin
            if (Cur[J] < '0') or (Cur[J] > '9') then Valid := False;
            SegLen := SegLen + 1;
          end;
        end;
        if Valid and (Dots = 3) then
        begin
          Result := Cur;
          Exit;
        end;
      end;
      Cur := '';
    end;
  end;
end;

{ ── 极简 HTTP GET（WinHTTP，Windows 自带）──────────────────────── }
function HttpGet(const Url: string; TimeoutMs: Integer): string;
var
  Http: Variant;
begin
  Result := '';
  try
    Http := CreateOleObject('WinHttp.WinHttpRequest.5.1');
    Http.Open('GET', Url, False);
    Http.SetTimeouts(TimeoutMs, TimeoutMs, TimeoutMs, TimeoutMs);
    { 强制直连，不走系统代理。
      必须这样做：本机若开着 Clash / 公司代理，请求会从代理出去，
      服务端看到的 client_address 就变成 127.0.0.1 或代理出口，
      自动识别的账号 IP 直接错掉。 }
    try
      Http.SetProxy(1, '');   { 1 = HTTPREQUEST_PROXYSETTING_DIRECT }
    except
    end;
    Http.Send;
    if Http.Status = 200 then
      Result := Http.ResponseText
    else
      Result := '';
  except
    Result := '';
  end;
end;

{ 从 JSON 里抠一个字符串字段。只处理 "key":"value" 这种简单形态，
  够用就行 —— 我们只取 accountIp / portalUrl 两个字段。 }
function JsonStr(const Json, Key: string): string;
var
  Pat, Rest: string;
  P, Q: Integer;
begin
  Result := '';
  Pat := '"' + Key + '"';
  P := Pos(Pat, Json);
  if P = 0 then Exit;
  Rest := Copy(Json, P + Length(Pat), Length(Json));
  P := Pos(':', Rest);
  if P = 0 then Exit;
  Rest := Copy(Rest, P + 1, Length(Rest));
  P := Pos('"', Rest);
  if P = 0 then Exit;
  Rest := Copy(Rest, P + 1, Length(Rest));
  Q := Pos('"', Rest);
  if Q = 0 then Exit;
  Result := Copy(Rest, 1, Q - 1);
end;

{ 向导初始化：先问门户「我这台机器是什么账号」，再退回文件名解析。

  为什么优先问门户：client_address 是服务端看到的、这台机器真实的局域网 IP，
  比「靠文件名传」和「自己枚举网卡」都准：
    · 文件名会被浏览器改名（Ferry-Setup-1.2.3.4 (1).exe）、被人手改
    · 枚举网卡在多网卡 / 虚拟网卡 / VPN 环境下很容易挑错那块
  文件名解析保留为兜底 —— 覆盖「装到另一台机器上」的场景。
}
procedure InitializeWizard;
begin
  AccountIP := '';

  { ① 问门户：不带 ip 参数，服务端按来源地址判定 }
  FetchedCfg := HttpGet('{#PortalBase}/api/config', 4000);
  if FetchedCfg <> '' then
    AccountIP := JsonStr(FetchedCfg, 'accountIp');

  { ② 门户没答上来，退回文件名 }
  if AccountIP = '' then
    AccountIP := ParseIPFromExeName(ExpandConstant('{srcexe}'));

  { ③ 都没有才让用户填 }
  NeedIpPage := (AccountIP = '');

  IpPage := CreateInputQueryPage(wpWelcome,
    '账号确认',
    '请输入你的局域网 IP',
    '门户给你分配账号时用的是这台机器的局域网 IP（形如 10.141.59.235）。' + #13#10 +
    '正常情况下这一步会自动跳过 —— 安装程序会向门户询问当前机器的 IP。' + #13#10 + #13#10 +
    '只有连不上门户、又从文件名解析不到时，才需要你手动填。');
  IpPage.Add('局域网 IP：', False);
  IpPage.Values[0] := '';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (PageID = IpPage.ID) and (not NeedIpPage) then
    Result := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if NeedIpPage and (CurPageID = IpPage.ID) then
  begin
    AccountIP := Trim(IpPage.Values[0]);
    if AccountIP = '' then
    begin
      { 静默安装没有界面可填 —— 绝不能弹窗，那会把 /VERYSILENT 卡死。
        直接放行：config 里 accountIp 留空，用户打开软件激活时
        服务端会按请求来源的真实 IP 建号，不需要这个字段。 }
      if WizardSilent then
        Exit;
      MsgBox('请填写局域网 IP。', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Url, CfgPath, Fallback: string;
  Fetched: Boolean;
begin
  if CurStep = ssPostInstall then
  begin
    CfgPath := ExpandConstant('{app}\config.json');
    Fetched := False;

    if AccountIP <> '' then
    begin
      Url := '{#PortalBase}/api/config?ip=' + AccountIP;
      FetchedCfg := HttpGet(Url, 8000);
      if FetchedCfg <> '' then
      begin
        SaveStringToFile(CfgPath, FetchedCfg, False);
        Fetched := True;
      end
      else
        FetchError := '连不上门户 ' + '{#PortalBase}' + ' 或该 IP 还没有账号。';
    end
    else
      FetchError := '没有识别到账号 IP。';

    if not Fetched then
    begin
      { 拉不到也要写一份**能用的**：门户地址填死、token 留空，
        用户打开软件后填密钥照样能激活 ——
        激活接口只认「请求来源 IP + 密钥」，不需要 config 里预先写 IP。 }
      Fallback :=
        '{' + #13#10 +
        '  "remoteHost": "10.13.24.1",' + #13#10 +
        '  "sshPort": 22,' + #13#10 +
          '  "sshUser": "pcserver",' + #13#10 +
        '  "httpPort": 10801,' + #13#10 +
        '  "accountIp": "' + AccountIP + '",' + #13#10 +
        '  "token": "",' + #13#10 +
        '  "portalUrl": "{#PortalBase}",' + #13#10 +
        '  "fallbackProxy": "",' + #13#10 +
        '  "expiresAt": 0' + #13#10 +
        '}';
      SaveStringToFile(CfgPath, Fallback, False);
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { 到「准备安装」页时把拉取结果告诉用户，别让他装完才发现配置是空的 }
  if CurPageID = wpReady then
  begin
    if FetchError <> '' then
      MsgBox(FetchError + #13#10 + #13#10 +
        '仍可继续安装。装好后打开渡口助手，在「输入密钥激活」里填入管理员给的密钥即可。',
        mbInformation, MB_OK);
  end;
end;
