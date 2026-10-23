#requires -Version 5.1
<#
  渡口助手（客户端版）· Ferry Client

  使用者机器上的控制面板。跟管理员本机那个「渡口助手.ps1」不是同一个东西：
    · 管理员版：管隧道、管双协议代理、管出口 IP 探测、管 Clash 兜底
    · 本版：只做一件事 —— 在「走渡口」和「直连」之间切换，并显示当前状态

  为什么必须有它：
    部署包跑完是一次性的，之后用户想把借网关掉、恢复直连，没有任何入口，
    系统代理会一直指着已经停掉的端口，表现就是「全网上不去」。
    这个面板就是为了这件事存在。

  设计约束（使用者机器环境不可控）：
    · 不依赖任何第三方组件，纯 PowerShell 5.1 + WinForms
    · 端口不写死 —— 从同目录的 config.json 读（部署包生成时写入实际端口）
    · 关窗口 = 收到托盘，不退出（避免误关后再也找不到）
    · 所有网络探测都用 TcpClient + 短超时，绝不用 Test-NetConnection
      （它对关闭端口要等满 14 秒，界面会假死）
#>

$ErrorActionPreference = 'Continue'

# ── 单实例锁 ──────────────────────────────────────────────────
# 不加这个，双击几次就开几个面板 + 几组 ssh/node 进程，互相抢 10800/10801 端口，
# 表现是「开了好几个渡口助手、状态还互相打架」。命名 Mutex 跨进程有效。
$script:AppMutex = New-Object System.Threading.Mutex($false, 'Local\FerryAssistant')
if (-not $script:AppMutex.WaitOne(0)) {
  # 已有实例在跑：把它的窗口顶到前面，然后自己安静退出
  try {
    $other = Get-Process -ErrorAction SilentlyContinue |
             Where-Object { $_.Id -ne $PID -and $_.MainWindowTitle -eq '渡口助手' } |
             Select-Object -First 1
    if ($other) {
      Add-Type -AssemblyName Microsoft.VisualBasic
      [Microsoft.VisualBasic.Interaction]::AppActivate($other.Id) | Out-Null
    } else {
      Add-Type -AssemblyName System.Windows.Forms
      $msg = '渡口助手已经在运行了。' + [Environment]::NewLine + [Environment]::NewLine +
             '窗口可能被关到了右下角托盘区（点 ^ 展开能看到）。'
      [System.Windows.Forms.MessageBox]::Show($msg, '渡口助手', 'OK', 'Information') | Out-Null
    }
  } catch { }
  exit 0
}

# ── 路径与配置 ────────────────────────────────────────────────

$AppDir = $PSScriptRoot
$CfgPath = Join-Path $AppDir 'config.json'
$StatePath = Join-Path $AppDir 'state.json'

function Read-JsonFile {
  param([string]$Path)
  if (-not (Test-Path $Path)) { return $null }
  try {
    # 必须显式 UTF8 解码：PS 5.1 的 Get-Content -Raw 按本机代码页（GBK）读，
    # 遇到 UTF-8 中文会变成乱码，ConvertFrom-Json 直接报错。
    $txt = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($Path))
    return $txt | ConvertFrom-Json
  } catch { return $null }
}

function Write-JsonFile {
  param([string]$Path, $Obj)
  try {
    $json = $Obj | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($Path, $json, (New-Object Text.UTF8Encoding($false)))
  } catch {}
}

$Cfg = Read-JsonFile $CfgPath
if (-not $Cfg) {
  [System.Windows.Forms.MessageBox]::Show(
    "找不到配置文件：`n$CfgPath`n`n请重新从渡口门户下载部署包。",
    '渡口助手', 'OK', 'Error') | Out-Null
  exit 1
}

$script:HttpPort = [int]$Cfg.httpPort      # 本地双协议代理端口（HTTP + SOCKS5 同端口）
$script:RemoteHost = "$($Cfg.remoteHost)"  # 远端主机（ssh 别名或 IP）
$script:SshPort = [int]$Cfg.sshPort
$script:AccountIp = "$($Cfg.accountIp)"
$script:Token = "$($Cfg.token)"
# 门户地址：激活密钥时用。.exe 安装时写进 config.json（见 ferry.iss 的 PortalBase）。
$script:PortalUrl = if ($Cfg.portalUrl) { "$($Cfg.portalUrl)".TrimEnd('/') } else { '' }
# 已激活 = 有 token。没有就在面板上引导用户输密钥。
$script:Activated = ($script:Token -ne '')

