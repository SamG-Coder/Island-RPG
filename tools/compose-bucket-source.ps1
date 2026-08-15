Add-Type -AssemblyName System.Drawing

$root = "C:\AI RPG AOE\artifacts\item-sprites\buckets"
$files = @(
    "bucket-empty-hires.jpg",
    "bucket-water-hires.jpg",
    "bucket-seawater-hires.jpg"
)
$first = [System.Drawing.Bitmap]::FromFile((Join-Path $root $files[0]))
$cell = 512
$sheet = New-Object System.Drawing.Bitmap ($cell * $files.Count), $cell
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::FromArgb(255, 255, 0, 255))
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$magenta = [System.Drawing.SolidBrush]::new(
    [System.Drawing.Color]::FromArgb(255, 255, 0, 255))

for ($i = 0; $i -lt $files.Count; $i++) {
    $src = if ($i -eq 0) { $first } else {
        [System.Drawing.Bitmap]::FromFile((Join-Path $root $files[$i]))
    }
    $g.DrawImage($src, $i * $cell, 0, $cell, $cell)
    # Cover the generator watermark in the lower-right of each cell.
    $g.FillRectangle(
        $magenta,
        $i * $cell + [int]($cell * .78),
        [int]($cell * .90),
        [int]($cell * .22),
        [int]($cell * .10))
    if ($i -ne 0) { $src.Dispose() }
}
$first.Dispose()
$magenta.Dispose()
$g.Dispose()

$sourceOut = Join-Path $root "bucket-items-source.png"
$sheet.Save($sourceOut, [System.Drawing.Imaging.ImageFormat]::Png)
Copy-Item $sourceOut "C:\AI RPG AOE\src\IslandRpg\Resources\Images\bucket-items-source.png" -Force
$sheet.Dispose()
Write-Output "Wrote $sourceOut"
