#requires -Version 5.1
<#
  Get-BorrowPorts.ps1 — 统一的端口解析（供 borrow-net.ps1 / 借网助手.ps1 / startup.ps1 复用）

  为什么单独一个文件：
    端口从「写死 10808/10809」改成「dual-proxy 自动探测」，就必须有一个
    唯一的读取入口。以前三处硬编码，改一处漏两处的事故已经踩过
    （见踩坑记录：同一个 bug 有两份实现，只修一处等于没修）。

  读取顺序（从具体到宽松）：
    1. 调用方显式传入的值（-SocksPort / -HttpPort）
    2. .proxy-state\ports.json —— dual-proxy 启动时写的实际端口
    3. 默认值 10808 / 10809

  ⚠️ 这个文件必须放在 tools\ 下，被三个脚本 dot-source。
     改动后不需要重启 GUI 之外的东西，但 GUI 要重启才加载新版本。
#>

# 兼容 PS 5.1（没有 ?? 和 ?.）
function Get-BorrowPorts {
  param(
    [int]$SocksPort = 0,
    [int]$HttpPort = 0,
    [string]$StateFile = ''
  )

  $result = [PSCustomObject]@{
    SocksPort = if ($SocksPort -gt 0) { $SocksPort } else { 10808 }
    HttpPort  = if ($HttpPort -gt 0)  { $HttpPort }  else { 10809 }
    Source    = 'default'
  }

  # 命令行指定了就优先，不用读文件
  if ($SocksPort -gt 0 -and $HttpPort -gt 0) {
    $result.Source = 'explicit'
    return $result
  }

  if (-not $StateFile) {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $StateFile = Join-Path (Split-Path -Parent $here) '.proxy-state\ports.json'
  }

  if (-not (Test-Path $StateFile)) {
    # 没有状态文件：说明 dual-proxy 还没以 auto 模式跑过，用默认值
    return $result
  }

  try {
    # 必须按 UTF8 读。PS 5.1 的 Get-Content -Raw 默认按本机代码页解码，
    # 遇到含中文路径或中文内容会出错（踩过的坑：ConvertFrom-Json 报
    # 「Invalid object passed in」）。
    $json = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($StateFile)) | ConvertFrom-Json
    if ($SocksPort -le 0 -and $json.socks) { $result.SocksPort = [int]$json.socks }
    if ($HttpPort -le 0 -and $json.http)  { $result.HttpPort  = [int]$json.http }
    $result.Source = 'state-file'
  }
  catch {
    # 读不出来也别崩，退回默认值
  }

  return $result
}

<#
  兼容旧调用方式：很多地方直接写 $SocksPort / $HttpPort。
  这个函数在 dot-source 时把这些变量设为解析结果。
#>
function Set-BorrowPortVars {
  param([int]$SocksPort = 0, [int]$HttpPort = 0)
  $p = Get-BorrowPorts -SocksPort $SocksPort -HttpPort $HttpPort
  Set-Variable -Name SocksPort -Value $p.SocksPort -Scope Script
  Set-Variable -Name HttpPort  -Value $p.HttpPort  -Scope Script
  return $p
}