# 本机 Clash / 其它代理的端口，用于「切回直连」时还原成它
$script:FallbackProxy = if ($Cfg.fallbackProxy) { "$($Cfg.fallbackProxy)" } else { '' }

# ── 日志（写到文件，出问题能查）────────────────────────────────
$LogPath = Join-Path $AppDir 'assistant.log'
function Log {
  param([string]$Msg)
  try {
    $line = "[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Msg
    Add-Content -Path $LogPath -Value $line -Encoding UTF8 -ErrorAction Stop
    # 超过 200KB 就截断，别让它无限长
    if ((Get-Item $LogPath -ErrorAction SilentlyContinue).Length -gt 204800) {
      $tail = Get-Content $LogPath -Tail 500
      Set-Content -Path $LogPath -Value $tail -Encoding UTF8
    }
  } catch {}
}

# ── 网络探测（短超时，不阻塞界面）──────────────────────────────
function Test-Port {
  param([int]$Port, [int]$TimeoutMs = 300)
  try {
    $c = New-Object Net.Sockets.TcpClient
    $ar = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
    $ok = $ar.AsyncWaitHandle.WaitOne($TimeoutMs, $false) -and $c.Connected
    $c.Close()
    return [bool]$ok
  } catch { return $false }
}

function Test-Net {
  # 直接连 baidu:443，验证「能不能上网」。不走系统代理 —— 测的是物理链路。
  param([int]$TimeoutMs = 3000)
  try {
    $c = New-Object Net.Sockets.TcpClient
    $ar = $c.BeginConnect('www.baidu.com', 443, $null, $null)
    $ok = $ar.AsyncWaitHandle.WaitOne($TimeoutMs, $false) -and $c.Connected
    $c.Close()
    return [bool]$ok
  } catch { return $false }
}

# ── 密钥激活 ─────────────────────────────────────────────────
# 用户要「密钥在软件里输入激活借网使用权限，而不是用来打开网页」。
# 校验在门户的 POST /api/activate（和网页登录同一套一次性密钥、同一套限流）。
function Invoke-Activate {
  param([string]$Code)

  if (-not $script:PortalUrl) {
    return @{ ok = $false; error = '安装包没带门户地址，请重新从门户获取软件' }
  }

  $url = "$script:PortalUrl/api/activate"
  $payload = @{ code = $Code; ip = $script:AccountIp } | ConvertTo-Json -Compress

  # 用 WebRequest 并显式清空代理：本机可能装着 Clash/系统代理，
  # 不清空的话激活请求会被劫持到一个已经停掉的端口上，表现是「一直转圈」。
  try {
    $req = [System.Net.WebRequest]::Create($url)
    $req.Method = 'POST'
    $req.ContentType = 'application/json; charset=utf-8'
    $req.Timeout = 10000
    $req.Proxy = $null
    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
    $req.ContentLength = $bytes.Length
    $st = $req.GetRequestStream()
    $st.Write($bytes, 0, $bytes.Length)
    $st.Close()

    $resp = $req.GetResponse()
    $reader = New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)
    $text = $reader.ReadToEnd()
    $reader.Close(); $resp.Close()
    $j = $text | ConvertFrom-Json
    if ($j.ok) { return @{ ok = $true; token = "$($j.token)"; data = $j } }
    return @{ ok = $false; error = "$($j.error)" }
  } catch [System.Net.WebException] {
    # 4xx/5xx 也走异常分支，这里把服务端给的 JSON 抠出来，
    # 否则用户只会看到「远程服务器返回错误 (401)」这种没用的信息。
    $ex = $_.Exception
    if ($ex.Response) {
      try {
        $r2 = New-Object IO.StreamReader($ex.Response.GetResponseStream(), [Text.Encoding]::UTF8)
        $t2 = $r2.ReadToEnd(); $r2.Close()
        $j2 = $t2 | ConvertFrom-Json
        if ($j2.error) { return @{ ok = $false; error = "$($j2.error)" } }
      } catch { }
    }
    return @{ ok = $false; error = "连不上门户 $script:PortalUrl（检查是否在校园网内）" }
  } catch {
    return @{ ok = $false; error = "激活失败：$($_.Exception.Message)" }
  }
}

