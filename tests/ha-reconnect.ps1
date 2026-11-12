param()
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testDir=Join-Path ([IO.Path]::GetTempPath()) ('StantsiyaDesk-HA-reconnect-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testDir)
try {
    $binary=Join-Path $testDir 'ReconnectTest.exe'
    $sources=@(Get-ChildItem -LiteralPath (Join-Path $project 'src') -Filter '*.cs' | ForEach-Object FullName)
    $sources+=Join-Path $PSScriptRoot 'ha-reconnect.cs'
    & $compiler /nologo /langversion:5 /codepage:65001 /platform:x64 /main:AliceDesk.HomeReconnectChecks "/out:$binary" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll /r:System.Net.Http.dll /r:System.Security.dll /r:System.Web.Extensions.dll /r:System.Xml.dll /r:System.Xml.Linq.dll /r:System.IO.Compression.dll /r:Microsoft.CSharp.dll /r:System.Management.dll @sources
    if($LASTEXITCODE -ne 0){throw 'HA reconnect test build failed.'}
    & $binary (Join-Path $testDir 'store')
    if($LASTEXITCODE -ne 0){throw 'HA reconnect test failed.'}
} finally {
    $path=[IO.Path]::GetFullPath($testDir)
    $parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if([IO.Path]::GetDirectoryName($path) -eq $parent -and [IO.Path]::GetFileName($path) -match '^StantsiyaDesk-HA-reconnect-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
