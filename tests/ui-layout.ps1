[CmdletBinding()]
param([Parameter(Mandatory)][string]$ApplicationPath,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$asm=[Reflection.Assembly]::LoadFrom((Resolve-Path $ApplicationPath).Path)
$null=New-Item -ItemType Directory -Force -Path $OutputDirectory
$dir=Join-Path ([IO.Path]::GetTempPath()) ('StantsiyaDesk-ui-'+[Guid]::NewGuid().ToString('N'))
$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$storeType=$asm.GetType('AliceDesk.ProfileStore')
$store=$storeType.GetConstructor($flags,$null,[type[]]@([string]),$null).Invoke([object[]]@([string]$dir))
$formType=$asm.GetType('AliceDesk.Shell')
$window=$formType.GetConstructor($flags,$null,[type[]]@($storeType,[bool]),$null).Invoke([object[]]@($store,$false))
try {
    $window.StartPosition='Manual'
    $window.Location=New-Object Drawing.Point(-2500,40)
    $window.Size=New-Object Drawing.Size(1100,760)
    $window.ShowInTaskbar=$false
    $window.Show()
    $method=$formType.GetMethod('ShowPage',$flags)
    foreach($page in @('home','ai','station','extensions','music','agent','recovery')) {
        $method.Invoke($window,[object[]]@([string]$page))
        [Windows.Forms.Application]::DoEvents()
        $bmp=New-Object Drawing.Bitmap($window.Width,$window.Height)
        try {
            $window.DrawToBitmap($bmp,(New-Object Drawing.Rectangle(0,0,$window.Width,$window.Height)))
            $bmp.Save((Join-Path $OutputDirectory ($page+'.png')))
        } finally { $bmp.Dispose() }
    }
} finally {
    $window.Dispose()
    $safeRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\StantsiyaDesk-ui-'
    if ([IO.Path]::GetFullPath($dir).StartsWith($safeRoot,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $dir)) {Remove-Item -LiteralPath $dir -Recurse -Force}
}
