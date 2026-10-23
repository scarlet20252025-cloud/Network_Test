# 顺序测试：确认 dual-proxy 能否感知运行中的 settings 变化
$U = 'E:\肥鱼不乐园\borrow-net\users.json'
$txt = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($U))
$j = $txt | ConvertFrom-Json
$name = $j.users[0].name
$tok = $j.users[0].token

Set-Location 'E:\肥鱼不乐园\borrow-net'

Write-Host "=== 步骤 1：先看 bridge 当前的上限 ==="
$st = Invoke-RestMethod -Uri 'http://127.0.0.1:10801/api/status' -TimeoutSec 10
Write-Host ("  bridge 当前 maxConnsPerUser = " + $st.users.settings.maxConnsPerUser)

Write-Host "`n=== 步骤 2：改成 1 ==="
& node tools/proxy-admin.mjs settings --max-conns 1 | Select-Object -Last 2
Start-Sleep -Seconds 1

Write-Host "`n=== 步骤 3：bridge 是否感知 ==="
$st = Invoke-RestMethod -Uri 'http://127.0.0.1:10801/api/status' -TimeoutSec 10
Write-Host ("  bridge 现在 maxConnsPerUser = " + $st.users.settings.maxConnsPerUser)
Write-Host ("  （若仍是 40，说明 bridge 没有热加载 settings，需要重启）")

Write-Host "`n=== 步骤 4：发 3 个并发请求 ==="
$jobs = @()
for ($i = 1; $i -le 3; $i++) {
  $jobs += Start-Job -ArgumentList $name, $tok -ScriptBlock {
    param($n, $t)
    & curl.exe -s -o NUL -w '%{http_code}' --max-time 25 -x http://127.0.0.1:10809 -U "$($n):$($t)" https://github.com
  }
}
$res = $jobs | Wait-Job | Receive-Job
$jobs | Remove-Job
Write-Host ("  3 个并发结果: " + ($res -join ' '))

Write-Host "`n=== 步骤 5：恢复 40 ==="
& node tools/proxy-admin.mjs settings --max-conns 40 | Select-Object -Last 2
