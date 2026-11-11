using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AliceDesk
{
    internal sealed class DeviceTransport
    {
        internal string Host, Binary, PrivateKey, Store;
        internal int Port;
        internal bool Adb;
        internal string Serial { get { return (Host.Contains(":") ? "[" + Host + "]" : Host) + ":" + Port; } }
        internal string KnownHosts { get { return Path.Combine(Store, Hash(Encoding.UTF8.GetBytes(Serial)) + ".hosts"); } }
        internal static readonly Encoding Utf = new UTF8Encoding(false);
        internal static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        internal static string SystemTool(string name)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", name + ".exe");
            if (!File.Exists(path)) throw new IOException("В Windows отсутствует " + name + ". Установите компонент «Клиент OpenSSH». Ссылка есть в разделе подключения.");
            return path;
        }
        internal static IPAddress Resolve(string name, CancellationToken cancel)
        {
            name = name.Trim();
            if (name.StartsWith("[") && name.EndsWith("]")) name = name.Substring(1, name.Length - 2);
            IPAddress literal;
            if (IPAddress.TryParse(name, out literal)) return literal;
            if (!Regex.IsMatch(name, @"\A(?=.{1,253}\z)[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?\z") || Uri.CheckHostName(name) != UriHostNameType.Dns)
                throw new ArgumentException("Введите IP-адрес или сетевое имя одной станции, без пути и порта.");
            var task = Dns.GetHostAddressesAsync(name);
            if (!task.Wait(4500, cancel)) throw new TimeoutException("Имя станции не удалось разрешить за 4,5 секунды. Укажите её IP.");
            var addresses = task.Result.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1);
            var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6);
            if (ip == null) throw new IOException("Для сетевого имени нет IP-адреса.");
            return ip;
        }
        internal static void Tcp(IPAddress ip, int port, CancellationToken cancel)
        {
            using (var client = new TcpClient(ip.AddressFamily))
            {
                var task = client.ConnectAsync(ip, port);
                if (!task.Wait(4500, cancel)) throw new TimeoutException("Нет ответа от выбранного порта. Проверьте адрес, порт и доступ после перезагрузки.");
            }
        }

        // Windows CreateProcess command line escaping. No local command shell.
        internal static string Arg(string value)
        {
            var buffer = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') buffer.Append('\\', 2 * slashes + 1);
                else buffer.Append('\\', slashes);
                buffer.Append(c); slashes = 0;
            }
            return buffer.Append('\\', slashes * 2).Append('"').ToString();
        }

        internal static Process Open(string file, IEnumerable<string> args)
        {
            var info = new ProcessStartInfo {
                FileName = file, Arguments = string.Join(" ", args.Select(Arg)), UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
                StandardOutputEncoding = Utf, StandardErrorEncoding = Utf, CreateNoWindow = true
            };
            return Process.Start(info);
        }
        internal static void Kill(Process proc) { try { if (!proc.HasExited) proc.Kill(); } catch (InvalidOperationException) {} }
        private static string BoundedRead(StreamReader reader, int limit)
        {
            var result = new StringBuilder();
            char[] buf = new char[4096];
            int count;
            while ((count = reader.Read(buf, 0, buf.Length)) != 0)
            {
                if (result.Length + count > limit) throw new IOException("Ответ станции превысил предел операции.");
                result.Append(buf, 0, count);
            }
            return result.ToString();
        }
        internal static string Run(string file, IEnumerable<string> args, string input, CancellationToken cancel, int ms, int limit = 512 * 1024)
        {
            cancel.ThrowIfCancellationRequested();
            using (var proc = Open(file, args))
            {
                var stdout = Task.Run(() => BoundedRead(proc.StandardOutput, limit));
                var stderr = Task.Run(() => BoundedRead(proc.StandardError, 64 * 1024));
                var write = Task.Run(() => {
                    try { if (!string.IsNullOrEmpty(input)) { byte[] data = Utf.GetBytes(input); proc.StandardInput.BaseStream.Write(data, 0, data.Length); } }
                    finally { proc.StandardInput.Close(); }
                });
                var watch = Stopwatch.StartNew();
                try
                {
                    while (!proc.WaitForExit(70))
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (watch.ElapsedMilliseconds > ms) throw new TimeoutException("Истекло время ожидания ответа. Перед повтором заново проверьте состояние.");
                        if (stdout.IsFaulted || stderr.IsFaulted) throw new IOException("Получен слишком большой или повреждённый ответ.");
                    }
                    if (!Task.WaitAll(new Task[] { stdout, stderr }, 2000, cancel)) throw new TimeoutException("Канал вывода не закрылся.");
                    if (proc.ExitCode != 0) throw new IOException(Failure(stderr.Result, stdout.Result, proc.ExitCode));
                    if (!write.Wait(2000,cancel)) throw new TimeoutException("Клиент не завершил отправку.");
                    return stdout.Result.Replace("\r\n", "\n");
                }
                finally { Kill(proc); }
            }
        }
        internal static string Failure(string err, string output, int code)
        {
            string both = (err + "\n" + output).ToLowerInvariant();
            if (both.Contains("host key") || both.Contains("identification has changed")) return "SSH: ключ станции неизвестен или изменился. Перепроверьте отпечаток у владельца устройства.";
            if (both.Contains("permission denied")) return "SSH: сервер отклонил ключ. Друг должен установить публичный ключ пользователя для root.";
            if (both.Contains("unauthorized")) return "ADB: станция не разрешила этот ключ. Проверьте сопряжение.";
            if (both.Contains("offline")) return "ADB: выбранная станция offline. Проверьте её питание и сетевой ADB.";
            if (both.Contains("refused") || both.Contains("timed out") || both.Contains("no route")) return "Сетевой транспорт недоступен. Проверьте адрес, порт и активацию SSH/ADB.";
            return "Клиент не завершил операцию (код " + code + "). Проверьте доступ, наличие утилит и состояние станции.";
        }
        internal IList<string> SshArgs(string remoteCommand)
        {
            var args = new List<string> {"-F","none","-T","-o","BatchMode=yes","-o","ConnectTimeout=5","-o","IdentitiesOnly=yes",
                "-o","ClearAllForwardings=yes","-o","StrictHostKeyChecking=yes","-o","UpdateHostKeys=no","-o","HostKeyAlias=alicedesk-device",
                "-o","GlobalKnownHostsFile=none","-o","UserKnownHostsFile=\"" + KnownHosts.Replace('\\','/') + "\"","-p",Port.ToString()};
            if (!string.IsNullOrEmpty(PrivateKey)) { args.Add("-i"); args.Add(PrivateKey); }
            args.Add("root@" + Host); args.Add(remoteCommand);
            return args;
        }
        internal static KeyValuePair<string,string> ParseScan(string output)
        {
            var rows = output.Split('\n').Select(x => x.Trim().Split(new[]{' ','\t'}, StringSplitOptions.RemoveEmptyEntries))
                .Where(x => x.Length == 3 && new[]{"ssh-ed25519","ecdsa-sha2-nistp256","ssh-rsa"}.Contains(x[1])).ToList();
            foreach (string algorithm in new[]{"ssh-ed25519","ecdsa-sha2-nistp256","ssh-rsa"})
                foreach (var row in rows.Where(r => r[1] == algorithm))
                {
                    byte[] key; try { key = Convert.FromBase64String(row[2]); } catch (FormatException) { continue; }
                    if (key.Length < 23 || key.Length > 8192) continue;
                    using (var sha = SHA256.Create()) return new KeyValuePair<string,string>(
                        "alicedesk-device " + algorithm + " " + row[2] + "\n",
                        algorithm + " SHA256:" + Convert.ToBase64String(sha.ComputeHash(key)).TrimEnd('='));
                }
            throw new IOException("Не удалось получить поддерживаемый публичный ключ SSH.");
        }
        internal void Pin(Func<string,bool> confirm, CancellationToken cancel)
        {
            if (File.Exists(KnownHosts)) return; // The SSH client independently enforces the pin on every connection.
            var key = ParseScan(Run(SystemTool("ssh-keyscan"), new[]{"-T","5","-p",Port.ToString(),Host}, "", cancel, 13000));
            if (!confirm("Сверьте этот отпечаток с отпечатком, полученным у друга независимым способом:\n\n" + key.Value + "\n\nСохранить его для выбранного адреса?")) throw new OperationCanceledException();
            Directory.CreateDirectory(Store);
            using (var fs = new FileStream(KnownHosts, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { byte[] data = Utf.GetBytes(key.Key); fs.Write(data,0,data.Length); fs.Flush(true); }
        }
        internal static void RequireSerial(string output, string serial)
        {
            var row = output.Split('\n').Select(x => x.Trim().Split(new[]{' ','\t'}, StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(x => x.Length >= 2 && x[0] == serial);
            if (row == null) throw new IOException("ADB не видит выбранный сетевой сеанс. Перепроверьте порт и доступ по сети.");
            if (row[1] == "unauthorized" || row[1] == "offline") throw new IOException(Failure(row[1], "", 1));
            if (row[1] != "device") throw new IOException("Этот сеанс ADB ещё не готов к работе.");
        }
        internal void Authenticate(Func<string,bool> confirm, CancellationToken cancel)
        {
            if (Adb) {
                Run(Binary,new[]{"connect",Serial},"",cancel,8000);
                RequireSerial(Run(Binary,new[]{"devices"},"",cancel,6000), Serial);
            } else Pin(confirm, cancel);
            if (Shell("printf 'HELLO'", cancel, 8000).Trim() != "HELLO") throw new IOException("Подтвердить выполнение команды на выбранном устройстве не удалось.");
        }
        internal string Shell(string body, CancellationToken cancel, int ms = 16000, int limit = 512 * 1024)
        {
            string nonce = "ALICEDESK_" + Guid.NewGuid().ToString("N") + "_";
            string script = "export LC_ALL=C\nexport PATH=/data/opt/bin:/opt/bin:/opt/sbin:/usr/sbin:/usr/bin:/sbin:/bin\n(\n" + body.Replace("\r","") +
                "\n)\nrc=$?\nprintf '\\n" + nonce + "%d\\n' \"$rc\"\n";
            var args = Adb ? (IList<string>)new[] {"-s",Serial,"shell","-T","sh","-s"} : SshArgs("sh -s");
            var result = Run(Binary,args,script,cancel,ms,limit);
            var mark = result.LastIndexOf("\n" + nonce,StringComparison.Ordinal);
            if (mark < 0) throw new IOException("Устройство не подтвердило окончание команды.");
            var code = result.Substring(mark + nonce.Length + 1).Trim();
            if (code != "0") throw new IOException("Станция отклонила операцию (код " + Regex.Replace(code, @"[^0-9]","") + "). Проверьте права, доступные программы и прежнее состояние.");
            return result.Substring(0,mark);
        }
    }
}