function Show-ActivateDialog {
  Add-Type -AssemblyName Microsoft.VisualBasic
  $nl = [Environment]::NewLine
  $tip = '请输入管理员给你的密钥（8 位字母数字）。' + $nl + $nl +
         "密钥一次性、绑定本机 IP $script:AccountIp。"
  $code = [Microsoft.VisualBasic.Interaction]::InputBox($tip, '激活渡口', '')
  if ([string]::IsNullOrWhiteSpace($code)) { return }

  $r = Invoke-Activate -Code $code.Trim().ToUpper()
  if (-not $r.ok) {
    [System.Windows.Forms.MessageBox]::Show("$($r.error)", '激活失败', 'OK', 'Warning') | Out-Null
    return
  }

  # 落盘：把 token 和门户下发的端口写回 config.json
  $script:Token = $r.token
  $script:Activated = $true
  if ($r.data.remoteHost) { $script:RemoteHost = "$($r.data.remoteHost)" }
  if ($r.data.sshPort)    { $script:SshPort    = [int]$r.data.sshPort }
  if ($r.data.httpPort)   { $script:HttpPort   = [int]$r.data.httpPort }

  $cfgNow = @{
    remoteHost    = $script:RemoteHost
    sshPort       = $script:SshPort
    httpPort      = $script:HttpPort
    accountIp     = $script:AccountIp
    token         = $script:Token
    fallbackProxy = $script:FallbackProxy
    portalUrl     = $script:PortalUrl
  }
  Write-JsonFile -Path $CfgPath -Data $cfgNow
  try { Log "激活成功，账号 $script:AccountIp" } catch { }

  [System.Windows.Forms.MessageBox]::Show(
    '激活成功！' + $nl + $nl + '现在可以点「开始借网」。',
    '渡口', 'OK', 'Information') | Out-Null
  try { Update-UI | Out-Null } catch { }
}

# ── 系统代理读写 ─────────────────────────────────────────────

$RegPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'

function Get-Proxy {
  $en = (Get-ItemProperty $RegPath -Name ProxyEnable -ErrorAction SilentlyContinue).ProxyEnable
  $srv = (Get-ItemProperty $RegPath -Name ProxyServer -ErrorAction SilentlyContinue).ProxyServer
  return @{ Enabled = [bool]($en -eq 1); Server = "$srv" }
}

function Set-Proxy {
  param([int]$Enable, [string]$Server)
  Set-ItemProperty -Path $RegPath -Name ProxyEnable -Value $Enable -Type DWord
  if ($Server -ne '') {
    Set-ItemProperty -Path $RegPath -Name ProxyServer -Value $Server -Type String
  }
  # 广播变更，让已经在跑的浏览器立刻感知（否则要重启浏览器）
  try {
    if (-not ('FerryInet' -as [type])) {
      Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class FerryInet {
  [DllImport("wininet.dll", SetLastError=true, CharSet=CharSet.Auto)]
  public static extern bool InternetSetOption(IntPtr h, int o, IntPtr b, int l);
  public static void Refresh() {
    InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
    InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
  }
}
'@ -ErrorAction SilentlyContinue
    }
    [FerryInet]::Refresh()
  } catch {}
}

# 借网地址判定：指向本机 httpPort 就算「走渡口」
$script:BorrowServer = "http://127.0.0.1:$($script:HttpPort)"

function Test-Borrowing {
  $p = Get-Proxy
  return ($p.Enabled -and $p.Server -eq $script:BorrowServer)
}

# ── 进程管理 ─────────────────────────────────────────────────
function Get-AppProcs {
  $r = @{ keeper = @(); bridge = @() }
  Get-CimInstance Win32_Process -Filter "Name='powershell.exe' or Name='pwsh.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*$AppDir*ssh-tunnel-keeper.ps1*" } |
    ForEach-Object { $r.keeper += $_.ProcessId }
  Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*$AppDir*dual-proxy.mjs*" } |
    ForEach-Object { $r.bridge += $_.ProcessId }
  return $r
}

function Start-All {
  param([switch]$Quiet)
  $p = Get-AppProcs
  $started = @()

  if ($p.keeper.Count -eq 0) {
    $keeper = Join-Path $AppDir 'ssh-tunnel-keeper.ps1'
    if (Test-Path $keeper) {
      Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $keeper,
        '-RemoteHost', $script:RemoteHost, '-SocksPort', "$($script:HttpPort - 1)"
      ) | Out-Null
      $started += '隧道'
    }
  }

  if ($p.bridge.Count -eq 0) {
    $bridge = Join-Path $AppDir 'dual-proxy.mjs'
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if ((Test-Path $bridge) -and $node) {
      Start-Process -FilePath $node.Source -WindowStyle Hidden -ArgumentList @(
        $bridge, '--listen', "$($script:HttpPort)", '--socks', "$($script:HttpPort - 1)",
        '--keeper', (Join-Path $AppDir 'ssh-tunnel-keeper.ps1'),
        '--remote', $script:RemoteHost,
        '--log', (Join-Path $AppDir 'bridge.log')
      ) | Out-Null
      $started += '代理'
    }
  }
  if (-not $Quiet) {
    if ($started.Count) { Log "已启动: $($started -join ', ')" } else { Log '组件已在运行' }
  }
  return $started
}

