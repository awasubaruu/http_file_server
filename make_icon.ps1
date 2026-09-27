Add-Type -AssemblyName System.Drawing

$size = 48
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

# blue rounded background
$bgBrush = [System.Drawing.Brushes]::DodgerBlue
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 10
$path.AddArc(0, 0, $r, $r, 180, 90)
$path.AddArc($size - $r, 0, $r, $r, 270, 90)
$path.AddArc($size - $r, $size - $r, $r, $r, 0, 90)
$path.AddArc(0, $size - $r, $r, $r, 90, 90)
$path.CloseFigure()
$g.FillPath($bgBrush, $path)

# white folder
$folderBrush = [System.Drawing.Brushes]::White
$g.FillRectangle($folderBrush, 10, 18, 28, 18)
$p1 = [System.Drawing.Point]::new(10, 18)
$p2 = [System.Drawing.Point]::new(16, 12)
$p3 = [System.Drawing.Point]::new(26, 12)
$p4 = [System.Drawing.Point]::new(29, 18)
$points = [System.Drawing.Point[]]@($p1, $p2, $p3, $p4)
$g.FillPolygon($folderBrush, $points)

$g.Dispose()

$icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$dir = 'D:\Dev\http_file_server\src\HttpFileServer\Assets'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$fs = [System.IO.File]::Create("$dir\icon.ico")
$icon.Save($fs)
$fs.Dispose()
$bmp.Dispose()
Write-Host "icon.ico created"
