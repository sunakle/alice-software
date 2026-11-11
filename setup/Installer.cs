using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class Installation
{
    const string Product="Пульт Станции";
    const string AppName="StantsiyaDesk.exe";
    const string AppId="StantsiyaDesk";
    readonly string root, startMenu, uninstallKey, runKey;
    public Installation(string root,string startMenu,string uninstallKey,string runKey)
    {
        this.root=Path.GetFullPath(root); this.startMenu=startMenu; this.uninstallKey=uninstallKey; this.runKey=runKey;
    }
    static string Own { get { return Assembly.GetExecutingAssembly().Location; } }
    string Programs { get { return Path.Combine(root,"Programs"); } }
    static string Hash(byte[] bytes)
    {
        using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
    }
    static byte[] Payload()
    {
        var asm=Assembly.GetExecutingAssembly();
        using(var stream=asm.GetManifestResourceStream("payload.zip"))
        using(var dest=new MemoryStream())
        using(var expected=new StreamReader(asm.GetManifestResourceStream("payload.sha256"))) {
            stream.CopyTo(dest);var bytes=dest.ToArray();
            if(!String.Equals(Hash(bytes),expected.ReadToEnd().Trim(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Контрольная сумма дистрибутива расходится.");
            return bytes;
        }
    }
    static void CheckPath(string path)
    {
        var at=new DirectoryInfo(Path.GetFullPath(path));
        while(at!=null) {
            if(at.Exists && (at.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("В пути обнаружена ссылка. Проверь каталог установки.");
            at=at.Parent;
        }
    }
    static void DeleteTree(string path)
    {
        if(!Directory.Exists(path))return;
        CheckPath(path);
        foreach(var sub in Directory.GetDirectories(path)) DeleteTree(sub);
        foreach(var file in Directory.GetFiles(path))File.Delete(file);
        Directory.Delete(path);
    }
    static void Shortcut(string path,string exe)
    {
        if(!File.Exists(exe))throw new FileNotFoundException("Программа отсутствует.",exe);
        CheckPath(Path.GetDirectoryName(path));Directory.CreateDirectory(Path.GetDirectoryName(path));
        Type type=Type.GetTypeFromProgID("WScript.Shell",true);
        dynamic shell=Activator.CreateInstance(type);
        dynamic link=shell.CreateShortcut(path);
        try { link.TargetPath=exe;link.WorkingDirectory=Path.GetDirectoryName(exe);link.Description=Product;link.Save(); }
        finally {System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);}
    }
    internal string Install()
    {
        CheckPath(Programs); Directory.CreateDirectory(Programs);
        byte[] bytes=Payload();
        string final=Path.Combine(Programs,Hash(bytes).Substring(0,20));
        string stage=Path.Combine(Programs,".staging-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try {
            using(var zip=new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read)) {
                foreach(var entry in zip.Entries) {
                    string target=Path.GetFullPath(Path.Combine(stage,entry.FullName));
                    if(!target.StartsWith(stage+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Неверный путь в дистрибутиве.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if(entry.Name.Length==0)continue;
                    using(var dest=new FileStream(target,FileMode.CreateNew))
                    using(var src=entry.Open())src.CopyTo(dest);
                }
            }
            File.Copy(Own,Path.Combine(stage,"Remove.exe"));
            if(!File.Exists(Path.Combine(stage,AppName)))throw new InvalidDataException("В архиве отсутствует приложение.");
            if(!Directory.Exists(final)) Directory.Move(stage,final);
            else {
                // Repair a damaged copy without deleting the working build until staging has completed.
                string previous=stage+".previous";
                Directory.Move(final,previous);
                try {Directory.Move(stage,final);}
                catch {Directory.Move(previous,final);throw;}
                DeleteTree(previous);
            }
            string app=Path.Combine(final,AppName);
            Shortcut(Path.Combine(startMenu,Product+".lnk"),app);
            using(var key=Registry.CurrentUser.CreateSubKey(uninstallKey)) {
                key.SetValue("DisplayName",Product);key.SetValue("DisplayVersion","1.0.0");key.SetValue("Publisher","sunakle / alice-software");
                key.SetValue("DisplayIcon",app);key.SetValue("InstallLocation",final);
                key.SetValue("UninstallString","\""+Path.Combine(final,"Remove.exe")+"\" --uninstall");
                key.SetValue("NoModify",1,RegistryValueKind.DWord);key.SetValue("NoRepair",1,RegistryValueKind.DWord);
            }
            using(var key=Registry.CurrentUser.OpenSubKey(runKey,true)) {
                if(key!=null && key.GetValue(AppId)!=null) key.SetValue(AppId,"\""+app+"\" --agent");
            }
            return app;
        } finally {DeleteTree(stage);}
    }
    internal void Remove(bool clearData)
    {
        CheckPath(root);
        using(var key=Registry.CurrentUser.OpenSubKey(runKey,true)) {if(key!=null)key.DeleteValue(AppId,false);}
        // A detached local client cannot authenticate to HA after its cached token and backup are erased.
        string profiles=Path.Combine(root,"Data","profiles");
        if(Directory.Exists(profiles))foreach(var dir in Directory.GetDirectories(profiles)) {
            CheckPath(dir);string vault=Path.Combine(dir,"vault");if(!Directory.Exists(vault))continue;CheckPath(vault);
            string token=Hash(Encoding.UTF8.GetBytes("agent.haToken")).ToUpperInvariant()+".bin";
            File.Delete(Path.Combine(vault,token));File.Delete(Path.Combine(vault,token+".bak"));
        }
        File.Delete(Path.Combine(startMenu,Product+".lnk"));
        if(Directory.Exists(startMenu)&&!Directory.EnumerateFileSystemEntries(startMenu).Any()) Directory.Delete(startMenu);
        DeleteTree(Programs);
        if(clearData)DeleteTree(Path.Combine(root,"Data"));
        Registry.CurrentUser.DeleteSubKeyTree(uninstallKey,false);
        if(Directory.Exists(root)&&!Directory.EnumerateFileSystemEntries(root).Any())Directory.Delete(root);
    }
    static Installation Real()
    {
        return new Installation(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppId),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),Product),
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\StantsiyaDesk",@"Software\Microsoft\Windows\CurrentVersion\Run");
    }
    static bool Ask(string message) {return MessageBox.Show(message,Product,MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes;}
    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        try {
            if(args.Length==2 && args[0]=="--test-lifecycle")return Lifecycle(Path.GetFullPath(args[1]));
            if(args.Contains("--uninstall")) {
                if(!Ask("Удалить приложение «Пульт Станции» из этой учётной записи?"))return 0;
                bool clear=Ask("Удалить также профили, локальные ключи и резервные копии? Нажми «Нет», чтобы сохранить их для следующей установки.");
                string temp=Path.Combine(Path.GetTempPath(),"StantsiyaDesk-remove-"+Guid.NewGuid().ToString("N")+".exe");
                File.Copy(Own,temp);Process.Start(temp,"--remove-worker "+Process.GetCurrentProcess().Id+(clear?" --data":""));return 0;
            }
            bool worker=args.Length>=2&&args[0]=="--remove-worker";
            if(worker) {
                int pid; if(!Int32.TryParse(args[1],out pid))return 2;
                try {using(var parent=Process.GetProcessById(pid))parent.WaitForExit(8000);}catch(ArgumentException){}
            } else if(!Ask("Установить «Пульт Станции» для текущего пользователя Windows? Ярлык появится в меню «Пуск»."))return 0;
            bool acquired;
            string mutexName=@"Local\StantsiyaDesk.App."+WindowsIdentity.GetCurrent().User.Value;
            string launch=null;
            using(var mutex=new Mutex(true,mutexName,out acquired)) {
                if(!acquired) {MessageBox.Show("Заверши работу приложения через его значок рядом с часами и повтори операцию.",Product);return 3;}
                try { if(worker)Real().Remove(args.Contains("--data"));else launch=Real().Install(); }
                finally {mutex.ReleaseMutex();}
            }
            if(worker){MessageBox.Show("Приложение удалено.",Product);return 0;}
            if(Ask("Установка завершена. Открыть «Пульт Станции»?"))Process.Start(launch);
            return 0;
        } catch(Exception ex){MessageBox.Show(ex.Message,Product,MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
    }
    static int Lifecycle(string report)
    {
        string id=Guid.NewGuid().ToString("N");
        string temp=Path.Combine(Path.GetTempPath(),"StantsiyaDesk-setup-test-"+id);
        string registry=@"Software\StantsiyaDeskInstallTests\"+id;
        var inst=new Installation(Path.Combine(temp,"app"),Path.Combine(temp,"menu"),registry+@"\Uninstall",registry+@"\Run");
        try {
            string app=inst.Install();
            if(!File.Exists(app)||!File.Exists(Path.Combine(temp,"menu",Product+".lnk")))throw new Exception("Нет файла или ярлыка.");
            string data=Path.Combine(temp,"app","Data");
            Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"keep"),"sample");
            using(var key=Registry.CurrentUser.CreateSubKey(registry+@"\Run"))key.SetValue(AppId,"old");
            File.WriteAllText(app,"damaged");inst.Install();
            if(new FileInfo(app).Length<32768||File.ReadAllText(Path.Combine(data,"keep"))!="sample")throw new Exception("Повторная установка не восстановила программу или потеряла данные.");
            string vault=Path.Combine(data,"profiles",Guid.NewGuid().ToString("N"),"vault");Directory.CreateDirectory(vault);
            string token=Path.Combine(vault,Hash(Encoding.UTF8.GetBytes("agent.haToken")).ToUpperInvariant()+".bin");File.WriteAllText(token,"fixture");
            inst.Remove(false);
            using(var key=Registry.CurrentUser.OpenSubKey(registry+@"\Run"))
                if(key!=null && key.GetValue(AppId)!=null)throw new Exception("Автозапуск остался включён.");
            if(File.Exists(app)||File.Exists(token)||!File.Exists(Path.Combine(data,"keep")))throw new Exception("Ошибка удаления с сохранением профилей.");
            inst.Install();inst.Remove(true);
            if(Directory.Exists(data))throw new Exception("Каталог данных не удалён.");
            File.WriteAllText(report,"OK   Установка, восстановление файлов, ярлык, повторная установка, сохранение профилей, удаление сопряжения, автозапуска и полное удаление.",new UTF8Encoding(false));
            return 0;
        } catch(Exception ex){File.WriteAllText(report,"FAIL "+ex);return 1;}
        finally { Registry.CurrentUser.DeleteSubKeyTree(registry,false);DeleteTree(temp); }
    }
}
