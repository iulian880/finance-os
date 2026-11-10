<#
.SYNOPSIS
  Generates the application icon (src/FinanceOS.App/Assets/app.ico): the brand mark from the sidebar,
  a white check on an indigo rounded square, in every size Windows asks for (16 to 256 px).
  Runs in Windows PowerShell 5.1 (System.Drawing).
#>
param(
    [string]$OutPath = (Join-Path $PSScriptRoot '..\src\FinanceOS.App\Assets\app.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = New-Object System.Collections.Generic.List[byte[]]

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square
    $radius = [Math]::Max(2, [int]($s * 0.25))
    $d = $radius * 2
    $w = $s - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90)
    $path.AddArc(0, $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point 0, 0),
        (New-Object System.Drawing.Point $s, $s),
        [System.Drawing.Color]::FromArgb(255, 99, 102, 241),
        [System.Drawing.Color]::FromArgb(255, 79, 70, 229))
    $g.FillPath($brush, $path)

    # Check mark: Lucide "check" (M20,6 L9,17 L4,12 on a 24 grid), centred, like the sidebar brand.
    $box = $s * 0.6
    $scale = $box / 24
    $origin = ($s - $box) / 2
    $points = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ([single]($origin + 4 * $scale)), ([single]($origin + 12.5 * $scale))),
        (New-Object System.Drawing.PointF ([single]($origin + 9 * $scale)), ([single]($origin + 17.5 * $scale))),
        (New-Object System.Drawing.PointF ([single]($origin + 20 * $scale)), ([single]($origin + 6.5 * $scale))))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][Math]::Max(1.6, $s * 0.085))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($pen, $points)

    $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $images.Add($ms.ToArray())
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$images[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()

New-Item -ItemType Directory -Force (Split-Path $OutPath) | Out-Null
[System.IO.File]::WriteAllBytes($OutPath, $out.ToArray())
"Wrote $OutPath ($($out.Length) bytes, $($sizes.Count) sizes)"
