# Builds bin\MultimonWallpaper.exe - a single portable file, no installer, no SDK needed.
# Uses the C# compiler that ships with Windows (.NET Framework 4).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force "$root\bin" | Out-Null

# generate the app icon (blue landscape monitor + teal portrait monitor)
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 64, 64
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::Transparent)
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 120, 215))), 2, 14, 40, 30)
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 190, 170))), 46, 6, 16, 52)
$g.Dispose()
$icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$ico = "$root\bin\app.ico"
$fs = [IO.File]::Create($ico); $icon.Save($fs); $fs.Close()

& $csc /nologo /target:winexe /optimize+ /out:"$root\bin\MultimonWallpaper.exe" `
    /win32icon:$ico /win32manifest:"$root\src\app.manifest" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "$root\src\MultimonWallpaper.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
[IO.File]::Delete($ico)
"Built $root\bin\MultimonWallpaper.exe"
