[CmdletBinding()]
param([Parameter(Mandatory)][string]$ApplicationPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$assembly=[Reflection.Assembly]::LoadFrom((Resolve-Path $ApplicationPath).Path)
$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$shellType=$assembly.GetType('AliceDesk.Shell',$true)
$storeType=$assembly.GetType('AliceDesk.ProfileStore',$true)
$path=Join-Path ([IO.Path]::GetTempPath()) ('StantsiyaDesk-window-'+[Guid]::NewGuid().ToString('N'))
$store=$storeType.GetConstructor($flags,$null,[type[]]@([string]),$null).Invoke([object[]]@([string]$path))
$shell=$shellType.GetConstructor($flags,$null,[type[]]@($storeType,[bool]),$null).Invoke([object[]]@($store,$false))
try {
    $shell.StartPosition='Manual'
    $shell.Location=New-Object Drawing.Point(-3000,60)
    $shell.ShowInTaskbar=$false
    $shell.Show()
    [Windows.Forms.Application]::DoEvents()
    if($shell.FormBorderStyle -ne 'Sizable' -or !$shell.ControlBox -or !$shell.MinimizeBox -or !$shell.MaximizeBox){throw 'The native window controls are missing.'}
    if($shell.Region -ne $null -or $shell.ClientSize -eq $shell.Size){throw 'The window must use the native frame without a custom clipping region.'}
    $original=$shell.Bounds
    $shell.WindowState='Maximized'
    [Windows.Forms.Application]::DoEvents()
    if($shell.WindowState -ne 'Maximized' -or $shell.Region -ne $null){throw 'Zoom did not maximize cleanly.'}
    $shell.WindowState='Normal'
    [Windows.Forms.Application]::DoEvents()
    if($shell.WindowState -ne 'Normal' -or $shell.Size -ne $original.Size){throw 'Zoom did not restore the window size.'}
    $shell.WindowState='Minimized'
    [Windows.Forms.Application]::DoEvents()
    if($shell.Visible -or $shell.WindowState -ne 'Minimized'){throw 'Minimize did not follow shell tray behavior.'}
    $shell.WindowState='Normal'
    $shell.Show()
    [Windows.Forms.Application]::DoEvents()
    $shell.Close()
    [Windows.Forms.Application]::DoEvents()
    if(!$shell.IsDisposed){throw 'Close did not end the window lifetime.'}
    Write-Output 'OK: native resizable window, maximize, restore, minimize to tray and close.'
}
finally {
    $shell.Dispose()
    $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\StantsiyaDesk-window-'
    if([IO.Path]::GetFullPath($path).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $path)) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
