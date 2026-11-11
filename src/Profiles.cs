using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AliceDesk
{
    internal static class Disk
    {
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }
        internal static T Read<T>(string path)
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Файл превышает 1 МБ.");
            return new JavaScriptSerializer { RecursionLimit = 12 }.Deserialize<T>(File.ReadAllText(path, Utf8));
        }
        internal static void Atomic(string file, string value, bool keepBackup = true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    byte[] bytes = Utf8.GetBytes(value);
                    if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Файл превышает 1 МБ.");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(file)) File.Replace(temp, file, keepBackup ? file + ".bak" : null);
                else File.Move(temp, file);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public sealed class SettingsFile
    {
        public int version { get; set; }
        public string name { get; set; }
        public Dictionary<string, string> settings { get; set; }
    }

    public sealed class Profile
    {
        readonly object gate = new object();
        SettingsFile data;
        public string Id { get; private set; }
        public string DataDirectory { get; private set; }
        public string Name { get { lock (gate) return data.name; } }
        public override string ToString() { return Name; }

        internal Profile(string dir, SettingsFile settings)
        {
            DataDirectory = dir;
            Id = Path.GetFileName(dir);
            data = settings;
        }
        internal static bool Shareable(string key)
        {
            return Regex.IsMatch(key, @"\A(?:station\.(?:expected|address|transport|sshPort|adbPort)|agent\.actions|media\.output|ai\.(?:provider|custom|voice|bridge\.(?:base|host|prefix|route)|(?:deepseek|compatible|ollama|lmstudio|local|p[0-9a-f]{32})\.(?:base|model|name)))\z");
        }
        internal static SettingsFile Validate(SettingsFile file, bool portable)
        {
            if (file == null || file.version != 1 || file.settings == null || file.settings.Count > 256)
                throw new InvalidDataException("Неподдерживаемый или повреждённый формат профиля.");
            file.name = ValidName(file.name);
            var safe = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in file.settings) {
                if (!Regex.IsMatch(kv.Key, @"\A[A-Za-z0-9_.-]{1,140}\z") || kv.Value == null || kv.Value.Length > 262144)
                    throw new InvalidDataException("Повреждён параметр профиля.");
                if (portable && !Shareable(kv.Key)) continue;
                if (portable && kv.Key == "agent.actions") {
                    var rules = new JavaScriptSerializer().Deserialize<List<DeskAction>>(kv.Value);
                    if (rules == null || rules.Count > 256) throw new InvalidDataException("Неверный список действий.");
                    safe.Add(kv.Key, Disk.Json(rules.Where(PortableRule).ToArray()));
                } else safe.Add(kv.Key, kv.Value);
            }
            return new SettingsFile { version = 1, name = file.name, settings = safe };
        }
        static bool PortableRule(DeskAction rule)
        {
            if(rule==null || rule.Id==null || rule.Phrases==null || rule.Target==null) return false;
            if(rule.Kind=="exe" || rule.Kind=="media") return true;
            Uri u;
            return rule.Kind=="url" && Uri.TryCreate(rule.Target,UriKind.Absolute,out u) && u.Scheme=="https" &&
                u.UserInfo=="" && u.Query=="" && u.Fragment=="";
        }
        internal static string ValidName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.Any(Char.IsControl))
                throw new ArgumentException("Введи название длиной от 1 до 80 символов.");
            return name.Trim();
        }
        internal SettingsFile Snapshot(bool portable)
        {
            lock (gate) return Validate(data, portable);
        }
        internal void Save() { Disk.Atomic(Path.Combine(DataDirectory, "profile.json"), Disk.Json(data)); }
        public string Get(string key, string fallback = "")
        {
            lock (gate) { string value; return data.settings.TryGetValue(key, out value) ? value : fallback; }
        }
        public void Set(string key, string value)
        {
            SetMany(new Dictionary<string,string> { { key, value } });
        }
        internal void SetMany(Dictionary<string,string> changes)
        {
            lock (gate) {
                var next = Snapshot(false);
                foreach(var kv in changes) {
                    if (kv.Value == null) next.settings.Remove(kv.Key); else next.settings[kv.Key] = kv.Value;
                }
                Commit(next);
            }
        }
        internal void Rename(string name)
        {
            lock (gate) { var next = Snapshot(false); next.name = ValidName(name); Commit(next); }
        }
        void Commit(SettingsFile next)
        {
            next = Validate(next, false);
            Disk.Atomic(Path.Combine(DataDirectory, "profile.json"), Disk.Json(next));
            data = next;
        }
        string SecretFile(string key)
        {
            using (var sha = SHA256.Create()) {
                var hash = BitConverter.ToString(sha.ComputeHash(Disk.Utf8.GetBytes(key))).Replace("-", "");
                return Path.Combine(DataDirectory, "vault", hash + ".bin");
            }
        }
        public string Secret(string key)
        {
            lock (gate) {
                string path = SecretFile(key);
                if (!File.Exists(path)) return "";
                try {
                    byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(path)),
                        Disk.Utf8.GetBytes(Id + "|" + key), DataProtectionScope.CurrentUser);
                    try { return Disk.Utf8.GetString(bytes); } finally { Array.Clear(bytes, 0, bytes.Length); }
                } catch (CryptographicException) { throw new InvalidDataException("Windows не смогла расшифровать ключ этого профиля. Сохрани ключ заново."); }
            }
        }
        public void PutSecret(string key, string value)
        {
            lock (gate) {
                string file = SecretFile(key);
                if (String.IsNullOrEmpty(value)) {
                    if (Directory.Exists(Path.GetDirectoryName(file))) { File.Delete(file + ".bak"); File.Delete(file); }
                    return;
                }
                byte[] raw = Disk.Utf8.GetBytes(value);
                try {
                    Disk.Atomic(file, Convert.ToBase64String(ProtectedData.Protect(raw, Disk.Utf8.GetBytes(Id + "|" + key), DataProtectionScope.CurrentUser)));
                    File.Delete(file + ".bak");
                } finally { Array.Clear(raw, 0, raw.Length); }
            }
        }
    }

    internal sealed class ProfileStore
    {
        internal static readonly string AppRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StantsiyaDesk");
        readonly string root;
        internal readonly List<Profile> Items = new List<Profile>();
        internal readonly List<string> Notices = new List<string>();
        internal Profile Active { get; private set; }
        internal ProfileStore(string root)
        {
            this.root = root;
            string profiles = Path.Combine(root, "profiles");
            Directory.CreateDirectory(profiles);
            foreach (var dir in Directory.GetDirectories(profiles).OrderBy(x=>x)) {
                Guid id;
                if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out id)) continue;
                var file = Path.Combine(dir, "profile.json");
                try { Items.Add(new Profile(dir, Profile.Validate(Disk.Read<SettingsFile>(file), false))); }
                catch (Exception) {
                    try {
                        var recover = Profile.Validate(Disk.Read<SettingsFile>(file + ".bak"), false);
                        if (File.Exists(file)) File.Move(file, file + ".bad-" + Guid.NewGuid().ToString("N"));
                        var profile = new Profile(dir, recover);
                        profile.Save();
                        Items.Add(profile);
                        Notices.Add("Профиль «" + profile.Name + "» восстановлен из предыдущей копии.");
                    } catch (Exception) { Notices.Add("Повреждённый профиль пропущен. Его файлы сохранены в " + dir); }
                }
            }
            if (Items.Count == 0) Create("Лайт 2 с часами");
            string last = "";
            var index = Path.Combine(root, "active");
            if (File.Exists(index) && new FileInfo(index).Length < 100) last = File.ReadAllText(index).Trim();
            Active = Items.FirstOrDefault(x=>x.Id == last) ?? Items[0];
        }
        internal void Select(Profile p)
        {
            if (!Items.Contains(p)) throw new ArgumentException("Неизвестный профиль.");
            Disk.Atomic(Path.Combine(root, "active"), p.Id);
            Active = p;
        }
        Profile Add(SettingsFile data)
        {
            string dir = Path.Combine(root, "profiles", Guid.NewGuid().ToString("N"));
            var result = new Profile(dir, Profile.Validate(data, false));
            result.Save();
            Items.Add(result);
            return result;
        }
        internal Profile Create(string name)
        {
            return Add(new SettingsFile { version=1, name=name, settings=new Dictionary<string,string>() });
        }
        internal Profile Duplicate(Profile p, string name)
        {
            var data = p.Snapshot(true); data.name = name; return Add(data);
        }
        internal void Export(Profile p, string path)
        {
            // Exports contain only portable preferences, never keys or approvals from this PC.
            Disk.Atomic(path, Disk.Json(p.Snapshot(true)), false);
        }
        internal Profile Import(string path)
        {
            return Add(Profile.Validate(Disk.Read<SettingsFile>(path), true));
        }
    }
}
