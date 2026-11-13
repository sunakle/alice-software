param([Parameter(Mandatory)][string]$ApplicationPath,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$asm=[Reflection.Assembly]::LoadFrom((Resolve-Path $ApplicationPath).Path)
$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$temp=Join-Path ([IO.Path]::GetTempPath()) ('sd-ui-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$storeType=$asm.GetType('AliceDesk.ProfileStore')
$store=$storeType.GetConstructor($flags,$null,[type[]]@([string]),$null).Invoke([object[]]@([string]$temp))
$shellType=$asm.GetType('AliceDesk.Shell')
$window=$shellType.GetConstructor($flags,$null,[type[]]@($storeType,[bool]),$null).Invoke([object[]]@($store,$false))
function Descendants($parent) {
    foreach($child in $parent.Controls) { $child; Descendants $child }
}
function Capture([string]$name) {
    [Windows.Forms.Application]::DoEvents()
    $bmp=New-Object Drawing.Bitmap($window.Width,$window.Height)
    try {$window.DrawToBitmap($bmp,(New-Object Drawing.Rectangle(0,0,$window.Width,$window.Height)));$bmp.Save((Join-Path $OutputDirectory ($name+'.png')))}finally{$bmp.Dispose()}
}
try {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory))
    $window.StartPosition='Manual';$window.Location=New-Object Drawing.Point(-2500,40);$window.Size=New-Object Drawing.Size(880,620);$window.ShowInTaskbar=$false;$window.Show()
    $show=$shellType.GetMethod('ShowPage',$flags)
    $music=$shellType.GetField('music',$flags).GetValue($window)
    $show.Invoke($window,@('music'))
    $spotify=@(Descendants $music | Where-Object {$_.GetType().Name -eq 'SpotifyPage'})[0]
    $footer=$spotify.Controls | Where-Object {$_.Name -eq 'spotify-status'}
    Write-Output ('Footer actual='+$footer.Height+' preferred='+$footer.PreferredSize.Height)
    if($footer.Height -gt 105){throw 'Idle status footer wastes viewport space.'}
    Capture 'spotify-top'
    $body=@(Descendants $spotify | Where-Object {$_.GetType().Name -eq 'SettingsBody'})[0]
    if($footer.Bottom -ne $spotify.ClientSize.Height -or $body.Bottom -gt $footer.Top){throw 'Spotify status overlaps settings or is not at the bottom.'}
    $body.AutoScrollPosition=New-Object Drawing.Point(0,10000)
    Capture 'spotify-bottom'
    if($body.HorizontalScroll.Visible){throw 'Spotify page has horizontal overflow.'}
    $music.Controls[0].SelectedIndex=1;Capture 'bluetooth'
    $show.Invoke($window,@('agent'))
    $agent=$shellType.GetField('agent',$flags).GetValue($window)
    $agent.Controls[0].SelectedIndex=1;Capture 'voice-connection'
    $agent.GetType().GetMethod('NewSpotify',$flags).Invoke($agent,@())
    [Windows.Forms.Application]::DoEvents()
    if($agent.Controls[0].SelectedIndex -ne 0){throw 'New Spotify action leaves editor hidden on HA tab.'}
    $kind=$agent.GetType().GetField('type',$flags).GetValue($agent)
    if($kind.SelectedIndex -ne 3){throw 'New action is not Spotify.'}
    Capture 'voice-editor'
    foreach($row in @(Descendants $agent | Where-Object {$_.Visible -and $_ -is [Windows.Forms.FlowLayoutPanel]})) {
        if($row.HorizontalScroll.Visible){throw 'Voice editor has horizontal overflow.'}
    }
    Write-Output 'PASS: compact Spotify tabs, visible status, vertical scrolling, Bluetooth, HA and new phrase editor navigation.'
} finally {
    $window.Dispose()
    $absolute=[IO.Path]::GetFullPath($temp);$parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if([IO.Path]::GetDirectoryName($absolute) -eq $parent -and [IO.Path]::GetFileName($absolute) -match '^sd-ui-[a-f0-9]{8}$' -and (Test-Path -LiteralPath $absolute)){Remove-Item -LiteralPath $absolute -Recurse -Force}
}
