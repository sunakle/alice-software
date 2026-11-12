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
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AliceDesk;
namespace AliceDesk {
    internal static class Runner {
        static void CheckBridgePlan(AiPage page, Profile profile) {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type type = typeof(AiPage);
            var voice = (ComboBox)type.GetField("voice", flags).GetValue(page);
            var host = (ComboBox)type.GetField("bridgeHost", flags).GetValue(page);
            var baseField = (TextBox)type.GetField("hostBase", flags).GetValue(page);
            var save = type.GetMethod("SaveVoice", flags);
            voice.SelectedIndex = 1;
            host.SelectedIndex = 0;
            baseField.Text = "";
            bool rejected = false;
            try { save.Invoke(page, null); }
            catch (TargetInvocationException ex) {
                if (!(ex.InnerException is AiFailure) ||
                    ex.InnerException.Message.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) < 0)
                    throw;
                rejected = true;
            }
            if (!rejected || profile.Get("ai.voice", "stock") != "stock")
                throw new Exception("Localhost on the speaker must be rejected before saving the bridge plan.");

            host.SelectedIndex = 1;
            save.Invoke(page, null);
            if (profile.Get("ai.voice") != "bridge" || profile.Get("ai.bridge.host") != "server" ||
                profile.Get("ai.bridge.base") != "")
                throw new Exception("A bridge on the server must accept the local endpoint fallback.");

            host.SelectedIndex = 0;
            baseField.Text = "http://192.168.1.22:11434";
            save.Invoke(page, null);
            if (profile.Get("ai.bridge.host") != "speaker" ||
                profile.Get("ai.bridge.base") != "http://192.168.1.22:11434")
                throw new Exception("A speaker bridge must accept a LAN endpoint.");
        }
        [STAThread] static int Main(string[] args) {
            try {
            AiPage.SelfTest();
            AiLoopback.Verify();
            HaTlsChecks.Run();
            Application.EnableVisualStyles();
            var store = new ProfileStore(args[0]);
            store.Active.Set("ai.provider", "ollama");
            store.Active.Set("ai.ollama.base", "http://127.0.0.1:11434");
            using (var page = new AiPage(store.Active)) {
                page.CreateControl(); page.PerformLayout(); CheckBridgePlan(page, store.Active);
            }
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
