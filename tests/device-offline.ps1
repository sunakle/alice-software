[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$AppExe)
$ErrorActionPreference = 'Stop'
$file = (Resolve-Path -LiteralPath $AppExe).Path
$app = [Reflection.Assembly]::LoadFrom($file)
$type = $app.GetType('AliceDesk.DevicePage', $true)
try {
    $type.GetMethod('SelfTest').Invoke($null, @())
    'PASS: device recognition, ABI/firmware gates, SSH trust handling, ADB selection, TTS frames, URI checks, encrypted backup and loopback TCP.'
} catch {
    throw $_.Exception.GetBaseException()
}
