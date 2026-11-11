param()
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('StantsiyaDesk-AI-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testDir)
try {
    $entry = Join-Path $testDir 'Runner.cs'
    $binary = Join-Path $testDir 'AiSmoke.exe'
    $fixture = @'
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AliceDesk;
namespace AliceDesk {
    internal static class Runner {
        [STAThread] static int Main(string[] args) {
            try {
            AiPage.SelfTest();
            AiLoopback.Verify();
            HaTlsChecks.Run();
            Application.EnableVisualStyles();
            var store = new ProfileStore(args[0]);
            using (var page = new AiPage(store.Active)) { page.CreateControl(); page.PerformLayout(); }
            Console.WriteLine("OK  AI fixtures, loopback HTTP and HTTPS/SNI, HA route selection, Windows controls, Quasar config");
            return 0;
            } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
'@
    [IO.File]::WriteAllText($entry, $fixture, [Text.UTF8Encoding]::new($false))
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $project 'src') -Filter '*.cs' | ForEach-Object FullName)
    $sources += Join-Path $PSScriptRoot 'ai-loopback.cs'
    $sources += Join-Path $PSScriptRoot 'ha-tls.cs'
    $sources += $entry
    & $compiler /nologo /langversion:5 /codepage:65001 /platform:x64 /main:AliceDesk.Runner "/out:$binary" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll /r:System.Net.Http.dll /r:System.Security.dll /r:System.Web.Extensions.dll /r:System.Xml.dll /r:System.Xml.Linq.dll /r:System.IO.Compression.dll /r:Microsoft.CSharp.dll /r:System.Management.dll @sources
    if ($LASTEXITCODE -ne 0) { throw 'AI self-test build failed.' }
    & $binary (Join-Path $testDir 'store')
    if ($LASTEXITCODE -ne 0) { throw 'AI self-test failed.' }
} finally {
    $path = [IO.Path]::GetFullPath($testDir)
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if (([IO.Path]::GetDirectoryName($path) -eq $parent) -and ([IO.Path]::GetFileName($path) -match '^StantsiyaDesk-AI-[0-9a-f]{32}$')) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
