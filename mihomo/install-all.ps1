# 渡口 · 一键部署（在本机跑，一条命令搞定全部）
#
# 用户的目标是「便捷好用，一键安装部署」，所以这个脚本要做完所有这些事，
# 不让用户记任何命令：
#   1. 检查环境（ssh / mihomo / 远端可达性）
#   2. 把 UDP 中继部署到远端（自动 scp + 远端自启）
#   3. 配置本机 mihomo 统一入口
#   4. 装单一自启项（清掉会抢端口的旧项）
#   5. 端到端验证（TCP 出口 + UDP 数据报）
#   6. 建桌面快捷方式
#
# 用法（普通用户权限，不需要管理员）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File install-all.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File install-all.ps1 -Remote pcs -UdpPort 1080
#   powershell -NoProfile -ExecutionPolicy Bypass -File install-all.ps1 -Uninstall
#
# 设计原则：**每一步失败都明确说原因和下一步**，不静默跳过。
# 之前踩过的坑是「脚本说成功、实际没生效」，那比报错更糟。

param(
  [string]$Remote = 'pcs',
  [int]$UdpPort = 1080,
  [switch]$Uninstall,
  [switch]$SkipRemote,
  [switch]$SkipVerify
)

# 不用 'Stop'。
#
# 为什么：这个脚本大量调用外部程序（ssh / scp / curl / python），
# 而 PS 5.1 在 $ErrorActionPreference='Stop' 下会把**外部程序的 stderr**
# 也当成终止性错误抛出（NativeCommandError）。ssh 连上时总会往 stderr 写
# 那几行 post-quantum 警告，于是脚本在第 2 步就整个崩掉 ——
# 实测报错还是乱码的「所在位置 行:1 字符: 83」，完全看不出真因。
# 改成 Continue，并在需要的地方显式检查 $LASTEXITCODE。
$ErrorActionPreference = 'Continue'

$Here = $PSScriptRoot
$Root = Split-Path -Parent $Here
$Keeper = Join-Path $Here 'mihomo-keeper.ps1'
$ServerPy = Join-Path $Root 'wireguard\ferry-socks5-server.py'
$DeployPs = Join-Path $Root 'wireguard\deploy-udp-relay.ps1'

$script:Failures = @()

function Step { param($n, $m) Write-Host ''; Write-Host "── $n. $m" -ForegroundColor Cyan }
function Say  { param($m) Write-Host "   $m" }
function Ok   { param($m) Write-Host "   [OK] $m" -ForegroundColor Green }
function Warn { param($m) Write-Host "   [!] $m" -ForegroundColor Yellow }
function Bad  { param($m) Write-Host "   [X] $m" -ForegroundColor Red; $script:Failures += $m }
function Hint { param($m) Write-Host "       $m" -ForegroundColor DarkGray }

function Test-PortOpen {
  param([int]$Port, [string]$HostName = '127.0.0.1', [int]$TimeoutMs = 600)
  try {
    $c = New-Object Net.Sockets.TcpClient
    $iar = $c.BeginConnect($HostName, $Port, $null, $null)
    $ok = $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)
    $c.Close()
    return [bool]$ok
  } catch { return $false }
}

# 跑远端命令，返回 stdout。
#
# `2>$null` 不是可选的美化：PS 5.1 会把外部程序的 stderr 当成错误流写进
# 管道，ssh 每次连上都会往 stderr 打 post-quantum 警告，混在输出里既干扰
# 文本判断，也会让 $ErrorActionPreference='Stop' 直接抛 NativeCommandError。
# 把 stderr 丢掉，只看 stdout + $LASTEXITCODE。
function Invoke-Remote {
  param([string]$Command, [int]$TimeoutSec = 120)
  $out = & ssh -o ConnectTimeout=8 -o BatchMode=yes -o LogLevel=ERROR $Remote $Command 2>$null
  $code = $LASTEXITCODE
  $text = ($out | Where-Object { $_ }) -join "`n"
  return @{ Text = $text.Trim(); Code = $code }
}

# ══════════════════════════ 主流程 ══════════════════════════

Write-Host ''
Write-Host '════════════════════════════════════════════' -ForegroundColor Cyan
Write-Host '  渡口 Ferry · 一键部署' -ForegroundColor Cyan
Write-Host '════════════════════════════════════════════' -ForegroundColor Cyan

