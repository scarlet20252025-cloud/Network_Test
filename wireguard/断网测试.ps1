# 断网测试：删掉默认路由，验证「经渡口还能不能上网」
#
# 学校断网 = 本机拿不到公网出口，但局域网仍然互通
# 所以「删默认路由」能模拟，但**有一个前提要先补上**：
#
#   本机 10.141.59.235 属于 10.141.56.0/22，
#   而 xt(10.113.66.6) 与 pcs(10.13.24.1) 都**不在**这个网段 ——
#   它们此刻是靠「默认网关」转发才通的。
#
#   直接删默认路由 → 连 xt 也断 → WG / SSH / SOCKS5 全废 → 什么都测不了。
#
#   正确做法：删默认路由之前，先给远端补 /32 明细路由，
#   让「公网断、远端仍可达」这个状态成立。这才是真实的断网场景。
#
# 保命：全程 try/finally，并把恢复命令打印出来 —— 脚本若被杀，
#       照着那行手打也能把网络救回来。

$ErrorActionPreference = 'Continue'
$peers = @('10.113.66.6', '10.13.24.1')   # 需要保住的远端

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
if (-not $def) { Show '当前没有默认路由，已经是断网状态？'; exit 1 }

$gw    = $def.NextHop
$ifIdx = $def.ifIndex
$alias = (Get-NetAdapter -InterfaceIndex $ifIdx -ErrorAction SilentlyContinue).Name

Show ''
Show '=== 当前网络 ==='
Show ("  默认网关 : $gw")
Show ("  出口网卡 : $alias (ifIndex=$ifIdx)")
Show ("  要保住的远端 : " + ($peers -join ', '))

$restoreCmd = "New-NetRoute -DestinationPrefix '0.0.0.0/0' -NextHop '$gw' -InterfaceIndex $ifIdx"
Show ''
Show '=== 万一网络回不来，手动执行这一行 ==='
Show ("  $restoreCmd")
Show ''

$added = @()
try {
    # ── 2. 先给远端补 /32 明细路由（走原网关）──
    Show '=== 给远端补 /32 明细路由 ==='
    foreach ($p in $peers) {
        $exist = Get-NetRoute -DestinationPrefix "$p/32" -ErrorAction SilentlyContinue
        if (-not $exist) {
            New-NetRoute -DestinationPrefix "$p/32" -NextHop $gw -InterfaceIndex $ifIdx -ErrorAction SilentlyContinue | Out-Null
            $added += $p
            Show ("  + $p/32 via $gw")
        } else {
            Show ("  = $p/32 已存在，跳过")
        }
    }

    # ── 3. 删默认路由（这一刻起公网断）──
    Show ''
    Show '=== 删除默认路由（模拟断网）==='
    Remove-NetRoute -DestinationPrefix '0.0.0.0/0' -Confirm:$false -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    $still = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue
    if ($still) { Show ('  ! 还有 ' + @($still).Count + ' 条默认路由（可能是 Radmin VPN 那条）') }
    else { Show '  已删除，公网应该不通了' }

    Show ''
    Show '=== 确认远端还可达 ==='
    foreach ($p in $peers) {
        $ok = Test-Connection -ComputerName $p -Count 1 -Quiet -ErrorAction SilentlyContinue
        Show ("  ping $p : " + $(if ($ok) { '通' } else { '不通（这条线路没法测了）' }))
    }

    # ── 4. 测试 ──
    Show ''
    Show '=== A. 直连（应该全失败）==='
    for ($i = 1; $i -le 3; $i++) {
        $code = & curl.exe -s -o nul -w '%{http_code}' --max-time 6 --noproxy '*' 'http://www.baidu.com' 2>$null
        if (-not $code) { $code = '000' }
        Show ("  第 $i 次: HTTP $code" + $(if ($code -eq '000') { '  <- 断网确认' } else { '  <- 还有网？' }))
    }

    Show ''
    Show '=== B. 经渡口 SOCKS5（WG 线）==='
    $wg = Get-NetAdapter -Name 'ferry-wg' -ErrorAction SilentlyContinue
    if ($wg -and $wg.Status -eq 'Up') {
        for ($i = 1; $i -le 3; $i++) {
            $r = & curl.exe -s -o nul -w '%{http_code} %{time_total}s %{size_download}B' --max-time 10 `
                 --socks5-hostname 10.66.67.1:1080 'http://www.baidu.com' 2>$null
            if (-not $r) { $r = '000 失败' }
            Show ("  第 $i 次: $r")
        }
        Show ''
        Show '=== C. 经渡口 SOCKS5（HTTPS）==='
        $r = & curl.exe -s -o nul -w '%{http_code} %{time_total}s' --max-time 10 `
             --socks5-hostname 10.66.67.1:1080 'https://www.baidu.com' 2>$null
        if (-not $r) { $r = '000 失败' }
        Show ("  $r")
    } else {
        Show '  ferry-wg 没连上，跳过（先跑 启用WG线路.cmd）'
    }

    Show ''
    Show '=== D. 经 SSH 线路（对照）==='
    foreach ($port in @(10802, 10801)) {
        $ok = Test-NetConnection -ComputerName 127.0.0.1 -Port $port -InformationLevel Quiet -WarningAction SilentlyContinue
        if ($ok) {
            $r = & curl.exe -s -o nul -w '%{http_code} %{time_total}s' --max-time 10 `
                 --proxy "http://127.0.0.1:$port" 'http://www.baidu.com' 2>$null
            Show ("  经 127.0.0.1:$port : $(if ($r) { $r } else { '000 失败' })")
        } else {
            Show ("  127.0.0.1:$port 没开，跳过")
        }
    }
}
finally {
    # ── 5. 无论发生什么都恢复 ──
    Show ''
    Show '=== 恢复默认路由 ==='
    $now = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue
    if (-not $now) {
        New-NetRoute -DestinationPrefix '0.0.0.0/0' -NextHop $gw -InterfaceIndex $ifIdx -ErrorAction SilentlyContinue | Out-Null
        Start-Sleep -Seconds 2
    }
    $chk = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue
    if ($chk) { Show ("  已恢复: $($chk[0].NextHop) -> ifIndex $($chk[0].ifIndex)") }
    else {
        Show '  自动恢复失败！手动执行：'
        Show ("     $restoreCmd")
    }

    foreach ($p in $added) {
        Remove-NetRoute -DestinationPrefix "$p/32" -Confirm:$false -ErrorAction SilentlyContinue
        Show ("  - 清理明细路由 $p/32")
    }

    Show ''
    $code = & curl.exe -s -o nul -w '%{http_code}' --max-time 8 --noproxy '*' 'http://www.baidu.com' 2>$null
    if (-not $code) { $code = '000' }
    Show ("  直连自检: HTTP $code" + $(if ($code -eq '200') { '  <- 网络已恢复' } else { '  <- 还没恢复，检查网关' }))
    Show ''
}
