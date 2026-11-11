using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Net;
using System.Net.Sockets;

// Reflection targets the transport in the actual packaged executable.
class TransportRunner {
    static Assembly app;
    static readonly BindingFlags Inner = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
    static Type TypeOf(string name) { return app.GetType("AliceDesk."+name,true); }
    static object Call(object target,string name,params object[] args) {
        Type type=target as Type ?? target.GetType();
        try { return type.GetMethod(name,Inner).Invoke(target is Type?null:target,args); }
        catch (TargetInvocationException e) { throw e.InnerException; }
    }
    static void Set(object instance,string name,object v) { instance.GetType().GetField(name,Inner).SetValue(instance,v); }
    static void Yes(bool ok,string label) { if(!ok) throw new Exception("Failed: "+label); Console.WriteLine("OK "+label); }
    static void Fails(Action action,string label,Func<Exception,bool> reason) {
        try { action(); } catch (Exception e) { Yes(reason(e),label+" ["+e.GetType().Name+": "+e.Message+"]"); return; }
        throw new Exception("Expected failure: "+label);
    }
    static int Main(string[] args) {
        try { Run(args); return 0; } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void Run(string[] args) {
        app=Assembly.LoadFrom(args[0]); string tmp=args[1]; int port=int.Parse(args[2]);
        string mode=Path.Combine(tmp,"mode"), file=Path.Combine(tmp,"remote-sources");
        Type transport=TypeOf("DeviceTransport"), backup=TypeOf("DeviceBackup");
        object d=Activator.CreateInstance(transport,true);
        Set(d,"Host","127.0.0.1"); Set(d,"Port",port); Set(d,"Store",Path.Combine(tmp,"host keys with spaces"));
        Set(d,"Binary",Call(transport,"SystemTool","ssh")); Set(d,"PrivateKey",Path.Combine(tmp,"client-allowed"));
        var ct=CancellationToken.None;
        Func<string,bool> approve=(msg)=>msg.Contains(File.ReadAllText(Path.Combine(tmp,"fingerprint")).Trim());
        Func<string,bool> deny=(msg)=>false;
        Action auth=()=>Call(d,"Authenticate",approve,ct);
        Fails(()=>Call(d,"Authenticate",deny,ct),"unknown host declined",e=>e is OperationCanceledException);
        auth(); Yes(true,"known server approved by independent SHA256 and client authenticated");
        Set(d,"PrivateKey",Path.Combine(tmp,"client-denied"));
        Fails(auth,"wrong client key",e=>e is IOException && e.Message.Contains("отклонил ключ"));
        Set(d,"PrivateKey",Path.Combine(tmp,"client-allowed"));
        File.WriteAllText(mode,"rotate");
        Fails(auth,"changed server key",e=>e is IOException && e.Message.Contains("ключ станции"));
        File.WriteAllText(mode,"normal"); auth();
        File.WriteAllText(mode,"stall");
        Fails(()=>Call(d,"Shell","printf 'HELLO'",ct,550,8192),"SSH timeout stops client",e=>e is TimeoutException);
        File.WriteAllText(mode,"normal");
        string source="/data/opt/etc/yse/sources";
        var inv=Call(TypeOf("DeviceInventory"),"Parse","compatible=yandex,station-lite2\nmodel=plum\nsku=YNDX-00026\nbuild=0.test.1\nelf=1 1\nmachine=40 0\nuid=0\nbinding="+new string('b',64));
        Func<object> read=()=>Call(backup,"Read",d,inv,source,ct);
        File.WriteAllText(file,"original-feed\n"); var original=read();
        string bak=(string)Call(original,"Save",Path.Combine(tmp,"backup")); original=Call(backup,"Load",bak);
        Yes(File.Exists(bak),"remote file read, verified and saved as DPAPI backup");
        File.WriteAllText(file,"new-feed\n"); var changed=read();
        File.WriteAllText(mode,"cut-read"); Fails(()=>read(),"interrupted backup not accepted",e=>e is IOException);
        File.WriteAllText(mode,"cut-before");
        Fails(()=>Call(original,"Restore",d,inv,changed,ct),"lost connection before restore commit",e=>e is IOException);
        Yes(File.ReadAllText(file)=="new-feed\n","original destination unchanged before commit");
        File.WriteAllText(mode,"cut-after");
        Fails(()=>Call(original,"Restore",d,inv,changed,ct),"lost acknowledgement after restore commit",e=>e is IOException);
        Yes(File.ReadAllText(file)=="original-feed\n","file was committed once before loss of acknowledgement");
        File.WriteAllText(mode,"normal"); var reconciled=read(); Call(original,"Restore",d,inv,reconciled,ct);
        Yes(File.ReadAllText(file)=="original-feed\n","re-read state and safely retried");
        var closed = new TcpListener(IPAddress.Loopback,0); closed.Start(); int closedPort=((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
        Fails(()=>Call(transport,"Tcp",IPAddress.Loopback,closedPort,ct),"refused loopback TCP",e=>e.GetBaseException() is SocketException);
        Set(d,"Adb",true); Set(d,"Binary",Path.Combine(tmp,"FixtureAdb.exe")); Set(d,"Port",5555);
        File.WriteAllText(mode,"adb-normal"); auth();
        string[] argv=File.ReadAllLines(Path.Combine(tmp,"adb-calls"));
        Yes(Array.Exists(argv,l=>l.StartsWith("-s 127.0.0.1:5555 shell -T sh -s")) && !Array.Exists(argv,l=>l.Contains("OTHER-TEST-DEVICE")),"ADB child process addresses only selected serial among two");
        File.WriteAllText(mode,"adb-offline"); Fails(auth,"ADB offline",e=>e.Message.Contains("offline"));
        File.WriteAllText(mode,"adb-unauthorized"); Fails(auth,"ADB unauthorized",e=>e.Message.Contains("не разрешила"));
        File.WriteAllText(mode,"adb-missing"); Fails(auth,"ADB missing intended device",e=>e.Message.Contains("не видит"));
    }
}
