#requires -Version 5.1
<#
  渡口安装程序 · Ferry Setup

  设计目标（对齐用户要求）：
    · 有引导：欢迎 → 检查环境 → 选项 → 安装 → 完成，五步，看得到进度
    · 可选快捷方式：桌面 / 开始菜单，各自独立勾选
    · 可选开机自启
    · 装完问「是否现在启动」

  安装什么：
    %LOCALAPPDATA%\Ferry\
      ├─ 渡口助手.ps1        （客户端版控制面板）
      ├─ ssh-tunnel-keeper.ps1
      ├─ dual-proxy.mjs
      ├─ 渡口助手.cmd        （双击入口，绕过 PS 执行策略）
      ├─ config.json        （端口/远端/账号/令牌，由门户生成时写入）
      └─ uninstall.cmd      （卸载入口）
    可选：桌面 / 开始菜单快捷方式、开机自启

  为什么用 .cmd 做入口：
    双击 .ps1 会被默认执行策略拦掉（一闪而过，用户不知道发生了什么）。
    .cmd 里显式 -ExecutionPolicy Bypass 才可靠。
#>

param(
  # 静默安装：由 Ferry-Setup.exe（Inno Setup）调用，不创建任何窗口。
  # 见下方 $InstallDir 之后的静默分支。
  [switch]$Silent,
  # 可选：指定 config.json 路径（默认读同目录）。.exe 安装时传 {app}\config.json。
  [string]$ConfigFile = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# ── 安装参数（从同目录 config.json 读，由门户生成的 .cmd 写入）──
# 为什么用外部配置文件而不是往脚本里塞占位符：
#   占位符替换要处理引号转义、数值/字符串区分、特殊字符，出错面大；
#   config.json 是标准 JSON，两边都用现成库读写，稳得多。
$CfgPath = Join-Path $PSScriptRoot 'config.json'
$SetupCfg = @{
  RemoteHost = ''
  SshPort    = 22
  HttpPort   = 10801
  AccountIp  = ''
  Token      = ''
  PortalUrl  = ''
}
if (-not (Test-Path $CfgPath)) {
  [System.Windows.Forms.MessageBox]::Show(
    "找不到 config.json。`n`n请不要单独运行这个脚本 —— 双击门户下载的「渡口安装.cmd」。",
    '渡口安装', 'OK', 'Error') | Out-Null
  exit 1
}
try {
  # 必须显式 UTF-8：PS 5.1 的 Get-Content 按本机代码页读，中文会乱码
  $rawCfg = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($CfgPath))
  $j = $rawCfg | ConvertFrom-Json
  if ($j.remoteHost) { $SetupCfg.RemoteHost = [string]$j.remoteHost }
  if ($j.sshPort)    { $SetupCfg.SshPort    = [int]$j.sshPort }
  if ($j.httpPort)   { $SetupCfg.HttpPort   = [int]$j.httpPort }
  if ($j.accountIp)  { $SetupCfg.AccountIp  = [string]$j.accountIp }
  if ($j.token)      { $SetupCfg.Token      = [string]$j.token }
  if ($j.portalUrl)  { $SetupCfg.PortalUrl  = [string]$j.portalUrl }
} catch {
  [System.Windows.Forms.MessageBox]::Show(
    "config.json 读不了：`n$($_.Exception.Message)`n`n请重新从门户下载安装包。",
    '渡口安装', 'OK', 'Error') | Out-Null
  exit 1
}

$InstallDir = Join-Path $env:LOCALAPPDATA 'Ferry'