function Stop-All {
  $p = Get-AppProcs
  foreach ($id in ($p.keeper + $p.bridge)) {
    Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
  }
  # 隧道用的 ssh.exe 也要杀，否则它变孤儿继续占着端口。
  # 只杀带本目录特征的，别动用户自己的 ssh。
  Get-CimInstance Win32_Process -Filter "Name='ssh.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match "-D\s+127\.0\.0\.1:$($script:HttpPort - 1)\b" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Log '已停止隧道与代理'
}

# ── 状态 ─────────────────────────────────────────────────────
function Get-State {
  $proxy = Get-Proxy
  $procs = Get-AppProcs
  $tunnelUp = Test-Port -Port ($script:HttpPort - 1)
  $bridgeUp = Test-Port -Port $script:HttpPort
  $borrowing = ($proxy.Enabled -and $proxy.Server -eq $script:BorrowServer -and $bridgeUp)

  [PSCustomObject]@{
    Borrowing = $borrowing
    TunnelUp  = $tunnelUp
    BridgeUp  = $bridgeUp
    Keeper    = ($procs.keeper.Count + $procs.bridge.Count) -gt 0
    ProxyOn   = $proxy.Enabled
    ProxySrv  = $proxy.Server
    HasNet    = (Test-Net)
  }
}

# ══════════════════════════════════════════════════════════════
#  界面
# ══════════════════════════════════════════════════════════════

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$clrOk    = [System.Drawing.Color]::FromArgb(32, 178, 170)
$clrOkD   = [System.Drawing.Color]::FromArgb(22, 150, 143)
$clrBad   = [System.Drawing.Color]::FromArgb(231, 76, 60)
$clrIdle  = [System.Drawing.Color]::FromArgb(149, 165, 166)
$clrText  = [System.Drawing.Color]::FromArgb(44, 62, 80)
$clrMuted = [System.Drawing.Color]::FromArgb(140, 150, 160)
$clrBg    = [System.Drawing.Color]::FromArgb(242, 245, 246)
$clrCard  = [System.Drawing.Color]::White
$clrLine  = [System.Drawing.Color]::FromArgb(228, 233, 235)

$form                  = New-Object System.Windows.Forms.Form
$form.Text             = '渡口助手'
$form.Size             = New-Object System.Drawing.Size(430, 520)
$form.StartPosition    = 'CenterScreen'
$form.FormBorderStyle  = 'FixedDialog'
$form.MaximizeBox      = $false
$form.MinimumSize      = $form.Size
$form.BackColor        = $clrBg
$form.Font             = New-Object System.Drawing.Font('Microsoft YaHei', 9)

# 双缓冲，避免重绘闪烁
try {
  $pi = [System.Windows.Forms.Form].GetProperty('DoubleBuffered',
    [System.Reflection.BindingFlags]'Instance,NonPublic')
  $pi.SetValue($form, $true, $null)
} catch {}

# ── 圆角工具 ──
$script:CardRadius = 14
$script:BuildPath = {
  param([int]$w, [int]$h, [int]$r)
  $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
  $gp.AddArc(0, 0, $r, $r, 180, 90)
  $gp.AddArc($w - $r - 1, 0, $r, $r, 270, 90)
  $gp.AddArc($w - $r - 1, $h - $r - 1, $r, $r, 0, 90)
  $gp.AddArc(0, $h - $r - 1, $r, $r, 90, 90)
  $gp.CloseFigure()
  return $gp
}

function New-Card {
  param($Parent, [int]$X, [int]$Y, [int]$W, [int]$H)
  $p = New-Object System.Windows.Forms.Panel
  $p.Location = New-Object System.Drawing.Point($X, $Y)
  $p.Size = New-Object System.Drawing.Size($W, $H)
  $p.BackColor = $clrCard
  try {
    $path = & $script:BuildPath $W $H $script:CardRadius
    $p.Region = New-Object System.Drawing.Region -ArgumentList $path
  } catch {}
  $Parent.Controls.Add($p)
  return $p
}

# ── 顶部状态卡 ──
$pnlTop = New-Card -Parent $form -X 16 -Y 14 -W 382 -H 116

