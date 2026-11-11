# Optional interactive acceptance. Opens the installed Chrome normally on this PC.
# No arguments, no navigation, no changes to the browser or saved application profiles.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ApplicationPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
if (![Environment]::UserInteractive) { throw 'Run this from an interactive Windows session.' }
$asm=[Reflection.Assembly]::LoadFrom((Resolve-Path $ApplicationPath).Path)
$commands=$asm.GetType('AliceDesk.MediaCommands',$true)
$flags=[Reflection.BindingFlags]'NonPublic,Static'
$action=New-Object AliceDesk.DeskAction
$action.Id='chrome'
$action.Phrases='открой хром'
$action.Kind='exe'
$action.Target=$commands.GetMethod('Chrome',$flags).Invoke($null,@())
if (![IO.File]::Exists($action.Target)) {throw 'Chrome is not installed at its standard per-user or system path.'}
$actions=New-Object 'System.Collections.Generic.List[AliceDesk.DeskAction]'
$actions.Add($action)
$matched=$commands.GetMethod('Resolve',$flags).Invoke($null,[object[]]@($actions.PSObject.BaseObject,$action.Phrases))
$null=$commands.GetMethod('Validate',$flags).Invoke($null,[object[]]@($matched))
$approval=$commands.GetMethod('Fingerprint',$flags).Invoke($null,[object[]]@($matched))
$null=$commands.GetMethod('Run',$flags).Invoke($null,[object[]]@($matched,$approval))
Write-Output 'OK: phrase resolved, installed Chrome approved, Windows accepted the launch. Visual confirmation belongs to the owner.'
