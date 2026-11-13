using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AliceDesk {
    internal sealed class SpotifyPage : UserControl {
        readonly Profile profile;
        readonly SpotifyClient client;
        readonly TextBox clientId,target;
        readonly ComboBox devices;
        readonly Label status;
        readonly FlowLayoutPanel body;
        readonly Button cancel;
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        CancellationTokenSource operation;
        bool loading;
        internal SpotifyPage(Profile profile,Action openCommands) {
            this.profile=profile;client=new SpotifyClient(profile);Dock=DockStyle.Fill;
            body=MediaUi.Body(this);
            MediaUi.Line(body,"1. Подключите Spotify",true);
            MediaUi.Line(body,"Хаб запускает выбранную музыку через Spotify Premium. Нужны приложение Spotify и ваше приложение в Spotify Developer Dashboard. Пароль Spotify вводится только в браузере.");
            MediaUi.Line(body,"Client ID приложения Spotify");
            clientId=MediaUi.Field(body,profile.Get("local.spotify.clientId"),430);
            MediaUi.Line(body,"В настройках приложения добавьте Redirect URI точно как ниже:");
            var redirect=MediaUi.Field(body,SpotifyClient.Redirect,430);redirect.ReadOnly=true;
            var row=MediaUi.Row(body);
            MediaUi.Btn(row,"Открыть Dashboard",delegate {TryOpen("https://developer.spotify.com/dashboard");});
            MediaUi.Btn(row,"Войти через браузер",async delegate {await Run(async ct=>{await client.Login(clientId.Text,ct);return "Spotify подключён. Откройте плеер и обновите устройства.";});},true);
            MediaUi.Btn(row,"Выйти",delegate {
                try {profile.PutSecret(SpotifyClient.VaultKey,"");profile.Set("local.spotify.device",null);devices.Items.Clear();status.Text="Токены удалены с этого ПК. Доступ приложения также можно отозвать в аккаунте Spotify.";}
                catch(Exception ex) {status.Text=ex.Message;}
            });
            MediaUi.Line(body,"2. Выберите, где играть",true);
            MediaUi.Line(body,"Для Лайт 2 через Bluetooth выбирайте здесь компьютер с официальным Spotify. Аудиовыход Spotify назначьте на колонку во вкладке «Звук и Bluetooth». Устройство Connect можно выбрать, если оно уже доступно в Spotify.");
            devices=new SettingsCombo {Width=430,DropDownStyle=ComboBoxStyle.DropDownList};body.Controls.Add(devices);
            MediaUi.Btn(body,"Обновить устройства",async delegate {await Run(async ct=>{
                var list=await client.Devices(ct);if(IsDisposed)return "";
                loading=true;
                try {
                    devices.Items.Clear();string selected=profile.Get("local.spotify.device");
                    foreach(var d in list) {int i=devices.Items.Add(d);if(d.id==selected)devices.SelectedIndex=i;}
                } finally {loading=false;}
                return list.Count==0?"Нет доступных устройств. Откройте Spotify и запустите любой трек.":devices.SelectedIndex<0?"Выберите устройство из списка. Сохранённый выбор мог устареть.":"Выбранное устройство доступно.";
            });});
            devices.SelectedIndexChanged+=delegate {
                if(loading)return;var device=devices.SelectedItem as SpotifyDevice;if(device==null)return;
                try {profile.Set("local.spotify.device",device.id);status.Text="Команды будут отправляться на «"+device.name+"».";}
                catch(Exception ex) {status.Text=ex.Message;}
            };
            MediaUi.Line(body,"3. Проверьте музыку и задайте фразу",true);
            MediaUi.Line(body,"Скопируйте в Spotify ссылку: Поделиться → Копировать ссылку. Поддерживаются песня, исполнитель, альбом и плейлист.");
            target=MediaUi.Field(body,"",530);
            row=MediaUi.Row(body);
            MediaUi.Btn(row,"Проиграть ссылку",async delegate {await Run(async ct=>{await client.Play(target.Text,ct);return "Spotify принял команду воспроизведения. Проверьте звук на колонке.";});},true);
            MediaUi.Btn(row,"Настроить фразы",delegate {if(openCommands!=null)openCommands();});
            MediaUi.Line(body,"В разделе «Голос и команды» выберите тип «Spotify», вставьте ссылку и свои фразы. Экспортируйте пакет Home Assistant. Штатная Алиса сама по себе эти фразы хабу не передаёт: нужен настроенный голосовой вход Assist.");
            MediaUi.Line(body,"На Лайт 2 Pulsar останавливает штатную Алису. Совместимость его голосового входа с Bluetooth-музыкой на этой же колонке ещё требует проверки. Автономный режим без ПК не реализован.");
            MediaUi.Btn(body,"Инструкция по подключению",delegate {TryOpen(Path.Combine(Application.StartupPath,"docs","spotify.md"));});
            // Status lives outside the scrolling settings, so errors and cancellation stay visible.
            body.Dock=DockStyle.None;
            var footer=new Panel {Name="spotify-status",Height=54,Padding=new Padding(12,6,12,8)};
            Controls.Add(footer);
            status=MediaUi.Line(footer,"Войдите в аккаунт и выберите устройство. Настройка голоса — следующий шаг.");
            cancel=MediaUi.Btn(footer,"Отменить операцию",delegate {if(operation!=null)operation.Cancel();});cancel.Visible=false;
            bool arrangingStatus=false;
            Layout+=delegate {
                if(arrangingStatus)return;arrangingStatus=true;
                try {
                    status.MaximumSize=new System.Drawing.Size(Math.Max(100,ClientSize.Width-24),0);
                    status.Location=new System.Drawing.Point(12,8);
                    cancel.Location=new System.Drawing.Point(12,status.Bottom+5);
                    int height=(cancel.Visible?cancel.Bottom:status.Bottom)+10;
                    footer.SetBounds(0,Math.Max(0,ClientSize.Height-height),ClientSize.Width,height);
                    body.SetBounds(0,0,ClientSize.Width,Math.Max(1,footer.Top));
                } finally {arrangingStatus=false;}
            };
            status.TextChanged+=delegate {PerformLayout();};
            cancel.VisibleChanged+=delegate {PerformLayout();};
        }
        void TryOpen(string path) {try {MediaUi.Open(path);}catch(Exception ex){status.Text=ex.Message;}}
        async Task Run(Func<CancellationToken,Task<string>> work) {
            if(operation!=null)return;
            var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);operation=current;
            body.Enabled=false;cancel.Visible=true;status.Text="Выполняю… Для входа подтвердите доступ в браузере (до 3 минут).";
            try {string result=await work(current.Token);if(!IsDisposed)status.Text=result;}
            catch(OperationCanceledException){if(!IsDisposed)status.Text="Операция отменена или истекло время ожидания. Можно повторить.";}
            catch(Exception ex){if(!IsDisposed)status.Text="Не выполнено. "+ex.Message;}
            finally {operation=null;current.Dispose();if(!IsDisposed){body.Enabled=true;cancel.Visible=false;}}
        }
        protected override void Dispose(bool disposing) {
            if(disposing){lifetime.Cancel();lifetime.Dispose();client.Dispose();}base.Dispose(disposing);
        }
    }
}
