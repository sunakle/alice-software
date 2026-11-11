using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AliceDesk
{
    public sealed partial class AiPage : UserControl
    {
        private readonly Profile profile;
        private readonly AiClient client = new AiClient();
        private readonly List<AiTurn> history = new List<AiTurn>();
        private readonly ComboBox provider = Choice(false);
        private readonly ComboBox models = Choice(true);
        private readonly TextBox address = Input();
        private readonly TextBox secret = new TextBox {Dock = DockStyle.Fill, UseSystemPasswordChar = true, MaxLength = 4096};
        private readonly TextBox ask = new TextBox {Dock = DockStyle.Fill, Multiline = true, Height = 49, MaxLength = 8000};
        private readonly TextBox transcript = new TextBox {Dock = DockStyle.Fill, ReadOnly = true,
            Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 135, BackColor = SystemColors.Window};
        private readonly Label dest = Note("");
        private readonly Label keyState = Note("");
        private readonly Label result = Note("Запросов с этого ПК пока не было.");
        private readonly List<Control> locked = new List<Control>();
        private readonly Dictionary<Control,bool> beforeRequest = new Dictionary<Control,bool>();
        private Button cancelButton;
        private CancellationTokenSource active;
        private bool loading;
        private string lastContext = "";
        private string currentProvider = "";
        private string loadedAddress = "";
        private readonly Dictionary<string, string> names = new Dictionary<string, string> {
            {"deepseek","DeepSeek"}, {"compatible","Совместимый облачный API"}, {"ollama","Ollama"},
            {"lmstudio","LM Studio"}, {"local","Другой локальный сервер"}
        };
        private readonly string[] builtins = {"deepseek","compatible","ollama","lmstudio","local"};

        private static TextBox Input() { return new TextBox {Dock = DockStyle.Fill, MaxLength = 2048}; }
        private static ComboBox Choice(bool editable)
        {
            return new ComboBox {DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill, MaxLength = 240};
        }
        private static Label Note(string text)
        {
            return new Label {Text = text, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(2,4,4,4)};
        }
        private static Button Btn(string caption, Action action)
        {
            var b = new Button {Text = caption, AutoSize = true, Padding = new Padding(5,2,5,2)};
            b.Click += delegate { action(); };
            return b;
        }
        private Button ActionButton(string caption, Action action)
        {
            var button = Btn(caption, delegate { Guard(action); });
            locked.Add(button);
            return button;
        }
        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex) { ShowProblem(ex); }
        }
        private void ShowProblem(Exception ex)
        {
            if (IsDisposed) return;
            MessageBox.Show(this, ex is AiFailure || ex is System.IO.InvalidDataException ?
                ex.Message : "Не удалось выполнить действие. Проверьте параметры и доступ к файлам профиля.",
                "Настройки нейросети", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        private static FlowLayoutPanel Bar(params Control[] controls)
        {
            var bar = new FlowLayoutPanel {AutoSize = true, Dock = DockStyle.Fill, WrapContents = true,
                Margin = Padding.Empty, Padding = Padding.Empty};
            bar.Controls.AddRange(controls);
            return bar;
        }
        private static TableLayoutPanel Grid(TabPage tab)
        {
            tab.AutoScroll = true;
            tab.Padding = new Padding(8);
            var grid = new TableLayoutPanel {Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2,
                Padding = new Padding(4), GrowStyle = TableLayoutPanelGrowStyle.AddRows, RowCount = 0};
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tab.Controls.Add(grid);
            return grid;
        }
        private static void Row(TableLayoutPanel grid, string caption, Control field)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (String.IsNullOrEmpty(caption))
            {
                grid.Controls.Add(field, 0, row);
                grid.SetColumnSpan(field, 2);
            }
            else
            {
                grid.Controls.Add(Note(caption), 0, row);
                grid.Controls.Add(field, 1, row);
            }
        }

        public AiPage(Profile p)
        {
            profile = p;
            Dock = DockStyle.Fill;
            var tabs = new TabControl {Dock = DockStyle.Fill};
            var textTab = new TabPage("Провайдер и чат");
            var voiceTab = new TabPage("Голос на Станции");
            tabs.TabPages.Add(textTab);
            tabs.TabPages.Add(voiceTab);
            Controls.Add(tabs);
            var grid = Grid(textTab);
            Row(grid, "Провайдер", provider);
            Row(grid, "", Bar(ActionButton("Добавить провайдера", AddProvider),
                ActionButton("Удалить свой профиль API", DeleteProvider)));
            Row(grid, "База API", address);
            Row(grid, "Ключ", secret);
            Row(grid, "", Bar(ActionButton("Сохранить ключ", SaveSecret), ActionButton("Удалить ключ", RemoveSecret)));
            Row(grid, "", keyState);
            Row(grid, "Модель", models);
            Row(grid, "", Bar(ActionButton("Сохранить настройки", SaveSettings), ActionButton("Обновить каталог моделей", delegate { RunApi(true); })));
            Row(grid, "", dest);
            Row(grid, "Ваш текст", ask);
            cancelButton = Btn("Остановить", Cancel);
            cancelButton.Enabled = false;
            Row(grid, "", Bar(ActionButton("Отправить запрос", delegate { RunApi(false); }), cancelButton,
                ActionButton("Новый диалог", delegate { ResetChat(); result.Text = "Начат новый диалог."; })));
            Row(grid, "", result);
            Row(grid, "Диалог", transcript);
            locked.AddRange(new Control[] {provider, address, secret, models, ask});
            SetupVoice(voiceTab);
            ReloadChoices(profile.Get("ai.provider", "deepseek"));
            provider.SelectedIndexChanged += delegate { Guard(delegate { SelectProvider(); }); };
            address.TextChanged += delegate {
                if (loading) return;
                secret.Clear();
                models.Items.Clear();
                ResetChat();
                UpdateDestination(false);
                result.Text = "Адрес изменён. Подключение к этому серверу ещё не проверялось.";
            };
            models.TextChanged += delegate { if (!loading) { ResetChat(); UpdateDestination(false); } };
            LoadProvider();
        }

        private static string DefaultBase(string id)
        {
            if (id == "deepseek") return "https://api.deepseek.com";
            if (id == "ollama") return "http://127.0.0.1:11434/v1";
            if (id == "lmstudio") return "http://127.0.0.1:1234/v1";
            return "";
        }
        private string SettingsKey(string name) { return "ai." + currentProvider + "." + name; }
        private List<string> CustomIds()
        {
            return profile.Get("ai.custom").Split(',').Where(id => id.Length == 33 && id[0] == 'p' &&
                id.Substring(1).All(Uri.IsHexDigit)).Distinct().Take(12).ToList();
        }
        private void ReloadChoices(string preferred)
        {
            loading = true;
            provider.Items.Clear();
            var ids = new List<string>(builtins);
            ids.AddRange(CustomIds());
            foreach (string id in ids)
                provider.Items.Add(new KeyValuePair<string,string>(id, names.ContainsKey(id) ? names[id] : profile.Get("ai." + id + ".name", "Свой провайдер")));
            provider.DisplayMember = "Value";
            provider.SelectedIndex = Math.Max(0, ids.IndexOf(preferred));
            loading = false;
        }
        private void SelectProvider()
        {
            if (loading || provider.SelectedIndex < 0) return;
            LoadProvider();
            profile.Set("ai.provider", currentProvider);
        }
        private void LoadProvider()
        {
            loading = true;
            currentProvider = ((KeyValuePair<string,string>)provider.SelectedItem).Key;
            address.Text = profile.Get(SettingsKey("base"), DefaultBase(currentProvider));
            loadedAddress = address.Text;
            address.ReadOnly = currentProvider == "deepseek";
            secret.Clear();
            models.Items.Clear();
            models.Text = profile.Get(SettingsKey("model"), "");
            loading = false;
            ResetChat();
            UpdateDestination(true);
            result.Text = "Выбран провайдер. Его каталог и генерацию можно проверить отдельно.";
        }
        private void AddProvider()
        {
            var ids = CustomIds();
            if (ids.Count >= 12) throw new AiFailure("В одном профиле можно создать до 12 дополнительных провайдеров.");
            using (var dialog = new Form {Text = "Новый облачный API", StartPosition = FormStartPosition.CenterParent,
                Width = 410, Height = 155, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false})
            {
                var name = new TextBox {Left = 14, Top = 32, Width = 362, MaxLength = 60};
                dialog.Controls.Add(new Label {Text = "Название HTTPS-провайдера", Left = 14, Top = 10, AutoSize = true});
                dialog.Controls.Add(name);
                var ok = new Button {Text = "Добавить", DialogResult = DialogResult.OK, Left = 276, Top = 66, Width = 100};
                dialog.Controls.Add(ok);
                dialog.AcceptButton = ok;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (String.IsNullOrWhiteSpace(name.Text)) throw new AiFailure("Введите название провайдера.");
                string id = "p" + Guid.NewGuid().ToString("N");
                ids.Add(id);
                profile.Set("ai." + id + ".name", name.Text.Trim());
                profile.Set("ai.custom", String.Join(",", ids));
                ReloadChoices(id);
                SelectProvider();
            }
        }
        private void DeleteProvider()
        {
            var ids = CustomIds();
            if (!ids.Contains(currentProvider)) throw new AiFailure("Можно удалить только добавленного вами провайдера.");
            if (MessageBox.Show(this, "Удалить настройки этого провайдера и его текущий ключ?", "Удаление",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            DeleteSavedSecret();
            ids.Remove(currentProvider);
            profile.Set("ai.custom", String.Join(",", ids));
            foreach (string k in new[] {"base","model","name"}) profile.Set(SettingsKey(k), null);
            ReloadChoices("deepseek");
            SelectProvider();
        }
        private AiEndpoint Endpoint() { return new AiEndpoint(currentProvider, address.Text); }
        private void DeleteSavedSecret()
        {
            if (String.IsNullOrWhiteSpace(loadedAddress)) return;
            AiEndpoint old;
            try { old = new AiEndpoint(currentProvider, loadedAddress); }
            catch (AiFailure) { return; }
            profile.PutSecret(old.SecretId, "");
        }
        private void SaveConfig(AiEndpoint endpoint)
        {
            if (!String.IsNullOrWhiteSpace(models.Text)) AiClient.ModelId(models.Text);
            if (!String.IsNullOrWhiteSpace(loadedAddress))
            {
                AiEndpoint old = null;
                try { old = new AiEndpoint(currentProvider, loadedAddress); } catch (AiFailure) { }
                if (old != null && old.SecretId != endpoint.SecretId) profile.PutSecret(old.SecretId, "");
            }
            profile.Set(SettingsKey("base"), endpoint.Base.AbsoluteUri.TrimEnd('/'));
            profile.Set(SettingsKey("model"), models.Text.Trim());
            loadedAddress = address.Text;
        }
        private void SaveSettings()
        {
            SaveConfig(Endpoint());
            UpdateDestination(true);
            result.Text = "Настройки провайдера сохранены.";
        }
        private void SaveSecret()
        {
            var endpoint = Endpoint();
            if (String.IsNullOrEmpty(secret.Text)) throw new AiFailure("Введите ключ в поле. Для удаления есть отдельная кнопка.");
            endpoint.CheckKey(secret.Text);
            SaveConfig(endpoint);
            profile.PutSecret(endpoint.SecretId, secret.Text);
            secret.Clear();
            UpdateDestination(true);
        }
        private void RemoveSecret()
        {
            var endpoint = Endpoint();
            profile.PutSecret(endpoint.SecretId, "");
            secret.Clear();
            UpdateDestination(true);
        }
        private void UpdateDestination(bool checkSavedKey)
        {
            try
            {
                var endpoint = Endpoint();
                dest.Text = "Получатель — " + endpoint.Base.AbsoluteUri +
                    ". Каталог отправляет ключ при наличии. Отправка текста передаёт ключ, выбранную модель, ваш текст и реплики текущего диалога. " +
                    (endpoint.Base.Scheme == "http" ? "Соединение локальное HTTP, без шифрования." :
                    "Для облачного сервера нужен интернет. Провайдер может списать средства за ответ.");
                if (checkSavedKey) keyState.Text = profile.Secret(endpoint.SecretId).Length > 0 ?
                    "Ключ сохранён под вашей учётной записью Windows для этого адреса." : "Сохранённого ключа для этого адреса нет.";
                else keyState.Text = "Ключ привязан к профилю и точной базе API. При смене адреса сохраните ключ для нового сервера.";
            }
            catch (AiFailure ex) { dest.Text = ex.Message; keyState.Text = "Адрес ещё не настроен."; }
            catch (System.IO.InvalidDataException ex) { keyState.Text = ex.Message; }
            catch (Exception) { keyState.Text = "Сохранённый ключ не удалось прочесть. Удалите его и сохраните новый."; }
        }
        private void ResetChat() { history.Clear(); transcript.Clear(); lastContext = ""; }
        private void Cancel() { if (active != null) active.Cancel(); }
        private CancellationTokenSource BeginWork()
        {
            if (active != null) throw new AiFailure("Уже выполняется проверка.");
            active = new CancellationTokenSource();
            beforeRequest.Clear();
            foreach (var field in locked) { beforeRequest[field] = field.Enabled; field.Enabled = false; }
            cancelButton.Enabled = true;
            if (voiceCancel != null) voiceCancel.Enabled = true;
            return active;
        }
        private void EndWork(CancellationTokenSource operation)
        {
            if (active == operation) active = null;
            operation.Dispose();
            if (IsDisposed) return;
            foreach (var entry in beforeRequest) entry.Key.Enabled = entry.Value;
            beforeRequest.Clear();
            cancelButton.Enabled = false;
            if (voiceCancel != null) voiceCancel.Enabled = false;
        }
        private async void RunApi(bool catalog)
        {
            CancellationTokenSource operation = null;
            var elapsed = Stopwatch.StartNew();
            try
            {
                var endpoint = Endpoint();
                if (secret.Text.Length != 0) throw new AiFailure("Сохраните введённый ключ или очистите поле перед запросом.");
                string key = profile.Secret(endpoint.SecretId);
                endpoint.CheckKey(key);
                if (!catalog) AiClient.ModelId(models.Text);
                SaveConfig(endpoint);
                operation = BeginWork();
                string model = models.Text.Trim();
                string question = ask.Text.Trim();
                result.Text = catalog ? "Загружаем каталог…" : "Ожидаем ответ модели…";
                if (catalog)
                {
                    var options = await client.Models(endpoint, key, operation.Token);
                    if (IsDisposed) return;
                    loading = true;
                    try {
                        models.Items.Clear(); models.Items.AddRange(options);
                        models.Text = model.Length == 0 ? options[0] : model;
                    } finally { loading = false; }
                    result.Text = "Каталог получен. Моделей — " + options.Length + ". Генерация не проверялась. " + elapsed.ElapsedMilliseconds + " мс.";
                }
                else
                {
                    string context = endpoint.SecretId + "\n" + model;
                    if (lastContext != context) ResetChat();
                    lastContext = context;
                    var answer = await client.Generate(endpoint, key, model, history, question, operation.Token);
                    if (IsDisposed) return;
                    history.Add(new AiTurn("user", question));
                    history.Add(new AiTurn("assistant", answer.Text));
                    transcript.AppendText((transcript.TextLength == 0 ? "" : "\r\n\r\n") + "Вы\r\n" + question + "\r\n\r\n" + answer.Model + "\r\n" + answer.Text);
                    ask.Clear();
                    result.Text = "Получен текстовый ответ от " + answer.Model + " за " + elapsed.ElapsedMilliseconds + " мс." +
                        (answer.Finish == "length" ? " Сервер остановил генерацию из-за лимита длины." : "") + " Голос колонки проверяется отдельно.";
                }
            }
            catch (OperationCanceledException) { if (!IsDisposed) result.Text = "Ожидание остановлено. Уже отправленный запрос мог быть обработан провайдером."; }
            catch (Exception ex) { if (!IsDisposed) result.Text = (ex is AiFailure || ex is System.IO.InvalidDataException ? ex.Message :
                "Не удалось завершить запрос. Проверьте настройки профиля.") + " Время — " + elapsed.ElapsedMilliseconds + " мс."; }
            finally { if (operation != null) EndWork(operation); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Cancel(); client.Dispose(); }
            base.Dispose(disposing);
        }
        public static void SelfTest() { AiChecks.Run(); }
    }
}
