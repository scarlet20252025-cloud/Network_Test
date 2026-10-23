#requires -Version 5.1
<#
  借网助手 · 图形界面版
  场景：本机被学校断网时，借远端 10.13.24.1（办公网）的出口上网。
  底层逻辑与 borrow-net.ps1 完全一致，只是把命令换成了按钮。

  用法：双击本文件（或在 PowerShell 里执行）即可打开窗口。

  日志：界面日志与未捕获异常都会镜像写入 .borrow-net\gui.log，
        窗口莫名消失时先看这个文件。
#>

# ---------------- 日志出口（最先建，后面任何崩溃都能留下记录） ----------------
$StateDir = Join-Path $PSScriptRoot '.borrow-net'
$LogFile  = Join-Path $StateDir 'gui.log'
try { if (-not (Test-Path $StateDir)) { $null = New-Item -ItemType Directory -Path $StateDir -Force } } catch {}

function Write-AppLog {
  param([string]$Msg, [string]$Level = 'INFO')
  $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
  $line  = "$stamp [$Level] $Msg"
  # Add-Content 每次开关文件句柄，崩溃时已写入的内容不会丢
  try { Add-Content -Path $LogFile -Value $line -Encoding UTF8 -ErrorAction Stop } catch {}
  try { if ($Host -and $Host.UI -and $Host.UI.RawUI) { Write-Host $line } } catch {}
}

# 兜底：未捕获异常也写日志，绝不让窗口「静默消失」
trap {
  Write-AppLog ("未捕获异常: " + $_.Exception.Message) 'FATAL'
  Write-AppLog ($_.ScriptStackTrace) 'FATAL'
  if ($_.Exception.InnerException) { Write-AppLog ("  内部: " + $_.Exception.InnerException.Message) 'FATAL' }
  continue
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Write-AppLog "======== 借网助手启动 pid=$PID PS=$($PSVersionTable.PSVersion) ========"

# ---------------- 配置 ----------------
$RemoteHost   = 'pcs'
$SocksPort    = 10808
# ⚠️ Windows 系统代理（WinINET 的 ProxyServer）只支持 HTTP 代理，不支持 SOCKS5。
# 必须把系统代理指向 dual-proxy 的 HTTP 端口，绝不能填 $SocksPort。
# 历史事故：这里曾写成 "127.0.0.1:$SocksPort"，导致浏览器 / .NET / Electron
# 把 SOCKS5 端口当 HTTP 代理去连 —— 全部秒失败，用户看到「开了借网浏览器打不开」。
$HttpPort     = 10809
$ProxyMode    = 'System'
$ToolsDir     = $PSScriptRoot
$KeeperScript = Join-Path $ToolsDir 'ssh-tunnel-keeper.ps1'
$BridgeScript = Join-Path $ToolsDir 'dual-proxy.mjs'

# ---------------- 工具函数（与 CLI 版共用逻辑） ----------------

function Get-KeeperProc {
  Get-CimInstance Win32_Process -Filter "Name='powershell.exe' or Name='pwsh.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*ssh-tunnel-keeper.ps1*" }
}

function Test-SocksAlive {
  # 不用 Test-NetConnection：它对关闭端口要等满 ~14s（Win10 SYN 无响应超时），
  # 会把界面启动和每次自动刷新都卡死。改用 TcpClient 异步连接 + 300ms 超时。
  try {
    $c = New-Object Net.Sockets.TcpClient
    $iar = $c.BeginConnect('127.0.0.1', $SocksPort, $null, $null)
    $ok = $iar.AsyncWaitHandle.WaitOne(300, $false)
    $c.Close()
    return [bool]$ok
  } catch { return $false }
}

function Get-EgressIp {
  param([string]$Proxy = '', [switch]$NoProxy)
  # 依次尝试多个出口 IP 查询源；单源失败不阻塞整行。用 curl 而不是 Invoke-WebRequest：
  # PS 5.1(.NET Framework) 的 -Proxy 不支持 socks5，而本 GUI 就跑在 powershell 5.1 下，
  # 用 IWR 会导致借网出口永远取不到（实测：同一个函数在 pwsh 7 里全过、在 5.1 里全 FAIL）。
  $urls = @('http://myip.ipip.net', 'http://ip.3322.net', 'http://members.3322.org/dyndns/getip')
  foreach ($u in $urls) {
    try {
      $curlArgs = @('-s', '--max-time', '6', $u)
      if ($NoProxy) { $curlArgs += '--noproxy', '*' }
      elseif ($Proxy) { $curlArgs += '--socks5-hostname', "127.0.0.1:$SocksPort" }
      $out = & curl.exe @curlArgs 2>$null
      if ($LASTEXITCODE -eq 0 -and $out) {
        $m = [regex]::Match("$out", '\d+\.\d+\.\d+\.\d+')
        if ($m.Success) { return $m.Value }
      }
    } catch {}
  }
  return ''
}