$lblBig = New-Object System.Windows.Forms.Label
$lblBig.Location = New-Object System.Drawing.Point(0, 16)
$lblBig.Size = New-Object System.Drawing.Size(382, 42)
$lblBig.TextAlign = 'MiddleCenter'
$lblBig.Font = New-Object System.Drawing.Font('Microsoft YaHei', 21, [System.Drawing.FontStyle]::Bold)
$lblBig.Text = '检测中…'
$pnlTop.Controls.Add($lblBig)

$lblDesc = New-Object System.Windows.Forms.Label
$lblDesc.Location = New-Object System.Drawing.Point(16, 62)
$lblDesc.Size = New-Object System.Drawing.Size(350, 20)
$lblDesc.TextAlign = 'MiddleCenter'
$lblDesc.ForeColor = $clrMuted
$lblDesc.Font = New-Object System.Drawing.Font('Microsoft YaHei', 9.5)
$lblDesc.Text = ''
$pnlTop.Controls.Add($lblDesc)

$lblAcct = New-Object System.Windows.Forms.Label
$lblAcct.Location = New-Object System.Drawing.Point(16, 86)
$lblAcct.Size = New-Object System.Drawing.Size(350, 20)
$lblAcct.TextAlign = 'MiddleCenter'
$lblAcct.ForeColor = $clrMuted
$lblAcct.Font = New-Object System.Drawing.Font('Consolas', 9)
$lblAcct.Text = "账号 $script:AccountIp"
$pnlTop.Controls.Add($lblAcct)

# 激活入口挂在账号行上：未激活时这行变成可点击的「点这里输入密钥」。
# 不再单独放一个按钮 —— 面板本来就窄，而且用户第一眼就该看到「没激活」。
$lblAcct.Cursor = [System.Windows.Forms.Cursors]::Hand
$lblAcct.Add_Click({
  if ($script:Activated) {
    $nl = [Environment]::NewLine
    $msg = '已激活，账号 ' + $script:AccountIp + '。' + $nl + $nl +
           '如果换机器或换 IP，需要向管理员要新密钥。'
    [System.Windows.Forms.MessageBox]::Show($msg, '渡口', 'OK', 'Information') | Out-Null
    return
  }
  Show-ActivateDialog
})

function Sync-AccountLabel {
  if ($script:Activated) {
    $lblAcct.Text = "账号 $script:AccountIp"
    $lblAcct.ForeColor = $clrMuted
    $lblAcct.Cursor = [System.Windows.Forms.Cursors]::Default
  } else {
    $lblAcct.Text = '未激活 —— 点这里输入密钥'
    $lblAcct.ForeColor = $clrBad
    $lblAcct.Cursor = [System.Windows.Forms.Cursors]::Hand
  }
}

# ── 链路明细 ──
$pnlDetail = New-Card -Parent $form -X 16 -Y 142 -W 382 -H 96

$lblDetTitle = New-Object System.Windows.Forms.Label
$lblDetTitle.Location = New-Object System.Drawing.Point(18, 10)
$lblDetTitle.Size = New-Object System.Drawing.Size(346, 16)
$lblDetTitle.Text = '链路明细'
$lblDetTitle.ForeColor = $clrMuted
$lblDetTitle.Font = New-Object System.Drawing.Font('Microsoft YaHei', 8)
$pnlDetail.Controls.Add($lblDetTitle)

$script:Rows = @()
for ($i = 0; $i -lt 3; $i++) {
  $rowY = 32 + $i * 20
  $lb = New-Object System.Windows.Forms.Label
  $lb.Location = New-Object System.Drawing.Point(18, $rowY)
  $lb.Size = New-Object System.Drawing.Size(346, 18)
  $lb.Font = New-Object System.Drawing.Font('Microsoft YaHei', 8.5)
  $lb.ForeColor = $clrText
  $lb.Text = ''
  $pnlDetail.Controls.Add($lb)
  $script:Rows += $lb
}

# ── 主按钮 ──
$btnOn = New-Object System.Windows.Forms.Button
$btnOn.Location = New-Object System.Drawing.Point(16, 254)
$btnOn.Size = New-Object System.Drawing.Size(186, 52)
$btnOn.Text = '开启渡口'
$btnOn.FlatStyle = 'Flat'
$btnOn.FlatAppearance.BorderSize = 0
$btnOn.FlatAppearance.MouseOverBackColor = $clrOkD
$btnOn.BackColor = $clrOk
$btnOn.ForeColor = [System.Drawing.Color]::White
$btnOn.Font = New-Object System.Drawing.Font('Microsoft YaHei', 13, [System.Drawing.FontStyle]::Bold)
$btnOn.Cursor = [System.Windows.Forms.Cursors]::Hand
$form.Controls.Add($btnOn)
try {
  $path = & $script:BuildPath 186 52 26
  $btnOn.Region = New-Object System.Drawing.Region -ArgumentList $path
} catch {}