# ══════════════════════════════════════════════════════════════════
#  静默安装分支（-Silent）
#
#  为什么需要它：Ferry-Setup.exe 已经把「选快捷方式 / 装完启动」这些向导
#  做完了，再弹一个 PowerShell 向导就是两层向导，用户会懵。所以这里只做
#  「落文件 + 写配置 + 生成入口」，界面交给 .exe。
#
#  和 GUI 分支的分工：
#    .exe（Inno Setup）     → 向导、选快捷方式、开机自启、装完启动、卸载注册
#    本脚本的静默分支        → 复制 3 个组件、写 config.json、生成 渡口助手.cmd / uninstall.cmd
#    本脚本的 GUI 分支       → 独立使用（门户发的 zip 包）时的完整体验
# ══════════════════════════════════════════════════════════════════
if ($Silent) {
  function SLog([string]$m) { Write-Host $m }
  try {
    SLog "安装目录: $InstallDir"
    if (-not (Test-Path $InstallDir)) { $null = New-Item -ItemType Directory -Path $InstallDir -Force }

    # ── 1. 复制组件 ──
    $srcDir = $PSScriptRoot
    foreach ($f in @('渡口助手.ps1', 'ssh-tunnel-keeper.ps1', 'dual-proxy.mjs')) {
      $src = Join-Path $srcDir $f
      if (-not (Test-Path $src)) { throw "缺少组件：$f（安装包不完整）" }
      Copy-Item $src (Join-Path $InstallDir $f) -Force
      SLog "  复制 $f"
    }

    # ── 2. 写 config.json ──
    $cfg = @{
      remoteHost    = $SetupCfg.RemoteHost
      sshPort       = $SetupCfg.SshPort
      httpPort      = $SetupCfg.HttpPort
      accountIp     = $SetupCfg.AccountIp
      token         = $SetupCfg.Token
      fallbackProxy = ''
      portalUrl    = $SetupCfg.PortalUrl
    }
    $json = $cfg | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $InstallDir 'config.json'), $json, (New-Object Text.UTF8Encoding($false)))
    SLog "  写 config.json（账号 $($SetupCfg.AccountIp)，代理端口 $($SetupCfg.HttpPort)）"

    $gbk = [Text.Encoding]::GetEncoding(936)

    # ── 3. 启动入口（.cmd 包 .ps1，绕过执行策略）──
    $cmdLines = @(
      '@echo off',
      'chcp 65001 >nul 2>&1',
      'setlocal',
      'set "P=%~dp0渡口助手.ps1"',
      'if not exist "%P%" (echo 找不到 渡口助手.ps1 & pause & exit /b 1)',
      'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%P%"',
      'endlocal'
    )
    [IO.File]::WriteAllLines((Join-Path $InstallDir '渡口助手.cmd'), $cmdLines, $gbk)
    SLog "  生成 渡口助手.cmd"

    # ── 4. 卸载入口（Inno 的卸载器做不了系统代理清理，必须留这个）──
    $unCmd = @(
      '@echo off',
      'chcp 65001 >nul 2>&1',
      'echo 正在卸载渡口...',
      'powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& { $s=(Get-ItemProperty ''HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'').ProxyServer; if ($s -like ''*127.0.0.1:' + $SetupCfg.HttpPort + '* '') { Set-ItemProperty ''HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'' -Name ProxyEnable -Value 0; Write-Host ''已关闭系统代理'' } }"',
      'powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Get-CimInstance Win32_Process -Filter ''Name=''''node.exe'''''' | Where-Object { $_.CommandLine -like ''*dual-proxy*'' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"',
      'powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Get-CimInstance Win32_Process -Filter ''Name=''''ssh.exe'''''' | Where-Object { $_.CommandLine -match ''-D 127.0.0.1:' + ($SetupCfg.HttpPort - 1) + ''' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"',
      'powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Remove-Item (Join-Path ([Environment]::GetFolderPath(''Startup'')) ''渡口*.lnk'') -Force -ErrorAction SilentlyContinue"',
      'echo 已切回直连、停止服务、删除自启。'
    )
    [IO.File]::WriteAllText((Join-Path $InstallDir 'uninstall.cmd'), ($unCmd -join "`r`n"), $gbk)
    SLog "  生成 uninstall.cmd"

    SLog 'SILENT-OK'
    exit 0
  } catch {
    Write-Host "SILENT-FAIL: $($_.Exception.Message)"
    exit 1
  }
}

$clrOk    = [System.Drawing.Color]::FromArgb(32, 178, 170)
$clrOkD   = [System.Drawing.Color]::FromArgb(22, 150, 143)
$clrText  = [System.Drawing.Color]::FromArgb(44, 62, 80)
$clrMuted = [System.Drawing.Color]::FromArgb(140, 150, 160)
$clrBg    = [System.Drawing.Color]::FromArgb(248, 250, 251)
$clrLine  = [System.Drawing.Color]::FromArgb(226, 232, 236)
$clrBad   = [System.Drawing.Color]::FromArgb(231, 76, 60)

