Add-Type -AssemblyName System.Drawing

$root = "C:\AI RPG AOE\src\IslandRpg\Resources\Images"
$sources = @(
    "bucket-empty-source.jpg",
    "bucket-water-source.jpg",
    "bucket-seawater-source.jpg"
)
$cell = 32
$sheet = New-Object System.Drawing.Bitmap ($cell * $sources.Count), $cell
$sheet.SetResolution(96, 96)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::Transparent)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

function Convert-ToKeyedBitmap([string]$path) {
    $src = [System.Drawing.Bitmap]::FromFile($path)
    $keyed = New-Object System.Drawing.Bitmap $src.Width, $src.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $minX = $src.Width; $minY = $src.Height; $maxX = -1; $maxY = -1
    $watermarkX = [int]($src.Width * .82)
    $watermarkY = [int]($src.Height * .88)
    for ($y = 0; $y -lt $src.Height; $y++) {
        for ($x = 0; $x -lt $src.Width; $x++) {
            $c = $src.GetPixel($x, $y)
            $magenta = [Math]::Min($c.R, $c.B) - $c.G
            if ($magenta -gt 70 -and $c.R -gt 140 -and $c.B -gt 140) {
                $keyed.SetPixel($x, $y, [System.Drawing.Color]::Transparent)
                continue
            }
            if ($x -ge $watermarkX -and $y -ge $watermarkY) {
                $keyed.SetPixel($x, $y, [System.Drawing.Color]::Transparent)
                continue
            }
            $keyed.SetPixel($x, $y, $c)
            if ($x -lt $minX) { $minX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
    $src.Dispose()
    $pad = 4
    $minX = [Math]::Max(0, $minX - $pad)
    $minY = [Math]::Max(0, $minY - $pad)
    $maxX = [Math]::Min($keyed.Width - 1, $maxX + $pad)
    $maxY = [Math]::Min($keyed.Height - 1, $maxY + $pad)
    $crop = New-Object System.Drawing.Rectangle $minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1)
    $cut = $keyed.Clone($crop, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $keyed.Dispose()
    return $cut
}

for ($i = 0; $i -lt $sources.Count; $i++) {
    $cut = Convert-ToKeyedBitmap (Join-Path $root $sources[$i])
    $scale = [Math]::Min(($cell - 2) / [double]$cut.Width, ($cell - 2) / [double]$cut.Height)
    $w = [Math]::Max(1, [int][Math]::Round($cut.Width * $scale))
    $h = [Math]::Max(1, [int][Math]::Round($cut.Height * $scale))
    $x = $i * $cell + [int](($cell - $w) / 2)
    $y = [int](($cell - $h) / 2)
    $g.DrawImage($cut, $x, $y, $w, $h)
    $cut.Dispose()
}

$g.Dispose()
$out = Join-Path $root "bucket-items.png"
$sheet.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()
Write-Output "Wrote $out"
