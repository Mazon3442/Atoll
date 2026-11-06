# Draws the package logos (black rounded square with a white "A"). Run once; output is committed.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "identity\Assets"
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($spec in @(@{ Name = "StoreLogo.png"; Size = 50 }, @{ Name = "Square44x44Logo.png"; Size = 44 }, @{ Name = "Square150x150Logo.png"; Size = 150 })) {
    $n = $spec.Size
    $bmp = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"
    $g.TextRenderingHint = "AntiAliasGridFit"
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [int]($n * 0.28)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($n - 1 - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($n - 1 - $r * 2, $n - 1 - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $n - 1 - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::Black, $path)

    $font = New-Object System.Drawing.Font "Segoe UI Semibold", ($n * 0.5), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = "Center"; $fmt.LineAlignment = "Center"
    $g.DrawString("A", $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 0, $n, $n), $fmt)

    $bmp.Save((Join-Path $out $spec.Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
"Logos written to $out"

# Velopack installer icon: a 256px logo wrapped in a single-entry ICO (PNG-in-ICO, Vista+).
$n = 256
$bmp = New-Object System.Drawing.Bitmap $n, $n
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = "AntiAlias"
$g.TextRenderingHint = "AntiAliasGridFit"
$g.Clear([System.Drawing.Color]::Transparent)
$r = [int]($n * 0.28)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
$path.AddArc($n - 1 - $r * 2, 0, $r * 2, $r * 2, 270, 90)
$path.AddArc($n - 1 - $r * 2, $n - 1 - $r * 2, $r * 2, $r * 2, 0, 90)
$path.AddArc(0, $n - 1 - $r * 2, $r * 2, $r * 2, 90, 90)
$path.CloseFigure()
$g.FillPath([System.Drawing.Brushes]::Black, $path)
$font = New-Object System.Drawing.Font "Segoe UI Semibold", ($n * 0.5), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = "Center"; $fmt.LineAlignment = "Center"
$g.DrawString("A", $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 0, $n, $n), $fmt)
$png = Join-Path $out "Atoll-256.png"
$bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

$pngBytes = [System.IO.File]::ReadAllBytes($png)
$ico = Join-Path $out "Atoll.ico"
$fs = [System.IO.File]::Create($ico)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0)                # reserved
$bw.Write([UInt16]1)                # type: icon
$bw.Write([UInt16]1)                # count
$bw.Write([Byte]0)                  # width 0 = 256
$bw.Write([Byte]0)                  # height 0 = 256
$bw.Write([Byte]0)                  # colors
$bw.Write([Byte]0)                  # reserved
$bw.Write([UInt16]1)                # planes
$bw.Write([UInt16]32)               # bit count
$bw.Write([UInt32]$pngBytes.Length) # bytes in resource
$bw.Write([UInt32]22)               # image offset (6-byte header + 16-byte entry)
$bw.Write($pngBytes)
$bw.Close()
"Icon written to $ico"