function Get-ProxyState {
  $k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
  $en = (Get-ItemProperty $k -Name ProxyEnable -ErrorAction SilentlyContinue).ProxyEnable
  $srv = (Get-ItemProperty $k -Name ProxyServer -ErrorAction SilentlyContinue).ProxyServer
  return @{ Enabled = [bool]($en -eq 1); Server = "$srv" }
}

function Set-Proxy {
  param([int]$Enable, [string]$Server)
  $k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
  Set-ItemProperty $k -Name ProxyEnable -Value $Enable -Type DWord
  if ($Server) { Set-ItemProperty $k -Name ProxyServer -Value $Server -Type String }
  $sig = @'
using System;
using System.Runtime.InteropServices;
public class InetOptRefresh {
  [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
  public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
  public const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
  public const int INTERNET_OPTION_REFRESH = 37;
  public static void Refresh() {
    InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
    InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
  }
}
'@
  if (-not ('InetOptRefresh' -as [type])) {
    Add-Type -TypeDefinition $sig -Language CSharp -ErrorAction SilentlyContinue
  }
  try { [InetOptRefresh]::Refresh() } catch {}
}

function Test-PortOpen {
  param([int]$Port, [int]$TimeoutMs = 300)
  try {
    $c = New-Object Net.Sockets.TcpClient
    $iar = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
    $ok = $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)
    $c.Close()
    return [bool]$ok
  } catch { return $false }
}

function Test-BridgeAlive { return (Test-PortOpen -Port $HttpPort -TimeoutMs 300) }

function Start-Bridge {
  # 启动 dual-proxy（HTTP+SOCKS5 单端口），已存在则复用。
  # 为什么必须启动它：系统代理只能填 HTTP 端口，没有这一层就没法让浏览器工作。
  if (Test-BridgeAlive) { return 'running' }
  $node = 'D:\nodejs\node.exe'
  if (-not (Test-Path $node)) {
    $nc = Get-Command node -ErrorAction SilentlyContinue
    if ($nc) { $node = $nc.Source }
  }
  if (-not (Test-Path $node) -or -not (Test-Path $BridgeScript)) { return 'missing' }

  $cmdLine = "`"$node`" `"$BridgeScript`" --listen $HttpPort --socks $SocksPort"
  try {
    # WMI 创建：完全脱离父进程，不会随 GUI 退出被回收
    $cr = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmdLine }
    if ($cr.ReturnValue -ne 0) { return "failed:$($cr.ReturnValue)" }
  } catch { return "error:$($_.Exception.Message)" }

  for ($i = 0; $i -lt 20; $i++) {
    if (Test-BridgeAlive) { return 'started' }
    Start-Sleep -Milliseconds 300
  }
  return 'timeout'
}