$btnOff = New-Object System.Windows.Forms.Button
$btnOff.Location = New-Object System.Drawing.Point(212, 254)
$btnOff.Size = New-Object System.Drawing.Size(186, 52)
$btnOff.Text = '切回直连'
$btnOff.FlatStyle = 'Flat'
$btnOff.FlatAppearance.BorderSize = 1
$btnOff.FlatAppearance.BorderColor = $clrLine
$btnOff.BackColor = $clrCard
$btnOff.ForeColor = $clrText
$btnOff.Font = New-Object System.Drawing.Font('Microsoft YaHei', 13, [System.Drawing.FontStyle]::Bold)
$btnOff.Cursor = [System.Windows.Forms.Cursors]::Hand
$form.Controls.Add($btnOff)
try {
  $path = & $script:BuildPath 186 52 26
  $btnOff.Region = New-Object System.Drawing.Region -ArgumentList $path
} catch {}

# ── 次级按钮 ──
$btnRefresh = New-Object System.Windows.Forms.Button
$btnRefresh.Location = New-Object System.Drawing.Point(16, 318)
$btnRefresh.Size = New-Object System.Drawing.Size(92, 28)
$btnRefresh.Text = '刷新'
$btnRefresh.FlatStyle = 'Flat'
$btnRefresh.FlatAppearance.BorderSize = 0
$btnRefresh.BackColor = [System.Drawing.Color]::FromArgb(236, 240, 242)
$btnRefresh.ForeColor = $clrText
$btnRefresh.Font = New-Object System.Drawing.Font('Microsoft YaHei', 8.5)
$btnRefresh.Cursor = [System.Windows.Forms.Cursors]::Hand
$form.Controls.Add($btnRefresh)

$chkAuto = New-Object System.Windows.Forms.CheckBox
$chkAuto.Location = New-Object System.Drawing.Point(118, 321)
$chkAuto.Size = New-Object System.Drawing.Size(120, 22)
$chkAuto.Text = '自动刷新'
$chkAuto.Checked = $true
$chkAuto.ForeColor = $clrMuted
$chkAuto.Font = New-Object System.Drawing.Font('Microsoft YaHei', 8.5)
$form.Controls.Add($chkAuto)

$btnUninstall = New-Object System.Windows.Forms.Button
$btnUninstall.Location = New-Object System.Drawing.Point(300, 318)
$btnUninstall.Size = New-Object System.Drawing.Size(98, 28)
$btnUninstall.Text = '卸载'
$btnUninstall.FlatStyle = 'Flat'
$btnUninstall.FlatAppearance.BorderSize = 0
$btnUninstall.BackColor = [System.Drawing.Color]::FromArgb(253, 237, 237)
$btnUninstall.ForeColor = $clrBad
$btnUninstall.Font = New-Object System.Drawing.Font('Microsoft YaHei', 8.5)
$btnUninstall.Cursor = [System.Windows.Forms.Cursors]::Hand
$form.Controls.Add($btnUninstall)

# ── 日志窗 ──
$pnlLog = New-Object System.Windows.Forms.Panel
$pnlLog.Location = New-Object System.Drawing.Point(16, 354)
$pnlLog.Size = New-Object System.Drawing.Size(382, 118)
$pnlLog.BackColor = [System.Drawing.Color]::FromArgb(38, 42, 48)
$form.Controls.Add($pnlLog)
try {
  $path = & $script:BuildPath 382 118 12
  $pnlLog.Region = New-Object System.Drawing.Region -ArgumentList $path
} catch {}

$txtLog = New-Object System.Windows.Forms.TextBox
$txtLog.Location = New-Object System.Drawing.Point(8, 8)
$txtLog.Size = New-Object System.Drawing.Size(354, 102)
$txtLog.Multiline = $true
$txtLog.ScrollBars = 'Vertical'
$txtLog.ReadOnly = $true
$txtLog.BackColor = [System.Drawing.Color]::FromArgb(38, 42, 48)
$txtLog.ForeColor = [System.Drawing.Color]::FromArgb(168, 220, 190)
$txtLog.Font = New-Object System.Drawing.Font('Consolas', 8.5)
$txtLog.BorderStyle = 'None'
$pnlLog.Controls.Add($txtLog)

# ── 托盘 ──
$notify = New-Object System.Windows.Forms.NotifyIcon
$notify.Text = '渡口助手'
try { $notify.Icon = [System.Drawing.SystemIcons]::Shield } catch {}
$notify.Visible = $true

