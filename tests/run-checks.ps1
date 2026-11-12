[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ApplicationPath,
    [switch]$Visual
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'Run this script using Windows PowerShell 5.1.'
}

$application = (Resolve-Path -LiteralPath $ApplicationPath).Path
$shell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('StantsiyaDesk-checks-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$failed = New-Object 'System.Collections.Generic.List[string]'
$skipped = New-Object 'System.Collections.Generic.List[string]'

function Run-Check([string]$Name, [string]$File, [string[]]$Arguments) {
    Write-Host ('Running: ' + $Name)
    try {
        & $shell -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $File) @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw ('Exit code ' + $LASTEXITCODE)
        }
        Write-Host ('PASS: ' + $Name)
    } catch {
        $failed.Add($Name)
        Write-Warning ('FAIL: ' + $Name + ' - ' + $_.Exception.Message)
    }
}

try {
    # Run each script in its own Windows PowerShell process. Several scripts set
    # process-wide WinForms options and load the application assembly.
    Run-Check 'AI, loopback HTTP and local TLS' 'ai-smoke.ps1' @()
    Run-Check 'Home Assistant reconnection and acknowledgements' 'ha-reconnect.ps1' @()
    Run-Check 'Device fixtures and encrypted backups' 'device-offline.ps1' @('-AppExe', $application)
    $bash = Join-Path $env:ProgramFiles 'Git\bin\bash.exe'
    if (Test-Path -LiteralPath $bash) {
        Run-Check 'LED recovery shell fixture' 'led-watchdog.ps1' @()
    } else {
        $skipped.Add('LED recovery shell fixture')
        Write-Warning 'SKIP: LED recovery shell fixture requires Git for Windows.'
    }
    Run-Check 'Windows commands and media' 'media-windows.ps1' @('-ApplicationPath', $application)
    Run-Check 'Window navigation and section search' 'ui-layout.ps1' @('-ApplicationPath', $application, '-OutputDirectory', (Join-Path $temp 'layout'))
    Run-Check 'Compact window layout' 'ui-layout.ps1' @('-ApplicationPath', $application, '-OutputDirectory', (Join-Path $temp 'compact'), '-Width', '880', '-Height', '620')
    Run-Check 'Window controls' 'window-chrome.ps1' @('-ApplicationPath', $application)

    if ($Visual) {
        # These capture a temporary test window on the unlocked desktop.
        Run-Check 'Visible overview painting' 'live-paint.ps1' @('-ApplicationPath', $application)
        Run-Check 'Visible page and tab transitions' 'switch-frames.ps1' @('-ApplicationPath', $application)
        Run-Check 'Visible tab backgrounds' 'tab-strip.ps1' @('-ApplicationPath', $application)
    }
    if ($failed.Count -gt 0) {
        throw ('Checks failed: ' + ($failed -join ', '))
    }
    if ($skipped.Count -gt 0) {
        Write-Host ('Checks passed with skips: ' + ($skipped -join ', '))
    } else {
        Write-Host 'All selected checks passed.'
    }
} finally {
    $path = [IO.Path]::GetFullPath($temp)
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($path) -eq $parent -and
        [IO.Path]::GetFileName($path) -match '^StantsiyaDesk-checks-[0-9a-f]{32}$' -and
        (Test-Path -LiteralPath $path)) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
