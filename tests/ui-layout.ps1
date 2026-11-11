[CmdletBinding()]
param([Parameter(Mandatory)][string]$ApplicationPath,[Parameter(Mandatory)][string]$OutputDirectory,
      [int]$Width=1100,[int]$Height=760)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
[Windows.Forms.Application]::SetUnhandledExceptionMode([Windows.Forms.UnhandledExceptionMode]::ThrowException)
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
    $window.Size=New-Object Drawing.Size($Width,$Height)
    $window.ShowInTaskbar=$false
    $window.Show()
    $method=$formType.GetMethod('ShowPage',$flags)
    foreach($page in @('home','ai','station','extensions','music','agent','recovery')) {
        Write-Verbose ("Opening " + $page)
        $method.Invoke($window,[object[]]@([string]$page))
        [Windows.Forms.Application]::DoEvents()
        $bmp=New-Object Drawing.Bitmap($window.Width,$window.Height)
        try {
            $window.DrawToBitmap($bmp,(New-Object Drawing.Rectangle(0,0,$window.Width,$window.Height)))
            $bmp.Save((Join-Path $OutputDirectory ($page+'.png')))
        } finally { $bmp.Dispose() }
    }
    $searchBox=$formType.GetField('search',$flags).GetValue($window)
    $inputBox=$searchBox.GetType().GetField('Input',$flags).GetValue($searchBox)
    $links=$formType.GetField('links',$flags).GetValue($window)
    $heading=$formType.GetField('title',$flags).GetValue($window)
    $inputBox.Focus() | Out-Null
    $inputBox.Text='Spotify'
    if(!$links['music'].Visible -or $links['station'].Visible){throw 'Section search failed to filter destinations.'}
    $keyEvent=New-Object Windows.Forms.KeyEventArgs([Windows.Forms.Keys]::Enter)
    [void][Windows.Forms.TextBox].GetMethod('OnKeyDown',$flags).Invoke($inputBox,[object[]]@($keyEvent.PSObject.BaseObject))
    if($heading.Text -ne $links['music'].Text -or $inputBox.Text.Length -ne 0){throw 'Enter did not open the matching section.'}
    $formType.GetField('back',$flags).GetValue($window).PerformClick()
    if($heading.Text -ne $links['recovery'].Text){throw 'Back did not restore the previous section.'}
    $formType.GetField('forward',$flags).GetValue($window).PerformClick()
    if($heading.Text -ne $links['music'].Text){throw 'Forward did not restore the selected section.'}
    Write-Output 'OK: seven sections, section search, Enter, back and forward.'
} finally {
    $window.Dispose()
    $safeRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\StantsiyaDesk-ui-'
    if ([IO.Path]::GetFullPath($dir).StartsWith($safeRoot,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $dir)) {Remove-Item -LiteralPath $dir -Recurse -Force}
}
