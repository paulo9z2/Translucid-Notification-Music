# Converte uma PNG existente para o app.ico (multi-tamanho, PNG embutido).
param(
    [string]$Source = "C:\Users\9z2.pj\Documents\Lightshot\Screenshot_87.png",
    [string]$OutIco = "$PSScriptRoot\..\src\Translucid.App\app.ico"
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"

function New-SizePng {
    param([System.Drawing.Image]$src, [int]$size, [string]$outPath)
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    $srcW = $src.Width; $srcH = $src.Height
    $canvas = [math]::Max($srcW, $srcH)
    $scale = $size / $canvas
    $dw = [math]::Round($srcW * $scale)
    $dh = [math]::Round($srcH * $scale)
    $dx = [math]::Round(($size - $dw) / 2)
    $dy = [math]::Round(($size - $dh) / 2)
    $g.DrawImage($src, $dx, $dy, $dw, $dh)

    $g.Dispose()
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  PNG $size ok"
}

if (-not (Test-Path $Source)) { throw "Imagem nao encontrada: $Source" }
$src = [System.Drawing.Image]::FromFile($Source)
Write-Host "Origem: $($src.Width)x$($src.Height) -> $OutIco"

$sizes = 16, 24, 32, 48, 64, 128, 256
$tmp = Join-Path $env:TEMP "translucid_icon_final"
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

$entries = @()
foreach ($s in $sizes) {
    $p = Join-Path $tmp "icon_$s.png"
    New-SizePng $src $s $p
    $entries += @{ s = $s; p = $p; b = $null }
}
$src.Dispose()

$fs = [System.IO.File]::Create($OutIco)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)

$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $e.b = [System.IO.File]::ReadAllBytes($e.p)
    $encH = if ($e.s -ge 256) { 0 } else { $e.s }
    $bw.Write([byte]$encH); $bw.Write([byte]$encH)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([int]$e.b.Length); $bw.Write([int]$offset)
    $offset += $e.b.Length
}
foreach ($e in $entries) { $bw.Write($e.b) }
$bw.Flush(); $fs.Close()
Write-Host "ICONE GERADO: $OutIco"