function Start-Keeper {
  if (Get-KeeperProc) { return 'running' }
  $arg = "-NoProfile -ExecutionPolicy Bypass -File `"$KeeperScript`" -RemoteHost $RemoteHost -SocksPort $SocksPort"
  $shell = New-Object -ComObject WScript.Shell
  $null = $shell.Run("powershell.exe $arg", 0, $false)
  return 'started'
}

function Stop-Keeper {
  $p = Get-KeeperProc
  if ($p) { $p | ForEach-Object { Stop-Process -Id $_.ProcessId -Force } }
  $sshProcs = Get-CimInstance Win32_Process -Filter "Name='ssh.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*-D*127.0.0.1:$SocksPort*" }
  if ($sshProcs) { $sshProcs | ForEach-Object { Stop-Process -Id $_.ProcessId -Force } }
}

function Wait-Port {
  param([int]$Port, [int]$TimeoutSec = 15)
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $deadline) {
    if (Test-SocksAlive) { return $true }
    Start-Sleep -Milliseconds 500
  }
  return $false
}

# ---------------- 异步出口探测 ----------------
# 为什么必须异步：Get-EgressIp 单源最多 6s、三源串联最坏 18s。放在 UI 线程里
# 每次刷新都会把界面冻住（实测 CLI Status 要 15s）。改成后台 runspace 定期刷新，
# UI 只读缓存，刷新变成毫秒级。
$script:Probe = [hashtable]::Synchronized(@{
    Direct = ''
    Borrow = ''
    When   = [DateTime]::MinValue
    Busy   = $false
    Error  = ''
  })

$script:ProbeRs = $null
$script:ProbePs = $null
$script:ProbeHandle = $null

$script:ProbeScript = {
  param($Probe, $SocksPort, $NeedBorrow)
  $Probe.Busy = $true
  try {
    $urls = @('http://myip.ipip.net', 'http://ip.3322.net', 'http://members.3322.org/dyndns/getip')
    function GetIp([string]$mode) {
      foreach ($u in $urls) {
        try {
          $a = @('-s', '--max-time', '6', $u)
          if ($mode -eq 'direct') { $a += '--noproxy', '*' }
          elseif ($mode -eq 'socks') { $a += '--socks5-hostname', "127.0.0.1:$SocksPort" }
          $o = & curl.exe @a 2>$null
          if ($LASTEXITCODE -eq 0 -and $o) {
            $m = [regex]::Match("$o", '\d+\.\d+\.\d+\.\d+')
            if ($m.Success) { return $m.Value }
          }
        } catch {}
      }
      return ''
    }
    $Probe.Direct = GetIp 'direct'
    if ($NeedBorrow) { $Probe.Borrow = GetIp 'socks' } else { $Probe.Borrow = '' }
    $Probe.When = [DateTime]::Now
    $Probe.Error = ''
  }
  catch { $Probe.Error = $_.Exception.Message }
  finally { $Probe.Busy = $false }
}

function Initialize-Probe {
  if ($script:ProbeRs) { return }
  try {
    $rs = [runspacefactory]::CreateRunspace()
    $rs.ApartmentState = 'MTA'
    $rs.ThreadOptions  = 'ReuseThread'
    $rs.Open()
    $script:ProbeRs = $rs
  }
  catch { Write-AppLog "创建探测 runspace 失败: $($_.Exception.Message)" 'WARN' }
}

function Start-Probe {
  param([bool]$NeedBorrow)
  Initialize-Probe
  if (-not $script:ProbeRs) { return }
  if ($script:Probe.Busy) { return }      # 上一轮还没跑完，跳过
  try {
    $ps = [powershell]::Create()
    $ps.Runspace = $script:ProbeRs
    $null = $ps.AddScript($script:ProbeScript).AddArgument($script:Probe).AddArgument($SocksPort).AddArgument($NeedBorrow)
    $script:ProbePs = $ps
    $script:ProbeHandle = $ps.BeginInvoke()
  }
  catch { Write-AppLog "启动探测失败: $($_.Exception.Message)" 'WARN' }
}

function Complete-Probe {
  if (-not $script:ProbeHandle) { return }
  if (-not $script:ProbeHandle.IsCompleted) { return }
  try { $null = $script:ProbePs.EndInvoke($script:ProbeHandle) } catch {}
  try { $script:ProbePs.Dispose() } catch {}
  $script:ProbePs = $null
  $script:ProbeHandle = $null
}

function Get-State {
  $alive  = Test-SocksAlive
  $bridge = Test-BridgeAlive
  $st     = Get-ProxyState
  $keeper = [bool](Get-KeeperProc)

  # 「借网生效」的判据：系统代理指向 HTTP 端口（浏览器真能用）+ 隧道存活 + bridge 存活。
  # 注意：如果系统代理还是历史的 SOCKS 端口写法，这里判为「未生效」并置 StaleProxy，
  # 让 UI 提示用户重新点一次「开启借网」来修复，而不是把它当成正常状态。
  $httpProxy = "http://127.0.0.1:$HttpPort"
  $socksProxy = "127.0.0.1:$SocksPort"
  $stale = ($st.Enabled -and $st.Server -eq $socksProxy)
  $borrowing = ($st.Enabled -and $st.Server -eq $httpProxy -and $alive -and $bridge)

  # 出口 IP 一律读后台缓存，绝不在 UI 线程里发网络请求
  $directIp = "$($script:Probe.Direct)"
  $borrowIp = if ($borrowing) { "$($script:Probe.Borrow)" } else { '' }

  [PSCustomObject]@{
    SocksAlive = $alive
    BridgeAlive = $bridge
    StaleProxy = $stale
    ProxyOn    = $st.Enabled
    ProxySrv   = $st.Server
    Keeper     = $keeper
    Borrowing  = $borrowing
    DirectIp   = $directIp
    BorrowIp   = $borrowIp
    HasNet     = [bool]($directIp -or $borrowIp)
    CurrentIp  = if ($borrowing) { $borrowIp } else { $directIp }
    ProbeAge   = if ($script:Probe.When -gt [DateTime]::MinValue) { [int](([DateTime]::Now - $script:Probe.When).TotalSeconds) } else { -1 }
  }
}

# ---------------- 界面 ----------------

$form                 = New-Object System.Windows.Forms.Form
$form.Text           = '借网助手'
$form.Size           = New-Object System.Drawing.Size(470, 560)
$form.StartPosition  = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox    = $false
$form.MinimumSize    = $form.Size
$form.Font           = New-Object System.Drawing.Font('Microsoft YaHei', 9)
$form.BackColor      = [System.Drawing.Color]::FromArgb(248, 249, 250)

# ---- 双缓冲 + WS_EX_COMPOSITED：彻底消除还原时的黑块闪烁 ----
# 背景：WinForms 里改 ShowInTaskbar 会 RecreateHandle()，句柄一重建，
# 子控件来不及重绘就露出窗体底色（截图里那些黑块）。
# 正解是根本不动 ShowInTaskbar，改用 Hide()/Show() 做托盘化 —— Hide 不重建句柄。
try {
  $pi = [System.Windows.Forms.Form].GetProperty('DoubleBuffered',
    [System.Reflection.BindingFlags]'Instance,NonPublic')
  $pi.SetValue($form, $true, $null)
} catch { Write-AppLog "DoubleBuffered 设置失败: $($_.Exception.Message)" 'WARN' }

# ---- 托盘化：统一入口，避免各处重复逻辑 ----
# 规范做法（对照 Windows 桌面软件通行行为）：
#   * 托盘图标自程序启动就常驻，直到退出才消失 —— 不是"最小化后才冒出来"
#   * 主窗口最小化/关闭 = 隐藏到托盘，进程继续跑
#   * 只有托盘菜单「退出」才真正结束进程
#   * 全程不碰 ShowInTaskbar：改它会 RecreateHandle()，句柄重建时子控件来不及重绘
#     就露出窗体底色（就是之前那一片黑块）。Hide()/Show() 不重建句柄，安全。
function Show-MainWindow {
  Write-AppLog '显示主窗口'
  if (-not $form.Visible) { $form.Show() }
  $form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
  $form.Activate()
  $form.Refresh()
}

function Hide-ToTray {
  Write-AppLog '隐藏到托盘（进程继续运行）'
  $form.Hide()
}

$form.Add_Resize({
  # 用户点最小化按钮：先让窗体走完最小化动画，再隐藏（直接 Hide 会留动画残影）
  if ($form.WindowState -eq [System.Windows.Forms.FormWindowState]::Minimized) {
    $form.BeginInvoke([Action]{ Hide-ToTray }) | Out-Null
  }
})

# ===== 托盘图标 =====
# 启动即常驻，只有「退出」才 Dispose —— 这是 Windows 桌面软件的通行行为
$script:NotifyIcon = New-Object System.Windows.Forms.NotifyIcon
$script:NotifyIcon.Text = '借网助手'
$script:NotifyIcon.BalloonTipTitle = '借网助手'
$script:NotifyIcon.Visible = $true

# 图标：优先用 make-shortcut.ps1 生成的 .ico，取不到就退化为系统 Application 图标
$script:TrayIco = $null
$icoCandidate = Join-Path $StateDir 'borrow-net.ico'
if (Test-Path $icoCandidate) {
  try { $script:TrayIco = New-Object System.Drawing.Icon($icoCandidate) } catch { $script:TrayIco = $null }
}
if (-not $script:TrayIco) {
  try { $script:TrayIco = [System.Drawing.SystemIcons]::Application } catch {}
}
if ($script:TrayIco) { $script:NotifyIcon.Icon = $script:TrayIco }

$mnuShow = New-Object System.Windows.Forms.ToolStripMenuItem
$mnuShow.Text = '显示主窗口'
$mnuShow.Add_Click({ Show-MainWindow })
$mnuToggle = New-Object System.Windows.Forms.ToolStripMenuItem
$mnuToggle.Text = '开启借网 / 切回直连'
$mnuToggle.Add_Click({
    $s = Get-State
    if ($s.Borrowing) { $btnOff.PerformClick() } else { $btnOn.PerformClick() }
  })
$mnuExit = New-Object System.Windows.Forms.ToolStripMenuItem
$mnuExit.Text = '退出'
$mnuExit.Add_Click({
    Write-AppLog '托盘菜单: 退出'
    # 退出前把系统代理还原，避免留下指向已停隧道的代理导致全网不通
    try {
      $bakFile = Join-Path $StateDir 'proxy-backup.txt'
      if (Test-Path $bakFile) {
        $bak = (Get-Content $bakFile -Raw).Trim()
        $en, $srv = $bak -split '\|', 2
        $enVal = if ($en -match '^(1|true)$') { 1 } else { 0 }
        # 备份若也是 SOCKS 写法（历史污染）则不还原，直接关代理
        if ($srv -eq "127.0.0.1:$SocksPort") {
          Set-Proxy -Enable 0 -Server ''
          Write-AppLog "退出前：备份是 SOCKS 写法，已关闭系统代理"
        }
        else {
          Set-Proxy -Enable $enVal -Server $srv
          Write-AppLog "退出前已还原系统代理: $srv"
        }
      }
      else {
        Set-Proxy -Enable 0 -Server ''
        Write-AppLog '退出前：无备份，已关闭系统代理'
      }
    } catch { Write-AppLog "退出前还原代理失败: $($_.Exception.Message)" 'WARN' }
    if ($script:NotifyIcon) { $script:NotifyIcon.Visible = $false; $script:NotifyIcon.Dispose() }
    [System.Windows.Forms.Application]::Exit()
    [Environment]::Exit(0)
  })

$mnuStrip = New-Object System.Windows.Forms.ContextMenuStrip
$null = $mnuStrip.Items.Add($mnuShow)
$null = $mnuStrip.Items.Add($mnuToggle)
$null = $mnuStrip.Items.Add('-')
$null = $mnuStrip.Items.Add($mnuExit)
$script:NotifyIcon.ContextMenuStrip = $mnuStrip

$script:NotifyIcon.Add_MouseDoubleClick({ Show-MainWindow })

# 窗口「关闭」按钮 = 隐藏到托盘，不退出；退出只能从托盘菜单选
$form.Add_FormClosing({
    param($s, $e)
    if ($e.CloseReason -eq [System.Windows.Forms.CloseReason]::UserClosing) {
      $e.Cancel = $true
      Hide-ToTray
    }
  })

$form.Add_FormClosed({
  Write-AppLog "FormClosed"
  if ($script:NotifyIcon) { $script:NotifyIcon.Visible = $false; $script:NotifyIcon.Dispose() }
})

$clrOk    = [System.Drawing.Color]::FromArgb(16, 163, 127)
$clrBad   = [System.Drawing.Color]::FromArgb(214, 84, 71)
$clrIdle  = [System.Drawing.Color]::FromArgb(110, 122, 136)
$clrText  = [System.Drawing.Color]::FromArgb(40, 44, 52)
$clrMuted = [System.Drawing.Color]::FromArgb(140, 148, 156)

# ===== 顶部：大号状态卡 =====
$pnlCard              = New-Object System.Windows.Forms.Panel
$pnlCard.Location     = New-Object System.Drawing.Point(16, 14)
$pnlCard.Size         = New-Object System.Drawing.Size(422, 118)
$pnlCard.BackColor    = [System.Drawing.Color]::White
$form.Controls.Add($pnlCard)

$lblBig               = New-Object System.Windows.Forms.Label
$lblBig.Location      = New-Object System.Drawing.Point(0, 12)
$lblBig.Size          = New-Object System.Drawing.Size(422, 40)
$lblBig.TextAlign     = 'MiddleCenter'
$lblBig.Font          = New-Object System.Drawing.Font('Microsoft YaHei', 20, [System.Drawing.FontStyle]::Bold)
$lblBig.Text          = '检测中…'
$pnlCard.Controls.Add($lblBig)

$lblPath              = New-Object System.Windows.Forms.Label
$lblPath.Location     = New-Object System.Drawing.Point(16, 56)
$lblPath.Size         = New-Object System.Drawing.Size(390, 20)
$lblPath.TextAlign    = 'MiddleCenter'
$lblPath.ForeColor    = $clrMuted
$lblPath.Font         = New-Object System.Drawing.Font('Microsoft YaHei', 9.5)
$lblPath.Text         = '正在检查网络路径…'
$pnlCard.Controls.Add($lblPath)

$lblIpNow             = New-Object System.Windows.Forms.Label
$lblIpNow.Location    = New-Object System.Drawing.Point(16, 80)
$lblIpNow.Size        = New-Object System.Drawing.Size(390, 22)
$lblIpNow.TextAlign   = 'MiddleCenter'
$lblIpNow.Font        = New-Object System.Drawing.Font('Consolas', 11, [System.Drawing.FontStyle]::Bold)
$lblIpNow.Text        = ''
$pnlCard.Controls.Add($lblIpNow)

# ===== 中部：两个出口 IP 对照 =====
$grpIp                = New-Object System.Windows.Forms.GroupBox
$grpIp.Location       = New-Object System.Drawing.Point(16, 144)
$grpIp.Size           = New-Object System.Drawing.Size(422, 92)
$grpIp.Text           = '  出口 IP（不同 = 借网链路生效）  '
$grpIp.ForeColor      = $clrIdle
$grpIp.BackColor      = [System.Drawing.Color]::White
$form.Controls.Add($grpIp)

$lblDirectT           = New-Object System.Windows.Forms.Label
$lblDirectT.Location  = New-Object System.Drawing.Point(14, 26)
$lblDirectT.Size      = New-Object System.Drawing.Size(96, 20)
$lblDirectT.Text      = '直连出口'
$lblDirectT.ForeColor = $clrMuted
$grpIp.Controls.Add($lblDirectT)

$lblDirectV           = New-Object System.Windows.Forms.Label
$lblDirectV.Location  = New-Object System.Drawing.Point(110, 26)
$lblDirectV.Size      = New-Object System.Drawing.Size(296, 20)
$lblDirectV.Font      = New-Object System.Drawing.Font('Consolas', 10)
$lblDirectV.Text      = '—'
$grpIp.Controls.Add($lblDirectV)

$lblBorrowT           = New-Object System.Windows.Forms.Label
$lblBorrowT.Location  = New-Object System.Drawing.Point(14, 54)
$lblBorrowT.Size      = New-Object System.Drawing.Size(96, 20)
$lblBorrowT.Text      = '借网出口'
$lblBorrowT.ForeColor = $clrMuted
$grpIp.Controls.Add($lblBorrowT)

$lblBorrowV           = New-Object System.Windows.Forms.Label
$lblBorrowV.Location  = New-Object System.Drawing.Point(110, 54)
$lblBorrowV.Size      = New-Object System.Drawing.Size(296, 20)
$lblBorrowV.Font      = New-Object System.Drawing.Font('Consolas', 10)
$lblBorrowV.Text      = '—'
$grpIp.Controls.Add($lblBorrowV)

# ===== 链路明细 =====
$lblDetail            = New-Object System.Windows.Forms.Label
$lblDetail.Location   = New-Object System.Drawing.Point(16, 244)
$lblDetail.Size       = New-Object System.Drawing.Size(422, 54)
$lblDetail.ForeColor  = $clrMuted
$lblDetail.Font       = New-Object System.Drawing.Font('Microsoft YaHei', 8.5)
$lblDetail.Text       = ''
$form.Controls.Add($lblDetail)

# ===== 主按钮 =====
$btnOn                = New-Object System.Windows.Forms.Button
$btnOn.Location       = New-Object System.Drawing.Point(16, 306)
$btnOn.Size           = New-Object System.Drawing.Size(204, 54)
$btnOn.Text           = '开启借网'
$btnOn.FlatStyle      = 'Flat'
$btnOn.FlatAppearance.BorderSize = 0
$btnOn.BackColor      = $clrOk
$btnOn.ForeColor      = [System.Drawing.Color]::White
$btnOn.Font           = New-Object System.Drawing.Font('Microsoft YaHei', 13, [System.Drawing.FontStyle]::Bold)
$btnOn.Cursor         = [System.Windows.Forms.Cursors]::Hand
$form.Controls.Add($btnOn)

$btnOff               = New-Object System.Windows.Forms.Button
$btnOff.Location      = New-Object System.Drawing.Point(230, 306)
$btnOff.Size          = New-Object System.Drawing.Size(208, 54)
$btnOff.Text          = '切回直连'
$btnOff.FlatStyle     = 'Flat'
$btnOff.FlatAppearance.BorderSize = 1
$btnOff.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(205, 210, 215)
$btnOff.BackColor     = [System.Drawing.Color]::White
$btnOff.ForeColor     = [System.Drawing.Color]::FromArgb(80, 88, 96)
$btnOff.Font          = New-Object System.Drawing.Font('Microsoft YaHei', 13, [System.Drawing.FontStyle]::Bold)
$btnOff.Cursor        = [System.Windows.Forms.Cursors]::Hand
$form.Controls.Add($btnOff)

# ===== 次级按钮 =====
$btnRefresh           = New-Object System.Windows.Forms.Button
$btnRefresh.Location  = New-Object System.Drawing.Point(16, 370)
$btnRefresh.Size      = New-Object System.Drawing.Size(96, 28)
$btnRefresh.Text      = '刷新状态'
$btnRefresh.FlatStyle = 'Flat'
$btnRefresh.FlatAppearance.BorderSize = 0
$btnRefresh.BackColor = [System.Drawing.Color]::FromArgb(236, 239, 242)
$btnRefresh.ForeColor = [System.Drawing.Color]::FromArgb(70, 78, 86)
$btnRefresh.Cursor    = [System.Windows.Forms.Cursors]::Hand
$form.Controls.Add($btnRefresh)

$chkAuto              = New-Object System.Windows.Forms.CheckBox
$chkAuto.Location     = New-Object System.Drawing.Point(122, 373)
$chkAuto.Size         = New-Object System.Drawing.Size(120, 22)
$chkAuto.Text         = '每 10 秒自动刷新'
$chkAuto.ForeColor    = $clrMuted
$chkAuto.Font         = New-Object System.Drawing.Font('Microsoft YaHei', 8.5)
$chkAuto.Checked      = $true
$form.Controls.Add($chkAuto)

$lblHint              = New-Object System.Windows.Forms.Label
$lblHint.Location     = New-Object System.Drawing.Point(250, 374)
$lblHint.Size         = New-Object System.Drawing.Size(188, 22)
$lblHint.Text         = "远端 $RemoteHost · 只切系统代理，不动路由"
$lblHint.ForeColor    = [System.Drawing.Color]::FromArgb(180, 186, 192)
$lblHint.TextAlign    = 'MiddleRight'
$lblHint.Font         = New-Object System.Drawing.Font('Microsoft YaHei', 7.5)
$form.Controls.Add($lblHint)

# ===== 日志 =====
$txtLog               = New-Object System.Windows.Forms.TextBox
$txtLog.Location      = New-Object System.Drawing.Point(16, 406)
$txtLog.Size          = New-Object System.Drawing.Size(422, 108)
$txtLog.Multiline     = $true
$txtLog.ScrollBars    = 'Vertical'
$txtLog.ReadOnly      = $true
$txtLog.BackColor     = [System.Drawing.Color]::FromArgb(38, 42, 48)
$txtLog.ForeColor     = [System.Drawing.Color]::FromArgb(168, 220, 190)
$txtLog.Font          = New-Object System.Drawing.Font('Consolas', 8.5)
$txtLog.BorderStyle   = 'None'
$form.Controls.Add($txtLog)

# ---------------- 逻辑 ----------------

$script:AutoTimer = New-Object System.Windows.Forms.Timer
$script:AutoTimer.Interval = 10000
$script:AutoTimer.Add_Tick({ if ($chkAuto.Checked) { $null = Update-Status } })

# 轻量心跳：只做「端口是否就绪 / 后台探测是否完成」这类毫秒级判断，
# 让开启借网后的状态变化能在一两秒内反映到界面，而不用等 10 秒大刷新。
$script:PendingOn = $null
$script:BeatTimer = New-Object System.Windows.Forms.Timer
$script:BeatTimer.Interval = 1000
$script:BeatTimer.Add_Tick({
    if (-not $chkAuto.Checked) { return }
    $need = $false
    if ($script:PendingOn) { $need = $true }
    elseif ($script:ProbeHandle -and $script:ProbeHandle.IsCompleted) { $need = $true }
    elseif ($script:Probe.Busy -ne $true -and $script:Probe.When -eq [DateTime]::MinValue) { $need = $true }
    if ($need) { $null = Update-Status }
  })

function Add-Log {
  param([string]$msg)
  $txtLog.AppendText("$(Get-Date -Format 'HH:mm:ss')  $msg`r`n")
  Write-AppLog $msg
}

function Set-UiBusy {
  param([bool]$busy)
  $btnOn.Enabled      = -not $busy
  $btnOff.Enabled     = -not $busy
  $btnRefresh.Enabled = -not $busy
  $form.Cursor = if ($busy) { [System.Windows.Forms.Cursors]::WaitCursor } else { [System.Windows.Forms.Cursors]::Default }
  [System.Windows.Forms.Application]::DoEvents()
}

function Update-Status {
  # 先把后台探测结果收进来（已完成才收，绝不等待）
  Complete-Probe

  $s = Get-State

  # ---- 处理「已点开启、正在等端口」的待办（非阻塞）----
  if ($script:PendingOn) {
    if ($s.SocksAlive) {
      $script:PendingOn = $null
      Add-Log "SOCKS 127.0.0.1:$SocksPort 已就绪"
      if ($ProxyMode -eq 'System') {
        try {
          # 先确保 dual-proxy（HTTP 端口）活着 —— 系统代理只能填它
          $br = Start-Bridge
          if ($br -eq 'missing') {
            Add-Log '❌ 找不到 node 或 dual-proxy.mjs，无法建 HTTP 代理，已跳过系统代理切换'
          }
          elseif (-not (Test-BridgeAlive)) {
            Add-Log "❌ HTTP 代理 $HttpPort 未就绪（$br），已跳过系统代理切换（写 SOCKS 端口会让浏览器全部打不开）"
          }
          else {
            if (-not (Test-Path $StateDir)) { $null = New-Item -ItemType Directory -Path $StateDir -Force }
            $cur = Get-ProxyState
            # 备份只在「当前值不是我们自己的 HTTP 代理」时覆盖，避免把备份刷成借网地址
            $bakFile = Join-Path $StateDir 'proxy-backup.txt'
            if ($cur.Server -ne "http://127.0.0.1:$HttpPort") {
              Set-Content $bakFile -Value "$($cur.Enabled)|$($cur.Server)" -Encoding ASCII
            }
            Set-Proxy -Enable 1 -Server "http://127.0.0.1:$HttpPort"
            Add-Log "系统代理已切换 → http://127.0.0.1:$HttpPort（HTTP 代理）"
          }
        } catch { Add-Log "切换系统代理失败: $($_.Exception.Message)" }
      }
      $s = Get-State
      Start-Probe -NeedBorrow $true
    }
    elseif ((Get-Date) -gt $script:PendingOn) {
      $script:PendingOn = $null
      Add-Log '隧道 20 秒内未就绪，请检查 SSH 通道（可先命令行试 ssh pcs hostname）'
    }
  }

  # 结果过期就补一次后台探测：不阻塞，界面立刻用旧值渲染
  $stale = ($s.ProbeAge -lt 0) -or ($s.ProbeAge -gt 20)
  if ($stale -and -not $script:Probe.Busy) { Start-Probe -NeedBorrow $s.Borrowing }

  # ---- 顶部大状态：现在用什么网 + 有没有网 ----
  if ($s.Borrowing) {
    $lblBig.Text      = '借网中'
    $lblBig.ForeColor = $clrOk
    $lblPath.Text     = "本机 → $RemoteHost（办公网）→ 互联网"
  }
  elseif ($s.HasNet) {
    $lblBig.Text      = '直连'
    $lblBig.ForeColor = $clrIdle
    $lblPath.Text     = '本机 → 学校网络 → 互联网'
  }
  else {
    $lblBig.Text      = '无网络'
    $lblBig.ForeColor = $clrBad
    $lblPath.Text     = '两个出口都不通 —— 点「开启借网」试试'
  }

  if ($s.CurrentIp) {
    $lblIpNow.Text      = "当前出口 $($s.CurrentIp)"
    $lblIpNow.ForeColor = if ($s.Borrowing) { $clrOk } else { $clrText }
  }
  else {
    $lblIpNow.Text      = '当前无出口'
    $lblIpNow.ForeColor = $clrBad
  }

  # ---- 出口对照 ----
  $lblDirectV.Text      = if ($s.DirectIp) { $s.DirectIp } else { '不可用' }
  $lblDirectV.ForeColor = if ($s.DirectIp) { $clrText } else { $clrBad }
  $lblBorrowV.Text      = if ($s.BorrowIp) { $s.BorrowIp } else { '未启用' }
  $lblBorrowV.ForeColor = if ($s.BorrowIp) { $clrOk } else { $clrMuted }

  # ---- 链路明细 ----
  $ageTxt = if ($s.ProbeAge -lt 0) { '探测中…' }
            elseif ($script:Probe.Busy) { "刷新中（上次 $($s.ProbeAge)s 前）" }
            else { "$($s.ProbeAge)s 前" }
  $lblDetail.Text = @(
    "隧道 SOCKS : $(if ($s.SocksAlive) { '存活' } else { '未监听' })      保活进程 : $(if ($s.Keeper) { '运行中' } else { '未运行' })",
    "HTTP 代理  : $(if ($s.BridgeAlive) { "存活 127.0.0.1:$HttpPort" } else { "未监听 127.0.0.1:$HttpPort" })",
    "系统代理   : $(if ($s.ProxyOn) { '开' } else { '关' })  $($s.ProxySrv)",
    "网络可用   : $(if ($s.HasNet) { '是' } else { '否' })      出口探测 : $ageTxt"
  ) -join "`n"

  if ($s.StaleProxy) {
    $lblDetail.Text += "`n⚠ 系统代理是旧版 SOCKS 写法，浏览器会打不开 —— 点「开启借网」自动修复"
  }

  $btnOn.Enabled  = -not $s.Borrowing
  $btnOff.Enabled = $s.Borrowing
  return $s
}

