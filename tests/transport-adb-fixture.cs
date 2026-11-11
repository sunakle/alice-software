using System;
using System.IO;
using System.Text.RegularExpressions;
class AdbFixture {
    static int Main(string[] args) {
        string tmp=AppDomain.CurrentDomain.BaseDirectory, mode=File.ReadAllText(Path.Combine(tmp,"mode"));
        string call=string.Join(" ",args);
        File.AppendAllText(Path.Combine(tmp,"adb-calls"),call+Environment.NewLine);
        if(call=="connect 127.0.0.1:5555") { Console.WriteLine("connected to 127.0.0.1:5555"); return 0; }
        if(call=="devices") {
            Console.WriteLine("List of devices attached\nOTHER-TEST-DEVICE\tdevice");
            if(mode!="adb-missing") Console.WriteLine("127.0.0.1:5555\t"+(mode=="adb-offline"?"offline":mode=="adb-unauthorized"?"unauthorized":"device"));
            return 0;
        }
        if(call!="-s 127.0.0.1:5555 shell -T sh -s") return 1;
        string script=Console.In.ReadToEnd(); Match m=Regex.Match(script,@"ALICEDESK_[a-f0-9]{32}_");
        if(!m.Success || !script.Contains("printf 'HELLO'")) return 2;
        Console.WriteLine("HELLO\n"+m.Value+"0"); return 0;
    }
}