# ══════════════════════════════════════════════════════════════
#  界面骨架
# ══════════════════════════════════════════════════════════════
$form                 = New-Object System.Windows.Forms.Form
$form.Text            = '渡口 · 安装'
$form.Size            = New-Object System.Drawing.Size(560, 460)
$form.StartPosition   = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox     = $false
$form.MinimumSize     = $form.Size
$form.BackColor       = $clrBg
$form.Font            = New-Object System.Drawing.Font('Microsoft YaHei', 9)

try {
  $pi = [System.Windows.Forms.Form].GetProperty('DoubleBuffered',
    [System.Reflection.BindingFlags]'Instance,NonPublic')
  $pi.SetValue($form, $true, $null)
} catch {}

# 顶部：标题 + 副标题
$pnlHead = New-Object System.Windows.Forms.Panel
$pnlHead.Location = New-Object System.Drawing.Point(0, 0)
$pnlHead.Size = New-Object System.Drawing.Size(560, 76)
$pnlHead.BackColor = [System.Drawing.Color]::White
$form.Controls.Add($pnlHead)

$lblTitle = New-Object System.Windows.Forms.Label
$lblTitle.Location = New-Object System.Drawing.Point(28, 18)
$lblTitle.Size = New-Object System.Drawing.Size(500, 26)
$lblTitle.Font = New-Object System.Drawing.Font('Microsoft YaHei', 14, [System.Drawing.FontStyle]::Bold)
$lblTitle.ForeColor = $clrText
$lblTitle.Text = '欢迎使用渡口'
$pnlHead.Controls.Add($lblTitle)

$lblSub = New-Object System.Windows.Forms.Label
$lblSub.Location = New-Object System.Drawing.Point(30, 46)
$lblSub.Size = New-Object System.Drawing.Size(500, 20)
$lblSub.ForeColor = $clrMuted
$lblSub.Font = New-Object System.Drawing.Font('Microsoft YaHei', 9)
$lblSub.Text = '本机网络受限时，借一台能上网的内网主机中转流量'
$pnlHead.Controls.Add($lblSub)

$line = New-Object System.Windows.Forms.Panel
$line.Location = New-Object System.Drawing.Point(0, 76)
$line.Size = New-Object System.Drawing.Size(560, 1)
$line.BackColor = $clrLine
$form.Controls.Add($line)

# 内容区（多页共用，切换 Visible）
$pnlBody = New-Object System.Windows.Forms.Panel
$pnlBody.Location = New-Object System.Drawing.Point(28, 92)
$pnlBody.Size = New-Object System.Drawing.Size(504, 268)
$pnlBody.BackColor = $clrBg
$form.Controls.Add($pnlBody)

# ── 页 1：欢迎 ──
$pg1 = New-Object System.Windows.Forms.Panel
$pg1.Dock = 'Fill'
$pg1.BackColor = $clrBg
$pnlBody.Controls.Add($pg1)

$txt1 = New-Object System.Windows.Forms.Label
$txt1.Location = New-Object System.Drawing.Point(0, 6)
$txt1.Size = New-Object System.Drawing.Size(500, 190)
$txt1.Font = New-Object System.Drawing.Font('Microsoft YaHei', 9.5)
$txt1.ForeColor = $clrText
$txt1.Text = @"
安装程序会在这台电脑上配置：

  · 一条到远端的 SSH 隧道（加密）
  · 一个本地代理（HTTP + SOCKS5 同端口）
  · 一个控制面板，用来随时在「走渡口」和「直连」之间切换

安装位置
  $InstallDir

第一次使用需要把公钥发给管理员（安装过程中会显示），
之后不用再做任何配置。

本工具仅用于个人学习与内网连通性测试，请遵守所在网络的使用规定。
"@
$pg1.Controls.Add($txt1)

# ── 页 2：环境检查 ──
$pg2 = New-Object System.Windows.Forms.Panel
$pg2.Dock = 'Fill'
$pg2.BackColor = $clrBg
$pg2.Visible = $false
$pnlBody.Controls.Add($pg2)

$txt2 = New-Object System.Windows.Forms.TextBox
$txt2.Location = New-Object System.Drawing.Point(0, 4)
$txt2.Size = New-Object System.Drawing.Size(500, 240)
$txt2.Multiline = $true
$txt2.ReadOnly = $true
$txt2.ScrollBars = 'Vertical'
$txt2.BackColor = [System.Drawing.Color]::White
$txt2.ForeColor = $clrText
$txt2.Font = New-Object System.Drawing.Font('Consolas', 9)
$txt2.BorderStyle = 'FixedSingle'
$pg2.Controls.Add($txt2)