$btnOn.Add_Click({
  # 不阻塞：启动 keeper 后立刻返回，端口就绪与否由状态轮询接管。
  # 原来在这里 Wait-Port 15s，界面会僵住十几秒 —— 这正是"点了没反应"的元凶。
  Set-UiBusy $true
  try {
    $s = Get-State
    if ($s.Borrowing) {
      Add-Log '借网已在运行'
    }
    else {
      # 历史遗留：系统代理被旧版写成 SOCKS 端口，先清掉避免被当成正常状态
      if ($s.StaleProxy) {
        Add-Log "⚠ 检测到系统代理是旧版 SOCKS 写法（$($s.ProxySrv)），浏览器会全部失败，正在修复"
      }

      Add-Log '启动保活隧道…'
      $r = Start-Keeper
      Add-Log "keeper: $r"

      # 立即尝试拉起 HTTP 代理（不依赖 PendingOn，隧道没起来也能先建 bridge）
      $br = Start-Bridge
      Add-Log "HTTP 代理(dual-proxy): $br"

      $script:PendingOn = (Get-Date).AddSeconds(20)   # 20s 内自动检测端口就绪
      Add-Log '等待 SOCKS 就绪（后台检测，界面可继续操作）'
    }
  }
  catch { Add-Log "错误: $($_.Exception.Message)" }
  finally { Set-UiBusy $false; $null = Update-Status }
})

