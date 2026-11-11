function New-ProductIcon([string]$path) {
    Add-Type -AssemblyName System.Drawing
    $bitmap=New-Object Drawing.Bitmap(64,64)
    $g=[Drawing.Graphics]::FromImage($bitmap)
    $blue=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(35,76,128))
    $light=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(176,216,255))
    $white=New-Object Drawing.Pen([Drawing.Color]::White,4)
    $ring=New-Object Drawing.Pen([Drawing.Color]::FromArgb(106,203,247),3)
    $background=New-Object Drawing.Drawing2D.GraphicsPath
    $png=New-Object IO.MemoryStream
    try {
        $g.SmoothingMode='AntiAlias'
        $background.AddArc(3,4,18,18,180,90); $background.AddArc(43,4,18,18,270,90)
        $background.AddArc(43,42,18,18,0,90); $background.AddArc(3,42,18,18,90,90); $background.CloseFigure()
        $g.FillPath($blue,$background)
        $g.DrawEllipse($ring,19,12,26,10)
        $white.StartCap='Round'; $white.EndCap='Round'
        $g.DrawLine($white,18,34,18,43); $g.DrawLine($white,24,33,24,43)
        $g.FillEllipse($light,30,34,3,3); $g.FillEllipse($light,30,40,3,3)
        $g.DrawLine($white,39,33,39,43); $g.DrawLine($white,46,33,46,43)
        $bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
        $writer=New-Object IO.BinaryWriter([IO.File]::Create($path))
        try {
            $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]1)
            $writer.Write([byte]64); $writer.Write([byte]64); $writer.Write([UInt16]0)
            $writer.Write([UInt16]1); $writer.Write([UInt16]32)
            $writer.Write([UInt32]$png.Length); $writer.Write([UInt32]22)
            $writer.Write($png.ToArray())
        } finally { $writer.Dispose() }
    } finally {
        $png.Dispose(); $background.Dispose(); $blue.Dispose(); $light.Dispose(); $white.Dispose(); $ring.Dispose(); $g.Dispose(); $bitmap.Dispose()
    }
}
