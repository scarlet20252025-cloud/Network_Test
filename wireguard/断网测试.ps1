# 断网测试：删掉默认路由，验证「经渡口还能不能上网」
#
# 这是 docs/断网验证方法.md 里那套做法的自动化版本：
#   学校断网 = 本机拿不到公网出口，但局域网仍然互通
#   所以「删默认路由、保留 10.0.0.0/8」就能精确模拟
#
# 安全措施（重要）：
#   1. 只在 try/finally 里改路由，异常也会走恢复
#   2. 额外挂一个 3 分钟后的计划任务做兜底 —— 万一脚本被杀掉、
#      机器断电重启，那条任务会把默认路由加回来
#   3. 测试前先记下原始网关和接口索引，恢复时按原样写回
#
# 用法：右键「以管理员身份运行」断网测试.cmd

$ErrorActionPreference = 'Stop'
$taskName = 'FerryRouteRestore'

function Show($msg) { Write-Host $msg }

# ── 0. 必须管理员 ──
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Show '需要管理员权限'; exit 1
}

# ── 1. 记下当前默认路由 ──
$def = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
       Sort-Object RouteMetric, InterfaceMetric | Select-Object -First 1
if (-not $def) { Show '当前没有默认路由，已经处于断网状态？'; exit 1 }

$gw     = $def.NextHop
$ifIdx  = $def.ifIndex
$alias  = (Get-NetAdapter -InterfaceIndex $ifIdx -ErrorAction SilentlyContinue).Name

Show ''
Show '=== 当前网络 ==='
Show ("  默认网关 : $gw")
Show ("  出口网卡 : $alias (ifIndex=$ifIdx)")

# ── 2. 兜底任务：3 分钟后无条件恢复 ──
$restoreCmd = "New-NetRoute -DestinationPrefix '0.0.0.0/0' -NextHop '$gw' -InterfaceIndex $ifIdx -ErrorAction SilentlyContinue"
$at = (Get-Date).AddMinutes(3).ToString('HH:mm')
& schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
& schtasks.exe /Create /TN $taskName /TR "powershell -NoProfile -WindowStyle Hidden -Command `"$restoreCmd`"" `
    /SC ONCE /ST $at /RU SYSTEM /RL HIGHEST /F 2>&1 | Out-Null
Show ("  兜底任务已挂：$at 自动恢复（万一我这边出事）")

# ── 3. 动手：删默认路由 ──
try {
    Show ''
    Show '=== 删除默认路由（模拟断网）==='
    Remove-NetRoute -DestinationPrefix '0.0.0.0/0' -Confirm:$false -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Show '  已删除。现在公网应该完全不通。'

    Show ''
    Show '=== A. 直连测试（应该失败）==='
    for ($i = 1; $i -le 3; $i++) {
        $code = & curl.exe -s -o nul -w '%{http_code}' --max-time 6 --noproxy '*' 'http://www.baidu.com' 2>$null
        Show ("  第 $i 次: HTTP $code" + $(if ($code -eq '000') { '  ← 断网确认' } else { '' }))
    }

    Show ''
    Show '=== B. 经渡口 SOCKS5（WG 线上）==='
    Show '  前提：WireGuard 隧道已连上（先跑 启用WG线路.cmd）'
    $wg = Get-NetAdapter -Name 'ferry-wg' -ErrorAction SilentlyContinue
    if ($wg -and $wg.Status -eq 'Up') {
        for ($i = 1; $i -le 3; $i++) {
            $r = & curl.exe -s -o nul -w '%{http_code} %{time_total}s %{size_download}B' --max-time 10 `
                 --socks5-hostname 10.66.67.1:1080 'http://www.baidu.com' 2>$null
            Show ("  第 $i 次: $r")
        }
        Show ''
        Show '=== C. 经渡口 SOCKS5（HTTPS）==='
        $r = & curl.exe -s -o nul -w '%{http_code} %{time_total}s' --max-time 10 `
             --socks5-hostname 10.66.67.1:1080 'https://www.baidu.com' 2>$null
        Show ("  $r")
    } else {
        Show '  ferry-wg 没连上，跳过。先跑 启用WG线路.cmd。'
    }

    Show ''
    Show '=== D. 经 SSH 线路（对照组，SSH 隧道要开着）==='
    $sshPort = 10802
    $sshOk = Test-NetConnection -ComputerName 127.0.0.1 -Port $sshPort -InformationLevel Quiet -WarningAction SilentlyContinue
    if ($sshOk) {
        $r = & curl.exe -s -o nul -w '%{http_code} %{time_total}s' --max-time 10 `
             --proxy "http://127.0.0.1:$sshPort" 'http://www.baidu.com' 2>$null
        Show ("  经 127.0.0.1:$sshPort : $r")
    } else {
        Show "  127.0.0.1:$sshPort 没开，跳过"
    }
}
finally {
    # ── 4. 恢复（无论上面发生什么）──
    Show ''
    Show '=== 恢复默认路由 ==='
    $now = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue
    if (-not $now) {
        New-NetRoute -DestinationPrefix '0.0.0.0/0' -NextHop $gw -InterfaceIndex $ifIdx -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    $chk = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue
    if ($chk) {
        Show ("  已恢复: $($chk[0].NextHop) -> ifIndex $($chk[0].ifIndex)")
    } else {
        Show '  ⚠ 恢复失败！手动执行：'
        Show ("     New-NetRoute -DestinationPrefix '0.0.0.0/0' -NextHop $gw -InterfaceIndex $ifIdx")
    }
    & schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
    Show '  兜底任务已清理'
    Show ''
    # 确认网络回来了
    $code = & curl.exe -s -o nul -w '%{http_code}' --max-time 8 --noproxy '*' 'http://www.baidu.com' 2>$null
    Show ("  直连自检: HTTP $code" + $(if ($code -eq '200') { '  ← 网络已恢复' } else { '  ← 可能还没恢复' }))
}
