using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AliceDesk {
    public sealed class DeskAction {
        public string Id { get; set; }
        public string Phrases { get; set; }
        public string Kind { get; set; }
        public string Target { get; set; }
        public override string ToString() { return Id + "  ·  " + Phrases; }
    }
    internal static class MediaCommands {
        internal static readonly string[] MediaKeys = { "play-pause", "next", "previous", "mute", "volume-up", "volume-down" };
        static readonly byte[] KeyCodes = { 0xb3, 0xb0, 0xb1, 0xad, 0xaf, 0xae };
        static readonly HashSet<string> Shells = new HashSet<string>(new[] {
            "cmd","powershell","powershell_ise","pwsh","cscript","wscript","mshta","rundll32","regsvr32","msiexec","conhost","wsl","bash","sh","reg",
            "python","pythonw","py","ruby","node","deno","perl","java","javaw","dotnet","msbuild","installutil","control","wmic","explorer","schtasks",
            "dllhost","hh","forfiles","wt","terminal","ssh","autohotkey","msdt","regedit","regasm","regsvcs","csc","fsi","cmstp"
        },StringComparer.OrdinalIgnoreCase);
        static bool Interpreter(string file) {
            string stem = Path.GetFileNameWithoutExtension(file ?? "");
            return Shells.Contains(stem) || Regex.IsMatch(stem,@"^(python(w)?[0-9.]*|autohotkey.*|powershell.*)$",RegexOptions.IgnoreCase);
        }
        internal static string Phrase(string text) {
            return Regex.Replace((text ?? "").Trim().TrimEnd('.','!','?').ToLowerInvariant().Replace('ё','е'),@"\s+"," ");
        }
        internal static string[] Aliases(string phrases) {
            var names = (phrases ?? "").Split(new[] {';', '\n'},StringSplitOptions.RemoveEmptyEntries)
                .Select(Phrase).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            if (names.Length == 0 || names.Length > 16 || names.Any(s => s.Length > 96 || s.Any(Char.IsControl)))
                throw new InvalidOperationException("Введите 1–16 фраз длиной до 96 символов через точку с запятой.");
            return names;
        }
        internal static DeskAction Resolve(IEnumerable<DeskAction> actions, string phrase) {
            var matches = actions.Where(a => Aliases(a.Phrases).Contains(Phrase(phrase))).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException(matches.Length == 0 ? "Для этой фразы нет действия." : "Фраза назначена нескольким действиям.");
            return matches[0];
        }
        internal static string Chrome() {
            foreach (var dir in new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}) {
                string file = Path.Combine(dir,@"Google\Chrome\Application\chrome.exe");
                if (File.Exists(file)) return file;
            } return "";
        }
        static bool SafeLocalExe(string input) {
            if (String.IsNullOrWhiteSpace(input) || !Regex.IsMatch(input,@"^[A-Za-z]:[\\/]") ||
                input.IndexOf(':',2) >= 0 || input.IndexOf('"') >= 0 || !Path.GetExtension(input).Equals(".exe",StringComparison.OrdinalIgnoreCase)) return false;
            string path = Path.GetFullPath(input);
            return new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Fixed && !Interpreter(path);
        }
        internal static DeskAction Validate(DeskAction a) {
            if (a == null || !Regex.IsMatch(a.Id ?? "", @"\A[a-z][a-z0-9_-]{0,47}\z")) throw new InvalidOperationException("ID действия — 1–48 строчных латинских букв, цифр, «_» или «-», начиная с буквы.");
            Aliases(a.Phrases);
            if (String.IsNullOrEmpty(a.Target) || a.Target.Length > 2048) throw new InvalidOperationException("Задайте цель действия.");
            if (a.Kind == "exe") {
                if (!SafeLocalExe(a.Target)) throw new InvalidOperationException("Выберите обычный .exe на локальном диске. Командные интерпретаторы не поддерживаются.");
                a.Target = Path.GetFullPath(a.Target);
                if (!File.Exists(a.Target)) throw new FileNotFoundException("Указанная программа не найдена.");
                string winDir = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\') + "\\";
                if (a.Target.StartsWith(winDir,StringComparison.OrdinalIgnoreCase) &&
                    !new[]{"notepad.exe","mspaint.exe","calc.exe"}.Contains(Path.GetFileName(a.Target),StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Выберите пользовательское приложение. Системная программа не разрешена.");
                for (string path = a.Target; path != null; path = Path.GetDirectoryName(path)) {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Выберите исходный файл вне символических ссылок.");
                }
                var info = FileVersionInfo.GetVersionInfo(a.Target);
                if (Interpreter(info.OriginalFilename) || Interpreter(info.InternalName)) throw new InvalidOperationException("Запуск интерпретатора не разрешён.");
            } else if (a.Kind == "url") {
                Uri u;
                if (!Uri.TryCreate(a.Target,UriKind.Absolute,out u) || u.Scheme != "https" || u.UserInfo != "" || String.IsNullOrEmpty(u.Host))
                    throw new InvalidOperationException("Введите полный HTTPS-адрес без пароля.");
                a.Target = u.AbsoluteUri;
            } else if(a.Kind=="spotify") {
                a.Target=SpotifyClient.Target(a.Target);
            } else if (a.Kind != "media" || !MediaKeys.Contains(a.Target)) throw new InvalidOperationException("Неизвестное действие или медиаклавиша.");
            return a;
        }
        internal static string Fingerprint(DeskAction a) {
            using (var hash = SHA256.Create()) using (var bytes = new MemoryStream()) {
                using (var w = new BinaryWriter(bytes,Encoding.UTF8,true)) {
                    w.Write(a.Id); w.Write(a.Kind); w.Write(a.Phrases); w.Write(a.Target);
                    if (a.Kind == "exe") using (var file = File.OpenRead(a.Target)) w.Write(hash.ComputeHash(file));
                }
                return Convert.ToBase64String(hash.ComputeHash(bytes.ToArray()));
            }
        }
        internal static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength=200000, RecursionLimit=12 };
        internal static List<DeskAction> Load(Profile p) {
            string json = p.Get("agent.actions","");
            if (json.Length == 0) return new List<DeskAction> { new DeskAction { Id="chrome", Phrases="открой хром; открой браузер", Kind="exe", Target=Chrome() } };
            var defs = Serializer.Deserialize<List<DeskAction>>(json);
            if (defs == null || defs.Count > 256 || defs.Any(a => a == null || a.Id == null || a.Phrases == null || a.Kind == null || a.Target == null)) throw new InvalidDataException("Некорректный список команд в профиле.");
            if (defs.Select(a=>a.Id).Distinct().Count()!=defs.Count) throw new InvalidDataException("ID команд должны быть уникальными.");
            return defs;
        }
        [DllImport("user32.dll")] static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
        internal static void Run(DeskAction action,string approval) {
            Validate(action);
            if(action.Kind=="spotify")throw new InvalidOperationException("Spotify требует асинхронного запуска через подключённый профиль.");
            if (String.IsNullOrEmpty(approval))
                throw new InvalidOperationException("Действие или программа изменились. Локально разрешите выбранную цель в редакторе.");
            if (action.Kind == "exe") {
                // Refuse replacement between validating the approved binary and creating its process.
                using (File.Open(action.Target,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    VerifyApproval(action,approval);
                    var process = Process.Start(new ProcessStartInfo(action.Target) { UseShellExecute=false, WorkingDirectory=Path.GetDirectoryName(action.Target) });
                    if (process != null) process.Dispose();
                }
                return;
            }
            VerifyApproval(action,approval);
            if (action.Kind == "url") MediaUi.Open(action.Target);
            else {
                byte code = KeyCodes[Array.IndexOf(MediaKeys,action.Target)]; keybd_event(code,0,0,UIntPtr.Zero); keybd_event(code,0,2,UIntPtr.Zero);
            }
        }
        internal static void VerifyApproval(DeskAction action,string approval) {
            if (approval != Fingerprint(action)) throw new InvalidOperationException("Действие или программа изменились. Локально разрешите выбранную цель в редакторе.");
        }
        static void Reject(Action test) {
            try { test(); } catch (Exception) { return; } throw new Exception("Ожидался отказ агента.");
        }
        internal static void Test() {
            var a = Validate(new DeskAction { Id="example",Phrases="открой сайт; сайт",Kind="url",Target="https://example.org/" });
            if (Resolve(new[]{a}," ОТКРОЙ  сайт! ") != a) throw new Exception("Не распознана фраза.");
            Reject(delegate { Resolve(new[]{a,a},"сайт"); }); Reject(delegate { Resolve(new[]{a},"неизвестно"); });
            Reject(delegate { Run(a,""); }); string pin=Fingerprint(a); a.Target="https://example.com/"; Reject(delegate { Run(a,pin); });
            foreach (string target in new[]{"cmd.exe", @"C:\Windows\System32\cmd.exe", @"\\server\share\program.exe", @"C:\Windows\System32\notepad.exe:other.exe"}) {
                a.Target=target; a.Kind="exe"; Reject(delegate { Validate(a); });
            }
            a.Kind="url"; a.Target="file:///C:/Windows"; Reject(delegate { Validate(a); });
        }
    }
}
