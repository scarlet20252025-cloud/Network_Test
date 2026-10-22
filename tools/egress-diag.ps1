#requires -Version 5.1
<#
  出口探测诊断：逐个源、逐种方式打印耗时与错误，用来定位 GUI 里「借网出口未启用」的真因。
  用法：先把隧道开起来（borrow-net.ps1 -Action On），再跑本脚本。
#>
$SocksPort = 10808
$urls = @('http://myip.ipip.net', 'http://members.3322.org/dyndns/getip', 'http://ip.3322.net')

function Probe {
  param([string]$Url, [string]$Proxy, [int]$TimeoutSec = 8)
  $sw = [Diagnostics.Stopwatch]::StartNew()
  try {
    $p = @{ Uri = $Url; TimeoutSec = $TimeoutSec; UseBasicParsing = $true }
    if ($Proxy) { $p['Proxy'] = $Proxy }
    $r = Invoke-WebRequest @p -ErrorAction Stop
    $m = [regex]::Match($r.Content, '\d+\.\d+\.\d+\.\d+')
    return @{ Ok = $true; Ms = $sw.ElapsedMilliseconds; Ip = $m.Value; Err = '' }
  }
  catch {
    return @{ Ok = $false; Ms = $sw.ElapsedMilliseconds; Ip = ''; Err = $_.Exception.Message }
  }
}

"=== PSVersion $($PSVersionTable.PSVersion)  进程 $PID ==="
"=== 环境代理变量: HTTP_PROXY=$env:HTTP_PROXY HTTPS_PROXY=$env:HTTPS_PROXY ==="
""
foreach ($u in $urls) {
  $a = Probe $u ''
  $b = Probe $u 'socks5://127.0.0.1:10808'
  $c = Probe $u $null
  "{0,-42}`n   默认(读系统代理) : {1,6}ms  {2}`n   socks5 隧道        : {3,6}ms  {4}`n   -Proxy `$null       : {5,6}ms  {6}" -f `
    $u, $a.Ms, $(if ($a.Ok) { "OK $($a.Ip)" } else { "FAIL $($a.Err)" }), `
    $b.Ms, $(if ($b.Ok) { "OK $($b.Ip)" } else { "FAIL $($b.Err)" }), `
    $c.Ms, $(if ($c.Ok) { "OK $($c.Ip)" } else { "FAIL $($c.Err)" })
  ""
}
"=== curl 对照 ==="
foreach ($u in $urls) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $o = & curl.exe -s --max-time 8 --socks5-hostname 127.0.0.1:10808 $u 2>&1
  "  curl socks5 {0,-38} {1,6}ms -> {2}" -f $u, $sw.ElapsedMilliseconds, ("$o".Trim())
}