# ── 页 3：选项 ──
$pg3 = New-Object System.Windows.Forms.Panel
$pg3.Dock = 'Fill'
$pg3.BackColor = $clrBg
$pg3.Visible = $false
$pnlBody.Controls.Add($pg3)

$chkDesktop = New-Object System.Windows.Forms.CheckBox
$chkDesktop.Location = New-Object System.Drawing.Point(4, 16)
$chkDesktop.Size = New-Object System.Drawing.Size(460, 24)
$chkDesktop.Text = '在桌面创建快捷方式'
$chkDesktop.Checked = $true
$chkDesktop.ForeColor = $clrText
$pg3.Controls.Add($chkDesktop)

$chkStartMenu = New-Object System.Windows.Forms.CheckBox
$chkStartMenu.Location = New-Object System.Drawing.Point(4, 48)
$chkStartMenu.Size = New-Object System.Drawing.Size(460, 24)
$chkStartMenu.Text = '在开始菜单创建快捷方式'
$chkStartMenu.Checked = $true
$chkStartMenu.ForeColor = $clrText
$pg3.Controls.Add($chkStartMenu)

$chkAutoStart = New-Object System.Windows.Forms.CheckBox
$chkAutoStart.Location = New-Object System.Drawing.Point(4, 80)
$chkAutoStart.Size = New-Object System.Drawing.Size(460, 24)
$chkAutoStart.Text = '开机自动启动（只建隧道，不自动切代理）'
$chkAutoStart.Checked = $true
$chkAutoStart.ForeColor = $clrText
$pg3.Controls.Add($chkAutoStart)

$chkLaunch = New-Object System.Windows.Forms.CheckBox
$chkLaunch.Location = New-Object System.Drawing.Point(4, 112)
$chkLaunch.Size = New-Object System.Drawing.Size(460, 24)
$chkLaunch.Text = '安装完成后立即启动'
$chkLaunch.Checked = $true
$chkLaunch.ForeColor = $clrText
$pg3.Controls.Add($chkLaunch)

$lblOptHint = New-Object System.Windows.Forms.Label
$lblOptHint.Location = New-Object System.Drawing.Point(4, 152)
$lblOptHint.Size = New-Object System.Drawing.Size(496, 90)
$lblOptHint.ForeColor = $clrMuted
$lblOptHint.Font = New-Object System.Drawing.Font('Microsoft YaHei', 8.5)
$lblOptHint.Text = @"
说明
  · 「开机自动启动」只是把隧道拉起来备用，不会动你的系统代理。
    想用的时候在控制面盘点「开启渡口」。
  · 装了之后随时可以在控制面盘点「卸载」切回直连、删自启。
"@
$pg3.Controls.Add($lblOptHint)

# ── 页 4：安装进度 ──
$pg4 = New-Object System.Windows.Forms.Panel
$pg4.Dock = 'Fill'
$pg4.BackColor = $clrBg
$pg4.Visible = $false
$pnlBody.Controls.Add($pg4)

$txt4 = New-Object System.Windows.Forms.TextBox
$txt4.Location = New-Object System.Drawing.Point(0, 4)
$txt4.Size = New-Object System.Drawing.Size(500, 212)
$txt4.Multiline = $true
$txt4.ReadOnly = $true
$txt4.ScrollBars = 'Vertical'
$txt4.BackColor = [System.Drawing.Color]::White
$txt4.ForeColor = $clrText
$txt4.Font = New-Object System.Drawing.Font('Consolas', 9)
$txt4.BorderStyle = 'FixedSingle'
$pg4.Controls.Add($txt4)

$bar = New-Object System.Windows.Forms.ProgressBar
$bar.Location = New-Object System.Drawing.Point(0, 228)
$bar.Size = New-Object System.Drawing.Size(500, 18)
$bar.Style = 'Continuous'
$bar.Minimum = 0
$bar.Maximum = 100
$pg4.Controls.Add($bar)

# ── 页 5：完成 ──
$pg5 = New-Object System.Windows.Forms.Panel
$pg5.Dock = 'Fill'
$pg5.BackColor = $clrBg
$pg5.Visible = $false
$pnlBody.Controls.Add($pg5)

