using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AliceDesk {
    public sealed class AgentPage : UserControl {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string TokenKey = "agent.haToken";
        readonly Profile profile;
        readonly ListBox definitions;
        readonly TextBox id, phrases, target, manual, server, token, pair, journal;
        readonly ComboBox type, network;
        readonly Label netStatus;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval=2000 };
        List<DeskAction> actions;
        MediaHome home;
        CancellationTokenSource session;
        bool polling;
        string lastState, pendingId, pendingResult;
        DateTime retryAt;
        public AgentPage(Profile p) {
            Dock=DockStyle.Fill; profile=p;
            string loadError=null;
            try { actions=MediaCommands.Load(p); }
            catch (Exception ex) { actions=new List<DeskAction>(); loadError="Не удалось загрузить команды профиля. "+ex.Message; }
            var tabs=new SettingsTabs();
            Controls.Add(tabs);
            var local=new TabPage("Команды этого ПК"); var ha=new TabPage("Подключение Home Assistant");
            tabs.TabPages.Add(local); tabs.TabPages.Add(ha);
            var b=MediaUi.Body(local);
            MediaUi.Line(b,"Команды этого ПК",true);
            MediaUi.Line(b,"Выберите действие или создайте своё. Разрешение на выполнение выдаётся на этом компьютере и не переносится с импортом профиля.");
            definitions=new ListBox { Width=525, Height=68 }; b.Controls.Add(definitions);
            var row=MediaUi.Row(b);
            MediaUi.Line(row,"ID"); id=MediaUi.Field(row,"",145);
            MediaUi.Line(row,"Тип"); type=new SettingsCombo { Width=160,DropDownStyle=ComboBoxStyle.DropDownList };
            type.Items.AddRange(new object[]{"Программа .exe","Сайт HTTPS","Медиаклавиша"}); row.Controls.Add(type);
            MediaUi.Line(b,"Фразы и синонимы через точку с запятой");
            phrases=MediaUi.Field(b,"",490);
            MediaUi.Line(b,"Цель. Для медиаклавиши допустимы play-pause, next, previous, mute, volume-up, volume-down.");
            row=MediaUi.Row(b); target=MediaUi.Field(row,"",380);
            MediaUi.Btn(row,"Выбрать .exe",delegate {
                using (var dialog=new OpenFileDialog { Filter="Программа (*.exe)|*.exe",CheckFileExists=true }) {
                    if (dialog.ShowDialog(this)==DialogResult.OK) { target.Text=dialog.FileName; type.SelectedIndex=0; }
                }
            });
            row=MediaUi.Row(b);
            MediaUi.Btn(row,"Новое",delegate { definitions.ClearSelected(); id.Text=""; phrases.Text=""; target.Text=""; type.SelectedIndex=0; id.Focus(); });
            MediaUi.Btn(row,"Сохранить и разрешить",delegate { Guard(Save); },true);
            MediaUi.Btn(row,"Удалить",delegate { Guard(Remove); });
            MediaUi.Line(b,"Испытать фразу вручную",true);
            row=MediaUi.Row(b); manual=MediaUi.Field(row,"открой хром",245);
            MediaUi.Btn(row,"Выполнить на этом ПК",delegate {
                Guard(delegate {
                    var a=MediaCommands.Resolve(actions,manual.Text); Log("Фраза совпала с «"+a.Id+"».");
                    Execute(a.Id);
                });
            });
            MediaUi.Btn(row,"Подтверждаю результат",delegate { Log("Пользователь подтвердил видимый результат на ПК."); });
            var autorun=new CheckBox { Text="Запускать агент при входе в Windows",AutoSize=true }; b.Controls.Add(autorun);
            using (var key=Registry.CurrentUser.OpenSubKey(RunKey)) autorun.Checked=key!=null && key.GetValue("StantsiyaDesk")!=null;
            autorun.CheckedChanged+=delegate { Guard(delegate {
                using (var key=Registry.CurrentUser.CreateSubKey(RunKey)) {
                    if (autorun.Checked) key.SetValue("StantsiyaDesk","\""+Application.ExecutablePath+"\" --agent");
                    else key.DeleteValue("StantsiyaDesk",false);
                }
                Log(autorun.Checked ? "Автозапуск включён для текущего пользователя." : "Автозапуск отключён.");
            }); };
            journal=new TextBox { Width=610,Height=85,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical }; b.Controls.Add(journal);
            if (loadError!=null) Log(loadError);
            b=MediaUi.Body(ha);
            MediaUi.Line(b,"Команды из Home Assistant",true);
            MediaUi.Line(b,"ПК сам подключается по HTTPS к выбранному домашнему серверу. Нужны действительный сертификат сервера и токен HA. Оба устройства должны работать. Агенту не нужен входящий порт.");
            MediaUi.Line(b,"Адрес домашнего сервера (например, https://ha.example.org:8123)");
            server=MediaUi.Field(b,profile.Get("local.agent.haOrigin",""),395);
            MediaUi.Line(b,"Домашний интерфейс Windows, через который доступен HA");
            row=MediaUi.Row(b); network=new SettingsCombo {Width=320, DropDownStyle=ComboBoxStyle.DropDownList};
            row.Controls.Add(network);
            MediaUi.Btn(row,"Обновить список",delegate { Guard(LoadNetworks); });
            try {LoadNetworks();} catch (Exception) { /* Local Windows commands remain available without a network interface. */ }
            MediaUi.Line(b,"Долгоживущий токен HA для этого ПК. Для сохранённого сервера можно оставить пустым.");
            token=MediaUi.Field(b,"",395); token.UseSystemPasswordChar=true;
            server.TextChanged+=delegate {token.Clear();};
            MediaUi.Line(b,"Адресат команды для примера YAML. Его можно скопировать.");
            string pairingId=profile.Get("local.agent.pair","");
            if (pairingId.Length==0) { pairingId=Guid.NewGuid().ToString("N"); profile.Set("local.agent.pair",pairingId); }
            pair=MediaUi.Field(b,pairingId,320); pair.ReadOnly=true;
            row=MediaUi.Row(b);
            MediaUi.Btn(row,"Проверить и подключить",async delegate { await Connect(); },true);
            MediaUi.Btn(row,"Отозвать доступ",delegate { Guard(Revoke); });
            MediaUi.Btn(row,"Инструкция Home Assistant",delegate { Guard(ShowGuide); });
            netStatus=MediaUi.Line(b,"Отключено. Локальные команды доступны.");
            MediaUi.Line(b,"Голос до прошивки",true);
            MediaUi.Line(b,"Независимый навык «ПК Мост» использует собственный клиент и код привязки. В обычной Алисе сначала вызовите навык. Для своего агента можно использовать Assist и сценарий из инструкции.");
            MediaUi.Btn(b,"Навык «ПК Мост»",delegate { Guard(delegate { MediaUi.Open("https://dialogs.yandex.ru/store/skills/0e1c9e99-moj-komp-yute"); }); });
            definitions.SelectedIndexChanged+=delegate { Edit(); };
            RefreshList(null);
            timer.Tick+=async delegate { await Tick(); };
            if (profile.Get("local.agent.haEnabled","")=="1") {
                try {
                    var root=MediaHome.Address(profile.Get("local.agent.haOrigin","")); var key=profile.Secret(TokenKey);
                    if (String.IsNullOrEmpty(key)) throw new InvalidOperationException("Нет токена. Подключите HA заново.");
                    session=new CancellationTokenSource(); home=new MediaHome(root,key,profile.Get("local.agent.haNetwork",""));
                    netStatus.Text="Проверяю сохранённое подключение…"; timer.Start();
                } catch (Exception ex) { netStatus.Text=ex.Message; }
            }
        }
        void LoadNetworks() {
            var selected=network.SelectedItem as MediaHome.Adapter;
            string preferred=selected==null ? profile.Get("local.agent.haNetwork","") : selected.Id;
            network.Items.Clear();
            foreach(var adapter in MediaHome.Adapters()) {
                int index=network.Items.Add(adapter);
                if(adapter.Id==preferred)network.SelectedIndex=index;
            }
        }
        void RefreshList(string select) {
            definitions.Items.Clear(); foreach (var a in actions) definitions.Items.Add(a);
            int i=actions.FindIndex(a=>a.Id==select);
            if (actions.Count>0) definitions.SelectedIndex=Math.Max(0,i);
            else { id.Text=phrases.Text=target.Text=""; type.SelectedIndex=0; }
        }
        void ShowGuide() {
            string docs=Path.Combine(Application.StartupPath,"docs");
            using (var f=new Form { Text="Настройка Home Assistant", StartPosition=FormStartPosition.CenterParent,
                Width=760,Height=560,MinimizeBox=false }) {
                var viewer=new TextBox { Dock=DockStyle.Fill, Multiline=true, ReadOnly=true,
                    ScrollBars=ScrollBars.Vertical, Text=File.ReadAllText(Path.Combine(docs,"home-assistant.md")).Replace("\r\n","\n").Replace("\n","\r\n") };
                f.Controls.Add(viewer);
                var folder=new Button { Text="Открыть папку с примерами YAML",Dock=DockStyle.Bottom,Height=34 };
                folder.Click+=delegate { Guard(delegate { MediaUi.Open(docs); }); }; f.Controls.Add(folder);
                f.ShowDialog(this);
            }
        }
        void Edit() {
            var a=definitions.SelectedItem as DeskAction; if (a==null) return;
            id.Text=a.Id; phrases.Text=a.Phrases; target.Text=a.Target;
            type.SelectedIndex=a.Kind=="url"?1:a.Kind=="media"?2:0;
        }
        void Save() {
            var a=MediaCommands.Validate(new DeskAction { Id=id.Text.Trim(),Phrases=phrases.Text.Trim(),Target=target.Text.Trim(),Kind=type.SelectedIndex==1?"url":type.SelectedIndex==2?"media":"exe" });
            var old=definitions.SelectedItem as DeskAction;
            if (actions.Any(x=>x!=old && (x.Id==a.Id || MediaCommands.Aliases(x.Phrases).Intersect(MediaCommands.Aliases(a.Phrases)).Any())))
                throw new InvalidOperationException("Такой ID или фраза уже назначены другому действию.");
            if (MessageBox.Show(this,"Разрешить «"+a.Id+"» запускать только эту цель?\r\n\r\n"+a.Target,
                "Разрешение на этом ПК",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes) return;
            string fingerprint=MediaCommands.Fingerprint(a);
            var updated=new List<DeskAction>(actions); if (old!=null) updated.Remove(old); updated.Add(a);
            var change=new Dictionary<string,string>{{"agent.actions",MediaCommands.Serializer.Serialize(updated)}};
            if(old!=null)change["local.agent.approved."+old.Id]=null;
            change["local.agent.approved."+a.Id]=fingerprint;
            profile.SetMany(change);
            actions=updated; RefreshList(a.Id); Log("Локально разрешено «"+a.Id+"».");
        }
        void Remove() {
            var a=definitions.SelectedItem as DeskAction; if (a==null) return;
            var updated=new List<DeskAction>(actions); updated.Remove(a);
            profile.SetMany(new Dictionary<string,string>{{"local.agent.approved."+a.Id,null},{"agent.actions",MediaCommands.Serializer.Serialize(updated)}});
            actions=updated; RefreshList(null);
            Log("Разрешение отозвано, действие удалено.");
        }
        void Guard(Action action) { try { action(); } catch (Exception ex) { Log("Отклонено. "+ex.Message); } }
        void Log(string line) {
            if (IsDisposed || journal==null) return;
            if (journal.TextLength>10000) journal.Clear();
            journal.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  "+line+Environment.NewLine);
        }
        void Execute(string actionId) {
            var a=actions.SingleOrDefault(x=>x.Id==actionId);
            if (a==null) throw new InvalidOperationException("Действие не настроено на этом ПК.");
            Log("Агент получил действие «"+a.Id+"».");
            MediaCommands.Run(a,profile.Get("local.agent.approved."+a.Id,""));
            Log("Windows приняла вызов для «"+a.Id+"».");
        }
        void Stop() {
            timer.Stop();
            var old=session; session=null;
            if (old!=null) { old.Cancel(); old.Dispose(); }
            if (home!=null) { home.Dispose(); home=null; }
            lastState=pendingId=pendingResult=null; retryAt=DateTime.MinValue;
        }
        async Task Connect() {
            CancellationTokenSource lease=null; MediaHome candidate=null;
            try {
                Uri root=MediaHome.Address(server.Text.Trim());
                var adapter=network.SelectedItem as MediaHome.Adapter;
                if(adapter==null)throw new InvalidOperationException("Выберите интерфейс Windows для подключения к HA.");
                string saved=profile.Get("local.agent.haOrigin",""), entered=token.Text.Trim();
                if (entered.Length==0 && saved==root.AbsoluteUri) entered=profile.Secret(TokenKey);
                if (String.IsNullOrEmpty(entered)) throw new InvalidOperationException("Введите отдельный токен для этого адреса.");
                Stop(); profile.Set("local.agent.haEnabled",""); lease=session=new CancellationTokenSource();
                var ct=lease.Token;
                netStatus.Text="Проверяю защищённое соединение и два помощника…";
                candidate=new MediaHome(root,entered,adapter.Id); await candidate.Verify(ct);
                if (lease!=session || IsDisposed) return;
                // Clear identity binding before replacing a credential. A failure between writes leaves no enabled route.
                profile.Set("local.agent.haOrigin",""); profile.PutSecret(TokenKey,entered);
                profile.Set("local.agent.haOrigin",root.AbsoluteUri); profile.Set("local.agent.haNetwork",adapter.Id);
                profile.Set("local.agent.haEnabled","1");
                home=candidate; candidate=null; token.Clear(); netStatus.Text="Подключено. Ожидаю команду через Home Assistant."; timer.Start();
            } catch (Exception ex) {
                if (!IsDisposed && (lease==null || lease==session)) netStatus.Text="Подключение не выполнено. "+ex.Message;
            } finally { if (candidate!=null) candidate.Dispose(); }
        }
        async Task Tick() {
            var lease=session; var conn=home;
            if (polling || lease==null || conn==null || DateTime.UtcNow<retryAt) return;
            var ct=lease.Token; polling=true;
            try {
                ct.ThrowIfCancellationRequested();
                if (pendingId!=null) {
                    await conn.Ack(pendingId,pendingResult,ct); ct.ThrowIfCancellationRequested();
                    pendingId=pendingResult=null;
                }
                string state=await conn.ReadCommand(ct);
                if (session!=lease || ct.IsCancellationRequested || IsDisposed) return;
                if (state==lastState) return;
                lastState=state; string ev,cmd; long now=MediaHome.Now();
                netStatus.Text="Канал HA подключён. Ожидаю команду для этого ПК.";
                if (!MediaHome.Decode(state,pair.Text,now,out ev,out cmd)) return;
                bool duplicate;
                string recent=MediaHome.Remember(profile.Get("local.agent.haSeen",""),ev,now,out duplicate);
                if (duplicate) return;
                // Persist before starting an application, including across agent restarts.
                profile.Set("local.agent.haSeen",recent);
                pendingId=ev;
                try { Execute(cmd); pendingResult="ok"; netStatus.Text="Windows приняла действие «"+cmd+"»."; }
                catch (Exception ex) { pendingResult="denied"; netStatus.Text="Команда отклонена. "+ex.Message; Log(netStatus.Text); }
                await conn.Ack(ev,pendingResult,ct);
                if (session==lease) pendingId=pendingResult=null;
            } catch (Exception ex) {
                if (session==lease && !IsDisposed) { netStatus.Text="HA недоступен. "+ex.Message+" Повторю через 10 секунд."; retryAt=DateTime.UtcNow.AddSeconds(10); lastState=null; }
            } finally { polling=false; }
        }
        void Revoke() {
            Stop(); profile.Set("local.agent.haEnabled",""); profile.Set("local.agent.haOrigin","");
            profile.Set("local.agent.haNetwork","");
            profile.PutSecret(TokenKey,""); profile.Set("local.agent.haSeen","");
            pair.Text=Guid.NewGuid().ToString("N"); profile.Set("local.agent.pair",pair.Text); token.Clear();
            netStatus.Text="Агент отключён. Прежний адресат отозван. Удалите токен в профиле пользователя Home Assistant.";
        }
        protected override void Dispose(bool disposing) {
            if (disposing) { Stop(); timer.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