$btnOff.Add_Click({
  Set-UiBusy $true
  try {
    Add-Log '还原系统代理…'
    $bakFile = Join-Path $StateDir 'proxy-backup.txt'
    if (Test-Path $bakFile) {
      $bak = (Get-Content $bakFile -Raw).Trim()
      $en, $srv = $bak -split '\|', 2
      $enVal = if ($en -match '^(1|true)$') { 1 } else { 0 }
      # 备份里如果存的也是 SOCKS 端口写法（历史污染），不能还原它 —— 直接关代理
      if ($srv -eq "127.0.0.1:$SocksPort") {
        Add-Log "备份里是 SOCKS 写法（$srv），不还原，直接关闭系统代理"
        Set-Proxy -Enable 0 -Server ''
      }
      else {
        Set-Proxy -Enable $enVal -Server $srv
        Add-Log "已还原为 $srv"
      }
    }
    else {
      Set-Proxy -Enable 0 -Server ''
      Add-Log '无备份，已关闭系统代理'
    }
    Add-Log '停止隧道…'
    Stop-Keeper
    Add-Log '已切回直连（HTTP 代理进程保留，下次开启直接复用）'
  }
  catch { Add-Log "错误: $($_.Exception.Message)" }
  finally { Set-UiBusy $false; $null = Update-Status }
})

$btnRefresh.Add_Click({
    $null = Update-Status
    Start-Probe -NeedBorrow (Get-State).Borrowing
    Add-Log '已刷新'
  })

# 启动
$script:AutoTimer.Start()
$script:BeatTimer.Start()
Start-Probe -NeedBorrow $false
$null = Update-Status
Add-Log '界面已就绪（托盘图标常驻，关闭窗口不会退出程序）'
Add-Log '退出请用托盘右键菜单 → 退出'
Write-AppLog "进入消息循环 Application::Run()"

try {
  # 必须用 Application::Run 而不是 ShowDialog：
  # ShowDialog 是模态循环，窗口一旦被最小化，模态循环立刻返回、进程随之退出
  # （实测日志：最小化 → FormClosing reason=None → FormClosed → 消息循环结束）。
  [System.Windows.Forms.Application]::Run($form)
}
catch {
  Write-AppLog ("消息循环异常: " + $_.Exception.Message) 'FATAL'
  Write-AppLog ($_.ScriptStackTrace) 'FATAL'
  throw
}
Write-AppLog "消息循环结束，正常退出"
