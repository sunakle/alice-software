param(
    [Parameter(Mandatory=$true)][string]$ApplicationPath
)
# Run with Windows PowerShell 5.1 in a regular interactive user session.
# No installed program is launched; the test EXE writes one marker in its own temporary directory.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('StantsiyaDesk-media-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($workspace)
try {
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $ApplicationPath).Path)
    $compile = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $source = Join-Path $workspace 'Marker.cs'
    $markerExe = Join-Path $workspace 'AllowedHelper.exe'
    $result = Join-Path $workspace 'invoked.txt'
    [IO.File]::WriteAllText($source, @'
using System;
using System.IO;
class Marker {
    static void Main() {
        File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "invoked.txt"), "1");
    }
}
'@)
    & $compile /nologo /target:winexe "/out:$markerExe" $source
    if ($LASTEXITCODE -ne 0) { throw 'Fixture executable failed to build.' }
    $commands = $assembly.GetType('AliceDesk.MediaCommands', $true)
    $binding = [Reflection.BindingFlags]'NonPublic,Static'
    $record = [Activator]::CreateInstance($assembly.GetType('AliceDesk.DeskAction',$true))
    $record.Id='test-target'
    $record.Phrases='open test'
    $record.Kind='exe'
    $record.Target=$markerExe
    [void]$commands.GetMethod('Validate',$binding).Invoke($null,@($record))
    $approval = $commands.GetMethod('Fingerprint',$binding).Invoke($null,@($record))
    $run = $commands.GetMethod('Run',$binding)
    [void]$run.Invoke($null,@($record,$approval))
    $timeout = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath $result) -and $timeout.ElapsedMilliseconds -lt 6000) { Start-Sleep -Milliseconds 60 }
    if (!(Test-Path -LiteralPath $result) -or [IO.File]::ReadAllText($result) -ne '1') { throw 'Approved test EXE did not run once.' }
    $changed=$false
    for ($attempt=0; $attempt -lt 20 -and !$changed; $attempt++) {
        try {
            $stream=[IO.File]::Open($markerExe,[IO.FileMode]::Append,[IO.FileAccess]::Write)
            try { $stream.WriteByte(0) } finally { $stream.Dispose() }
            $changed=$true
        } catch [IO.IOException] { Start-Sleep -Milliseconds 100 }
    }
    if (!$changed) { throw 'Fixture EXE could not be changed.' }
    $blocked=$false
    try { [void]$run.Invoke($null,@($record,$approval)) }
    catch { if ($_.Exception.GetBaseException() -is [InvalidOperationException]) { $blocked=$true } else { throw } }
    if (!$blocked -or [IO.File]::ReadAllText($result) -ne '1') { throw 'Old approval accepted after a binary change.' }
    [AliceDesk.MediaPage]::SelfTest()
    Write-Output 'OK: real Windows EXE launch, changed binary rejected, phrase/HA/PCM tests and audio endpoint enumeration.'
} finally {
    # Delete only the individual files created above, under our fresh temporary directory.
    foreach ($name in @('Marker.cs','AllowedHelper.exe','invoked.txt')) {
        $f=Join-Path $workspace $name
        if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force }
    }
    [IO.Directory]::Delete($workspace, $false)
}
