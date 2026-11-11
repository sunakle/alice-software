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
function FindControls($root,[string]$type) {
    if($root.GetType().FullName -eq $type){ $root }
    foreach($child in $root.Controls){ FindControls $child $type }
}
try {
    $shell.StartPosition='Manual'
    $shell.Location=New-Object Drawing.Point(-3000,60)
    $shell.ShowInTaskbar=$false
    $shell.Show()
    [Windows.Forms.Application]::DoEvents()
    $dots=@(FindControls $shell 'AliceDesk.WindowDot')
    if($dots.Count -ne 3 -or $dots[0].AccessibleRole -ne 'PushButton'){throw 'The three native window actions are missing.'}
    if($shell.Region -eq $null -or $shell.ClientSize -ne $shell.Size){throw 'The restored window has no custom frame or retains a system caption.'}
    $original=$shell.Bounds
    $dots[2].PerformClick()
    [Windows.Forms.Application]::DoEvents()
    if($shell.WindowState -ne 'Maximized' -or $shell.Region -ne $null){throw 'Zoom did not maximize cleanly.'}
    if($shell.RectangleToScreen($shell.ClientRectangle) -ne [Windows.Forms.Screen]::FromControl($shell).WorkingArea){throw 'Maximized content covers the taskbar or leaves a gap.'}
    $dots[2].PerformClick()
    [Windows.Forms.Application]::DoEvents()
    if($shell.WindowState -ne 'Normal' -or $shell.Size -ne $original.Size){throw 'Zoom did not restore the window size.'}
    $dots[1].PerformClick()
    [Windows.Forms.Application]::DoEvents()
    if($shell.Visible -or $shell.WindowState -ne 'Minimized'){throw 'Minimize did not follow shell tray behavior.'}
    $shell.WindowState='Normal'
    $shell.Show()
    [Windows.Forms.Application]::DoEvents()
    $dots[0].PerformClick()
    [Windows.Forms.Application]::DoEvents()
    if(!$shell.IsDisposed){throw 'Close did not end the window lifetime.'}
    Write-Output 'OK: rounded borderless window, accessible traffic lights, maximize within work area, restore, minimize to tray and close.'
}
finally {
    $shell.Dispose()
    $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\StantsiyaDesk-window-'
    if([IO.Path]::GetFullPath($path).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $path)) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
