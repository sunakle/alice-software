param()
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testDir=Join-Path $project 'dist/spotify-checks'
[void][IO.Directory]::CreateDirectory($testDir)
$binary=Join-Path $testDir 'SpotifyChecks.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $project 'src') -Filter '*.cs' | ForEach-Object FullName)
$sources+=Join-Path $PSScriptRoot 'spotify-checks.cs'
& $compiler /nologo /langversion:5 /codepage:65001 /platform:x64 /main:AliceDesk.SpotifyChecks "/out:$binary" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll /r:System.Net.Http.dll /r:System.Security.dll /r:System.Web.Extensions.dll /r:System.Xml.dll /r:System.Xml.Linq.dll /r:System.IO.Compression.dll /r:Microsoft.CSharp.dll /r:System.Management.dll @sources
if($LASTEXITCODE -ne 0){throw 'Spotify test build failed.'}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('sd-sp-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
try {
    & $binary $fixture
    if($LASTEXITCODE -ne 0){throw 'Spotify checks failed.'}
    Copy-Item -LiteralPath (Join-Path $fixture 'fixture.yaml') -Destination $testDir -Force
} finally {
    $absolute=[IO.Path]::GetFullPath($fixture)
    $parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if([IO.Path]::GetDirectoryName($absolute) -eq $parent -and [IO.Path]::GetFileName($absolute) -match '^sd-sp-[a-f0-9]{8}$' -and (Test-Path -LiteralPath $absolute)) {
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}