$txt5 = New-Object System.Windows.Forms.TextBox
$txt5.Location = New-Object System.Drawing.Point(0, 4)
$txt5.Size = New-Object System.Drawing.Size(500, 236)
$txt5.Multiline = $true
$txt5.ReadOnly = $true
$txt5.ScrollBars = 'Vertical'
$txt5.BackColor = [System.Drawing.Color]::White
$txt5.ForeColor = $clrText
$txt5.Font = New-Object System.Drawing.Font('Consolas', 9)
$txt5.BorderStyle = 'FixedSingle'
$pg5.Controls.Add($txt5)

# ── 底部按钮 ──
$btnBack = New-Object System.Windows.Forms.Button
$btnBack.Location = New-Object System.Drawing.Point(268, 388)
$btnBack.Size = New-Object System.Drawing.Size(84, 34)
$btnBack.Text = '上一步'
$btnBack.FlatStyle = 'Flat'
$btnBack.FlatAppearance.BorderSize = 1
$btnBack.FlatAppearance.BorderColor = $clrLine
$btnBack.BackColor = [System.Drawing.Color]::White
$btnBack.ForeColor = $clrText
$btnBack.Visible = $false
$form.Controls.Add($btnBack)

$btnNext = New-Object System.Windows.Forms.Button
$btnNext.Location = New-Object System.Drawing.Point(360, 388)
$btnNext.Size = New-Object System.Drawing.Size(94, 34)
$btnNext.Text = '下一步'
$btnNext.FlatStyle = 'Flat'
$btnNext.FlatAppearance.BorderSize = 0
$btnNext.BackColor = $clrOk
$btnNext.ForeColor = [System.Drawing.Color]::White
$btnNext.Font = New-Object System.Drawing.Font('Microsoft YaHei', 9.5, [System.Drawing.FontStyle]::Bold)
$form.Controls.Add($btnNext)

$btnCancel = New-Object System.Windows.Forms.Button
$btnCancel.Location = New-Object System.Drawing.Point(462, 388)
$btnCancel.Size = New-Object System.Drawing.Size(70, 34)
$btnCancel.Text = '取消'
$btnCancel.FlatStyle = 'Flat'
$btnCancel.FlatAppearance.BorderSize = 0
$btnCancel.BackColor = $clrBg
$btnCancel.ForeColor = $clrMuted
$form.Controls.Add($btnCancel)

for ($i = 0; $i -lt 3; $i++) {
  $btn = @($btnBack, $btnNext, $btnCancel)[$i]
  try {
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $btn.Width; $h = $btn.Height; $r = 8
    $gp.AddArc(0, 0, $r, $r, 180, 90)
    $gp.AddArc($w - $r - 1, 0, $r, $r, 270, 90)
    $gp.AddArc($w - $r - 1, $h - $r - 1, $r, $r, 0, 90)
    $gp.AddArc(0, $h - $r - 1, $r, $r, 90, 90)
    $gp.CloseFigure()
    $btn.Region = New-Object System.Drawing.Region -ArgumentList $gp
  } catch {}
}

# ══════════════════════════════════════════════════════════════
#  安装逻辑
# ══════════════════════════════════════════════════════════════
$script:Step = 1
$script:EnvOk = $false
$script:PubKey = ''
$script:InstallOk = $false

function Show-Step {
  param([int]$N)
  $script:Step = $N
  $pg1.Visible = ($N -eq 1)
  $pg2.Visible = ($N -eq 2)
  $pg3.Visible = ($N -eq 3)
  $pg4.Visible = ($N -eq 4)
  $pg5.Visible = ($N -eq 5)

  $btnBack.Visible = ($N -gt 1 -and $N -lt 4)
  $btnCancel.Visible = ($N -lt 4)

  switch ($N) {
    1 { $lblTitle.Text = '欢迎使用渡口'
        $lblSub.Text = '本机网络受限时，借一台能上网的内网主机中转流量'
        $btnNext.Text = '开始'; $btnNext.Visible = $true }
    2 { $lblTitle.Text = '正在检查环境'
        $lblSub.Text = '确认必需组件都在，并生成 SSH 密钥'
        $btnNext.Text = '下一步'; $btnNext.Visible = $true }
    3 { $lblTitle.Text = '安装选项'
        $lblSub.Text = '选择快捷方式与启动方式'
        $btnNext.Text = '安装'; $btnNext.Visible = $true }
    4 { $lblTitle.Text = '正在安装'
        $lblSub.Text = '请稍候'
        $btnNext.Visible = $false }
    5 { $lblTitle.Text = '安装完成'
        $lblSub.Text = if ($script:InstallOk) { '可以用控制面板随时开关渡口' } else { '安装未完成，请看下方信息' }
        $btnNext.Text = '完成'; $btnNext.Visible = $true }
  }
  [System.Windows.Forms.Application]::DoEvents()
}

