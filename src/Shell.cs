using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AliceDesk
{
    internal sealed class Shell : BigSurWindow
    {
        readonly ProfileStore profiles;
        readonly ProfileButton profileButton=new ProfileButton();
        readonly Panel body=new Panel();
        readonly FlowLayoutPanel nav=new FlowLayoutPanel();
        readonly SectionSearch search=new SectionSearch();
        readonly Label title=new Label(), noMatches=new Label();
        readonly HistoryButton back=new HistoryButton("back","Назад"), forward=new HistoryButton("forward","Вперёд");
        readonly Dictionary<string,SidebarButton> links=new Dictionary<string,SidebarButton>();
        readonly Dictionary<string,string> keywords=new Dictionary<string,string>();
        readonly List<string> history=new List<string>();
        readonly ToolTip tips=new ToolTip();
        readonly NotifyIcon tray=new NotifyIcon();
        readonly bool background;
        int cursor=-1;
        Control home;
        DevicePage station;
        AiPage ai;
        AgentPage agent;
        MediaPage music;

        internal Shell(ProfileStore store, bool background)
        {
            this.profiles=store;
            this.background=background;
            Text="Пульт Станции";
            Icon=Icon.ExtractAssociatedIcon(typeof(Shell).Assembly.Location);
            Font=new Font("Segoe UI",10);
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            StartPosition=FormStartPosition.CenterScreen;
            var area=Screen.PrimaryScreen.WorkingArea;
            Size=new Size(Math.Min(1160,area.Width),Math.Min(800,area.Height));
            MinimumSize=new Size(Math.Min(850,area.Width),Math.Min(575,area.Height));
            BackColor=Color.FromArgb(249,248,251);ForeColor=ShellArt.Ink;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=1,ColumnCount=2,Margin=Padding.Empty,Padding=Padding.Empty};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,212));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Controls.Add(layout);
            var side=new ShellSidebar {Dock=DockStyle.Fill,Margin=Padding.Empty};
            layout.Controls.Add(side,0,0);
            SetSidebar(side);
            var content=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty,BackColor=Color.FromArgb(249,248,251)};
            layout.Controls.Add(content,1,0);
            body.Dock=DockStyle.Fill;content.Controls.Add(body);
            var toolbar=Toolbar();content.Controls.Add(toolbar);
            var trayMenu=new ContextMenuStrip();
            trayMenu.Items.Add("Открыть",null,(s,e)=>RestoreWindow());
            trayMenu.Items.Add("Завершить работу",null,(s,e)=>Close());
            tray.Icon=Icon;tray.Text=Text;tray.ContextMenuStrip=trayMenu;
            tray.DoubleClick+=(s,e)=>RestoreWindow();
            Resize+=delegate {if(WindowState==FormWindowState.Minimized){tray.Visible=true;Hide();}};
            Rebuild();
        }
        void SetSidebar(Panel side)
        {
            nav.FlowDirection=FlowDirection.TopDown;nav.WrapContents=false;nav.AutoScroll=true;
            nav.Dock=DockStyle.Fill;nav.Padding=new Padding(11,4,10,8);nav.BackColor=Color.Transparent;
            side.Controls.Add(nav);
            var header=new Panel {Dock=DockStyle.Top,Height=89,BackColor=Color.Transparent};
            side.Controls.Add(header);EnableTitleDrag(header);
            var lights=CreateWindowButtons();lights.Location=new Point(15,14);header.Controls.Add(lights);
            var brand=new Label {Text="Пульт Станции",AutoSize=true,Location=new Point(19,54),BackColor=Color.Transparent,
                ForeColor=ShellArt.Ink,Font=new Font("Segoe UI Semibold",14)};
            header.Controls.Add(brand);EnableTitleDrag(brand);
            Group("Управление");
            AddNav("home","Обзор","главная домой начало");
            AddNav("station","Станция","подключение колонка ip ssh adb прошивка свет озвучка");
            AddNav("ai","Нейросети","deepseek дипсик ollama llm чат api модели голос");
            AddNav("agent","Команды ПК","chrome хром программы home assistant голос команды");
            AddNav("music","Музыка","spotify спотифай звук bluetooth блютуз");
            Group("Инструменты");
            AddNav("extensions","Расширения","каталог возможности плагины airplay часы");
            AddNav("recovery","Восстановление","копии диагностика резервное хранение пакеты");
            AddNav("help","Руководство","справка инструкции помощь");
            noMatches.Text="Разделы не найдены";noMatches.AutoSize=true;noMatches.ForeColor=ShellArt.Muted;
            noMatches.Margin=new Padding(8,8,0,0);noMatches.Visible=false;nav.Controls.Add(noMatches);
            profileButton.Height=65;profileButton.Dock=DockStyle.Bottom;
            profileButton.Click+=(s,e)=>OpenProfileMenu();side.Controls.Add(profileButton);
            tips.SetToolTip(profileButton,"Переключить профиль или открыть настройки профилей");
        }
        Control Toolbar()
        {
            var bar=new Panel {Dock=DockStyle.Top,Height=55,BackColor=Color.FromArgb(248,248,250)};
            var line=new Panel{BackColor=Color.FromArgb(222,221,228),Height=1,Dock=DockStyle.Bottom};
            bar.Controls.Add(line);EnableTitleDrag(bar);
            back.Location=new Point(11,11);forward.Location=new Point(43,11);
            back.Click+=(s,e)=>MoveHistory(-1);forward.Click+=(s,e)=>MoveHistory(1);
            tips.SetToolTip(back,"Назад (Alt+←)");tips.SetToolTip(forward,"Вперёд (Alt+→)");
            bar.Controls.Add(back);bar.Controls.Add(forward);
            title.AutoSize=true;title.Font=new Font("Segoe UI Semibold",12);title.ForeColor=ShellArt.Ink;
            title.Location=new Point(82,15);bar.Controls.Add(title);EnableTitleDrag(title);
            bar.Controls.Add(search);
            bar.Layout+=(s,e)=>search.Location=new Point(bar.ClientSize.Width-search.Width-bar.Height/3,(bar.Height-search.Height)/2);
            search.Input.TextChanged+=(s,e)=>FilterSidebar();
            search.Input.KeyDown+=(s,e)=>{
                if(e.KeyCode==Keys.Escape){search.Input.Clear();ActiveControl=null;e.SuppressKeyPress=true;}
                if(e.KeyCode==Keys.Enter || e.KeyCode==Keys.Down){
                    var match=links.Values.FirstOrDefault(b=>b.Visible);
                    if(match!=null) {if(e.KeyCode==Keys.Enter)Guard(()=>ShowPage(match.Page));else match.Focus();}
                    else if(ProfileMatches())Guard(()=>ShowPage("profiles"));
                    e.SuppressKeyPress=true;
                }
            };
            tips.SetToolTip(search.Input,"Найти раздел (Ctrl+K)");return bar;
        }
        void Group(string name)
        {
            nav.Controls.Add(new Label {Text=name,AutoSize=true,BackColor=Color.Transparent,ForeColor=ShellArt.Muted,
                Font=new Font("Segoe UI Semibold",8.5f),Margin=new Padding(9,13,0,3)});
        }
        void AddNav(string id,string caption,string aliases)
        {
            var button=new SidebarButton(id,caption) {Size=new Size(187,33),Margin=new Padding(0,1,0,1)};
            button.Click+=(s,e)=>Guard(()=>ShowPage(id));
            nav.Controls.Add(button);links.Add(id,button);keywords.Add(id,caption+" "+aliases);
        }
        bool ProfileMatches()
        {
            string term=search.Input.Text.Trim();
            return term.Length>0 && ("Профили и обмен импорт экспорт " + profiles.Active.Name).IndexOf(term,StringComparison.CurrentCultureIgnoreCase)>=0;
        }
        void FilterSidebar()
        {
            string term=search.Input.Text.Trim();int count=0;
            nav.SuspendLayout();
            foreach(var pair in links) {
                bool show=term.Length==0 || keywords[pair.Key].IndexOf(term,StringComparison.CurrentCultureIgnoreCase)>=0;
                pair.Value.Visible=show;if(show)count++;
            }
            foreach(Control c in nav.Controls)if(c is Label && c!=noMatches)c.Visible=term.Length==0;
            noMatches.Text=ProfileMatches()?"Enter — открыть профили":"Разделы не найдены";
            noMatches.Visible=count==0 && term.Length>0;
            nav.ResumeLayout();
        }
        void SyncProfile()
        {
            profileButton.Text=profiles.Active.Name;profileButton.AccessibleName="Текущий профиль — "+profiles.Active.Name+". Выбрать профиль";
            profileButton.Invalidate();
        }
        void OpenProfileMenu()
        {
            var menu=new ContextMenuStrip {ShowImageMargin=false,ShowCheckMargin=true};
            foreach(var entry in profiles.Items){
                var p=entry;
                var item=new ToolStripMenuItem(p.Name.Replace("&","&&")){Checked=p==profiles.Active};
                item.Click+=(s,e)=>Guard(()=>{if(profiles.Active!=p){profiles.Select(p);Rebuild();}});
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());menu.Items.Add("Профили и обмен…",null,(s,e)=>Guard(()=>ShowPage("profiles")));
            menu.Closed+=(s,e)=>BeginInvoke((Action)menu.Dispose);
            menu.Show(profileButton,new Point(8,0),ToolStripDropDownDirection.AboveRight);
        }
        protected override bool ProcessCmdKey(ref Message msg,Keys keys)
        {
            if(keys==(Keys.Control|Keys.K)){search.Input.Focus();search.Input.SelectAll();return true;}
            if(keys==(Keys.Alt|Keys.Left)){MoveHistory(-1);return true;}
            if(keys==(Keys.Alt|Keys.Right)){MoveHistory(1);return true;}
            return base.ProcessCmdKey(ref msg,keys);
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
            if(disposing) { tray.Visible=false; tray.Dispose(); tips.Dispose(); }
            base.Dispose(disposing);
        }
        void Guard(Action run)
        {
            try {run();} catch(Exception ex) { MessageBox.Show(this, ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning); }
        }
        void Rebuild()
        {
            SyncProfile();
            while(body.Controls.Count>0)body.Controls[0].Dispose();
            home=Home();
            station=new DevicePage(profiles.Active);ai=new AiPage(profiles.Active);
            agent=new AgentPage(profiles.Active);music=new MediaPage(profiles.Active);
            foreach(var view in new Control[]{home,station,ai,agent,music}){
                view.Dock=DockStyle.Fill;view.Visible=false;body.Controls.Add(view);
                if(view!=home)Style(view);
            }
            history.Clear();cursor=-1;ShowPage("home");
        }
        internal static void Style(Control root){BigSurTheme.Apply(root);}
        void ShowPage(string id)
        {
            search.Input.Clear();
            if(id=="profiles"){ManageProfiles();return;}
            if(id=="help"){using(var guide=new Guide())guide.ShowDialog(this);return;}
            if(!links.ContainsKey(id))return;
            if(cursor<0 || history[cursor]!=id){
                if(cursor+1<history.Count)history.RemoveRange(cursor+1,history.Count-cursor-1);
                history.Add(id);cursor=history.Count-1;
            }
            DisplayPage(id);
        }
        void MoveHistory(int change)
        {
            int next=cursor+change;if(next<0 || next>=history.Count)return;
            search.Input.Clear();cursor=next;DisplayPage(history[cursor]);
        }
        void DisplayPage(string id)
        {
            Control next=id=="ai"?(Control)ai:id=="agent"?agent:id=="music"?music:id=="home"?home:(Control)station;
            foreach(Control page in body.Controls)page.Visible=page==next;
            next.BringToFront();
            if(next==station)station.Navigate(id);
            title.Text=links[id].Text;back.Enabled=cursor>0;forward.Enabled=cursor+1<history.Count;
            foreach(var link in links.Values){
                link.Selected=link.Page==id;link.AccessibleDescription=link.Selected?"Текущий раздел":null;link.Invalidate();
            }
        }
        Control Home(){return new BigSurHome(profiles.Active,id=>Guard(()=>ShowPage(id)));}
        static string Ask(IWin32Window owner,string title,string initial)
        {
            using(var dlg=new Form { Text=title,StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,
                MinimizeBox=false,MaximizeBox=false,ClientSize=new Size(430,92) }) {
                var input=new TextBox{Text=initial,Bounds=new Rectangle(12,13,405,27),MaxLength=80};
                var ok=new SettingsButton{Text="Сохранить",AutoSize=false,Bounds=new Rectangle(216,49,99,31),DialogResult=DialogResult.OK};
                var cancel=new SettingsButton{Text="Отмена",AutoSize=false,Bounds=new Rectangle(322,49,95,31),DialogResult=DialogResult.Cancel};
                dlg.Controls.AddRange(new Control[]{input,ok,cancel}); Style(dlg); dlg.AcceptButton=ok; dlg.CancelButton=cancel; input.SelectAll();
                return dlg.ShowDialog(owner)==DialogResult.OK?input.Text:null;
            }
        }
        void ManageProfiles()
        {
            var original=profiles.Active;
            using(var dlg=new Sheet("Профили Станций") {Size=new Size(690,410),MinimumSize=new Size(570,370)}) {
                var box=new ListBox {Dock=DockStyle.Fill,AccessibleName="Профили"};
                var bar=new FlowLayoutPanel {Dock=DockStyle.Bottom,Height=90,Padding=new Padding(3,8,0,0)};
                dlg.Page.Controls.Add(box);dlg.Page.Controls.Add(bar);
                Action reload=()=>{box.Items.Clear(); box.Items.AddRange(profiles.Items.ToArray()); box.SelectedItem=profiles.Active;};
                Action<string,Action> button=(caption,act)=>{var b=new SettingsButton {Text=caption};b.Click+=(s,e)=>Guard(act);bar.Controls.Add(b);};
                Func<Profile> sel=()=>(Profile)box.SelectedItem;
                button("Открыть",()=>{if(sel()!=null){profiles.Select(sel());dlg.DialogResult=DialogResult.OK;}});
                button("Создать",()=>{var name=Ask(dlg,"Название нового профиля","Новая Станция");if(name!=null){var p=profiles.Create(name);reload();box.SelectedItem=p;}});
                button("Переименовать",()=>{var p=sel();if(p==null)return;var name=Ask(dlg,"Изменить название",p.Name);if(name!=null){p.Rename(name);reload();box.SelectedItem=p;}});
                button("Ожидаемая модель",()=>{var p=sel();if(p==null)return;var val=Ask(dlg,"Ожидаемая модель устройства",p.Get("station.expected","YNDX-00026"));if(val!=null)p.Set("station.expected",val.Trim());});
                button("Дублировать",()=>{var p=sel();if(p==null)return;var name=Ask(dlg,"Название копии",p.Name+" — копия");if(name!=null){var copy=profiles.Duplicate(p,name);reload();box.SelectedItem=copy;}});
                button("Экспорт…",()=>{var p=sel();if(p==null)return;using(var f=new SaveFileDialog {Filter="Профиль JSON|*.json",FileName="station-profile.json"})if(f.ShowDialog(dlg)==DialogResult.OK)profiles.Export(p,f.FileName);});
                button("Импорт…",()=>{using(var f=new OpenFileDialog {Filter="Профиль JSON|*.json"})if(f.ShowDialog(dlg)==DialogResult.OK){var p=profiles.Import(f.FileName);reload();box.SelectedItem=p;}});
                reload();box.DoubleClick+=(s,e)=>{if(sel()!=null){profiles.Select(sel());dlg.DialogResult=DialogResult.OK;}};
                Style(dlg.Page);dlg.ShowDialog(this);
                if(original!=profiles.Active) Rebuild();
                else {
                    SyncProfile();
                    bool visible=home.Visible;home.Dispose();home=Home();home.Dock=DockStyle.Fill;home.Visible=visible;
                    body.Controls.Add(home);if(visible)home.BringToFront();
                }
            }
        }
    }

    internal class Sheet : BigSurWindow
    {
        internal readonly Panel Page=new Panel {Dock=DockStyle.Fill,Padding=new Padding(16),BackColor=Color.FromArgb(246,246,249)};
        internal Sheet(string heading)
        {
            Text=heading;StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;
            Font=new Font("Segoe UI",10);BackColor=Color.FromArgb(248,248,251);
            var bar=new Panel{Dock=DockStyle.Top,Height=53,Padding=new Padding(16,8,14,8),BackColor=BackColor};
            var name=new Label{Text=heading,AutoSize=true,Font=new Font("Segoe UI Semibold",13),ForeColor=ShellArt.Ink,Location=new Point(17,12)};
            var done=new SettingsButton{Text="Готово",Dock=DockStyle.Right,DialogResult=DialogResult.Cancel};
            bar.Controls.Add(name);bar.Controls.Add(done);CancelButton=done;Controls.Add(Page);Controls.Add(bar);EnableTitleDrag(bar);
        }
    }

    internal sealed class Guide : Sheet
    {
        internal Guide() : base("Руководство владельца")
        {
            Text="Руководство владельца"; StartPosition=FormStartPosition.CenterParent; Size=new Size(830,610);
            var topics=new SettingsCombo {Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="Раздел руководства"};
            var content=new RichTextBox {Dock=DockStyle.Fill,ReadOnly=true,BackColor=Color.White,Font=new Font("Segoe UI",11),BorderStyle=BorderStyle.None,DetectUrls=true};
            Page.Controls.Add(content);Page.Controls.Add(topics);
            string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"docs");
            var names=new Dictionary<string,string>{{"Первый запуск и передача колонки","owner.md"},{"Станция и восстановление","device.md"},{"Нейросети и голос","ai.md"},{"Музыка и голосовые команды","media.md"},{"Проверка сборки","verification.md"}};
            topics.SelectedIndexChanged+=(s,e)=>{string f=Path.Combine(dir,names[(string)topics.SelectedItem]); content.Text=File.Exists(f)?File.ReadAllText(f,Disk.Utf8):"Файл отсутствует в установленной копии.";};
            topics.Items.AddRange(names.Keys.ToArray());topics.SelectedIndex=0;
            Shell.Style(Page);
        }
    }
}
