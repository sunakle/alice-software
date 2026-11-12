using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AliceDesk {
    internal sealed class PendingHomeAck {
        public string origin {get;set;}
        public string id {get;set;}
        public string result {get;set;}
    }

    public sealed class AgentPage : UserControl {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string TokenKey = "agent.haToken";
        const string AlternateTokenKey = "agent.haToken2";
        const string SecondAlternateTokenKey = "agent.haToken3";
        readonly Profile profile;
        readonly ListBox definitions;
        readonly TextBox id, phrases, target, manual, server, token, pair, journal;
        readonly ComboBox type, network;
        readonly Label netStatus;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval=2000 };
        List<DeskAction> actions;
        MediaHome home;
        CancellationTokenSource session;
        CancellationTokenSource connecting;
        bool polling;
        string lastState;
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
            server=MediaUi.Field(b,ActiveOrigin(),395);
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
            if (ActiveEnabled()) {
                try {
                    var root=MediaHome.Address(ActiveOrigin()); var key=profile.Secret(ActiveTokenKey());
                    if (String.IsNullOrEmpty(key)) throw new InvalidOperationException("Нет токена. Подключите HA заново.");
                    session=new CancellationTokenSource(); home=new MediaHome(root,key,ActiveNetwork());
                    netStatus.Text="Проверяю сохранённое подключение…"; timer.Start();
                } catch (Exception ex) { netStatus.Text=ex.Message; }
            }
        }
        void LoadNetworks() {
            var selected=network.SelectedItem as MediaHome.Adapter;
            string preferred=selected==null ? ActiveNetwork() : selected.Id;
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
                Shell.Style(f);
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
            lastState=null; retryAt=DateTime.MinValue;
        }
        bool UseNewConnection() {
            // Older releases ignore "v2", but their revoke and reconnect actions
            // replace it. Bind the route to the current pairing ID as well.
            string pairId=profile.Get("local.agent.pair","");
            return pairId.Length>0 && profile.Get("local.agent.haV2","")=="1" &&
                profile.Get("local.agent.haEnabled","")=="v2" &&
                profile.Get("local.agent.haV2Pair","")==pairId;
        }
        string ActiveOrigin() {
            return profile.Get(UseNewConnection() ? "local.agent.haV2Origin" : "local.agent.haOrigin","");
        }
        string ActiveNetwork() {
            return profile.Get(UseNewConnection() ? "local.agent.haV2Network" : "local.agent.haNetwork","");
        }
        bool ActiveEnabled() {
            return profile.Get(UseNewConnection() ? "local.agent.haV2Enabled" : "local.agent.haEnabled","")=="1";
        }
        string ActiveTokenKey() {
            if(!UseNewConnection())return TokenKey;
            string slot=profile.Get("local.agent.haV2TokenSlot","");
            if(slot=="2")return AlternateTokenKey;
            if(slot=="3")return SecondAlternateTokenKey;
            throw new InvalidDataException("Повреждена привязка токена Home Assistant в профиле.");
        }
        void CancelConnectionCheck() {
            var previous=connecting;
            connecting=null;
            if(previous!=null)previous.Cancel();
        }
        void SaveVerifiedConnection(Uri root, string adapterId, string credential, string activeKey) {
            // The current route continues to use its existing slot until all new
            // settings and the inactive credential have been written successfully.
            string nextKey=activeKey==AlternateTokenKey ? SecondAlternateTokenKey : AlternateTokenKey;
            string pairId=profile.Get("local.agent.pair","");
            if(pairId.Length==0)throw new InvalidDataException("Нет идентификатора адресата Home Assistant.");
            profile.PutSecret(nextKey,credential);
            profile.SetMany(new Dictionary<string,string> {
                {"local.agent.haV2","1"},{"local.agent.haV2Origin",root.AbsoluteUri},
                {"local.agent.haV2Network",adapterId},{"local.agent.haV2Enabled","1"},
                {"local.agent.haV2TokenSlot",nextKey==AlternateTokenKey ? "2" : "3"},{"local.agent.haV2Pair",pairId},
                // Legacy versions only know the original key. Never let them send
                // that credential to the new origin after a downgrade.
                {"local.agent.haOrigin",null},{"local.agent.haNetwork",null},{"local.agent.haEnabled","v2"}
            });
        }
        List<PendingHomeAck> PendingAcks() {
            string value=profile.Get("local.agent.pendingAcks","");
            if(value.Length==0)return new List<PendingHomeAck>();
            var entries=MediaCommands.Serializer.Deserialize<List<PendingHomeAck>>(value);
            if(entries==null || entries.Any(item=>item==null ||
                !Regex.IsMatch(item.id??"", @"\A[A-Za-z0-9_-]{8,64}\z") ||
                (item.result!="ok" && item.result!="denied" && item.result!="unknown") ||
                !ValidAckOrigin(item.origin)))
                throw new InvalidDataException("Повреждён журнал ответов Home Assistant.");
            return entries;
        }
        static bool ValidAckOrigin(string origin) {
            try {return MediaHome.Address(origin).AbsoluteUri==origin;}
            catch(InvalidOperationException) {return false;}
        }
        void RemovePendingAck(string origin, string id) {
            var entries=PendingAcks();
            entries.RemoveAll(item=>item.origin==origin && item.id==id);
            profile.Set("local.agent.pendingAcks",entries.Count==0 ? null : MediaCommands.Serializer.Serialize(entries));
        }
        async Task Connect() {
            CancellationTokenSource check=null, newSession=null; MediaHome candidate=null;
            try {
                CancelConnectionCheck();
                Uri root=MediaHome.Address(server.Text.Trim());
                var adapter=network.SelectedItem as MediaHome.Adapter;
                if(adapter==null)throw new InvalidOperationException("Выберите интерфейс Windows для подключения к HA.");
                string saved=ActiveOrigin(), entered=token.Text.Trim();
                string activeKey=ActiveTokenKey();
                if (entered.Length==0 && saved==root.AbsoluteUri) entered=profile.Secret(activeKey);
                if (String.IsNullOrEmpty(entered)) throw new InvalidOperationException("Введите отдельный токен для этого адреса.");
                check=new CancellationTokenSource();
                connecting=check;
                netStatus.Text="Проверяю защищённое соединение и два помощника…";
                candidate=new MediaHome(root,entered,adapter.Id); await candidate.Verify(check.Token);
                if (check!=connecting || check.IsCancellationRequested || IsDisposed) return;

                SaveVerifiedConnection(root,adapter.Id,entered,activeKey);
                newSession=new CancellationTokenSource();
                Stop();
                session=newSession; newSession=null;
                home=candidate; candidate=null; token.Clear(); netStatus.Text="Подключено. Ожидаю команду через Home Assistant."; timer.Start();
            } catch (Exception ex) {
                if (!IsDisposed && (check==null || check==connecting)) netStatus.Text="Подключение не выполнено. "+ex.Message;
            } finally {
                if(check==connecting)connecting=null;
                if(check!=null)check.Dispose();
                if(newSession!=null)newSession.Dispose();
                if(candidate!=null)candidate.Dispose();
            }
        }
        async Task Tick() {
            var lease=session; var conn=home;
            if (polling || lease==null || conn==null || DateTime.UtcNow<retryAt) return;
            var ct=lease.Token; polling=true;
            try {
                ct.ThrowIfCancellationRequested();
                string origin=ActiveOrigin();
                var outstanding=PendingAcks().FirstOrDefault(item=>item.origin==origin);
                if(outstanding!=null) {
                    await conn.Ack(outstanding.id,outstanding.result,ct);
                    if(session!=lease || ct.IsCancellationRequested || IsDisposed)return;
                    RemovePendingAck(outstanding.origin,outstanding.id);
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
                // Reserve the event and record an unknown result in the same atomic write.
                // If the process stops during the action, restart reports uncertainty
                // instead of executing it again or claiming it succeeded.
                var entries=PendingAcks();
                if(entries.Count>=512)throw new InvalidOperationException(
                    "Очередь ответов HA заполнена. Подключите прежние серверы для отправки ответов или отзовите доступ.");
                var pending=new PendingHomeAck {origin=origin,id=ev,result="unknown"};
                entries.Add(pending);
                string journal;
                try { journal=MediaCommands.Serializer.Serialize(entries); }
                catch(InvalidOperationException) {
                    throw new InvalidOperationException("Очередь ответов HA заполнена. Подключите прежние серверы для отправки ответов или отзовите доступ.");
                }
                profile.SetMany(new Dictionary<string,string> {
                    {"local.agent.haSeen",recent},
                    {"local.agent.pendingAcks",journal}
                });
                try { Execute(cmd); pending.result="ok"; netStatus.Text="Windows приняла действие «"+cmd+"»."; }
                catch (Exception ex) { pending.result="denied"; netStatus.Text="Команда отклонена. "+ex.Message; Log(netStatus.Text); }
                profile.Set("local.agent.pendingAcks",MediaCommands.Serializer.Serialize(entries));
                await conn.Ack(ev,pending.result,ct);
                if(session==lease && !ct.IsCancellationRequested && !IsDisposed)RemovePendingAck(origin,ev);
            } catch (Exception ex) {
                if (session==lease && !IsDisposed) { netStatus.Text="HA недоступен. "+ex.Message+" Повторю через 10 секунд."; retryAt=DateTime.UtcNow.AddSeconds(10); lastState=null; }
            } finally { polling=false; }
        }
        void Revoke() {
            CancelConnectionCheck();
            Stop(); profile.SetMany(new Dictionary<string,string> {
                {"local.agent.haEnabled",null},{"local.agent.haOrigin",null},{"local.agent.haNetwork",null},
                {"local.agent.haV2",null},{"local.agent.haV2Enabled",null},{"local.agent.haV2Origin",null},
                {"local.agent.haV2Network",null},{"local.agent.haV2TokenSlot",null},{"local.agent.haV2Pair",null},
                {"local.agent.haSeen",null},{"local.agent.pendingAcks",null}
            });
            profile.PutSecret(TokenKey,""); profile.PutSecret(AlternateTokenKey,""); profile.PutSecret(SecondAlternateTokenKey,"");
            pair.Text=Guid.NewGuid().ToString("N"); profile.Set("local.agent.pair",pair.Text); token.Clear();
            netStatus.Text="Агент отключён. Прежний адресат отозван. Удалите токен в профиле пользователя Home Assistant.";
        }
        protected override void Dispose(bool disposing) {
            if (disposing) { CancelConnectionCheck(); Stop(); timer.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