function Add-Line {
  param([string]$S)
  $txt4.AppendText("$S`r`n")
  [System.Windows.Forms.Application]::DoEvents()
}

# 找 ssh
function Find-Ssh {
  $cands = @(
    "$env:SystemRoot\System32\OpenSSH\ssh.exe",
    "C:\Program Files\OpenSSH-Win64\ssh.exe",
    "C:\Program Files\Git\usr\bin\ssh.exe"
  )
  foreach ($c in $cands) { if (Test-Path $c) { return $c } }
  $g = Get-Command ssh.exe -ErrorAction SilentlyContinue
  if ($g) { return $g.Source }
  return $null
}

function Find-Node {
  $cands = @("$env:ProgramFiles\nodejs\node.exe", "D:\nodejs\node.exe")
  foreach ($c in $cands) { if (Test-Path $c) { return $c } }
  $g = Get-Command node.exe -ErrorAction SilentlyContinue
  if ($g) { return $g.Source }
  return $null
}

function Do-Check {
  $txt2.Text = ''
  $sb = New-Object Text.StringBuilder

  $ssh = Find-Ssh
  if ($ssh) { $null = $sb.AppendLine("[OK]   SSH  $ssh") }
  else { $null = $sb.AppendLine('[!!]   SSH  没找到 —— 需要 Windows 10 1809+ 或 Git for Windows') }

  $node = Find-Node
  if ($node) {
    $ver = (& $node --version 2>$null)
    $null = $sb.AppendLine("[OK]   Node $ver  $node")
  } else {
    $null = $sb.AppendLine('[!!]   Node 没找到 —— 本地代理需要它，去 nodejs.org 装 LTS 版')
  }

  $null = $sb.AppendLine('')
  $null = $sb.AppendLine("[..]   生成 SSH 密钥")

  # 密钥：没有就生成（空口令，免交互）
  $sshDir = Join-Path $env:USERPROFILE '.ssh'
  if (-not (Test-Path $sshDir)) { $null = New-Item -ItemType Directory -Path $sshDir -Force }
  $key = Join-Path $sshDir 'id_ed25519_ferry'
  if (-not (Test-Path $key)) {
    if ($ssh) {
      $keygen = Join-Path (Split-Path $ssh -Parent) 'ssh-keygen.exe'
      if (-not (Test-Path $keygen)) { $keygen = 'ssh-keygen.exe' }
      & $keygen -t ed25519 -f $key -N '""' -C "ferry@$env:COMPUTERNAME" 2>&1 | Out-Null
    }
  }
  if (Test-Path "$key.pub") {
    $script:PubKey = (Get-Content "$key.pub" -Raw).Trim()
    $null = $sb.AppendLine('[OK]   密钥已就绪')
    $null = $sb.AppendLine('')
    $null = $sb.AppendLine('把你的公钥发给管理员（加一次就够）：')
    $null = $sb.AppendLine('----------------------------------------')
    $null = $sb.AppendLine($script:PubKey)
    $null = $sb.AppendLine('----------------------------------------')
  } else {
    $null = $sb.AppendLine('[!!]   密钥生成失败')
  }

  $txt2.Text = $sb.ToString()
  $script:EnvOk = ($ssh -ne $null)
  return $script:EnvOk
}