$mnuShow = New-Object System.Windows.Forms.ToolStripMenuItem
$mnuShow.Text = '显示主窗口'
$mnuToggle = New-Object System.Windows.Forms.ToolStripMenuItem
$mnuToggle.Text = '开启 / 关闭'
$mnuExit = New-Object System.Windows.Forms.ToolStripMenuItem
$mnuExit.Text = '退出'
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$null = $menu.Items.Add($mnuShow)
$null = $menu.Items.Add($mnuToggle)
$null = $menu.Items.Add('-')
$null = $menu.Items.Add($mnuExit)
$notify.ContextMenuStrip = $menu

# ── 逻辑 ──
function Add-Log {
  param([string]$M)
  $txtLog.AppendText("$(Get-Date -Format 'HH:mm:ss')  $M`r`n")
  Log $M
}

function Update-UI {
  $s = Get-State

  if ($s.Borrowing) {
    $lblBig.Text = '正在走渡口'
    $lblBig.ForeColor = $clrOk
    $lblDesc.Text = '流量经远端主机中转'
  } elseif ($s.HasNet) {
    $lblBig.Text = '直连'
    $lblBig.ForeColor = $clrIdle
    $lblDesc.Text = '正常使用本机网络'
  } else {
    $lblBig.Text = '无网络'
    $lblBig.ForeColor = $clrBad
    $lblDesc.Text = '本机连不上网 —— 试试开启渡口'
  }

  $onOff = { param($b, $t, $f) if ($b) { $t } else { $f } }
  $script:Rows[0].Text = '隧道    ' + (& $onOff $s.TunnelUp '已连接' '未连接')
  $script:Rows[0].ForeColor = if ($s.TunnelUp) { $clrText } else { $clrMuted }

  $script:Rows[1].Text = '本地代理  ' + (& $onOff $s.BridgeUp "运行中（端口 $script:HttpPort）" '未运行')
  $script:Rows[1].ForeColor = if ($s.BridgeUp) { $clrText } else { $clrMuted }

  $script:Rows[2].Text = '系统代理  ' + (& $onOff $s.ProxyOn '开' '关') + '  ' + $s.ProxySrv
  $script:Rows[2].ForeColor = if ($s.Borrowing) { $clrOk } else { $clrText }

  try { Sync-AccountLabel } catch { }

  # 没激活就不让借网 —— 否则 ssh 会拿着空令牌去连，失败信息对用户毫无意义。
  if (-not $script:Activated) {
    $btnOn.Enabled = $false
    $lblBig.Text = '未激活'
    $lblBig.ForeColor = $clrBad
    $lblDesc.Text = '点下方的账号行，输入管理员给的密钥'
    $btnOff.Enabled = $s.Borrowing
    return $s
  }

  $btnOn.Enabled = -not $s.Borrowing
  $btnOff.Enabled = $s.Borrowing
  return $s
}

function Set-Busy {
  param([bool]$B)
  $btnOn.Enabled = -not $B
  $btnOff.Enabled = -not $B
  $btnRefresh.Enabled = -not $B
  $form.Cursor = if ($B) { [System.Windows.Forms.Cursors]::WaitCursor } else { [System.Windows.Forms.Cursors]::Default }
  [System.Windows.Forms.Application]::DoEvents()
}

# 开启
$btnOn.Add_Click({
  Set-Busy $true
  try {
    Add-Log '启动隧道与代理…'
    Start-All | Out-Null
    # 等端口就绪，最多 15 秒
    $ok = $false
    for ($i = 0; $i -lt 30; $i++) {
      Start-Sleep -Milliseconds 500
      if (Test-Port -Port $script:HttpPort) { $ok = $true; break }
    }
    if (-not $ok) {
      Add-Log '代理端口没起来，检查 node 是否安装'
      [System.Windows.Forms.MessageBox]::Show(
        "本地代理没启动成功。`n`n常见原因：`n· 这台机器没装 Node.js`n· 端口 $script:HttpPort 被占用`n`n日志：$LogPath",
        '渡口助手', 'OK', 'Warning') | Out-Null
    } else {
      # 备份原代理，再切到渡口。备份只存一次，避免反复覆盖成渡口地址。
      $cur = Get-Proxy
      if ($cur.Server -ne $script:BorrowServer) {
        Write-JsonFile $StatePath @{ prevEnabled = $cur.Enabled; prevServer = $cur.Server }
      }
      Set-Proxy -Enable 1 -Server $script:BorrowServer
      Add-Log "系统代理已切到 $script:BorrowServer"
    }
  } catch { Add-Log "出错: $($_.Exception.Message)" }
  finally { Set-Busy $false; Update-UI | Out-Null }
})