if ($Uninstall) {
  Step 1 '卸载'
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Keeper -Action Stop
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Keeper -Action Uninstall
  if (-not $SkipRemote) {
    $r = Invoke-Remote "& powershell -NoProfile -ExecutionPolicy Bypass -File `"`$env:LOCALAPPDATA\Ferry\deploy-udp-relay.ps1`" -Uninstall"
    if ($r.Code -eq 0) { Ok '远端中继已卸载' } else { Warn "远端卸载返回 $($r.Code)" }
  }
  Ok '本机已清理（客户端程序本身保留，需要时删整个 Ferry 目录）'
  Write-Host ''
  exit 0
}

# ── 1. 环境检查 ──
Step 1 '检查本机环境'

$sshExe = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
if (Test-Path $sshExe) { Ok "ssh: $sshExe" }
else { Bad '找不到 Windows 自带 ssh.exe'; Hint '设置 → 应用 → 可选功能 → 添加 OpenSSH 客户端' }

$miExe = 'D:\Clash Verge\verge-mihomo.exe'
if (Test-Path $miExe) { Ok "mihomo: $miExe" }
else {
  Bad "找不到 mihomo: $miExe"
  Hint '这个文件来自 Clash Verge。装了 Clash Verge 但路径不同的话，'
  Hint "改 mihomo\mihomo-keeper.ps1 里的 `$MiExe 变量。"
}

foreach ($f in @($Keeper, (Join-Path $Here 'config.template.yaml'))) {
  if (Test-Path $f) { Ok "存在: $(Split-Path -Leaf $f)" } else { Bad "缺失: $f" }
}

if ($script:Failures.Count) {
  Write-Host ''
  Bad '环境检查未通过，中止。先解决上面标 [X] 的项。'
  exit 1
}

# ── 2. 远端可达性 ──
Step 2 "检查远端 $Remote"
$r = Invoke-Remote 'echo FERRY-OK' 30
if ($r.Text -match 'FERRY-OK') {
  Ok "ssh $Remote 可用"
} else {
  Bad "ssh $Remote 不通"
  Hint '先确认能手工连：ssh ' + $Remote
  Hint '常见原因：密钥没配、IP 变了、远端 sshd 没开'
  if (-not $SkipRemote) { exit 1 }
}

# 解析远端 IP（用于后面的端口连通性判定）。
#
# 用 `ssh -G <别名>` 而不是在远端跑 Get-NetIPAddress —— 实测后者返回
# 192.168.56.1（VirtualBox 的 Host-Only 网卡），不是真正的可达地址，
# 拿它去连端口必然失败，会误判成「部署失败」。而 ssh -G 直接吐出
# ~/.ssh/config 里解析后的 HostName，正是我们实际连的那个地址。
$script:RemoteIp = ''
$g = & ssh -G $Remote 2>$null
$hn = (($g | Where-Object { $_ -match '^hostname\s+' }) -replace '^hostname\s+', '')
$hn = "$hn".Trim()
if ($hn -match '^\d+\.\d+\.\d+\.\d+$') {
  $script:RemoteIp = $hn
  Ok "远端地址: $($script:RemoteIp)"
} elseif ($Remote -match '^\d+\.\d+\.\d+\.\d+$') {
  $script:RemoteIp = $Remote
  Ok "远端地址: $($script:RemoteIp)"
} else {
  Warn "解析不出远端 IP（ssh -G 返回 '$hn'），改用别名做判定"
}

