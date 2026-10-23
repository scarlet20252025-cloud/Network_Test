$U = 'E:\肥鱼不乐园\borrow-net\users.json'
$j = Get-Content $U -Raw | ConvertFrom-Json
$name = $j.users[0].name
$tok  = $j.users[0].token
Write-Host "目标用户: $name"

Set-Location 'E:\肥鱼不乐园\borrow-net'
Write-Host "`n=== 设置单用户连接上限为 2 ==="
& node tools/proxy-admin.mjs settings --max-conns 2 | Select-Object -Last 3
Start-Sleep -Seconds 2

Write-Host "`n=== 并发 5 个请求（上限 2，应部分被拒）==="
$jobs = @()
for ($i = 1; $i -le 5; $i++) {
  $jobs += Start-Job -ArgumentList $name, $tok -ScriptBlock {
    param($n, $t)
    & curl.exe -s -o NUL -w '%{http_code}' --max-time 25 -x http://127.0.0.1:10809 -U "$($n):$($t)" https://github.com
  }
}
$res = $jobs | Wait-Job | Receive-Job
$jobs | Remove-Job
Write-Host ("  5 个并发结果: " + ($res -join ' '))
Write-Host "  (429 或 000 = 被上限拒绝；200 = 放行)"

Write-Host "`n=== 面板拒绝记录 ==="
$st = Invoke-RestMethod -Uri 'http://127.0.0.1:10801/api/status' -TimeoutSec 10
$st.users.denials | Select-Object -First 6 | ForEach-Object {
  Write-Host ("  $($_.reason)  from $($_.remote)")
}

Write-Host "`n=== 恢复上限为 40 ==="
& node tools/proxy-admin.mjs settings --max-conns 40 | Select-Object -Last 3
