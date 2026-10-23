# 验证热加载后的配额是否真的生效（不再需要重启 bridge）
$U = 'E:\肥鱼不乐园\borrow-net\users.json'
$txt = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($U))
$j = $txt | ConvertFrom-Json
$name = $j.users[0].name
$tok = $j.users[0].token
Write-Host "目标用户: $name"

Set-Location 'E:\肥鱼不乐园\borrow-net'

Write-Host "`n=== 当前上限（应为 1）==="
$st = Invoke-RestMethod -Uri 'http://127.0.0.1:10801/api/status' -TimeoutSec 10
Write-Host ("  bridge maxConns = " + $st.users.settings.maxConnsPerUser)

Write-Host "`n=== 发 4 个并发请求（上限 1，应 3 个被拒）==="
$jobs = @()
for ($i = 1; $i -le 4; $i++) {
  $jobs += Start-Job -ArgumentList $name, $tok -ScriptBlock {
    param($n, $t)
    & curl.exe -s -o NUL -w '%{http_code}' --max-time 30 -x http://127.0.0.1:10809 -U "$($n):$($t)" https://github.com
  }
}
$res = $jobs | Wait-Job | Receive-Job
$jobs | Remove-Job
Write-Host ("  4 个并发: " + ($res -join ' '))
Write-Host "  (200 = 放行；429 = 超限被拒)"

Write-Host "`n=== 面板拒绝记录 ==="
$st = Invoke-RestMethod -Uri 'http://127.0.0.1:10801/api/status' -TimeoutSec 10
$st.users.denials | Select-Object -First 5 | ForEach-Object {
  Write-Host ("  $($_.reason)  from $($_.remote)")
}

Write-Host "`n=== 恢复上限 40 ==="
& node tools/proxy-admin.mjs settings --max-conns 40 | Select-Object -Last 2