# ── 3. 部署远端 UDP 中继 ──
if (-not $SkipRemote) {
  Step 3 '部署远端 UDP 中继'

  foreach ($f in @($ServerPy, $DeployPs)) {
    if (-not (Test-Path $f)) { Bad "缺少源文件: $f"; exit 1 }
  }

  $remoteDir = '$env:LOCALAPPDATA\Ferry'
  # 远端目录用远端的 $env 展开，这里传字面量
  $null = Invoke-Remote "New-Item -ItemType Directory -Path `"$remoteDir`" -Force | Out-Null; 'ready'" 60

  # scp 用绝对路径，避免远端 $env 被本地展开（踩过这个坑：拷到了本机目录）
  $remoteBase = '/C:/Users/PCserver/AppData/Local/Ferry'
  foreach ($pair in @(@($ServerPy, 'ferry-socks5-server.py'), @($DeployPs, 'deploy-udp-relay.ps1'))) {
    $src = $pair[0]; $name = $pair[1]
    & scp -o ConnectTimeout=8 -o BatchMode=yes -o LogLevel=ERROR $src "$Remote`:$remoteBase/$name" 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { Ok "已上传 $name" }
    else { Bad "上传 $name 失败"; Hint '检查远端目录权限，或手工 scp 一次' }
  }

  $r = Invoke-Remote "& powershell -NoProfile -ExecutionPolicy Bypass -File `"$remoteDir\deploy-udp-relay.ps1`" -Port $UdpPort" 180

  # 判定用**端口是否真的能连**，不用中文文本匹配。
  # 踩过的坑：远端 PS 5.1 输出是 GBK，经 ssh 管道传回来变成乱码，
  # `-match '部署完成'` 永远不成立 —— 明明部署成功却报失败，
  # 然后用户会以为整件事没成。可靠判据只有「端口能不能连」。
  $remoteIp = $script:RemoteIp
  Start-Sleep -Seconds 1
  $remoteUp = $false
  if ($remoteIp) { $remoteUp = Test-PortOpen -Port $UdpPort -HostName $remoteIp -TimeoutMs 2500 }
  if ($remoteUp) {
    Ok "远端中继已就绪（$remoteIp`:$UdpPort 可连）"
    ($r.Text -split "`n") | Where-Object { $_ -match 'PID=\d+' } |
      Select-Object -First 2 | ForEach-Object { Say $_.Trim() }
  } else {
    Bad "远端中继端口 $UdpPort 连不上"
    Hint '看远端日志: %LOCALAPPDATA%\Ferry\logs\udp-relay.log'
    ($r.Text -split "`n") | Select-Object -Last 10 | ForEach-Object { Hint $_.Trim() }
  }
}

# ── 4. 本机 mihomo ──
Step 4 '配置本机统一入口'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Keeper -Action Stop 2>&1 | Out-Null
Start-Sleep -Milliseconds 500
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Keeper -Action Install 2>&1 | Out-String -Width 200 |
  ForEach-Object { $_.Trim() } | Where-Object { $_ } | ForEach-Object { Say $_ }

# 用 Watch 拉起整条链（SSH + mihomo），等它完成首轮体检
$startupCmd = Join-Path ([Environment]::GetFolderPath('Startup')) 'FerryMihomo.cmd'
if (Test-Path $startupCmd) {
  Start-Process -FilePath $startupCmd -WindowStyle Hidden
  Say '正在拉起（SSH 隧道 + mihomo，约 10 秒）...'
  $ready = $false
  for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    if (Test-PortOpen 10810) { $ready = $true; break }
  }
  if ($ready) { Ok '端点 127.0.0.1:10810 已就绪' }
  else {
    Bad '端点 10 秒内没起来'
    Hint '看日志: ' + (Join-Path $Here 'mihomo-keeper.log')
  }
} else {
  Bad '自启文件没写成功'
}

