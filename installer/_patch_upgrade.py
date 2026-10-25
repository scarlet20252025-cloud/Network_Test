import io

p = 'installer/ferry.iss'
s = io.open(p, encoding='utf-8-sig').read()

# ── 1. [InstallDelete]：清掉覆盖脚本/旧版本留下的残留 ──
old = '''[Run]'''
new = '''[InstallDelete]
; 升级时清掉旧版本留下的垃圾。这些都是「历史交付方式」的产物：
;   .bak  —— 覆盖更新脚本手工备份的旧 exe
;   .new  —— 覆盖脚本先放旁边等待替换的那份
;   *.tmp —— 配置原子写的中转文件（万一断电留下）
; 不清的话 _app 目录会越来越大，而且用户看到三个「渡口助手」会困惑。
Type: files; Name: "{app}\\渡口助手.exe.bak"
Type: files; Name: "{app}\\渡口助手.exe.new"
Type: files; Name: "{app}\\*.tmp"

[Run]'''
assert old in s, '1'
s = s.replace(old, new, 1)

# ── 2. InitializeSetup：先让旧客户端自己退出 ──
old2 = '''procedure InitializeWizard;
begin'''
new2 = '''{ 升级前先让旧客户端**自己**退出。

  为什么不用 taskkill：
    直接强杀会跳过它的清理步骤 —— 系统代理还指着 127.0.0.1:10801，
    而那个端口已经没了，用户装完就「整个网都上不去」，然后来找你说「装坏了」。
    /stop 分支里它会先 RestoreDirect() 还原原代理，再把隧道/子进程收干净。

  为什么放在 InitializeSetup 而不是 CloseApplications：
    CloseApplications 走 Restart Manager 发关闭消息，而托盘程序通常
    OnFormClosing 里 cancel 掉（缩到托盘），结果是「请关闭以下程序」卡住。 }
function InitializeSetup(): Boolean;
var
  R: Integer;
  OldExe: string;
begin
  Result := True;
  OldExe := ExpandConstant('{app}\\{#AppExeName}');
  if FileExists(OldExe) then
  begin
    Exec(OldExe, '/stop', '', SW_HIDE, ewWaitUntilTerminated, R);
    Sleep(900);
  end;
end;

procedure InitializeWizard;
begin'''
assert old2 in s, '2'
s = s.replace(old2, new2, 1)

# ── 3. ssPostInstall：删掉 0.9.2 时代残留的公共桌面快捷方式 ──
old3 = '''    if CurStep = ssPostInstall then
    begin'''
new3 = '''    if CurStep = ssPostInstall then
    begin
      { 0.9.2 的安装器在**公共桌面**留过一个「渡口助手」快捷方式，
        和现在 {autodesktop} 那个重复，而且图标带着 requireAdministrator
        时代的盾牌 —— 用户会以为是两个不同的程序。
        普通权限删公共桌面可能被拒，失败就算了，不影响安装。 }
      try
        DeleteFile(ExpandConstant('{commondesktop}\\渡口助手.lnk'));
      except
      end;
'''
assert old3 in s, '3'
s = s.replace(old3, new3, 1)

io.open(p, 'w', encoding='utf-8-sig', newline='').write(s)
print('iss upgrade-hardening ok')
