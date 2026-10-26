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
#define AppVersion "0.9.22"
#define AppExeName "渡口助手.exe"
#define PortalBase "http://10.13.24.1:8080"

[Setup]
AppId={{8F3A2B1C-4D5E-4F60-9A7B-1C2D3E4F5A6B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Ferry
; 装到 %LOCALAPPDATA%\Ferry，而不是 Program Files。
;
; 为什么用 per-user 目录 + admin 安装这个组合：
;   · 配置文件（config.json / state.json）要和 exe 放一起，而且程序运行时要
;     频繁改写它。放 Program Files 的话每次写都要提权（虽然 exe 已是管理员，
;     但 Win11 的虚拟化重定向会让写入跑到 VirtualStore，排查起来很痛苦）。
;   · UAC 提权是**同一个用户**的令牌提升，{localappdata} 仍然解析成
;     提权前那个用户的路径（C:\Users\<你>\AppData\Local），不会装到
;     Administrator 账户下。只有「右键 → 以其他用户身份运行」才会跑偏。
;
; ISCC 会为此报 UsedUserAreasWarning。这里显式关掉并说明理由 ——
; 不是忽略问题，是确认过「单人单机、自己是管理员」这个场景下它是安全的。
UsedUserAreasWarning=no
DefaultDirName={localappdata}\Ferry
DisableProgramGroupPage=yes
DisableDirPage=yes
; PrivilegesRequired=lowest：per-user 安装，不弹 UAC。
;
; 为什么从 admin 改回 lowest（0.9.6）：
;   admin + {localappdata} 这个组合有坑 —— 标准用户靠另一个管理员账户提权时，
;   安装目录会落到**那个管理员账户**的 AppData 下，用户自己的账户里找不到，
;   表现是「装完不见了」。而且客户端从 0.9.6 起是 asInvoker，本来就不用提权。
;   装到当前用户自己的 %LOCALAPPDATA%\Ferry 才是自洽的。
;
; 为什么必须提权（实测过才定的，不是想当然）：
;   ① 客户端 exe 的 manifest 是 requireAdministrator（TUN 要建虚拟网卡
;      + 改路由表）。安装器如果用 lowest，[Run] 段调用它时会弹 UAC，
;      **静默安装（/VERYSILENT）直接卡在那里** —— 装完等于没装。
;   ② 免 UAC 的开机自启靠计划任务（RunLevel=HighestAvailable）。
;      实测普通权限创建这种任务报 `ERROR: Access is denied.`（exit code 1），
;      只有提权的安装器才能建成功。
;
; 代价：安装时弹一次 UAC。可以接受 —— 装一次而已，
;       换来的是一劳永逸的免 UAC 自启。
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=Ferry-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 不弹语言选择框，直接用 [Languages] 里的第一个（chs）。
; 不写这两行的话，安装包资源里还有 Inno 内置的英文条目，用户第一步会看到
; "Select Setup Language" 的英文选择框，默认还是 English。
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage
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
; 主程序：SOCKS5+HTTP 双协议代理和隧道保活都在 exe 里，
; 目标机器只需要 Windows 自带的 ssh.exe，不需要 Node.js / PowerShell 脚本。
Source: "..\dist\渡口助手.exe";              DestDir: "{app}"; Flags: ignoreversion

; TUN 运行时（可选功能，但既然要做「游戏也能用」就必须带上）
;
; mihomo.exe 是 58 MB —— 安装包从 2 MB 变成 60 MB 左右，这是 TUN 的硬成本。
; 不带的话 TUN 只能在装了 Clash Verge 的机器上用，那就不算「一键」了。
; 用 lzma2/max 压缩后实际增量约 20 MB，可接受。
;
; ⚠ wintun.dll 必须和 mihomo.exe **放在同一目录**：
;   mihomo 用 Windows 默认 DLL 搜索顺序找 wintun.dll（先找 exe 所在目录），
;   放到别处会找不到，报的错还很含糊（"Cannot create a file when that file
;   already exists" 或直接没反应）。实测踩过。
Source: "..\mihomo\wintun.dll";              DestDir: "{app}"; Flags: ignoreversion
Source: "..\vendor\mihomo.exe";              DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\渡口助手"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "在借网与直连之间切换"; Tasks: startmenuicon
Name: "{autodesktop}\渡口助手";  Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "在借网与直连之间切换"; Tasks: desktopicon

; 注意：这里**故意没有** {userstartup} 的快捷方式。
;
; 为什么：客户端的 manifest 是 requireAdministrator，往启动文件夹放 .lnk
; 会导致每次开机弹一个 UAC 窗口，必须点「是」才能用 —— 跟「便捷」相反。
; 改由 [Run] 里调客户端的 /install-autostart 参数，让它自己建一个
; RunLevel=HighestAvailable 的计划任务（不弹 UAC）。见 [Code] 段。

[InstallDelete]
; 升级时清掉旧交付方式留下的垃圾：
;   .bak  —— 覆盖更新脚本手工备份的旧 exe
;   .new  —— 覆盖脚本先放旁边等待替换的那份
;   *.tmp —— 配置原子写的中转文件（断电可能留下）
; 不清的话安装目录会越积越多，用户还会看到好几个「渡口助手」。
Type: files; Name: "{app}\渡口助手.exe.bak"
Type: files; Name: "{app}\渡口助手.exe.new"
Type: files; Name: "{app}\*.tmp"

[Run]
; 只启动程序。开机自启不在这里设 —— 原因见 [Code] 里 ssPostInstall 的注释。
;
; 客户端 0.9.6 起是 asInvoker，CreateProcess 直接就能起来，
; 不再需要 shellexec，也不会再有「错误代码 740」。
;
; ⚠ 绝对不能加 skipifsilent：
;   自动更新调的是 `Ferry-Setup-x.y.z.exe /SILENT /NORESTART /CLOSEAPPLICATIONS`，
;   skipifsilent 会把这条 [Run] 正好跳过 —— 客户端先退出了、安装包又不启动它，
;   表现就是「更新完就没了」。
Filename: "{app}\{#AppExeName}"; Description: "立即启动渡口助手"; \
  Flags: postinstall nowait

[UninstallRun]
; 交给程序自己清：还原原代理 → 杀隧道/代理进程 → 删自启（含计划任务）
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
{ 升级前先让旧客户端**自己**退出。
  为什么不用 taskkill：强杀会跳过它的清理 —— 系统代理还指着 127.0.0.1:10801，
  而那个端口已经没了，用户装完就「整个网都上不去」。
  /stop 分支会先 RestoreDirect() 还原原代理，再收干净隧道和子进程。
  为什么不用 CloseApplications：它走 Restart Manager 发关闭消息，而托盘程序
  在 OnFormClosing 里 cancel（缩到托盘），结果是卡在「请关闭以下程序」。 }
function InitializeSetup(): Boolean;
var
  R: Integer;
  OldExe: string;
begin
  Result := True;
  { 不用 app 常量：InitializeSetup 阶段它还没定下来，按 DefaultDirName 的拼法构造。 }
  OldExe := ExpandConstant('{localappdata}\Ferry\{#AppExeName}');
  if FileExists(OldExe) then
  begin
    Exec(OldExe, '/stop', '', SW_HIDE, ewWaitUntilTerminated, R);
    Sleep(900);
  end;
end;

{ 旧版（requireAdministrator 时代）的进程是**管理员**进程，
  而本安装器是普通权限 —— 杀不掉它，文件也就替换不了。
  用户看到的是 Inno 的原始错误：
      DeleteFile 失败；错误代码 5。拒绝访问。
  三个技术选项（重试/跳过/取消）没人看得懂。实测 0.9.2 的用户就撞在这里。

  这里在真正开始安装前再确认一次，并给出两条可执行的路。 }
function ProcessStillRunning(): Boolean;
var
  R, N: Integer;
  Tmp, S: string;
  Raw: AnsiString;
begin
  Result := False;
  Tmp := ExpandConstant('{tmp}\ferry_proc.txt');
  DeleteFile(Tmp);
  Exec('powershell.exe',
    '-NoProfile -NonInteractive -Command "' +
    '$c=@(Get-Process -Name ''渡口助手'' -ErrorAction SilentlyContinue).Count; ' +
    'Set-Content -Path ''' + Tmp + ''' -Value $c -Encoding ASCII"',
    '', SW_HIDE, ewWaitUntilTerminated, R);
  if FileExists(Tmp) then
  begin
    Raw := '';
    if LoadStringFromFile(Tmp, Raw) then
    begin
      S := Trim(string(Raw));
      N := StrToIntDef(S, 0);
      Result := N > 0;
    end;
    DeleteFile(Tmp);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  R: Integer;
  OldExe: string;
begin
  Result := '';
  OldExe := ExpandConstant('{localappdata}\Ferry\{#AppExeName}');

  { 最后再试一次让它自己退：新版认识 /stop，会先还原系统代理再退出。 }
  if FileExists(OldExe) then
  begin
    Exec(OldExe, '/stop', '', SW_HIDE, ewWaitUntilTerminated, R);
    Sleep(800);
  end;

  { 检测两次，中间等 2 秒。
    为什么：/stop 本身也是一个「渡口助手」进程，它杀完主进程后要过一会儿才退出。
    只查一次会把「正在退出的自己」当成「用户还没退出」，于是中止安装 —— 实测踩过。 }
  if ProcessStillRunning() then
  begin
    Sleep(2000);
  end;

  if ProcessStillRunning() then
  begin
    Result :=
      '「渡口助手」还在运行，安装程序没法替换它的文件。' + #13#10 + #13#10 +
      '如果它是以管理员身份启动的旧版本，普通权限的安装程序关不掉它。' + #13#10 + #13#10 +
      '请任选一种，然后重新安装：' + #13#10 +
      '  1) 在右下角托盘图标上右键 → 退出' + #13#10 +
      '  2) 取消安装，右键这个安装包 →「以管理员身份运行」';
  end;
end;

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
  Url, CfgPath, Fallback, ExistingStr: string;
  ExistingCfg: AnsiString;
  Fetched: Boolean;
begin
  if CurStep = ssPostInstall then
  begin
    { 0.9.2 的安装器在公共桌面留过一个「渡口助手」快捷方式，和现在
      用户桌面那个重复，图标还带着 requireAdministrator 时代的盾牌，
      用户会以为是两个不同的程序。普通权限删可能被拒，删不掉就算了。 }
    DeleteFile(ExpandConstant('{commondesktop}\渡口助手.lnk'));
    { 开机自启：写一个标记文件，让客户端启动后自己去建计划任务。

      为什么不在 [Run] 里直接调 `渡口助手.exe /install-autostart`：
        那个 exe 的 manifest 是 requireAdministrator，而安装器**已经提权**。
        从高完整性进程用 CreateProcess 启动一个同样要求提权的 exe 会失败：
          CreateProcess 失败；错误代码 740。请求的操作需要提升。
        （实测弹的就是这个框，但程序本身还能正常启动 —— 因为另一条 [Run]
         走的是 ShellExecute，能提权，所以我们容易只看到"能用"而忽略这个报错）
        CreateProcess 不触发 UAC，所以这条路走不通。

      改成写标记 + 客户端自己处理：客户端启动时本来就是管理员，
      它调 schtasks 建 RunLevel=HighestAvailable 的任务毫无问题。
      见 Program.cs 里读 .want-autostart 的那段。

      放在 config.json 逻辑之前：那段有 `exit`（保留已有激活时直接返回），
      放后面会被跳过。 }
    if WizardIsTaskSelected('autostart') then
      SaveStringToFile(ExpandConstant('{app}\.want-autostart'), '1', False);

    CfgPath := ExpandConstant('{app}\config.json');
    Fetched := False;

    { 重装/升级时**绝不能覆盖已有的激活状态**。
      用户装过一次、输过密钥，config.json 里就有 token 和 expiresAt；
      安装程序再按 /api/config 重写一份（token 固定为空）就等于把激活清掉了。
      所以只要现有文件里有非空 token，就整份保留，只更新程序本体。 }
    if FileExists(CfgPath) then
    begin
      ExistingCfg := '';
      if LoadStringFromFile(CfgPath, ExistingCfg) then
      begin
        ExistingStr := string(ExistingCfg);   // AnsiString → Unicode
        if JsonStr(ExistingStr, 'token') <> '' then
        begin
          Log('现有 config.json 含激活令牌，保留不动（重装不丢激活）');
          exit;
        end;
      end;
    end;

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
