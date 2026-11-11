[CmdletBinding()]
param([switch]$Test)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$frame = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $frame 'csc.exe'
if (!(Test-Path $csc)) { throw '64-bit .NET Framework 4.7.2 or newer is required.' }
if ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release -lt 461808) {throw '.NET Framework 4.7.2 or newer is required.'}
$null = New-Item -ItemType Directory -Force -Path $dist
$stage = Join-Path $dist ('.work-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'payload'
$null = New-Item -ItemType Directory -Force -Path $payload
function Compile([string]$output, [string[]]$references, [string[]]$extra, [string[]]$sources) {
    $options = @('/nologo','/target:winexe','/platform:x64','/optimize+','/warn:4','/langversion:5','/codepage:65001',
        ('/win32manifest:' + (Join-Path $root 'setup\app.manifest')),('/win32icon:' + (Join-Path $stage 'icon.ico')),('/out:' + $output))
    $options += $references | ForEach-Object { '/reference:' + (Join-Path $frame ($_ + '.dll')) }
    & $csc @options @extra @sources
    if ($LASTEXITCODE -ne 0) { throw ('Compiler failed for ' + $output) }
}
function RunCheck([string]$exe, [string]$mode, [string]$report) {
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
    $p = Start-Process -FilePath $exe -ArgumentList ($mode + ' "' + $report + '"') -WindowStyle Hidden -PassThru
    if (!$p.WaitForExit(180000)) { $p.Kill(); throw ('Timed out: ' + $mode) }
    if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Encoding UTF8 | Write-Host }
    if ($p.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report)) { throw ('Failed: ' + $mode + ' exit=' + $p.ExitCode) }
}
try {
    . (Join-Path $root 'setup\icon.ps1')
    New-ProductIcon (Join-Path $stage 'icon.ico')
    $shared = @('System','System.Core','System.Drawing','System.Windows.Forms')
    $refs = $shared + @('System.Web.Extensions','System.Security','System.Net.Http','System.Xml','System.Xml.Linq','System.IO.Compression','Microsoft.CSharp','System.Management')
    $files = @(Get-ChildItem (Join-Path $root 'src\*.cs') -File | Sort-Object Name | ForEach-Object { $_.FullName })
    $productInfo = Join-Path $root 'setup\ProductInfo.cs'
    $files += $productInfo
    $app = Join-Path $payload 'StantsiyaDesk.exe'
    Compile $app $refs @(('/resource:' + (Join-Path $root 'catalog\features.json') + ',features.json')) $files
    Copy-Item (Join-Path $root 'docs') $payload -Recurse
    Copy-Item (Join-Path $root 'catalog') $payload -Recurse
    Copy-Item (Join-Path $root 'LICENSE*') $payload
    Copy-Item (Join-Path $root 'README.md') $payload
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $stage 'payload.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
    $hashPath = Join-Path $stage 'payload.sha256'
    [IO.File]::WriteAllText($hashPath,(Get-FileHash $zip -Algorithm SHA256).Hash,[Text.Encoding]::ASCII)
    $setup = Join-Path $stage 'StantsiyaDesk-Setup-x64.exe'
    Compile $setup ($shared + @('System.IO.Compression','Microsoft.CSharp')) @(('/resource:' + $zip + ',payload.zip'),('/resource:' + $hashPath + ',payload.sha256')) @((Join-Path $root 'setup\Installer.cs'),$productInfo)
    if ($Test) {
        RunCheck $app '--self-test' (Join-Path $dist 'tests.txt')
        RunCheck $setup '--test-lifecycle' (Join-Path $dist 'installer-tests.txt')
    }
    Copy-Item -LiteralPath $setup -Destination $dist -Force
    Copy-Item -LiteralPath $zip -Destination (Join-Path $dist 'StantsiyaDesk-portable-x64.zip') -Force
    $lines = @('StantsiyaDesk-Setup-x64.exe','StantsiyaDesk-portable-x64.zip') | ForEach-Object {
        (Get-FileHash (Join-Path $dist $_) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_
    }
    [IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'),[string[]]$lines,[Text.Encoding]::ASCII)
    Write-Host ('Ready: ' + $dist)
} finally {
    $absoluteStage = [IO.Path]::GetFullPath($stage)
    $safeRoot = [IO.Path]::GetFullPath($dist).TrimEnd('\') + '\.work-'
    if ($absoluteStage.StartsWith($safeRoot,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $absoluteStage)) {
        Remove-Item -LiteralPath $absoluteStage -Recurse -Force
    }
}