# ── 5. 端到端验证 ──
if (-not $SkipVerify -and (Test-PortOpen 10810)) {
  Step 5 '端到端验证'

  # 先确保处于借网状态，再等 fallback 组完成首轮健康检查。
  # 为什么必须等：mihomo 刚起来时 fallback 组的 url 还没探测完，
  # 此时请求会落到组里的第一项（或 DIRECT），出口 IP 看起来像「没借网」。
  # 实测：装完立刻测得到 116.23.159.136（电信，=直连），
  # 等 30 秒后再测才是 218.107.0.47（联通）。这是探测延迟，不是配置错。
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Keeper -Action On 2>&1 | Out-Null
  Say '等待线路健康检查完成（最多 35 秒）...'
  for ($i = 0; $i -lt 35; $i++) {
    Start-Sleep -Seconds 1
    $g = try {
      (Invoke-RestMethod -Uri 'http://127.0.0.1:19010/proxies/ferry-auto' -TimeoutSec 3).now
    } catch { $null }
    if ($g -and $g -ne 'ferry-auto') { break }
  }

  # TCP
  $tmp = Join-Path $env:TEMP 'ferry-eip.txt'
  $null = & curl.exe -s --max-time 20 -x 'http://127.0.0.1:10810' -o $tmp 'https://myip.ipip.net' 2>$null
  if (Test-Path $tmp) {
    $txt = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($tmp))
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    if ($txt -match 'IP[：:]\s*(\d+\.\d+\.\d+\.\d+)') {
      $ip = $Matches[1]
      Ok "TCP 出口 IP: $ip"
      # 顺手给出「换没换出口」的判断，这是用户最关心的
      $direct = '116.23.159.136'
      if ($ip -eq $direct) { Warn "出口与直连相同（$direct），可能线路没切过去；用 Status 复查" }
      else { Ok "出口已切换（直连是 $direct）" }
    } else { Warn 'TCP 请求有响应但解析不出 IP' }
  } else { Bad 'TCP 验证失败（请求没返回）' }

  # UDP
  $vpy = Join-Path $Here 'verify-udp-path.py'
  if (Test-Path $vpy) {
    $u = & python -u $vpy --count 3 2>&1
    $utext = $u -join "`n"
    # 同样不靠中文匹配：python 输出走管道也是 GBK/UTF-8 混乱。
    # 用「通/超时」这种稳定出现的 ASCII 或数字特征，外加平均延迟行。
    if ($utext -match '\[OK\].*UDP' -or $utext -match '平均\s*\d') { Ok 'UDP 中继可用（游戏/QUIC 具备基础）' }
    elseif ($utext -match '超时' -and $utext -notmatch '\d+/\d+ 通') {
      Bad 'UDP 不通'
      Hint "直连测远端: python socks5-udp-test.py --proxy $($script:RemoteIp):$UdpPort"
    } else { Warn 'UDP 验证输出异常（可能只是编码问题，可手工跑 verify-udp-path.py 确认）' }
  } else { Warn '缺少 verify-udp-path.py，跳过 UDP 验证' }
}

# ── 6. 桌面快捷方式 ──
Step 6 '创建桌面快捷方式'
$desktop = [Environment]::GetFolderPath('Desktop')
$lnk = Join-Path $desktop '渡口借网.lnk'
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($lnk)
$sc.TargetPath = 'powershell.exe'
$sc.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$Keeper`" -Action On"
$sc.WorkingDirectory = $Here
$sc.IconLocation = "$env:SystemRoot\System32\shell32.dll,13"
$sc.Description = '一键开启借网（TCP + UDP）'
$sc.Save()
Ok "已创建: $lnk"

$lnkOff = Join-Path $desktop '渡口恢复直连.lnk'
$sc2 = $ws.CreateShortcut($lnkOff)
$sc2.TargetPath = 'powershell.exe'
$sc2.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$Keeper`" -Action Off"
$sc2.WorkingDirectory = $Here
$sc2.IconLocation = "$env:SystemRoot\System32\shell32.dll,27"
$sc2.Description = '恢复直连'
$sc2.Save()
Ok "已创建: $lnkOff"

# ── 总结 ──
Write-Host ''
Write-Host '════════════════════════════════════════════' -ForegroundColor Cyan
if ($script:Failures.Count) {
  Write-Host "  完成，但有 $($script:Failures.Count) 项需要注意：" -ForegroundColor Yellow
  $script:Failures | ForEach-Object { Write-Host "    · $_" -ForegroundColor Yellow }
} else {
  Write-Host '  全部完成' -ForegroundColor Green
}
Write-Host '════════════════════════════════════════════' -ForegroundColor Cyan
Write-Host ''
Write-Host '  日常使用：' -ForegroundColor White
Write-Host '    双击桌面「渡口借网」    → 开启借网（TCP + UDP 自动分流）'
Write-Host '    双击桌面「渡口恢复直连」 → 关闭借网'
Write-Host ''
Write-Host '  需要代理的程序填：127.0.0.1:10810（HTTP 和 SOCKS5 同一个端口）'
Write-Host '  系统代理不会被改动，你原来的 Clash 照常工作。'
Write-Host ''
Write-Host '  命令行（可选）：' -ForegroundColor DarkGray
Write-Host "    .\mihomo-keeper.ps1 Status   # 看状态、三态出口、看护" -ForegroundColor DarkGray
Write-Host "    .\mihomo-keeper.ps1 On/Off   # 开关借网" -ForegroundColor DarkGray
Write-Host ''
