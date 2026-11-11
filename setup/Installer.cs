using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    const string Version="1.1.0";
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
                key.SetValue("DisplayName",Product);key.SetValue("DisplayVersion",Version);key.SetValue("Publisher","sunakle / alice-software");
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
    static bool Welcome(bool installed)
    {
        using(var dialog=new SetupDialog(installed))return dialog.ShowDialog()==DialogResult.OK;
    }

    // Only the two entry screens use the illustrated layout. Recovery and removal keep their existing prompts.
    sealed class SetupDialog : Form
    {
        internal SetupDialog(bool installed)
        {
            AutoScaleDimensions=new SizeF(96,96);
            AutoScaleMode=AutoScaleMode.Dpi;StartPosition=FormStartPosition.CenterScreen;
            Text=installed?"Установка завершена":"Установка Пульта Станции";
            Icon=Icon.ExtractAssociatedIcon(Own);
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            BackColor=Color.FromArgb(249,248,252);ForeColor=Color.FromArgb(35,32,47);
            Font=new Font("Segoe UI",10);ClientSize=new Size(520,345);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Margin=Padding.Empty,ColumnCount=1,RowCount=4};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));Controls.Add(layout);
            layout.Controls.Add(new SetupArt {Dock=DockStyle.Fill,Margin=Padding.Empty,AccessibleRole=AccessibleRole.Graphic,AccessibleName="Значок Пульта Станции"},0,0);
            var title=new Label {Text=installed?"Пульт Станции установлен":"Пульт Станции",AutoSize=true,
                Anchor=AnchorStyles.None,TextAlign=ContentAlignment.MiddleCenter,Margin=new Padding(12,13,12,2),Font=new Font(Font.FontFamily,20,FontStyle.Bold)};
            layout.Controls.Add(title,0,1);
            var body=new Label {AutoSize=true,Anchor=AnchorStyles.None,TextAlign=ContentAlignment.MiddleCenter,Margin=new Padding(16,0,16,14),
                ForeColor=Color.FromArgb(84,79,100),Text=installed?"Приложение можно открыть сейчас или из меню «Пуск».":"Версия "+Version+" для Windows x64.\nПриложение появится в меню «Пуск» у текущего пользователя."};
            layout.Controls.Add(body,0,2);
            var actions=new FlowLayoutPanel {AutoSize=true,FlowDirection=FlowDirection.LeftToRight,WrapContents=false,
                Anchor=AnchorStyles.None,Margin=new Padding(12,0,12,20)};
            layout.Controls.Add(actions,0,3);
            var cancel=new SetupButton(installed?"Готово":"Отмена",false,DialogResult.Cancel);
            var ok=new SetupButton(installed?"Открыть приложение":"Установить",true,DialogResult.OK);
            cancel.TabIndex=1;ok.TabIndex=0;actions.Controls.Add(cancel);actions.Controls.Add(ok);
            AcceptButton=ok;CancelButton=cancel;Shown+=(s,e)=>ok.Focus();
        }
    }
    static GraphicsPath Rounded(float x,float y,float width,float height,float radius)
    {
        var p=new GraphicsPath();float d=Math.Min(2*radius,Math.Min(width,height));
        p.AddArc(x,y,d,d,180,90);p.AddArc(x+width-d,y,d,d,270,90);
        p.AddArc(x+width-d,y+height-d,d,d,0,90);p.AddArc(x,y+height-d,d,d,90,90);p.CloseFigure();return p;
    }
    sealed class SetupArt : Control
    {
        internal SetupArt(){SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;if(Width<1||Height<1)return;
            g.SmoothingMode=SmoothingMode.AntiAlias;
            var waveScale=g.Save();g.ScaleTransform(Width/520f,Height/140f);
            var bounds=new RectangleF(0,0,520,140);
            using(var sky=new LinearGradientBrush(bounds,Color.FromArgb(142,206,241),Color.FromArgb(34,126,199),8))
                g.FillRectangle(sky,bounds);
            using(var waves=new GraphicsPath()) {
                waves.AddBezier(-4,-8,104,-8,170,53,241,60);waves.AddBezier(241,60,337,70,378,41,525,96);
                waves.AddLine(525,96,525,145);waves.AddLine(525,145,-4,145);waves.CloseFigure();
                using(var band=new LinearGradientBrush(bounds,Color.FromArgb(177,145,221),Color.FromArgb(228,229,250),0f))g.FillPath(band,waves);
            }
            using(var waves=new GraphicsPath()) {
                waves.AddBezier(-4,0,91,3,160,68,240,80);waves.AddBezier(240,80,322,92,410,68,525,114);
                waves.AddLine(525,114,525,145);waves.AddLine(525,145,-4,145);waves.CloseFigure();
                using(var band=new LinearGradientBrush(bounds,Color.FromArgb(232,81,121),Color.FromArgb(255,185,99),7f))g.FillPath(band,waves);
            }
            using(var waves=new GraphicsPath()) {
                waves.AddBezier(-4,39,101,33,94,81,194,94);waves.AddBezier(194,94,319,106,332,154,448,148);
                waves.AddLine(448,148,-4,148);waves.CloseFigure();
                using(var band=new LinearGradientBrush(bounds,Color.FromArgb(129,64,148),Color.FromArgb(247,75,111),2f))g.FillPath(band,waves);
            }
            g.Restore(waveScale);
            float side=Math.Min(Height*.63f,100*g.DpiX/96f);
            g.TranslateTransform((Width-side)/2f,(Height-side)/2f);g.ScaleTransform(side/64f,side/64f);
            using(var shadow=Rounded(2,5,60,58,13))
            using(var shade=new SolidBrush(Color.FromArgb(60,50,31,88)))g.FillPath(shade,shadow);
            using(var tile=Rounded(2,1,60,59,13))
            using(var finish=new LinearGradientBrush(new RectangleF(2,1,60,59),Color.FromArgb(204,198,255),Color.FromArgb(98,77,186),62))
            using(var border=new Pen(Color.FromArgb(166,239,232,255),.7f)) {g.FillPath(finish,tile);g.DrawPath(border,tile);}
            using(var light=new Pen(Color.FromArgb(248,249,255),2.65f))
            using(var dot=new SolidBrush(Color.White)) {
                g.DrawEllipse(light,21,15,22,7);light.StartCap=light.EndCap=LineCap.Round;
                g.DrawLine(light,19,35,19,44);g.DrawLine(light,25,35,25,44);
                g.FillEllipse(dot,30.9f,36,2.3f,2.3f);g.FillEllipse(dot,30.9f,41,2.3f,2.3f);
                g.DrawLine(light,39,35,39,44);g.DrawLine(light,45,35,45,44);
            }
        }
    }
    sealed class SetupButton : Button
    {
        readonly bool primary;
        internal SetupButton(string text,bool primary,DialogResult result)
        {
            Text=text;AccessibleName=text;this.primary=primary;DialogResult=result;
            FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;Cursor=Cursors.Hand;
            Width=text.Length>15?174:122;Height=34;Margin=new Padding(4,2,4,2);
            SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint,true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;float d=g.DpiX/96f;g.SmoothingMode=SmoothingMode.AntiAlias;
            bool hover=ClientRectangle.Contains(PointToClient(MousePosition));
            using(var p=Rounded(1,1,Width-2,Height-2,7*d))
            using(var fill=new SolidBrush(primary?(hover?Color.FromArgb(15,101,220):Color.FromArgb(21,114,233)):(hover?Color.FromArgb(242,240,249):Color.White)))
            using(var edge=new Pen(primary?Color.FromArgb(21,105,219):Color.FromArgb(203,201,214),d)) {g.FillPath(fill,p);g.DrawPath(edge,p);}
            TextRenderer.DrawText(g,Text,Font,ClientRectangle,primary?Color.White:Color.FromArgb(48,44,64),
                TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
            if(Focused && ShowFocusCues) {
                var r=ClientRectangle;r.Inflate(-(int)(4*d),-(int)(4*d));
                ControlPaint.DrawFocusRectangle(g,r,primary?Color.White:Color.Black,primary?Color.RoyalBlue:Color.White);
            }
        }
    }
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
            } else if(!Welcome(false))return 0;
            bool acquired;
            string mutexName=@"Local\StantsiyaDesk.App."+WindowsIdentity.GetCurrent().User.Value;
            string launch=null;
            using(var mutex=new Mutex(true,mutexName,out acquired)) {
                if(!acquired) {MessageBox.Show("Заверши работу приложения через его значок рядом с часами и повтори операцию.",Product);return 3;}
                try { if(worker)Real().Remove(args.Contains("--data"));else launch=Real().Install(); }
                finally {mutex.ReleaseMutex();}
            }
            if(worker){MessageBox.Show("Приложение удалено.",Product);return 0;}
            if(Welcome(true))Process.Start(launch);
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
