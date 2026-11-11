param(
    [Parameter(Mandatory=$true)][string]$ApplicationPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$application = (Resolve-Path -LiteralPath $ApplicationPath).Path
$temp = Join-Path ([IO.Path]::GetTempPath()) ('StantsiyaDesk-livepaint-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temp
try {
    $probe = Join-Path $temp 'probe.exe'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $manifest = Join-Path $PSScriptRoot '..\setup\app.manifest'
    & $compiler /nologo /target:exe "/out:$probe" "/win32manifest:$manifest" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $PSScriptRoot 'live-paint.cs')
    if ($LASTEXITCODE -ne 0) { throw "Could not compile the visible-window test." }
    $arguments = @($application,$temp)
    if ($OutputDirectory) {
        $directory = [IO.Path]::GetFullPath($OutputDirectory)
        $null = [IO.Directory]::CreateDirectory($directory)
        $arguments += $directory
    }
    & $probe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Visible-window test failed (exit $LASTEXITCODE)." }
} finally {
    $resolved = (Resolve-Path -LiteralPath $temp).Path
    if ((Split-Path $resolved -Parent) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
        (Split-Path $resolved -Leaf) -match '^StantsiyaDesk-livepaint-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
