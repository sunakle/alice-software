using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AliceDesk
{
    public sealed class DevicePage : UserControl
    {
        readonly Profile profile;
        readonly Panel station=new Panel(), recovery=new Panel(), extensions=new Panel();
        readonly Label status=new Label { Dock=DockStyle.Bottom, AutoEllipsis=true, Height=46, Padding=new Padding(12,4,5,2), BackColor=Color.FromArgb(239,245,250) };
        readonly TextBox address=new TextBox {Width=235, AccessibleName="IP-адрес или имя станции"};
        readonly ComboBox protocol=new SettingsCombo {Width=95,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="Протокол подключения"};
        readonly NumericUpDown port=new NumericUpDown {Minimum=1,Maximum=65535,Width=82,AccessibleName="Порт"};
        readonly TextBox report=new TextBox {Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Height=185,Width=625,WordWrap=true,AccessibleName="Текущая диагностика"};
        readonly TextBox recoveryReport=new TextBox {Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Height=165,Width=625,WordWrap=true,AccessibleName="Результат обслуживания"};
        readonly Label keyLabel=new Label {AutoSize=true,MaximumSize=new Size(620,0)};
        readonly SemaphoreSlim operation=new SemaphoreSlim(1,1);
        readonly CancellationTokenSource life=new CancellationTokenSource();
        CancellationTokenSource running;
        DeviceTransport live; DeviceInventory facts;
        static readonly Dictionary<string,DevicePage> Active=new Dictionary<string,DevicePage>();
        string BackupDir {get{return Path.Combine(profile.DataDirectory,"station-backups");}}
        static decimal Port(string s,int fallback) {int n;return int.TryParse(s,out n)&&n>=1&&n<=65535?n:fallback;}
        public DevicePage(Profile p)
        {
            profile=p;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=Padding.Empty,Margin=Padding.Empty};
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var pages=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty};
            foreach(var panel in new[]{station,recovery,extensions}) {panel.Dock=DockStyle.Fill;pages.Controls.Add(panel);}
            status.Dock=DockStyle.Fill;status.Margin=new Padding(12,1,12,6);
            status.Height=38;status.Padding=new Padding(10,1,8,1);status.TextAlign=ContentAlignment.MiddleLeft;
            layout.Controls.Add(pages,0,0);layout.Controls.Add(status,0,1);Controls.Add(layout);
            var body=MediaUi.Body(station); MediaUi.Line(body,"Подключить Станцию",true);
            MediaUi.Line(body,"Попросите у друга адрес, порт, способ входа и исходные копии. Уточните, работает ли доступ после обычной перезагрузки.");
            var handed=MediaUi.Row(body);
            foreach(string item in new[]{"Доступ проверен после перезагрузки","Исходные копии получены"}) {
                string key=item.StartsWith("Доступ")?"station.handOffBoot":"station.handOffBackup";
                var cb=new CheckBox {Text=item,AutoSize=true,Checked=p.Get(key)=="1"};handed.Controls.Add(cb);
                cb.CheckedChanged+=delegate {p.Set(key,cb.Checked?"1":"0");};
            }
            var conn=MediaUi.Row(body);
            MediaUi.Line(conn,"Адрес");
            address.Text=p.Get("station.address");conn.Controls.Add(address);
            protocol.Items.AddRange(new object[]{"SSH","ADB"}); protocol.SelectedIndex=p.Get("station.transport","SSH")=="ADB"?1:0;
            conn.Controls.Add(protocol);MediaUi.Line(conn,"Порт");conn.Controls.Add(port);SetPort();
            protocol.SelectedIndexChanged+=delegate{SetPort();InvalidateLive();};address.TextChanged+=delegate{InvalidateLive();};port.ValueChanged+=delegate{InvalidateLive();};
            var auth=MediaUi.Row(body);
            Button(auth,"Ключ SSH…",delegate { PickFile(false); });
            Button(auth,"Выбрать adb.exe…",delegate { PickFile(true); });
            Button(auth,"OpenSSH для Windows",delegate {MediaUi.Open("https://learn.microsoft.com/windows-server/administration/openssh/openssh_install_firstuse");});
            Button(auth,"Android Platform Tools",delegate {MediaUi.Open("https://developer.android.com/tools/releases/platform-tools");});
            body.Controls.Add(keyLabel);ShowKey();
            var row=MediaUi.Row(body);Button(row,"Сохранить адрес",delegate {SaveAddress();Notice("Параметры адреса сохранены.");});
            Button(row,"Проверить и подключить",Connect,true);Button(row,"Отменить",Cancel);
            MediaUi.Line(body,"Проверка работает в домашней сети, интернет не нужен. Станция и ПК должны быть включены.");
            body.Controls.Add(report);
            MediaUi.Line(body,"Проверка звука и света",true);
            var phrase=MediaUi.Field(body,"Привет! Это тест с моего компьютера.",486);phrase.MaxLength=2000;phrase.AccessibleName="Текст для озвучки Алисы";
            var tests=MediaUi.Row(body);
            Button(tests,"Сказать текст",delegate {string s=phrase.Text;Start(c=>TestTts(s,c));});
            Button(tests,"Короткий тон",delegate {Start(c=>TestTone(c));});
            Button(tests,"Вспышка 2 с",delegate {Start(c=>TestLed(c));});
            MediaUi.Line(body,"Озвучка отправит текст Яндексу через штатную сессию колонки. Для тона и верхнего света достаточно домашней сети.");
            MediaUi.Line(body,"Короткая проба файла или радио",true);
            var audio=MediaUi.Row(body);var url=MediaUi.Field(audio,"",268);url.AccessibleName="Прямой HTTPS-адрес аудиопотока";
            Button(audio,"Радио 12 с",delegate{string stream=DeviceAudio.RadioUrl(url.Text);Start(c=>TestAudio(null,stream,c));});
            Button(audio,"Файл WAV…",delegate{
                using(var pick=new OpenFileDialog{Title="Короткий аудиофайл до 1 МиБ",Filter="Звук WAV|*.wav"})if(pick.ShowDialog(this)==DialogResult.OK){
                    if(new FileInfo(pick.FileName).Length>DeviceAudio.MaxWave)throw new IOException("Файл превышает 1 МиБ.");
                    byte[] input=File.ReadAllBytes(pick.FileName);DeviceAudio.ValidateWave(input);Start(c=>TestAudio(input,null,c));
                }
            });
            BuildRecovery();
            extensions.Controls.Add(new ExtensionsView(p){Dock=DockStyle.Fill});
            lock(Active) Active[p.Id]=this;
            Navigate("station");Notice("Не подключено. Укажите адрес одной Станции.");
        }
        void SetPort(){bool adb=protocol.SelectedIndex==1;port.Value=Port(profile.Get(adb?"station.adbPort":"station.sshPort"),adb?5555:2222);}
        void InvalidateLive(){if(running!=null)running.Cancel();live=null;facts=null;Notice("Параметры изменились. Нужна новая проверка подключения.");}
        void SaveAddress() {profile.Set("station.address",address.Text.Trim());profile.Set("station.transport",protocol.Text);profile.Set(protocol.SelectedIndex==1?"station.adbPort":"station.sshPort",port.Value.ToString());}
        void ShowKey(){keyLabel.Text="Ключ SSH — "+(profile.Get("local.device.key")==""?"стандартный пользователя Windows":Path.GetFileName(profile.Get("local.device.key")));}
        void PickFile(bool adb) {
            using(var pick=new OpenFileDialog{Title=adb?"Выберите adb.exe из официальных Platform Tools":"Приватный ключ SSH владельца (не .pub)",Filter=adb?"Android Debug Bridge|adb.exe":"Ключ владельца|*.*"}){
                if(pick.ShowDialog(this)!=DialogResult.OK)return;
                if(adb && !string.Equals(Path.GetFileName(pick.FileName),"adb.exe",StringComparison.OrdinalIgnoreCase))throw new IOException("Выберите официальный adb.exe.");
                if(!adb && pick.FileName.EndsWith(".pub",StringComparison.OrdinalIgnoreCase))throw new IOException("Выбран публичный файл. Для входа выберите соответствующий приватный ключ.");
                if(new Uri(pick.FileName).IsUnc)throw new IOException("Выберите локальный файл Windows.");
                profile.Set(adb?"local.device.adb":"local.device.key",Path.GetFullPath(pick.FileName));ShowKey();InvalidateLive();
            }
        }
        private void BuildRecovery()
        {
            var body=MediaUi.Body(recovery);MediaUi.Line(body,"Диагностика и восстановление",true);
            MediaUi.Line(body,"Отчёт содержит характеристики платы и состояния служб. Содержимое конфигов, ключи, адрес и идентификатор владельца в экспорт не входят.");
            var b=MediaUi.Row(body);Button(b,"Повторить диагностику",Connect);Button(b,"Сохранить отчёт…",ExportReport);Button(b,"Отмена",Cancel);
            MediaUi.Line(body,"Пакеты на станции",true);
            var row=MediaUi.Row(body);var query=MediaUi.Field(row,"",180);query.AccessibleName="Часть имени пакета";
            Button(row,"Список и версии",delegate{Start(c=>Packages("",false,c));});
            Button(row,"Найти пакет",delegate{string q=query.Text;Start(c=>Packages(q,true,c));});
            MediaUi.Line(body,"Резервная копия с метаданными",true);
            var files=MediaUi.Row(body);var cb=new SettingsCombo{Width=315,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="Файл станции"};
            cb.Items.AddRange(new object[]{DeviceBackup.Sources,DeviceBackup.Launch});cb.SelectedIndex=0;files.Controls.Add(cb);
            Button(files,"Снять копию",delegate{string path=cb.Text;Start(c=>Backup(path,c));});
            var rb=MediaUi.Row(body);Button(rb,"Сравнить с копией…",delegate{ChooseBackup(false);});Button(rb,"Вернуть реестр yse…",delegate{ChooseBackup(true);});
            MediaUi.Line(body,"Копии хранятся на этом ПК, зашифрованы для вашей учётной записи Windows. Возврат поддержан для существующего файла sources через SSH на той же плате и сборке.");
            Button(body,"Инструкция по восстановлению HackAlice",delegate{MediaUi.Open("https://hackalice.me/start/recovery/");});
            body.Controls.Add(recoveryReport);
        }
        void Button(Control parent,string text,Action click,bool primary=false) {
            MediaUi.Btn(parent,text,delegate{try{click();}catch(Exception ex){Notice(Message(ex));}},primary);
        }
        public void Navigate(string section) {
            Control next=section=="recovery"?recovery:section=="extensions"?extensions:station;
            Control parent=next.Parent;
            parent.SuspendLayout();
            try {
                // Put the destination in place before removing the current page.
                next.Visible=true;
                next.BringToFront();
                foreach(var page in new[]{station,recovery,extensions})
                    if(page!=next && page.Visible)page.Visible=false;
            } finally { parent.ResumeLayout(true); }
        }
        protected override void Dispose(bool disposing) {
            if(disposing) {life.Cancel();Cancel();lock(Active){DevicePage found;if(Active.TryGetValue(profile.Id,out found)&&found==this)Active.Remove(profile.Id);}}
            base.Dispose(disposing);
        }
        void UI(Action act) {
            if(IsDisposed||Disposing||life.IsCancellationRequested)return;
            if(InvokeRequired) {try{Invoke(act);}catch(ObjectDisposedException){}catch(InvalidOperationException){}}
            else act();
        }
        bool Confirm(string message) {bool yes=false;UI(()=>yes=MessageBox.Show(this,message,"Пульт Станции",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes);return yes;}
        void Notice(string text){UI(()=>status.Text=text);}
        static string Message(Exception ex){ex=ex.GetBaseException();return ex is OperationCanceledException?"Операция отменена. Для незавершённого теста проверьте реальное состояние.":ex.Message;}
        void Cancel(){if(running!=null)running.Cancel();}
        async void Start(Action<CancellationToken> act)
        {
            if(!operation.Wait(0)) {Notice("Другая операция ещё выполняется.");return;}
            using(var linked=CancellationTokenSource.CreateLinkedTokenSource(life.Token)) {
                running=linked;
                try {await Task.Run(()=>act(linked.Token),linked.Token);}
                catch(Exception ex){Notice(Message(ex));}
                finally{running=null;operation.Release();}
            }
        }
        void Connect()
        {
            if(running!=null){Notice("Дождитесь завершения операции или нажмите «Отменить».");return;}
            string host=address.Text.Trim();bool adb=protocol.SelectedIndex==1;int number=(int)port.Value;
            SaveAddress(); live=null;facts=null;report.Clear();recoveryReport.Clear();
            Start(c=>{
                Action<string> log=s=>UI(()=>{report.AppendText(s+"\r\n");recoveryReport.AppendText(s+"\r\n");});
                try{
                    Notice("Проверка адреса…");
                    var ip=DeviceTransport.Resolve(host,c); log("Адрес разрешён — "+ip);
                    var sw=Stopwatch.StartNew();DeviceTransport.Tcp(ip,number,c);log("Выбранный TCP-порт доступен — "+sw.ElapsedMilliseconds+" мс.");
                    var tr=new DeviceTransport {Host=ip.ToString(),Port=number,Adb=adb,Store=Path.Combine(profile.DataDirectory,"ssh"),PrivateKey=profile.Get("local.device.key")};
                    if(adb) {
                        string adbPath=profile.Get("local.device.adb");
                        if(adbPath=="")adbPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Android","Sdk","platform-tools","adb.exe");
                        if(!File.Exists(adbPath)||!string.Equals(Path.GetFileName(adbPath),"adb.exe",StringComparison.OrdinalIgnoreCase))
                            throw new IOException("ADB отсутствует. Распакуйте официальные Platform Tools и выберите adb.exe.");
                        tr.Binary=adbPath;
                    } else tr.Binary=DeviceTransport.SystemTool("ssh");
                    tr.Authenticate(Confirm,c);log(adb?"ADB — выбран конкретный разрешённый сеанс.":"SSH — отпечаток сервера и аутентификация проверены.");
                    var data=DeviceInventory.Parse(tr.Shell(DeviceInventory.Script,c));c.ThrowIfCancellationRequested();
                    string now=DateTime.UtcNow.ToString("o"), old=profile.Get("local.device.report"), current=data.Report();
                    log((data.Plum?"Обнаружена платформа Plum.":"Платформа Plum не подтверждена.")+"\r\n"+current);
                    if(old!=""&&old!=current){
                        var delta=current.Split('\n').Where(x=>!old.Split('\n').Contains(x)&&x.Trim().Length>0);
                        log("Изменения с предыдущего снимка:\r\n"+string.Join("\r\n",delta));
                    }
                    UI(()=>{
                        c.ThrowIfCancellationRequested();
                        var snapshot=new Dictionary<string,string> {
                            {"local.device.report",current},{"local.device.checked",now},{"local.device.transport",adb?"ADB":"SSH"},
                            {"local.device.available.tts",!adb && data.TtsReady?"1":"0"},{"local.device.available.led",!adb && data.LedReady?"1":"0"},
                            {"local.device.identity",data.Value("binding")},{"local.device.stamp",data.Stamp}
                        };
                        foreach(string n in new[]{"sku","build","model"})snapshot["local.device."+n]=data.Value(n);
                        if(profile.Get("local.device.stamp")!=data.Stamp)
                            foreach(string test in new[]{"tts","led","tone","radio","wav"})snapshot["local.device.verified."+test]=null;
                        profile.SetMany(snapshot);
                        live=tr;facts=data;
                        Notice(data.Bound?"Станция опрошена. В отчёте указано, какие признаки удалось прочитать.":"Чтение завершено. Для чувствительных операций не хватает признаков модели, прошивки или экземпляра.");
                    });
                }catch(Exception e){log("Проверка остановилась — "+Message(e));throw;}
            });
        }
        DeviceInventory Fresh(CancellationToken c,bool requireSsh)
        {
            var t=live;var old=facts;
            if(t==null||old==null)throw new InvalidOperationException("Сначала подключите станцию и дождитесь инвентаризации.");
            if(requireSsh&&t.Adb)throw new InvalidOperationException("Подключитесь через SSH с проверенным ключом сервера.");
            var current=DeviceInventory.Parse(t.Shell(DeviceInventory.Script,c));
            if(old.Stamp!=current.Stamp){live=null;facts=null;throw new IOException("Изменилась плата, версия или привязка. Выполните новую инвентаризацию.");}
            return current;
        }
        void Evidence(string id,DeviceInventory f,string prompt)
        {
            if(Confirm(prompt)) {profile.Set("local.device.verified."+id,f.Stamp);Notice("Вы подтвердили результат на этой станции и прошивке.");}
            else Notice("Команда завершена. Физический результат владелец не подтвердил.");
        }
        void TestTts(string text,CancellationToken c) {
            DeviceTts.Request(text);
            var f=Fresh(c,true);
            if(!f.TtsReady)throw new IOException("Нужны подтверждённая Plum на испытанной сборке, root, 32-битный maind, socat и слушающий IPC 9875. Проверьте отчёт.");
            if(!Confirm("Убедитесь, что станция привязана к Яндексу, отвечает на обычные вопросы, сейчас молчит и сторонний голосовой режим остановлен. Отправить текст через SSH в штатный облачный синтез Яндекса?\n\n"+text))return;
            Notice("Ожидаем события начала и окончания речи…");DeviceTts.Speak(live,text,c);
            Evidence("tts",f,"Quasar передал события начала и окончания. Вы услышали именно введённую фразу на своей Станции?");
        }
        void TestTone(CancellationToken c) {
            var f=Fresh(c,true);
            if(!f.Ready||!f.Has("processes","maind")||f.Value("alsa")==""||f.Value("alsa")=="0")throw new IOException("Тон доступен после подтверждения Plum, ARM ABI, ALSA и штатной службы.");
            if(!Confirm("Воспроизвести короткий тихий тон 440 Гц на самой Станции? Файлы не изменяются. Завершите другой голосовой сеанс."))return;
            live.Shell("cd /system/vendor/quasar && LD_LIBRARY_PATH=/system/vendor/quasar:libs ./maind --gst-launch 'audiotestsrc num-buffers=8 wave=sine freq=440 volume=0.045 ! audioconvert ! audioresample ! alsasink' >/dev/null 2>&1",c,8000);
            Evidence("tone",f,"Станция завершила аудиопробу. Вы услышали тон из колонки?");
        }
        void TestAudio(byte[] input,string url,CancellationToken c) {
            var f=Fresh(c,true);
            if(!f.Ready||!f.Has("processes","maind")||f.Value("alsa")==""||f.Value("alsa")=="0")throw new IOException("Подтвердите подключение к Plum, наличие maind и ALSA в диагностике.");
            var t=live;
            if(input!=null) {
                if(!f.Has("tools","sha256sum")||!f.Has("tools","base64"))throw new IOException("Для проверки передачи нужны base64 и sha256sum на станции.");
                string available=t.Shell("df -Pk /tmp | awk 'END {print $4}'",c).Trim();long free;
                if(!long.TryParse(available,out free)||free<input.Length/1024+16)throw new IOException("В /tmp недостаточно свободного места для WAV.");
                if(!Confirm("Передать через SSH "+input.Length+" байт в новый временный файл /tmp, сверить SHA-256, проиграть до 12 секунд и удалить файл? Остановите конкурирующий звук."))return;
                DeviceAudio.Wav(t,input,c);
            }else{
                if(!Confirm("Станция подключится к "+url+" через интернет и попробует проиграть поток до 12 секунд. Сайт получит сетевой запрос от станции. Продолжить?"))return;
                DeviceAudio.Radio(t,url,c);
            }
            Evidence(input==null?"radio":"wav",f,"Проба завершилась. Вы слышали выбранное аудио из колонки?");
        }
        void TestLed(CancellationToken c) {
            var f=Fresh(c,true);if(!f.LedReady)throw new IOException("Не совпали проверенная сборка, доступность девяти каналов, ABI или фоновых утилит. Свет не менялся.");
            if(!Confirm("Закройте голосовые чаты, дождитесь окончания ответа. Программа сохранит на ПК яркость и триггеры девяти каналов. Колонка включит слабый синий свет до 2 секунд и сама вернёт штатный режим. Начать?"))return;
            var t=live;string state=DeviceLight.Snapshot(t,c);Directory.CreateDirectory(BackupDir);
            string file=Path.Combine(BackupDir,"led-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8)+".txt");
            File.WriteAllText(file,"Снимок верхнего света "+f.Stamp+"\r\n"+state,DeviceTransport.Utf);
            DeviceLight.Pulse(t,state,c);Evidence("led",f,"Станция подтвердила возврат триггеров. Вы видели короткую вспышку и штатную индикацию после неё?");
        }
        void ExportReport()
        {
            var text=profile.Get("local.device.report");if(text=="")throw new IOException("Сначала получите фактический отчёт с устройства.");
            using(var save=new SaveFileDialog{Filter="Текст UTF-8|*.txt",FileName="station-diagnostics.txt"}) if(save.ShowDialog(this)==DialogResult.OK)
                File.WriteAllText(save.FileName,"Диагностика Станции · UTC "+profile.Get("local.device.checked")+" · "+profile.Get("local.device.transport")+"\r\n"+text,DeviceTransport.Utf);
        }
        internal static string Query(string s) {
            s=s.Trim();if(!Regex.IsMatch(s,@"\A[A-Za-z0-9][A-Za-z0-9_.+-]{0,48}\z"))throw new ArgumentException("Для поиска введите имя пакета из букв a–z, цифр, точки, дефиса или плюса.");
            return s;
        }
        void Packages(string query,bool search,CancellationToken c)
        {
            if(search)query=Query(query);
            var f=Fresh(c,false);if(!f.Plum)throw new IOException("Не удалось определить Plum.");
            bool yse=f.Has("tools","yse");if(!yse&&!f.Has("tools","opkg"))throw new IOException("На станции не обнаружены yse и opkg.");
            string cmd=search?(yse?"yse search ":"opkg list ")+(yse?query:"'*"+query+"*'"):(yse?"yse list":"opkg list-installed");
            string prefix=(!search&&f.Has("tools","opkg"))?"opkg --version | head -n 1\n":"";
            string output=live.Shell(prefix+"rows=$("+cmd+" 2>/dev/null) || exit 79\nprintf '%s\\n' \"$rows\" | head -n 120 | cut -c1-160",c,18000);
            var clean=output.Split('\n').Select(line=>Regex.Match(line,@"^\s*([A-Za-z0-9][A-Za-z0-9_.:+/-]{0,70})(?:\s+(?:-\s+)?([A-Za-z0-9][A-Za-z0-9_.:+~-]{0,55}))?"))
                .Where(m=>m.Success).Select(m=>m.Groups[1].Value+"  "+m.Groups[2].Value).ToArray();
            UI(()=>recoveryReport.Text=clean.Length==0?"Совпадений в доступном списке нет.":string.Join("\r\n",clean));
            Notice("Прочитан локальный список менеджера без изменения реестров и пакетов.");
        }
        void Backup(string path,CancellationToken c) {
            var f=Fresh(c,true);if(!f.Bound)throw new IOException("Не удалось связать копию с конкретным экземпляром и прошивкой.");
            var b=DeviceBackup.Read(live,f,path,c);string outFile=b.Save(BackupDir);
            UI(()=>recoveryReport.Text="Сохранено на ПК:\r\n"+outFile+"\r\n"+path+"\r\nSHA-256 "+b.Digest+"\r\n"+b.Data.Length+" байт, mode "+b.Mode+", uid:gid "+b.Uid+":"+b.Gid);
            Notice("Хеш переданной копии совпал. Содержимое защищено учётной записью Windows.");
        }
        void ChooseBackup(bool restore)
        {
            using(var pick=new OpenFileDialog{Filter="Копия Станции|*.stationbak",InitialDirectory=Directory.Exists(BackupDir)?BackupDir:profile.DataDirectory}) {
                if(pick.ShowDialog(this)!=DialogResult.OK)return;
                string file=pick.FileName;Start(c=>{
                    var b=DeviceBackup.Load(file);var f=Fresh(c,true);var t=live;
                    if(!f.Bound||b.Stamp!=f.Stamp)throw new IOException("Копия относится к другой плате, привязке или прошивке.");
                    var old=DeviceBackup.Read(t,f,b.Target,c);
                    string plan=b.Target+"\r\nНа станции: "+old.Digest+" ("+old.Data.Length+" байт, "+old.Mode+", "+old.Uid+":"+old.Gid+")\r\nВ копии: "+b.Digest+" ("+b.Data.Length+" байт, "+b.Mode+", "+b.Uid+":"+b.Gid+")";
                    UI(()=>recoveryReport.Text=plan);if(!restore)return;
                    if(b.Target!=DeviceBackup.Sources)throw new IOException("Для системного launch.json доступно только чтение. Его восстанавливают отдельно по инструкции модели.");
                    if(b.Digest==old.Digest&&b.Mode==old.Mode&&b.Uid==old.Uid&&b.Gid==old.Gid){Notice("Содержимое и права уже совпадают с копией.");return;}
                    long free;if(!long.TryParse(f.Value("free"),out free)||free<(b.Data.Length/1024+16))throw new IOException("Недостаточно свободного места для атомарной замены.");
                    if(!Confirm("Сохранить текущее состояние на этом ПК и заменить реестр yse содержимым копии? Это изменит источники для будущих операций с пакетами. Понадобится "+(b.Data.Length/1024+16)+" КиБ.\r\n\r\n"+plan))return;
                    string undo=old.Save(BackupDir);b.Restore(t,f,old,c);
                    UI(()=>recoveryReport.AppendText("\r\nКонтроль после замены прошёл. Копия для отката:\r\n"+undo));
                    Notice("Реестр восстановлен и повторно проверен.");
                });
            }
        }
        public static Task<string> ProbeBridgeAsync(Profile p,CancellationToken cancel)
        {
            DevicePage page;lock(Active)Active.TryGetValue(p.Id,out page);
            if(page==null)throw new IOException("Откройте раздел «Станция» и подключитесь через SSH.");
            return Task.Run(()=>{
                using(var linked=CancellationTokenSource.CreateLinkedTokenSource(cancel,page.life.Token)){
                    if(!page.operation.Wait(0))throw new IOException("Дождитесь завершения текущей операции станции.");
                    try{
                        var f=page.Fresh(linked.Token,true);
                        if(!f.Bound||f.Value("bridge")!="present")throw new IOException("Совместимый установленный Quasar Bridge не найден на привязанной станции.");
                        string answer=page.live.Shell("sh /data/opt/quasar-bridge/app/start.sh -probe-llm-compat 2>/dev/null",linked.Token,125000,32000);
                        object json;try{json=new JavaScriptSerializer().DeserializeObject(answer);}catch{throw new IOException("Мост не вернул ожидаемый JSON. Тело ответа скрыто.");}
                        if(!(json is Dictionary<string,object>))throw new IOException("Мост вернул неподходящий тип ответа.");
                        return "Проба с хоста моста завершилась успешно. Получен объект параметров совместимости.";
                    }finally{page.operation.Release();}
                }
            },cancel);
        }
        public static void SelfTest(){DeviceChecks.Run();}
    }
}
