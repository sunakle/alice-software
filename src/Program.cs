using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace AliceDesk
{
    internal static class Program
    {
        internal static string AppLock { get { return @"Local\StantsiyaDesk.App." + WindowsIdentity.GetCurrent().User.Value; } }
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--self-test") {
                if (args.Length != 2) return 2;
                return RunTests(Path.GetFullPath(args[1]));
            }
            bool background = args.Contains("--agent");
            try {
                bool first;
                using (var mutex = new Mutex(true, AppLock, out first)) {
                    if (!first) {
                        if (!background) { try { using(var signal=EventWaitHandle.OpenExisting(AppLock+".Show")) signal.Set(); } catch(WaitHandleCannotBeOpenedException) {} }
                        return 0;
                    }
                    try {
                        using (var signal=new EventWaitHandle(false,EventResetMode.AutoReset,AppLock+".Show"))
                        using (var shell=new Shell(new ProfileStore(Path.Combine(ProfileStore.AppRoot,"Data")),background))
                        using (var timer=new System.Windows.Forms.Timer {Interval=350}) {
                            timer.Tick+=(s,e)=>{if(signal.WaitOne(0)){shell.ShowInTaskbar=true;shell.Show();shell.WindowState=FormWindowState.Normal;shell.Activate();}};
                            timer.Start();
                            Application.Run(shell);
                        }
                    } finally { mutex.ReleaseMutex(); }
                }
                return 0;
            } catch(Exception ex) {
                MessageBox.Show("Программа не смогла запуститься.\n"+ex.Message,"Пульт Станции",MessageBoxButtons.OK,MessageBoxIcon.Error);
                return 1;
            }
        }
        static int RunTests(string reportPath)
        {
            var results=new System.Collections.Generic.List<string>();
            int failed=0;
            Action<string,Action> check=(label,test)=>{
                try { test(); results.Add("OK   "+label); }
                catch(Exception ex) { results.Add("FAIL "+label+" — "+ex); failed++; }
            };
            string dir=Path.Combine(Path.GetTempPath(),"StantsiyaDesk-test-"+Guid.NewGuid().ToString("N"));
            try {
                check("Профили, атомарное сохранение, восстановление, импорт и DPAPI CurrentUser",()=>ProfileTests(dir));
                check("Обработка ответов и ошибок ИИ",AiPage.SelfTest);
                check("Действия ПК, сопряжение и музыка",MediaPage.SelfTest);
                check("Диагностика и ограничение операций Станции",DevicePage.SelfTest);
                check("Главное окно и загрузка всех разделов без подключённого устройства",()=>{
                    using (var shell=new Shell(new ProfileStore(Path.Combine(dir,"window")),false)) {
                        shell.Show(); Application.DoEvents(); shell.PerformLayout();
                        if (!shell.IsHandleCreated || !shell.Visible) throw new Exception("Окно не открыто.");
                        shell.Close();
                    }
                });
            } finally {
                try {
                    if(Directory.Exists(dir)) Directory.Delete(dir,true);
                } catch(IOException ex) {
                    failed++; results.Add("FAIL Очистка временных файлов — "+ex.Message);
                } catch(UnauthorizedAccessException ex) {
                    failed++; results.Add("FAIL Очистка временных файлов — "+ex.Message);
                }
                File.WriteAllLines(reportPath,results,Disk.Utf8);
            }
            return failed==0?0:1;
        }
        static void Require(bool ok) { if(!ok) throw new Exception("Условие проверки профилей не выполнено."); }
        static void ProfileTests(string root)
        {
            var store=new ProfileStore(root);
            var p=store.Active;
            p.Set("ai.deepseek.model","test-model");p.Set("ai.windowsVoice.rate","2");p.Set("local.stationpin","private-mark");p.PutSecret("initially.empty","");
            string marker="fixture-"+Guid.NewGuid().ToString("N");
            p.PutSecret("ai.test",marker);Require(p.Secret("ai.test")==marker);
            string vault=Path.Combine(p.DataDirectory,"vault");
            var existing=Directory.GetFiles(vault,"*.bin");
            p.PutSecret("corrupt.test",marker);
            string corrupt=Directory.GetFiles(vault,"*.bin").Except(existing).Single();
            File.WriteAllText(corrupt,"not valid base64");
            bool invalid=false;
            try { p.Secret("corrupt.test"); } catch(InvalidDataException) { invalid=true; }
            Require(invalid);
            File.WriteAllBytes(corrupt,new byte[]{0xff});
            invalid=false;
            try { p.Secret("corrupt.test"); } catch(InvalidDataException) { invalid=true; }
            Require(invalid);
            p.PutSecret("corrupt.test","");
            p.Set("agent.actions",Disk.Json(new[]{new DeskAction { Id="secretlink",Phrases="сайт",Kind="url",Target="https://example.org/?token=fixture-url-secret" }}));
            var export=Path.Combine(root,"export.json");store.Export(p,export);
            Require(!File.ReadAllText(export).Contains(marker)&&!File.ReadAllText(export).Contains("private-mark")&&!File.ReadAllText(export).Contains("fixture-url-secret"));
            foreach(var f in Directory.GetFiles(p.DataDirectory,"*",SearchOption.AllDirectories)) Require(!File.ReadAllText(f).Contains(marker));
            var imported=store.Import(export);
            Require(imported.Id!=p.Id&&imported.Get("ai.deepseek.model")=="test-model"&&imported.Get("ai.windowsVoice.rate")=="2"&&imported.Secret("ai.test")==""&&imported.Get("local.stationpin")=="");
            File.WriteAllText(export,"{\"version\":1,\"name\":\"X\",\"settings\":{\"local.approved\":\"x\",\"apiKey\":\"x\"}}");
            var untrusted=store.Import(export);Require(untrusted.Get("local.approved")==""&&untrusted.Get("apiKey")=="");
            var copy=store.Duplicate(p,"Копия");store.Select(copy);copy.Rename("Кабинет");
            Require(new ProfileStore(root).Active.Name=="Кабинет");
            File.WriteAllText(Path.Combine(copy.DataDirectory,"profile.json"),"broken");
            var recovered=new ProfileStore(root);Require(recovered.Notices.Count==1&&recovered.Active.Id==copy.Id);
            p.PutSecret("ai.test","");Require(p.Secret("ai.test")=="");
        }
    }
}
