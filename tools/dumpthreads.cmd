@echo off
rem Sample a .NET process's threads to find what is burning CPU.
rem Usage: dumpthreads.cmd <pid> <samples>
rem Requires the process to be runnable by this user (same session).
powershell -NoProfile -Command ^
  "$pid2 = %~1; $n = %~2;" ^
  "$p = Get-Process -Id $pid2 -ErrorAction SilentlyContinue;" ^
  "if (-not $p) { 'process not found'; exit 1 };" ^
  "for ($i=0; $i -lt $n; $i++) {" ^
  "  $snap = @($p.Threads | Where-Object { $_.ThreadState -eq 'Running' });" ^
  "  Write-Host ('--- sample ' + $i + ' : ' + $snap.Count + ' running threads ---');" ^
  "  foreach ($t in $snap) { Write-Host ('  tid=' + $t.Id + ' state=' + $t.ThreadState + ' wait=' + $t.WaitReason) };" ^
  "  Start-Sleep -Milliseconds 1500;" ^
  "  $p.Refresh();" ^
  "}"
