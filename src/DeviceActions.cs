using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AliceDesk
{
    internal static class DeviceTts
    {
        private static byte[] Var(int n) {
            var bytes=new List<byte>(); while(n>=128) { bytes.Add((byte)((n & 127)|128)); n >>=7; } bytes.Add((byte)n); return bytes.ToArray();
        }
        private static byte[] Join(params byte[][] pieces) { return pieces.SelectMany(a=>a).ToArray(); }
        private static byte[] Int(int k,int n) { return Join(Var(k*8),Var(n)); }
        private static byte[] Field(int k,byte[] data) { return Join(Var(k*8+2),Var(data.Length),data); }
        private static readonly byte[] Alice = Encoding.ASCII.GetBytes("AliceCapability");
        private static byte[] Frame(int k, byte[] msg) {
            byte[] env=Join(Field(252,Join(Field(7,Alice),Field(k,msg))),Int(253,210));
            return Join(BitConverter.GetBytes(env.Length),env);
        }
        internal static byte[] Request(string text)
        {
            if(string.IsNullOrWhiteSpace(text)||text.Length>2000) throw new ArgumentException("Введите от 1 до 2000 символов.");
            byte[] directive=Join(Field(1,DeviceTransport.Utf.GetBytes(text)),Int(2,1));
            return Join(Frame(20,Field(1,Alice)),Frame(8,Join(Int(1,18),Int(3,1),Field(13,directive))));
        }
        private static int Number(byte[] data,ref int off) {
            uint val=0; for(int shift=0;shift<=28 && off<data.Length;shift+=7) { byte n=data[off++]; if (shift==28 && n>7) break; val|=(uint)(n&127)<<shift; if(n<128)return (int)val; }
            throw new InvalidDataException("Повреждён пакет ответа IPC.");
        }
        private static Dictionary<int,object> Fields(byte[] data) {
            var d=new Dictionary<int,object>(); int i=0;
            while(i<data.Length) { int key=Number(data,ref i), tag=key>>3, kind=key&7; if(tag==0) throw new InvalidDataException();
                if(kind==0) { d[tag]=Number(data,ref i); continue; }
                int len=kind==2 ? Number(data,ref i) : (kind==1?8:(kind==5?4:-1));
                if(len<0||len>data.Length-i)throw new InvalidDataException();
                if(kind==2) { var bytes=new byte[len]; Buffer.BlockCopy(data,i,bytes,0,len); d[tag]=bytes; } i+=len;
            }
            return d;
        }
        private static byte[] Bytes(Dictionary<int,object> d,int k) { object v; return d.TryGetValue(k,out v) ? v as byte[] : null; }
        private static int Code(Dictionary<int,object> d,int k) { object v; return d.TryGetValue(k,out v)&&v is int?(int)v:-1; }
        internal static int Event(byte[] packet) {
            var env=Fields(packet);
            var remoteData=Bytes(env,252);
            if(remoteData==null)return -1;
            var remote=Fields(remoteData);
            var name=Bytes(remote,7);
            if(Code(env,253)!=3 && (Code(env,253)!=210 || name==null || !name.SequenceEqual(Alice)))return -1;
            var eventData=Bytes(remote,18); return eventData==null?-1:Code(Fields(eventData),1);
        }
        private static byte[] Exact(Stream st,int size) {
            byte[] data=new byte[size]; int pos=0;
            while(pos<size) { int n=st.Read(data,pos,size-pos); if(n==0)throw new EndOfStreamException("Канал озвучки закрылся без подтверждения."); pos+=n; } return data;
        }
        internal static void Speak(DeviceTransport transport,string text,CancellationToken cancel)
        {
            if(transport.Adb)throw new InvalidOperationException("Для облачной озвучки нужен защищённый SSH.");
            byte[] input=Request(text);
            using(var proc=DeviceTransport.Open(transport.Binary, transport.SshArgs("PATH=/data/opt/bin:/opt/bin:/usr/bin:/bin exec socat STDIO TCP:127.0.0.1:9875,connect-timeout=2")))
            {
                proc.BeginErrorReadLine(); // Do not store private messages from the provider.
                bool started=false;
                try {
                    proc.StandardInput.BaseStream.Write(input,0,input.Length); proc.StandardInput.BaseStream.Flush();
                    var read=Task.Run(()=>{ while(true) { int len=BitConverter.ToInt32(Exact(proc.StandardOutput.BaseStream,4),0);
                        if(len<1||len>512*1024)throw new InvalidDataException("Некорректная длина события IPC.");
                        int ev=Event(Exact(proc.StandardOutput.BaseStream,len)); if(ev==6)started=true;
                        if(ev==1 && started)return; if(ev==4)throw new IOException("Голосовая служба сообщила об ошибке. Проверьте авторизацию Яндекса и интернет на станции.");
                    }});
                    if(!read.Wait(35000,cancel))throw new TimeoutException("Нет подтверждения окончания речи. Колонка могла уже произнести фразу; автоматического повтора нет.");
                } finally { DeviceTransport.Kill(proc); }
            }
        }
    }

    internal sealed class DeviceBackup
    {
        internal const string Sources="/data/opt/etc/yse/sources", Launch="/system/vendor/quasar/launch.json";
        internal string Target, Stamp, Mode, Uid, Gid, Digest;
        internal byte[] Data;
        private static string RequireTarget(string path)
        {
            if(path!=Sources && path!=Launch) throw new InvalidDataException("Путь не входит в разрешённый список копирования.");
            return path;
        }
        internal static DeviceBackup Read(DeviceTransport d,DeviceInventory inv,string path,CancellationToken c)
        {
            RequireTarget(path);
            string script=@"set -e
f="+path+@"
[ -f ""$f"" ] && [ ! -L ""$f"" ] || exit 91
[ ""$(readlink -f ""$f"")"" = ""$f"" ] || exit 92
n=$(wc -c < ""$f""); [ ""$n"" -le 262144 ] || exit 93
stat -c '%a %u %g' ""$f""
sha256sum ""$f"" | cut -d' ' -f1
base64 ""$f""
sha256sum ""$f"" | cut -d' ' -f1";
            var lines=d.Shell(script,c,17000,450000).Trim().Split('\n');
            if(lines.Length<3)throw new IOException("Не получены целостная копия и метаданные.");
            string[] meta=lines[0].Trim().Split(' ');
            if(meta.Length!=3||!Regex.IsMatch(meta[0],@"\A[0-7]{3,4}\z")||!meta.Skip(1).All(v=>Regex.IsMatch(v,@"\A\d{1,9}\z")))throw new InvalidDataException("Некорректные права файла.");
            var bytes=Convert.FromBase64String(string.Concat(lines.Skip(2).Take(lines.Length-3)));
            string hash=DeviceTransport.Hash(bytes);
            if(hash!=lines[1].Trim()||hash!=lines.Last().Trim())throw new IOException("Хеш переданной копии не совпал.");
            return new DeviceBackup{Target=path,Stamp=inv.Stamp,Mode=meta[0],Uid=meta[1],Gid=meta[2],Digest=hash,Data=bytes};
        }
        internal string Save(string dir)
        {
            Directory.CreateDirectory(dir);
            string file=Path.Combine(dir,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8)+".stationbak");
            using(var mem=new MemoryStream()) using(var writer=new BinaryWriter(mem)) {
                writer.Write(1); foreach(string field in new[]{Target,Stamp,Mode,Uid,Gid,Digest})writer.Write(field);
                writer.Write(Data.Length); writer.Write(Data); writer.Flush();
                byte[] protectedBytes=ProtectedData.Protect(mem.ToArray(),null,DataProtectionScope.CurrentUser);
                using(var dest=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {dest.Write(protectedBytes,0,protectedBytes.Length);dest.Flush(true);}
            }
            return file;
        }
        internal static DeviceBackup Load(string file)
        {
            if(new FileInfo(file).Length>400000)throw new InvalidDataException("Копия слишком велика.");
            byte[] plain=ProtectedData.Unprotect(File.ReadAllBytes(file),null,DataProtectionScope.CurrentUser);
            using(var r=new BinaryReader(new MemoryStream(plain))) {
                if(r.ReadInt32()!=1)throw new InvalidDataException("Неизвестный формат копии.");
                var b=new DeviceBackup {Target=RequireTarget(r.ReadString()), Stamp=r.ReadString(), Mode=r.ReadString(), Uid=r.ReadString(), Gid=r.ReadString(), Digest=r.ReadString()};
                int len=r.ReadInt32(); if(len<0||len>262144)throw new InvalidDataException("Испорченная копия.");
                b.Data=r.ReadBytes(len);
                if(b.Data.Length!=len || r.BaseStream.Position!=r.BaseStream.Length || b.Digest!=DeviceTransport.Hash(b.Data) ||
                    !Regex.IsMatch(b.Mode,@"\A[0-7]{3,4}\z") || !Regex.IsMatch(b.Uid,@"\A\d{1,9}\z") || !Regex.IsMatch(b.Gid,@"\A\d{1,9}\z"))throw new InvalidDataException("Контрольная сумма или права копии повреждены.");
                return b;
            }
        }
        internal void Restore(DeviceTransport d,DeviceInventory current,DeviceBackup before,CancellationToken cancel)
        {
            if(d.Adb||!current.Bound||current.Value("uid")!="0"||current.Stamp!=Stamp||Target!=Sources||before.Target!=Target || before.Stamp!=Stamp)
                throw new InvalidOperationException("Для этой копии, модели или соединения восстановление недоступно.");
            if(before.Digest==Digest && before.Mode==Mode && before.Uid==Uid && before.Gid==Gid)return;
            // Target is a fixed regular file; only a same-directory replacement is allowed.
            string script=@"set -eu
umask 077
f="+Sources+@"
[ ! -L ""$f"" ] && [ ""$(readlink -f ""$f"")"" = ""$f"" ] && [ -f ""$f"" ] || exit 93
lock=""$f.alicedesk-lock""
mkdir ""$lock"" || exit 94
tmp=
cleanup() { [ -z ""$tmp"" ] || rm -f ""$tmp""; rmdir ""$lock""; }
trap cleanup EXIT
trap 'exit 95' HUP INT TERM
[ ""$(sha256sum ""$f"" | cut -d' ' -f1)"" = "+before.Digest+@" ] || exit 96
[ ""$(stat -c '%a %u %g' ""$f"")"" = '"+before.Mode+" "+before.Uid+" "+before.Gid+@"' ] || exit 96
tmp=$(mktemp ""$f.alicedesk.XXXXXX"")
base64 -d > ""$tmp"" <<'ALICEDESK_B64'
"+Convert.ToBase64String(Data)+@"
ALICEDESK_B64
[ ""$(sha256sum ""$tmp"" | cut -d' ' -f1)"" = "+Digest+@" ] || exit 97
chown "+Uid+":"+Gid+@" ""$tmp""
chmod "+Mode+@" ""$tmp""
mv -f ""$tmp"" ""$f""
tmp=
sync";
            d.Shell(script,cancel,22000);
            DeviceBackup after=Read(d,current,Target,cancel);
            if(after.Digest!=Digest||after.Mode!=Mode||after.Uid!=Uid||after.Gid!=Gid) throw new IOException("Контроль файла после восстановления не прошёл.");
        }
    }
}
