param([Parameter(Mandatory=$true)][string]$ApplicationPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $ApplicationPath).Path)
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$storeType = $assembly.GetType('AliceDesk.ProfileStore', $true)
$shellType = $assembly.GetType('AliceDesk.Shell', $true)
$dir = Join-Path ([IO.Path]::GetTempPath()) ('StantsiyaDesk-model-' + [Guid]::NewGuid().ToString('N'))
$store = [Activator]::CreateInstance($storeType, $flags, $null, @([string]$dir), $null)
$shell = [Activator]::CreateInstance($shellType, $flags, $null, @($store, $false), $null)
try {
    $shell.ShowInTaskbar = $false
    $shell.Location = New-Object Drawing.Point(-2500, 0)
    $shell.Show()
    [Windows.Forms.Application]::DoEvents()
    $overview = $shellType.GetField('home', $flags).GetValue($shell)
    $model = $overview.GetType().GetField('preview', $flags).GetValue($overview)
    $toggle = $overview.GetType().GetField('showClock', $flags).GetValue($overview)
    function PictureHash($control) {
        $image = New-Object Drawing.Bitmap($control.Width, $control.Height)
        $stream = New-Object IO.MemoryStream
        try {
            $control.DrawToBitmap($image, (New-Object Drawing.Rectangle(0,0,$control.Width,$control.Height)))
            $image.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $hash = [Security.Cryptography.SHA256]::Create()
            try { return [Convert]::ToBase64String($hash.ComputeHash($stream.ToArray())) } finally { $hash.Dispose() }
        } finally { $image.Dispose(); $stream.Dispose() }
    }
    $withClock = PictureHash $model
    $toggle.Checked = $false
    $withoutClock = PictureHash $model
    if ($withoutClock -eq $withClock) { throw 'Clock switch did not change the model.' }
    $profile = $storeType.GetProperty('Active', $flags).GetValue($store, $null)
    if ($profile.Get('preview.clock', '') -ne 'false') { throw 'Clock choice was not saved.' }
    $model.GetType().GetField('angle', $flags).SetValue($model, [single]0.7)
    if ((PictureHash $model) -eq $withoutClock) { throw 'Model rotation did not change the view.' }
    Write-Output 'OK: 3D preview, clock visibility and saved setting.'
} finally {
    $shell.Dispose()
    $root = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\StantsiyaDesk-model-'
    if ([IO.Path]::GetFullPath($dir).StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $dir)) {
        Remove-Item -LiteralPath $dir -Recurse -Force
    }
}
