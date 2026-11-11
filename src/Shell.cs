using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AliceDesk
{
    internal sealed class Shell : Form
    {
        readonly ProfileStore profiles;
        readonly ComboBox chooser = new ComboBox();
        readonly Panel body = new Panel();
        readonly FlowLayoutPanel nav = new FlowLayoutPanel();
        readonly Dictionary<string, Button> navButtons = new Dictionary<string, Button>();
        readonly Color ink = Color.FromArgb(28,43,66);
        readonly Color accent = Color.FromArgb(36,87,150);
        readonly NotifyIcon tray = new NotifyIcon();
        readonly bool background;
        Control home;
        DevicePage station;
        AiPage ai;
        AgentPage agent;
        MediaPage music;
        bool refreshing;

        internal Shell(ProfileStore store, bool background)
        {
            this.profiles=store;
            this.background=background;
            Text="Пульт Станции";
            Icon=Icon.ExtractAssociatedIcon(typeof(Shell).Assembly.Location);
            Font=new Font("Segoe UI", 10);
            StartPosition=FormStartPosition.CenterScreen;
            var area=Screen.PrimaryScreen.WorkingArea;
            Size=new Size(Math.Min(1220,area.Width),Math.Min(840,area.Height));
            MinimumSize=new Size(Math.Min(880,area.Width),Math.Min(590,area.Height));
            BackColor=Color.White;
            ForeColor=ink;
            AutoScaleMode=AutoScaleMode.Dpi;
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill, RowCount=1, ColumnCount=2, Margin=Padding.Empty, Padding=Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,196));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Controls.Add(layout);
            nav.FlowDirection=FlowDirection.TopDown; nav.Dock=DockStyle.Fill; nav.WrapContents=false;
            nav.AutoScroll=true; nav.Margin=Padding.Empty; nav.Padding=new Padding(10,17,7,8);
            nav.BackColor=Color.FromArgb(238,243,249);
            layout.Controls.Add(nav,0,0);
            nav.Controls.Add(new Label { Text=Text, ForeColor=ink, Font=new Font(Font.FontFamily,15,FontStyle.Bold), Size=new Size(171,43), Padding=new Padding(3,0,0,0) });
            nav.Controls.Add(new Label { Text="Текущий профиль", ForeColor=ink, AutoSize=true, Margin=new Padding(4,0,0,3) });
            chooser.DropDownStyle=ComboBoxStyle.DropDownList; chooser.Width=167;
            chooser.AccessibleName="Текущий профиль"; chooser.BackColor=Color.White; chooser.ForeColor=ink; nav.Controls.Add(chooser);
            chooser.SelectedIndexChanged+=delegate {
                if (!refreshing && chooser.SelectedItem!=null && chooser.SelectedItem!=profiles.Active)
                    Guard(delegate { profiles.Select((Profile)chooser.SelectedItem); Rebuild(); });
            };
            AddNav("home","Главная"); AddNav("station","Станция"); AddNav("ai","Нейросети");
            AddNav("agent","Команды ПК"); AddNav("music","Музыка"); AddNav("extensions","Расширения");
            AddNav("recovery","Восстановление"); AddNav("profiles","Профили и обмен"); AddNav("help","Руководство");
            body.Dock=DockStyle.Fill; body.Margin=Padding.Empty; body.Padding=new Padding(8);
            layout.Controls.Add(body,1,0);
            var trayMenu=new ContextMenuStrip();
            trayMenu.Items.Add("Открыть",null,(s,e)=>RestoreWindow());
            trayMenu.Items.Add("Завершить работу",null,(s,e)=>Close());
            tray.Icon=Icon; tray.Text=Text; tray.ContextMenuStrip=trayMenu;
            tray.DoubleClick+=(s,e)=>RestoreWindow();
            Resize+=delegate { if (WindowState==FormWindowState.Minimized) { tray.Visible=true; Hide(); } };
            Rebuild();
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if(background) { ShowInTaskbar=false; tray.Visible=true; Hide(); }
            if(!background && profiles.Notices.Count!=0) MessageBox.Show(this,String.Join("\n",profiles.Notices),Text);
        }
        void RestoreWindow() { ShowInTaskbar=true; Show(); WindowState=FormWindowState.Normal; Activate(); tray.Visible=false; }
        protected override void Dispose(bool disposing)
        {
            if(disposing) { tray.Visible=false; tray.Dispose(); }
            base.Dispose(disposing);
        }
        void Guard(Action run)
        {
            try {run();} catch(Exception ex) { MessageBox.Show(this, ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning); }
        }
        void AddNav(string id,string caption)
        {
            var b=new Button { Text=caption, Width=167, Height=36, TextAlign=ContentAlignment.MiddleLeft,
                Padding=new Padding(7,0,0,0), FlatStyle=FlatStyle.Flat, ForeColor=ink, Cursor=Cursors.Hand,
                Margin=new Padding(3, id=="home"?15:2,3,0) };
            b.FlatAppearance.BorderSize=0;
            b.Click+=(s,e)=>Guard(delegate { ShowPage(id); });
            navButtons[id]=b; nav.Controls.Add(b);
        }
        void Rebuild()
        {
            refreshing=true;
            try { chooser.Items.Clear(); chooser.Items.AddRange(profiles.Items.ToArray()); chooser.SelectedItem=profiles.Active; }
            finally { refreshing=false; }
            while(body.Controls.Count>0) body.Controls[0].Dispose();
            home=Home(); station=new DevicePage(profiles.Active); ai=new AiPage(profiles.Active);
            agent=new AgentPage(profiles.Active); music=new MediaPage(profiles.Active);
            foreach(var view in new Control[]{home,station,ai,agent,music}) {view.Dock=DockStyle.Fill; view.Visible=false; body.Controls.Add(view); Style(view);}
            ShowPage("home");
        }
        internal static void Style(Control root)
        {
            Color text=Color.FromArgb(29,45,64);
            if(root is TabPage) { ((TabPage)root).UseVisualStyleBackColor=false; root.BackColor=Color.White; }
            if(root is TextBoxBase || root is ComboBox || root is ListBox || root is NumericUpDown) {
                root.BackColor=Color.White; root.ForeColor=text;
            } else if(root is Button) {
                var b=(Button)root;
                b.FlatStyle=FlatStyle.Flat; b.UseVisualStyleBackColor=false; b.BackColor=Color.FromArgb(235,241,249);
                b.ForeColor=text; b.FlatAppearance.BorderColor=Color.FromArgb(166,185,207); b.Cursor=Cursors.Hand;
            } else if(root is Label || root is CheckBox) {
                if(root.BackColor.GetBrightness()>0.5F || root.BackColor==Color.Transparent)root.ForeColor=text;
            } else if(root is UserControl) {root.BackColor=Color.White;root.ForeColor=text;}
            foreach(Control child in root.Controls)Style(child);
        }
        void ShowPage(string id)
        {
            if(id=="profiles") { ManageProfiles(); return; }
            if(id=="help") { using(var guide=new Guide()) guide.ShowDialog(this); return; }
            var next=id=="ai"?(Control)ai:id=="agent"?agent:id=="music"?music:id=="home"?home:(Control)station;
            foreach(Control c in body.Controls) c.Visible=(c==next);
            next.BringToFront();
            if(next==station) station.Navigate(id);
            foreach(var pair in navButtons) {
                pair.Value.BackColor=pair.Key==id?accent:nav.BackColor;
                pair.Value.ForeColor=pair.Key==id?Color.White:ink;
            }
        }
        Control Home()
        {
            var scroll=new Panel {AutoScroll=true,BackColor=Color.White};
            var stack=new TableLayoutPanel { Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(22,20,24,20),ColumnCount=1 };
            scroll.Controls.Add(stack);
            stack.Controls.Add(new Label {Text="Моя Станция",Font=new Font(Font.FontFamily,24,FontStyle.Bold),ForeColor=ink,AutoSize=true});
            stack.Controls.Add(new Label {Text="Ожидаемая модель  ·  "+profiles.Active.Get("station.expected","Яндекс Станция Лайт 2 с часами · YNDX-00026"),
                ForeColor=Color.DimGray,AutoSize=true,Margin=new Padding(4,4,4,20),MaximumSize=new Size(630,0)});
            Route(stack,"У меня пока нет подключения","Проверь ключ DeepSeek, попробуй текстовый диалог. Голосовые фразы для своего ПК можно испытать в разделе «Команды ПК».","Открыть нейросети","ai");
            Route(stack,"Подключить мою Станцию","После прошивки возьми у друга адрес колонки, порт и способ входа. Первая проверка читает сведения об устройстве.","Проверить подключение","station");
            Route(stack,"Открыть сохранённый профиль","Выбери настройки другой Станции, создай копию или импортируй файл с параметрами.","Выбрать профиль","profiles");
            var small=new Button {Text="Свернуть в область уведомлений", AutoSize=true, Margin=new Padding(4,12,4,4)};
            small.Click+=(s,e)=>{tray.Visible=true; Hide();}; stack.Controls.Add(small);
            return scroll;
        }
        void Route(TableLayoutPanel parent,string title,string detail,string action,string target)
        {
            var panel=new TableLayoutPanel {AutoSize=true,Dock=DockStyle.Top,ColumnCount=1,BackColor=Color.FromArgb(245,248,252), Padding=new Padding(13,8,12,9),Margin=new Padding(3,0,3,10)};
            panel.Controls.Add(new Label{Text=title,AutoSize=true,Font=new Font(Font.FontFamily,13,FontStyle.Bold),ForeColor=ink});
            panel.Controls.Add(new Label{Text=detail,AutoSize=true,MaximumSize=new Size(630,0),Margin=new Padding(3,4,3,4)});
            var b=new Button{Text=action,AutoSize=true,Padding=new Padding(6,1,6,1)};
            b.Click+=(s,e)=>Guard(()=>ShowPage(target)); panel.Controls.Add(b); parent.Controls.Add(panel);
        }
        static string Ask(IWin32Window owner,string title,string initial)
        {
            using(var dlg=new Form { Text=title,StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,
                MinimizeBox=false,MaximizeBox=false,ClientSize=new Size(430,92) }) {
                var input=new TextBox{Text=initial,Bounds=new Rectangle(12,13,405,27),MaxLength=80};
                var ok=new Button{Text="Сохранить",Bounds=new Rectangle(216,52,99,27),DialogResult=DialogResult.OK};
                var cancel=new Button{Text="Отмена",Bounds=new Rectangle(322,52,95,27),DialogResult=DialogResult.Cancel};
                dlg.Controls.AddRange(new Control[]{input,ok,cancel}); dlg.AcceptButton=ok; dlg.CancelButton=cancel; input.SelectAll();
                return dlg.ShowDialog(owner)==DialogResult.OK?input.Text:null;
            }
        }
        void ManageProfiles()
        {
            var original=profiles.Active;
            using(var dlg=new Form {Text="Профили Станций",Font=Font,Size=new Size(650,385),MinimumSize=new Size(530,335),StartPosition=FormStartPosition.CenterParent}) {
                var box=new ListBox {Dock=DockStyle.Fill,AccessibleName="Профили"};
                var bar=new FlowLayoutPanel {Dock=DockStyle.Bottom,Height=95,Padding=new Padding(6)};
                dlg.Controls.Add(box); dlg.Controls.Add(bar);
                Action reload=()=>{box.Items.Clear(); box.Items.AddRange(profiles.Items.ToArray()); box.SelectedItem=profiles.Active;};
                Action<string,Action> button=(caption,act)=>{var b=new Button {Text=caption,AutoSize=true};b.Click+=(s,e)=>Guard(act);bar.Controls.Add(b);};
                Func<Profile> sel=()=>(Profile)box.SelectedItem;
                button("Открыть",()=>{if(sel()!=null){profiles.Select(sel());dlg.DialogResult=DialogResult.OK;}});
                button("Создать",()=>{var name=Ask(dlg,"Название нового профиля","Новая Станция");if(name!=null){var p=profiles.Create(name);reload();box.SelectedItem=p;}});
                button("Переименовать",()=>{var p=sel();if(p==null)return;var name=Ask(dlg,"Изменить название",p.Name);if(name!=null){p.Rename(name);reload();box.SelectedItem=p;}});
                button("Ожидаемая модель",()=>{var p=sel();if(p==null)return;var val=Ask(dlg,"Ожидаемая модель устройства",p.Get("station.expected","YNDX-00026"));if(val!=null)p.Set("station.expected",val.Trim());});
                button("Дублировать",()=>{var p=sel();if(p==null)return;var name=Ask(dlg,"Название копии",p.Name+" — копия");if(name!=null){var copy=profiles.Duplicate(p,name);reload();box.SelectedItem=copy;}});
                button("Экспорт…",()=>{var p=sel();if(p==null)return;using(var f=new SaveFileDialog {Filter="Профиль JSON|*.json",FileName="station-profile.json"})if(f.ShowDialog(dlg)==DialogResult.OK)profiles.Export(p,f.FileName);});
                button("Импорт…",()=>{using(var f=new OpenFileDialog {Filter="Профиль JSON|*.json"})if(f.ShowDialog(dlg)==DialogResult.OK){var p=profiles.Import(f.FileName);reload();box.SelectedItem=p;}});
                reload();box.DoubleClick+=(s,e)=>{if(sel()!=null){profiles.Select(sel());dlg.DialogResult=DialogResult.OK;}};
                Style(dlg);dlg.ShowDialog(this);
                if(original!=profiles.Active) Rebuild();
                else {
                    refreshing=true;
                    try {chooser.Items.Clear();chooser.Items.AddRange(profiles.Items.ToArray());chooser.SelectedItem=original;}
                    finally {refreshing=false;}
                    bool visible=home.Visible;home.Dispose();home=Home();home.Dock=DockStyle.Fill;home.Visible=visible;
                    body.Controls.Add(home);Style(home);if(visible)home.BringToFront();
                }
            }
        }
    }

    internal sealed class Guide : Form
    {
        internal Guide()
        {
            Text="Руководство владельца"; StartPosition=FormStartPosition.CenterParent; Size=new Size(830,610);
            var topics=new ComboBox {Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="Раздел руководства"};
            var content=new RichTextBox {Dock=DockStyle.Fill,ReadOnly=true,BackColor=Color.White,Font=new Font("Segoe UI",11),BorderStyle=BorderStyle.None,DetectUrls=true};
            Controls.Add(content);Controls.Add(topics);
            string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"docs");
            var names=new Dictionary<string,string>{{"Первый запуск и передача колонки","owner.md"},{"Станция и восстановление","device.md"},{"Нейросети и голос","ai.md"},{"Музыка и голосовые команды","media.md"},{"Проверка сборки","verification.md"}};
            topics.SelectedIndexChanged+=(s,e)=>{string f=Path.Combine(dir,names[(string)topics.SelectedItem]); content.Text=File.Exists(f)?File.ReadAllText(f,Disk.Utf8):"Файл отсутствует в установленной копии.";};
            topics.Items.AddRange(names.Keys.ToArray());topics.SelectedIndex=0;
            Shell.Style(this);
        }
    }
}
