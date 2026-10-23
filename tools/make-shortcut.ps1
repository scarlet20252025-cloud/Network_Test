# 生成「渡口助手」图标 + 快捷方式（一次性脚本）
# 用法: pwsh -File tools/make-shortcut.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$ToolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$IconDir  = Join-Path $ToolsDir '.ferry'
$IcoPath  = Join-Path $IconDir 'ferry.ico'
if (-not (Test-Path $IconDir)) { $null = New-Item -ItemType Directory -Path $IconDir }

# ---------- 1. 画图标 ----------
function New-IconBitmap([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap($size, $size)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.TextRenderingHint  = [System.Drawing.Text.TextRenderingHint]::AntiAlias
  $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

  # 圆角方形底：深蓝渐变
  $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $r = [int]($size * 0.22)
  $path.AddArc($rect.X, $rect.Y, $r * 2, $r * 2, 180, 90)
  $path.AddArc($rect.Right - $r * 2, $rect.Y, $r * 2, $r * 2, 270, 90)
  $path.AddArc($rect.Right - $r * 2, $rect.Bottom - $r * 2, $r * 2, $r * 2, 0, 90)
  $path.AddArc($rect.X, $rect.Bottom - $r * 2, $r * 2, $r * 2, 90, 90)
  $path.CloseFigure()
  $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
      $rect, [System.Drawing.Color]::FromArgb(255, 32, 58, 92),
             [System.Drawing.Color]::FromArgb(255, 18, 34, 58),
             [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
  $g.FillPath($lg, $path)

  # 地球网格
  $cx = $size / 2.0
  $cy = $size / 2.0
  $R  = $size * 0.28
  $white = [System.Drawing.Color]::FromArgb(235, 255, 255, 255)
  $penW  = [Math]::Max(1.0, $size * 0.030)
  $pen = New-Object System.Drawing.Pen($white, $penW)
  # 外圈
  $g.DrawEllipse($pen, ($cx - $R), ($cy - $R), ($R * 2), ($R * 2))
  # 经线（椭圆压扁）
  $penThin = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 255, 255, 255), $penW * 0.7)
  for ($i = 1; $i -le 3; $i++) {
    $w = $R * (0.25 * $i)
    $g.DrawEllipse($penThin, ($cx - $w), ($cy - $R), ($w * 2), ($R * 2))
  }
  # 纬线
  for ($i = 1; $i -le 2; $i++) {
    $h = $R * (0.28 * $i)
    $g.DrawArc($penThin, ($cx - $R), ($cy - $h), ($R * 2), ($h * 2), 0, 360)
    $g.DrawArc($penThin, ($cx - $R), ($cy - $R + $h - $h * 0.0 - $h), ($R * 2), ($h * 2), 0, 0)
  }
  # 水平赤道
  $g.DrawLine($penThin, ($cx - $R), $cy, ($cx + $R), $cy)

  # 右上角信号波纹（表示正在联网）
  $green = [System.Drawing.Color]::FromArgb(255, 52, 199, 123)
  $penG = New-Object System.Drawing.Pen($green, $penW * 1.1)
  $bx = $cx + $R * 0.52
  $by = $cy - $R * 0.52
  $g.DrawArc($penG, ($bx - $size * 0.05), ($by - $size * 0.05), ($size * 0.10), ($size * 0.10), 180, 360)
  $g.DrawArc($penG, ($bx - $size * 0.09), ($by - $size * 0.09), ($size * 0.18), ($size * 0.18), 180, 360)
  $g.FillEllipse((New-Object System.Drawing.SolidBrush($green)), ($bx - $size * 0.022), ($by - $size * 0.022), ($size * 0.044), ($size * 0.044))

  $g.Flush(); $g.Dispose()
  return $bmp
}

function Save-Ico([string]$path, [System.Drawing.Bitmap[]]$bmps) {
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($ms)
  $bw.Write([uint16]0)      # reserved
  $bw.Write([uint16]1)      # type = icon
  $bw.Write([uint16]$bmps.Count)
  $offset = 6 + (16 * $bmps.Count)
  foreach ($b in $bmps) {
    $pngMs = New-Object System.IO.MemoryStream
    $b.Save($pngMs, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $pngMs.ToArray(); $pngMs.Dispose()
    $dim = $b.Width
    $bw.Write([byte]$(if ($dim -ge 256) { 0 } else { $dim }))
    $bw.Write([byte]$(if ($b.Height -ge 256) { 0 } else { $b.Height }))
    $bw.Write([byte]0)      # colors
    $bw.Write([byte]0)      # reserved
    $bw.Write([uint16]1)    # planes
    $bw.Write([uint16]32)   # bpp
    $bw.Write([uint32]$bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $bytes.Length
  }
  foreach ($b in $bmps) {
    $pngMs = New-Object System.IO.MemoryStream
    $b.Save($pngMs, [System.Drawing.Imaging.ImageFormat]::Png)
    $bw.Write($pngMs.ToArray())
    $pngMs.Dispose(); $b.Dispose()
  }
  $bw.Flush()
  [IO.File]::WriteAllBytes($path, $ms.ToArray())
  $bw.Dispose(); $ms.Dispose()
  Write-Host ("  图标已生成: {0} ({1} 字节)" -f $path, (Get-Item $path).Length)
}

$b48 = New-IconBitmap 48
$b32 = New-IconBitmap 32
$b16 = New-IconBitmap 16
Save-Ico $IcoPath @($b48, $b32, $b16)

# ---------- 2. 生成快捷方式 ----------
$WshShell = New-Object -ComObject WScript.Shell

function New-Lnk([string]$lnkPath, [string]$desc) {
  if (Test-Path $lnkPath) { Remove-Item $lnkPath -Force }
  $lnk = $WshShell.CreateShortcut($lnkPath)
  # 用 powershell.exe 承载，-WindowStyle Hidden 隐藏控制台窗口，只显示 WinForms 界面
  $lnk.TargetPath       = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
  $lnk.Arguments        = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f (Join-Path $ToolsDir '渡口助手.ps1')
  $lnk.WorkingDirectory = $ToolsDir
  $lnk.IconLocation     = "$IcoPath,0"
  $lnk.Description      = $desc
  $lnk.WindowStyle      = 7   # 最小化启动，配合 -WindowStyle Hidden 彻底不闪黑窗
  $lnk.Save()
  Write-Host ("  快捷方式: {0}" -f $lnkPath)
}

$desktop = [Environment]::GetFolderPath('Desktop')
New-Lnk (Join-Path $desktop '渡口助手.lnk') '在直连与渡口之间切换，显示当前出口 IP'

# 开始菜单（当前用户）
$startDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
New-Lnk (Join-Path $startDir '渡口助手.lnk') '在直连与渡口之间切换，显示当前出口 IP'

Write-Host ""
Write-Host "完成。双击桌面「渡口助手」即可启动。" -ForegroundColor Green
