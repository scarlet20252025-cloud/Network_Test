# 查 Realtek 8852BE 的 PCIe ASPM 状态。
#
# 为什么查这个：8852BE 是 PCIe 网卡（不是 USB）。Realtek 的 PCIe 无线网卡
# 有已知的 ASPM（Active State Power Management）问题 —— 链路空闲时进入
# L1 低功耗态，收到包要退出 L1，退出过程有几十毫秒延迟。
# 这与实测的「2ms 与 80ms 随机跳变、72% 尖峰」形态完全吻合。
#
# 只读，不改任何东西。
# 用法： powershell -ExecutionPolicy Bypass -File tools\check-pcie-aspm.ps1

$ErrorActionPreference = 'Continue'

Write-Host '=== 1. 网卡的 PCIe 路径 ==='
$dev = Get-PnpDevice -Class Net -ErrorAction SilentlyContinue |
    Where-Object { $_.FriendlyName -like '*8852*' -or $_.InstanceId -like '*VEN_10EC&DEV_B852*' }
if (-not $dev) { Write-Host '  找不到 8852BE'; exit 1 }
Write-Host ("  {0}" -f $dev.FriendlyName)
Write-Host ("  {0}" -f $dev.InstanceId)

# 从 InstanceId 里拆出 PCI 总线/设备/功能号
$m = [regex]::Match($dev.InstanceId, 'PCI\\([^\\]+)\\')
$pciId = $m.Groups[1].Value
Write-Host ("  PCI 标识: {0}" -f $pciId)

Write-Host ''
Write-Host '=== 2. PCIe 链路电源状态（ASPM 相关）==='
# 用 WMI 查设备的电源管理能力
try {
    $pnp = Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue
    if ($pnp) {
        Write-Host '  MSPower_DeviceEnable（设备是否允许省电）:'
    }
} catch {}

# 查该设备的 PnPCapabilities（0x18 = 禁止系统关闭设备；0x10 = 禁止进入低功耗）
$base = 'HKLM:\SYSTEM\CurrentControlSet\Enum\PCI'
$found = $false
try {
    Get-ChildItem $base -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.PSChildName -like '*B852*' -or $_.PSChildName -like '*VEN_10EC*') {
            $found = $true
            Write-Host ("  设备键: {0}" -f $_.PSChildName)
            Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                $pn = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue)
                if ($pn.PSObject.Properties.Name -contains 'PnPCapabilities' -or
                    $pn.PSObject.Properties.Name -contains 'EnableL1ASPM' -or
                    $pn.PSObject.Properties.Name -contains 'ASPM') {
                    Write-Host ("    实例 {0}" -f $_.PSChildName)
                    Write-Host ("      PnPCapabilities = {0}" -f $pn.PnPCapabilities)
                    Write-Host ("      EnableL1ASPM   = {0}" -f $pn.EnableL1ASPM)
                    Write-Host ("      ASPM           = {0}" -f $pn.ASPM)
                }
            }
        }
    }
} catch { Write-Host ("  枚举失败: {0}" -f $_.Exception.Message) }

if (-not $found) {
    Write-Host '  未在 Enum\PCI 下匹配到设备键（可能需要管理员读取）'
}

Write-Host ''
Write-Host '=== 3. 全局 PCI Express 电源管理（当前电源计划）==='
$pow = (powercfg /query SCHEME_CURRENT SUB_PCIEXPRESS) -join "`n"
# ASPM 设置：0=关闭 1=中等省电 2=最大省电
$aspm = [regex]::Match($pow, '(?:当前交流电源设置索引|Current AC Power Setting Index)[^:]*:\s*(0x[0-9a-fA-F]+)')
if ($aspm.Success) {
    $v = [Convert]::ToInt32($aspm.Groups[1].Value.Substring(2), 16)
    $name = @{ 0 = '关闭（最高性能）'; 1 = '中等省电 L0s'; 2 = '最大省电 L1' }[$v]
    Write-Host ("  接电源时 ASPM = {0}  ({1})" -f $v, $name)
    if ($v -ge 1) {
        Write-Host '  ⚠ ASPM 开着 —— 这正是 Realtek 8852BE 的已知坑。' -ForegroundColor Yellow
        Write-Host '    链路空闲进 L1，收到包才退出，退出要几十毫秒。'
        Write-Host '    表现就是「2ms 和 80ms 随机跳」。'
    }
} else {
    Write-Host '  取不到 ASPM 值，原始输出片段：'
    ($pow -split "`n" | Select-String '索引|Index|ASPM|PCI' | Select-Object -First 6) | ForEach-Object { Write-Host ('    ' + $_) }
}

Write-Host ''
Write-Host '=== 4. 如果确认是 ASPM，改法（需要管理员）==='
Write-Host '  powercfg /setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 0'
Write-Host '  powercfg /setdcvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 0'
Write-Host '  powercfg /setactive SCHEME_CURRENT'
Write-Host '  （ASPM 是子组 GUID，powershell 里可以直接用这个名字）'