function Do-Install {
  $bar.Value = 5
  Add-Line "[1/6] 创建目录"
  if (-not (Test-Path $InstallDir)) { $null = New-Item -ItemType Directory -Path $InstallDir -Force }
  Add-Line "      $InstallDir"
  $bar.Value = 15

  Add-Line '[2/6] 复制组件'
  $srcDir = $PSScriptRoot
  $copied = 0
  foreach ($f in @('渡口助手.ps1', 'ssh-tunnel-keeper.ps1', 'dual-proxy.mjs')) {
    $src = Join-Path $srcDir $f
    if (Test-Path $src) {
      Copy-Item $src (Join-Path $InstallDir $f) -Force
      $copied++
      Add-Line "      $f"
    } else {
      Add-Line "      [缺少] $f"
    }
  }
  $bar.Value = 35

  Add-Line '[3/6] 写配置'
  $cfg = @{
    remoteHost   = $SetupCfg.RemoteHost
    sshPort      = $SetupCfg.SshPort
    httpPort     = $SetupCfg.HttpPort
    accountIp    = $SetupCfg.AccountIp
    token        = $SetupCfg.Token
    fallbackProxy = ''
    portalUrl    = $SetupCfg.PortalUrl
  }
  $json = $cfg | ConvertTo-Json -Depth 5
  [IO.File]::WriteAllText((Join-Path $InstallDir 'config.json'), $json, (New-Object Text.UTF8Encoding($false)))
  Add-Line '      config.json'
  $bar.Value = 45

  Add-Line '[4/6] 生成启动入口'
  # .cmd 包 .ps1：双击 .ps1 会被执行策略拦
  $cmdLines = @(
    '@echo off',
    'chcp 65001 >nul 2>&1',
    'setlocal',
    "set `"P=%~dp0渡口助手.ps1`"",
    'if not exist "%P%" (echo 找不到 渡口助手.ps1 & pause & exit /b 1)',
    'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%P%"',
    'endlocal'
  )
  $gbk = [Text.Encoding]::GetEncoding(936)
  [IO.File]::WriteAllLines((Join-Path $InstallDir '渡口助手.cmd'), $cmdLines, $gbk)
  Add-Line '      渡口助手.cmd'

  # 卸载入口
  $unCmd = @(
    '@echo off',
    'chcp 65001 >nul 2>&1',
    'echo 正在卸载渡口...',
    'powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& { $s=(Get-ItemProperty ''HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'').ProxyServer; if ($s -like ''*127.0.0.1:$($SetupCfg.HttpPort)*'') { Set-ItemProperty ''HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'' -Name ProxyEnable -Value 0; Write-Host ''已关闭系统代理'' }; Get-CimInstance Win32_Process -Filter ''Name=''''node.exe'''''' | Where-Object { $_.CommandLine -like ''*dual-proxy*'' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; Get-CimInstance Win32_Process -Filter ''Name=''''ssh.exe'''''' | Where-Object { $_.CommandLine -match ''-D 127.0.0.1:$($SetupCfg.HttpPort - 1)'' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force } }"',
    'powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Remove-Item (Join-Path ([Environment]::GetFolderPath(''Startup'')) ''渡口*.lnk'') -Force -ErrorAction SilentlyContinue; Remove-Item (Join-Path ([Environment]::GetFolderPath(''Startup'')) ''Ferry*.lnk'') -Force -ErrorAction SilentlyContinue"',
    'echo 已切回直连、停止服务、删除自启。',
    'echo 要彻底删除，请手动删除目录：%LOCALAPPDATA%\Ferry',
    'pause'
  )
  $unText = $unCmd -join "`r`n"
  $unText = $unText.Replace('$($SetupCfg.HttpPort)', "$($SetupCfg.HttpPort)").Replace('$($SetupCfg.HttpPort - 1)', "$($SetupCfg.HttpPort - 1)")
  [IO.File]::WriteAllText((Join-Path $InstallDir 'uninstall.cmd'), $unText, $gbk)
  Add-Line '      uninstall.cmd'
  $bar.Value = 60

  Add-Line '[5/6] 创建快捷方式'
  $ws = New-Object -ComObject WScript.Shell
  $lnkTarget = Join-Path $InstallDir '渡口助手.cmd'
  $made = @()
  if ($chkDesktop.Checked) {
    $lnk = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) '渡口助手.lnk'))
    $lnk.TargetPath = $lnkTarget
    $lnk.WorkingDirectory = $InstallDir
    $lnk.Description = '渡口 · 在借网与直连之间切换'
    $lnk.WindowStyle = 7
    $lnk.Save()
    $made += '桌面'
  }
  if ($chkStartMenu.Checked) {
    $sm = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $lnk = $ws.CreateShortcut((Join-Path $sm '渡口助手.lnk'))
    $lnk.TargetPath = $lnkTarget
    $lnk.WorkingDirectory = $InstallDir
    $lnk.Description = '渡口 · 在借网与直连之间切换'
    $lnk.WindowStyle = 7
    $lnk.Save()
    $made += '开始菜单'
  }
  Add-Line $(if ($made.Count) { "      $($made -join ' + ')" } else { '      （未选择）' })
  $bar.Value = 75

  Add-Line '[6/6] 配置开机自启'
  $startup = [Environment]::GetFolderPath('Startup')
  $autoLnk = Join-Path $startup '渡口自动启动.lnk'
  if ($chkAutoStart.Checked) {
    $lnk = $ws.CreateShortcut($autoLnk)
    $lnk.TargetPath = $lnkTarget
    $lnk.WorkingDirectory = $InstallDir
    $lnk.Description = '渡口 · 开机建立隧道（不自动切代理）'
    $lnk.WindowStyle = 7
    $lnk.Save()
    Add-Line '      已加入启动文件夹'
  } else {
    Remove-Item $autoLnk -Force -ErrorAction SilentlyContinue
    Add-Line '      未启用'
  }
  $bar.Value = 100

  $script:InstallOk = ($copied -ge 2)
  return $script:InstallOk
}

# ══════════════════════════════════════════════════════════════
#  事件
# ══════════════════════════════════════════════════════════════
$btnNext.Add_Click({
  try {
    switch ($script:Step) {
      1 { Show-Step 2
          Do-Check | Out-Null }
      2 { Show-Step 3 }
      3 {
          Show-Step 4
          $ok = Do-Install
          $txt5.Text = ''
          $sb = New-Object Text.StringBuilder
          if ($ok) {
            $null = $sb.AppendLine('[OK]   安装完成')
            $null = $sb.AppendLine('')
            $null = $sb.AppendLine("安装位置：`n  $InstallDir")
            $null = $sb.AppendLine('')
            $null = $sb.AppendLine('下一步：')
            if ($script:PubKey) {
              $null = $sb.AppendLine('  1. 把公钥发给管理员（如果还没发）：')
              $null = $sb.AppendLine('')
              $null = $sb.AppendLine('     ' + $script:PubKey)
              $null = $sb.AppendLine('')
              $null = $sb.AppendLine('  2. 打开「渡口助手」，点「开启渡口」')
            } else {
              $null = $sb.AppendLine('  打开「渡口助手」，点「开启渡口」')
            }
            $null = $sb.AppendLine('')
            $null = $sb.AppendLine('随时可以在控制面盘点「切回直连」或「卸载」。')
          } else {
            $null = $sb.AppendLine('[!!]   安装未完成')
            $null = $sb.AppendLine('')
            $null = $sb.AppendLine('组件复制失败，请确认这个安装包是完整的')
            $null = $sb.AppendLine('（和 .cmd 放在同一个目录里的那几个文件都在）。')
          }
          $txt5.Text = $sb.ToString()
          Show-Step 5
        }
      5 {
          if ($script:InstallOk -and $chkLaunch.Checked) {
            $lnk = Join-Path $InstallDir '渡口助手.cmd'
            if (Test-Path $lnk) { Start-Process -FilePath $lnk }
          }
          $form.Close()
        }
    }
  } catch {
    [System.Windows.Forms.MessageBox]::Show("出错：`n$($_.Exception.Message)", '渡口安装', 'OK', 'Error') | Out-Null
  }
})

$btnBack.Add_Click({ if ($script:Step -gt 1) { Show-Step ($script:Step - 1) } })

$btnCancel.Add_Click({
  $r = [System.Windows.Forms.MessageBox]::Show('确定要取消安装吗？', '渡口安装', 'YesNo', 'Question')
  if ($r -eq 'Yes') { $form.Close() }
})

# 已安装过 → 提示覆盖
if (Test-Path (Join-Path $InstallDir 'config.json')) {
  $r = [System.Windows.Forms.MessageBox]::Show(
    "检测到已经安装过。`n`n继续会覆盖现有配置（端口、令牌会更新）。`n`n要继续吗？",
    '渡口安装', 'YesNo', 'Question')
  if ($r -ne 'Yes') { exit 0 }
}

Show-Step 1
[void][System.Windows.Forms.Application]::Run($form)
