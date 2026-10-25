import io

p = 'installer/ferry.iss'
s = io.open(p, encoding='utf-8-sig').read()

# InitializeSetup 之后加 PrepareToInstall（检测旧进程仍在 → 友好提示）
anchor = '''function InitializeWizard;
begin'''
assert anchor in s, 'anchor'

block = '''{ 旧版（requireAdministrator 时代）进程是**管理员**进程，
  而我们这个安装器是普通权限 —— 杀不掉它，文件也就替换不了，
  用户会看到 Inno 的原始错误：
      DeleteFile 失败；错误代码 5。拒绝访问。
  三个技术选项（重试/跳过/取消）没人看得懂。实测 0.9.2 的用户就撞在这里。

  这里在真正开始安装前再确认一次，并给出可执行的两条路。 }
function ProcessStillRunning(): Boolean;
var
  R, N: Integer;
  Tmp, S: string;
  Raw: AnsiString;
begin
  Result := False;
  Tmp := ExpandConstant('{tmp}\\ferry_proc.txt');
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
  OldExe := ExpandConstant('{localappdata}\\Ferry\\{#AppExeName}');

  { 最后再试一次让它自己退（新版认识 /stop，会先还原系统代理）。 }
  if FileExists(OldExe) then
  begin
    Exec(OldExe, '/stop', '', SW_HIDE, ewWaitUntilTerminated, R);
    Sleep(800);
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

function InitializeWizard;
begin'''

s = s.replace(anchor, block, 1)
io.open(p, 'w', encoding='utf-8-sig', newline='').write(s)
print('PrepareToInstall added')
