[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$bash = Join-Path $env:ProgramFiles 'Git\bin\bash.exe'
if (!(Test-Path -LiteralPath $bash)) { throw 'Git for Windows is required for the shell fixture.' }
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('StantsiyaDesk-led-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testDirectory
try {
    $fixture = Join-Path $testDirectory 'fixture.exe'
    $deviceLight = Join-Path $PSScriptRoot '..\src\DeviceLight.cs'
    & $compiler /nologo /target:exe "/out:$fixture" $deviceLight (Join-Path $PSScriptRoot 'led-script-fixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile the LED script fixture.' }
    $generated = Join-Path $testDirectory 'generated.sh'
    & $fixture $generated
    if ($LASTEXITCODE -ne 0) { throw 'Could not generate the LED script.' }
    $unixDirectory = (& $bash -c 'cygpath -u "$1"' _ $testDirectory).Trim()
    $script = [IO.File]::ReadAllText($generated)
    $script = [regex]::Replace($script, '(?m)^stock\(\) \{.*$', 'stock() { cat "$1/trigger"; }')
    $script = [regex]::Replace($script, '(?m)^unmuted\(\) \{.*$', 'unmuted() { return 0; }')
    $script = $script.Replace('/sys/class/leds/plum:$color:ring$zone', ($unixDirectory + '/leds/${color}_${zone}'))
    $script = $script.Replace('/tmp/alicedesk-led', ($unixDirectory + '/alicedesk-led'))
    # Git for Windows has no setsid binary. This fixture substitutes an exec.
    $bin = Join-Path $testDirectory 'bin'
    $null = New-Item -ItemType Directory -Path $bin
    [IO.File]::WriteAllText((Join-Path $bin 'setsid'), ('#!/bin/sh' + [char]10 + 'exec "$@"' + [char]10), [Text.UTF8Encoding]::new($false))
    $env:PATH = $bin + [IO.Path]::PathSeparator + $env:PATH
    foreach ($zone in 1..3) {
        foreach ($color in @('red','green','blue')) {
            $dir = Join-Path $testDirectory ("leds\{0}_{1}" -f $color,$zone)
            $null = New-Item -ItemType Directory -Force -Path $dir
            [IO.File]::WriteAllText((Join-Path $dir 'trigger'), ('led_ring_fb' + [char]10))
            [IO.File]::WriteAllText((Join-Path $dir 'brightness'), ('7' + [char]10))
        }
    }
    function CheckLights {
        foreach ($zone in 1..3) {
            foreach ($color in @('red','green','blue')) {
                $dir = Join-Path $testDirectory ("leds\{0}_{1}" -f $color,$zone)
                if ((Get-Content -LiteralPath (Join-Path $dir 'trigger') -Raw).Trim() -ne 'led_ring_fb' -or
                    (Get-Content -LiteralPath (Join-Path $dir 'brightness') -Raw).Trim() -ne '7') {
                    throw "LED fixture was not restored: $color $zone"
                }
            }
        }
        if (Test-Path -LiteralPath (Join-Path $testDirectory 'alicedesk-led-owner')) { throw 'LED lock was not released.' }
    }
    [IO.File]::WriteAllText($generated, $script, [Text.UTF8Encoding]::new($false))
    & $bash $generated
    if ($LASTEXITCODE -ne 0) { throw "Normal LED fixture failed with exit $LASTEXITCODE." }
    CheckLights
    $trapLine = 'trap restore EXIT'
    if (!$script.Contains($trapLine)) { throw 'Lock acquisition injection point not found.' }
    $earlyInterrupt = $script.Replace($trapLine, ('kill -KILL $$' + [char]10 + $trapLine))
    [IO.File]::WriteAllText($generated, $earlyInterrupt, [Text.UTF8Encoding]::new($false))
    & $bash $generated
    if ($LASTEXITCODE -ne 87) { throw "Lock interruption returned $LASTEXITCODE instead of 87." }
    CheckLights
    $writeLine = 'echo "$val" > "$d/brightness"'
    if (!$script.Contains($writeLine)) { throw 'Worker injection point not found.' }
    $interrupted = $script.Replace($writeLine, ($writeLine + [char]10 + '  kill -KILL $$'))
    [IO.File]::WriteAllText($generated, $interrupted, [Text.UTF8Encoding]::new($false))
    & $bash $generated
    if ($LASTEXITCODE -ne 87) { throw "Interrupted LED fixture returned $LASTEXITCODE instead of 87." }
    CheckLights
    $completeLine = 'touch "$base/complete"'
    if (!$script.Contains($completeLine)) { throw 'Completion injection point not found.' }
    $lateInterrupt = $script.Replace($completeLine, ('kill -KILL $$' + [char]10 + $completeLine))
    [IO.File]::WriteAllText($generated, $lateInterrupt, [Text.UTF8Encoding]::new($false))
    & $bash $generated
    if ($LASTEXITCODE -ne 87) { throw "Late interruption returned $LASTEXITCODE instead of 87." }
    CheckLights
    $muteFile = $unixDirectory + '/muted'
    $muteScript = $script.Replace('unmuted() { return 0; }', ('unmuted() { [ ! -f "' + $muteFile + '" ]; }'))
    $loop = 'for tick in 1 2 3 4 5 6 7 8;'
    if (!$muteScript.Contains($loop)) { throw 'Mute injection point not found.' }
    $muteScript = $muteScript.Replace($loop, ('touch "' + $muteFile + '"' + [char]10 + $loop))
    [IO.File]::WriteAllText($generated, $muteScript, [Text.UTF8Encoding]::new($false))
    & $bash $generated
    if ($LASTEXITCODE -ne 87) { throw "Mute interruption returned $LASTEXITCODE instead of 87." }
    Remove-Item -LiteralPath (Join-Path $testDirectory 'muted')
    CheckLights
    $lock = Join-Path $testDirectory 'alicedesk-led-owner'
    [IO.File]::WriteAllText($lock, 'another active operation')
    [IO.File]::WriteAllText($generated, $script, [Text.UTF8Encoding]::new($false))
    & $bash $generated
    if ($LASTEXITCODE -ne 87) { throw "Contended LED fixture returned $LASTEXITCODE instead of 87." }
    if ((Get-Content -Raw -LiteralPath $lock) -ne 'another active operation') {
        throw 'A concurrent operation lost ownership of its LED lock.'
    }
    Remove-Item -LiteralPath $lock
    CheckLights
    $null = New-Item -ItemType Directory -Path $lock
    [IO.File]::WriteAllText((Join-Path $lock 'base'), 'old active operation')
    & $bash $generated
    if ($LASTEXITCODE -ne 87) { throw "Old directory lock returned $LASTEXITCODE instead of 87." }
    if ((Get-Content -Raw -LiteralPath (Join-Path $lock 'base')) -ne 'old active operation') {
        throw 'A previous operation lost its LED lock.'
    }
    Remove-Item -LiteralPath (Join-Path $lock 'base')
    Remove-Item -LiteralPath $lock
    CheckLights
    Write-Output 'OK: LED state is restored on normal completion and forced worker termination.'
} finally {
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    $root = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\StantsiyaDesk-led-'
    if ($resolved.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