# 关闭
$btnOff.Add_Click({
  Set-Busy $true
  try {
    Add-Log '切回直连…'
    # 还原顺序很重要：先还原代理，再停服务。
    # 反过来的话，代理会短暂指向一个已经停掉的端口，那几秒全网上不去。
    $prev = Read-JsonFile $StatePath
    if ($prev) {
      $en = if ($prev.prevEnabled) { 1 } else { 0 }
      $sv = "$($prev.prevServer)"
      Set-Proxy -Enable $en -Server $sv
      Add-Log "已还原系统代理: $(if($en){$sv}else{'关闭'})"
    } elseif ($script:FallbackProxy) {
      Set-Proxy -Enable 1 -Server $script:FallbackProxy
      Add-Log "已还原到 $script:FallbackProxy"
    } else {
      Set-Proxy -Enable 0 -Server ''
      Add-Log '已关闭系统代理'
    }
    Stop-All
  } catch { Add-Log "出错: $($_.Exception.Message)" }
  finally { Set-Busy $false; Update-UI | Out-Null }
})

$btnRefresh.Add_Click({ Update-UI | Out-Null; Add-Log '已刷新' })

# 卸载：切回直连 + 删自启 + 提示手动删目录
$btnUninstall.Add_Click({
  $r = [System.Windows.Forms.MessageBox]::Show(
    "卸载会：`n· 切回直连`n· 停止隧道与代理`n· 删除开机自启`n`n（不会自动删文件，方便你保留日志）`n`n继续？",
    '卸载渡口', 'YesNo', 'Question')
  if ($r -ne 'Yes') { return }
  Set-Busy $true
  try {
    # 先还原代理
    $prev = Read-JsonFile $StatePath
    if ($prev) {
      $en = if ($prev.prevEnabled) { 1 } else { 0 }
      Set-Proxy -Enable $en -Server "$($prev.prevServer)"
    } else {
      Set-Proxy -Enable 0 -Server ''
    }
    Stop-All
    # 删自启快捷方式
    $startup = [Environment]::GetFolderPath('Startup')
    Get-ChildItem $startup -Filter '*渡口*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem $startup -Filter '*Ferry*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Add-Log '已卸载（文件保留在 ' + $AppDir + '）'
    [System.Windows.Forms.MessageBox]::Show(
      "已切回直连并删除自启。`n`n如果完全不需要了，可以手动删除目录：`n$AppDir",
      '渡口助手', 'OK', 'Information') | Out-Null
  } catch { Add-Log "卸载出错: $($_.Exception.Message)" }
  finally { Set-Busy $false; Update-UI | Out-Null }
})

# 托盘菜单
$mnuShow.Add_Click({
  $form.Show()
  $form.WindowState = 'Normal'
  $form.Activate()
  $notify.Visible = $false
  Update-UI | Out-Null
})
$mnuToggle.Add_Click({
  if ((Get-State).Borrowing) { $btnOff.PerformClick() } else { $btnOn.PerformClick() }
})
$mnuExit.Add_Click({
  $notify.Visible = $false
  $notify.Dispose()
  [System.Windows.Forms.Application]::Exit()
  [Environment]::Exit(0)
})
$notify.Add_MouseDoubleClick({
  $form.Show()
  $form.WindowState = 'Normal'
  $form.Activate()
  $notify.Visible = $false
})

# 关窗口 = 收托盘
$form.Add_FormClosing({
  param($s, $e)
  # 只有「用户点 X」才取消关闭并隐藏；程序自己 Exit 时放行
  if ($e.CloseReason -eq [System.Windows.Forms.CloseReason]::UserClosing) {
    $e.Cancel = $true
    $form.Hide()
    $notify.Visible = $true
    $notify.ShowBalloonTip(1500, '渡口助手', '已收到托盘，双击图标可重新打开', 'None')
  }
})
$form.Add_FormClosed({
  $notify.Visible = $false
  $notify.Dispose()
})

# 定时刷新
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 3000
$timer.Add_Tick({ if ($chkAuto.Checked) { Update-UI | Out-Null } })
$timer.Start()

# 启动
Log "==== 渡口助手启动 pid=$PID 端口=$script:HttpPort 远端=$script:RemoteHost ===="
Update-UI | Out-Null
Add-Log "账号 $script:AccountIp · 远端 $script:RemoteHost"
if (-not (Get-State).HasNet) {
  Add-Log '检测到本机无网络，点「开启渡口」'
}

[void][System.Windows.Forms.Application]::Run($form)
