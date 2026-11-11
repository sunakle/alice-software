function New-ProductIcon([string]$path) {
    Add-Type -AssemblyName System.Drawing
    $master = New-Object Drawing.Bitmap(256,256)
    $g = [Drawing.Graphics]::FromImage($master)
    $shape = New-Object Drawing.Drawing2D.GraphicsPath
    $bounds = New-Object Drawing.Rectangle(2,1,60,59)
    $finish = New-Object Drawing.Drawing2D.LinearGradientBrush($bounds, [Drawing.Color]::FromArgb(204,198,255), [Drawing.Color]::FromArgb(98,77,186), 62.0)
    $edge = New-Object Drawing.Pen([Drawing.Color]::FromArgb(166,239,232,255),0.7)
    $light = New-Object Drawing.Pen([Drawing.Color]::FromArgb(248,249,255),2.65)
    try {
        $g.SmoothingMode='AntiAlias'
        $g.ScaleTransform(4,4)
        $shape.AddArc(2,1,26,26,180,90); $shape.AddArc(36,1,26,26,270,90)
        $shape.AddArc(36,34,26,26,0,90); $shape.AddArc(2,34,26,26,90,90); $shape.CloseFigure()
        $g.FillPath($finish,$shape); $g.DrawPath($edge,$shape)
        $g.DrawEllipse($light,21,15,22,7)
        $light.StartCap='Round'; $light.EndCap='Round'
        $g.DrawLine($light,19,35,19,44); $g.DrawLine($light,25,35,25,44)
        $g.FillEllipse([Drawing.Brushes]::White,30.9,36,2.3,2.3); $g.FillEllipse([Drawing.Brushes]::White,30.9,41,2.3,2.3)
        $g.DrawLine($light,39,35,39,44); $g.DrawLine($light,45,35,45,44)
        $sizes = @(16,32,48,64,128,256)
        $frames = @{}
        foreach ($size in $sizes) {
            $frame = New-Object Drawing.Bitmap($size,$size)
            $draw = [Drawing.Graphics]::FromImage($frame)
            $png = New-Object IO.MemoryStream
            try {
                $draw.InterpolationMode='HighQualityBicubic'; $draw.PixelOffsetMode='HighQuality'
                $draw.DrawImage($master,0,0,$size,$size)
                $frame.Save($png,[Drawing.Imaging.ImageFormat]::Png)
                $frames[$size]=$png.ToArray()
            } finally { $png.Dispose(); $draw.Dispose(); $frame.Dispose() }
        }
        $writer = New-Object IO.BinaryWriter([IO.File]::Create($path))
        try {
            $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
            $offset=6+16*$sizes.Count
            foreach ($size in $sizes) {
                $dim=[byte]($size % 256)
                $writer.Write($dim); $writer.Write($dim); $writer.Write([uint16]0)
                $writer.Write([uint16]1); $writer.Write([uint16]32)
                $writer.Write([uint32]$frames[$size].Length); $writer.Write([uint32]$offset)
                $offset += $frames[$size].Length
            }
            foreach ($size in $sizes) { $writer.Write([byte[]]$frames[$size]) }
        } finally { $writer.Dispose() }
    } finally { $light.Dispose(); $edge.Dispose(); $finish.Dispose(); $shape.Dispose(); $g.Dispose(); $master.Dispose() }
}
