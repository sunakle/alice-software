using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;

namespace AliceDesk
{
    internal static class DeviceChecks
    {
        static void Require(bool value,string name){if(!value)throw new InvalidOperationException("Проверка станции: "+name);}
        static void Reject(Action action) {try{action();}catch{return;}throw new InvalidOperationException("Принят неподходящий ввод.");}
        static byte[] Hex(string s) {return Enumerable.Range(0,s.Length/2).Select(i=>Convert.ToByte(s.Substring(i*2,2),16)).ToArray();}
        internal static string Fixture(){
            return "compatible=yandex,station-lite2 amlogic,axg\nmodel=Plum\nsku=YNDX-00026\nbuild="+DeviceInventory.KnownBuild+
                "\nelf=1 1\nmachine=40 0\nfloat=hard\nuid=0\nrings=9\nprocesses=maind\nipc=listen\ntools=socat setsid nohup\nbinding="+new string('a',64);
        }
        internal static void Run()
        {
            string catalogItems=String.Join(",",Enumerable.Range(0,30).Select(i=>"{\"id\":\"item"+i+"\",\"name\":\"Item\"}"));
            string catalogPrefix="{\"schema\":1,\"documentation\":{\"defaultSpace\":\"space\",\"materialLicense\":\"license\"},\"features\":[";
            Require(Catalog.Parse(catalogPrefix+catalogItems+"]}").features.Length==30,"чтение исправного каталога");
            Reject(()=>Catalog.Parse("{\"schema\":1,\"features\":["+catalogItems+"]}"));
            Reject(()=>Catalog.Parse(catalogPrefix+"null,"+catalogItems+"]}"));
            Reject(()=>Catalog.Parse("{broken"));
            string lightSnapshot=String.Join("\n",Enumerable.Range(1,3)
                .SelectMany(zone=>new[]{"red","green","blue"}.Select(color=>zone+" "+color+" 7 led_ring_fb")));
            Require(DeviceLight.ValidateSnapshot(lightSnapshot)==lightSnapshot,"проверка девяти каналов света");
            Reject(()=>DeviceLight.ValidateSnapshot(lightSnapshot.Replace("3 blue 7 led_ring_fb","1 red 7 led_ring_fb")));
            Reject(()=>DeviceLight.ValidateSnapshot(lightSnapshot.Replace("3 blue 7 led_ring_fb","3 blue 256 led_ring_fb")));
            Reject(()=>DeviceLight.PulseScript(lightSnapshot+"\\nEOF"));
            var facts=DeviceInventory.Parse(Fixture());
            Require(facts.Bound&&facts.Ready&&facts.LedReady&&facts.TtsReady,"условия аппаратного профиля");
            var unknown=DeviceInventory.Parse("model=Some other model\nsku=YNDX-00026");
            Require(!unknown.Ready&&!unknown.TtsReady,"один SKU не разрешает работу");
            Require(!DeviceInventory.Parse(Fixture().Replace("machine=40 0","machine=183 0")).TtsReady,"несовместимый ABI");
            Require(!DeviceInventory.Parse(Fixture().Replace("float=hard","float=soft")).TtsReady,"другая ARM float ABI");
            var upgraded=DeviceInventory.Parse(Fixture().Replace(DeviceInventory.KnownBuild,"1.22.333"));
            Require(!upgraded.LedReady&&facts.Stamp!=upgraded.Stamp,"инвалидация сборки");
            Require(!DeviceInventory.Parse(Fixture().Replace("rings=9","rings=8")).LedReady,"неполное дерево LED");
            Require(!DeviceInventory.Parse(Fixture().Replace("YNDX-00026","YNDX-00027")).Bound,"несовпадение SKU");
            Require(!DeviceInventory.Parse(Fixture().Replace("YNDX-00026","")).LedReady,"неизвестный SKU блокирует запись света");
            Require(!DeviceInventory.Parse(Fixture().Replace("yandex,station-lite2","yandex,station-midi")).Plum,"имя не перебивает противоречащую плату");
            var report=DeviceInventory.Parse(Fixture()+"\nserial=SECRET-123\ncookie=abc\nmodel=abc").Report();
            Require(!report.Contains("SECRET")&&!report.Contains(new string('a',64))&&!report.Contains("cookie="),"закрытые поля исключены");
            Require(DevicePage.Query("libusb-1.0")=="libusb-1.0","валидное имя пакета");Reject(()=>DevicePage.Query("x;reboot"));
            Reject(()=>DeviceTransport.Resolve("-oProxyCommand=bad",CancellationToken.None));
            Require(DeviceTransport.Resolve("127.0.0.1",CancellationToken.None).Equals(IPAddress.Loopback),"loopback");
            DeviceTransport.RequireSerial("List of devices attached\nphone device\n192.168.1.8:5555 device\n","192.168.1.8:5555");
            Reject(()=>DeviceTransport.RequireSerial("phone device\n192.168.1.8:5555 unauthorized","192.168.1.8:5555"));
            Reject(()=>DeviceTransport.RequireSerial("phone device\n192.168.1.8:5555 offline","192.168.1.8:5555"));
            Reject(()=>DeviceTransport.RequireSerial("phone device","192.168.1.8:5555"));
            Require(DeviceTransport.Failure("REMOTE HOST IDENTIFICATION HAS CHANGED","",255).Contains("ключ"),"смена ключа SSH");
            Reject(()=>DeviceTransport.ParseScan("127.0.0.1 ssh-ed25519 invalid-data"));
            var scanned=DeviceTransport.ParseScan("127.0.0.1 ssh-ed25519 "+Convert.ToBase64String(new byte[32]));
            Require(scanned.Key.StartsWith("alicedesk-device ssh-ed25519 ")&&scanned.Value.Contains("SHA256:"),"отпечаток");
            string compound=BitConverter.ToString(DeviceTts.Request("Привет!")).Replace("-","").ToLowerInvariant();
            Require(compound.EndsWith("31000000e20f2a3a0f416c6963654361706162696c6974794217081218016a110a0dd09fd180d0b8d0b2d0b5d182211001e80fd201"),"контрольный кадр TTS");
            Require(DeviceTts.Event(Hex("e20f059201020806e80f03"))==6,"событие начала");
            Require(DeviceTts.Event(Hex("e20f059201020801e80f03"))==1,"событие завершения");
            Reject(()=>DeviceTts.Event(new byte[]{0x80}));Reject(()=>DeviceTts.Request(new string('x',2001)));
            Require(DeviceAudio.RadioUrl("https://radio.example/stream")=="https://radio.example/stream","прямой поток HTTPS");
            Reject(()=>DeviceAudio.RadioUrl("https://user:password@radio.example/stream"));Reject(()=>DeviceAudio.RadioUrl("file://localhost/music"));
            Reject(()=>DeviceAudio.ValidateWave(new byte[45]));
            string dir=Path.Combine(Path.GetTempPath(),"station-device-check-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try{
                byte[] plain=DeviceTransport.Utf.GetBytes("example-fixture-private-token");
                var backup=new DeviceBackup{Stamp=facts.Stamp,Target=DeviceBackup.Sources,Mode="600",Uid="0",Gid="0",Digest=DeviceTransport.Hash(plain),Data=plain};
                string file=backup.Save(dir);Require(!DeviceTransport.Utf.GetString(File.ReadAllBytes(file)).Contains("fixture-private"),"шифрование копии");
                var restored=DeviceBackup.Load(file);Require(restored.Data.SequenceEqual(plain)&&restored.Mode=="600","содержимое и права копии");
                Reject(()=>restored.Restore(new DeviceTransport{Adb=true},facts,restored,CancellationToken.None));
                Reject(()=>restored.Restore(new DeviceTransport(),upgraded,restored,CancellationToken.None));
                File.WriteAllBytes(file,new byte[]{1,3,5});Reject(()=>DeviceBackup.Load(file));File.Delete(file);
            }finally{Directory.Delete(dir,false);}
            var listener=new TcpListener(IPAddress.Loopback,0);
            try{listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;DeviceTransport.Tcp(IPAddress.Loopback,port,CancellationToken.None);}
            finally{listener.Stop();}
        }
    }
}
