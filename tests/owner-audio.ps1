# Optional hardware check: list outputs. Supply -Index to play one short tone on that output.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ApplicationPath,[int]$Index=-1)
$ErrorActionPreference='Stop'
$asm=[Reflection.Assembly]::LoadFrom((Resolve-Path $ApplicationPath).Path)
$audio=$asm.GetType('AliceDesk.MediaAudio',$true)
$static=[Reflection.BindingFlags]'NonPublic,Static'
$instance=[Reflection.BindingFlags]'NonPublic,Instance'
$devices=$audio.GetMethod('Enumerate',$static).Invoke($null,@())
if($Index -lt 0) {
    for($n=0;$n -lt $devices.Count;$n++){ Write-Output ($n.ToString() + '  ' + $devices[$n].ToString()) }
    return
}
if($Index -ge $devices.Count){throw 'Requested output is unavailable.'}
$device=$devices[$Index]
$id=$device.GetType().GetField('Id',$instance).GetValue($device)
$audio.GetMethod('Play',$static).Invoke($null,[object[]]@($id))
Write-Output 'OK: Windows completed the short PCM tone on the selected output. Audibility must be confirmed by the listener.'
