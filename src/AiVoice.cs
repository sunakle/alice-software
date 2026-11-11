using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AliceDesk
{
    public sealed partial class AiPage
    {
        private readonly ComboBox voice = Choice(false);
        private readonly ComboBox bridgeHost = Choice(false);
        private readonly ComboBox routing = Choice(false);
        private readonly TextBox hostBase = Input();
        private readonly TextBox trigger = Input();
        private readonly Label voiceInfo = Note("");
        private readonly Label voiceResult = Note("");
        private readonly TextBox checklist = new TextBox {Multiline = true, ReadOnly = true,
            Dock = DockStyle.Fill, Height = 166, ScrollBars = ScrollBars.Vertical, BackColor = System.Drawing.SystemColors.Window};
        private Button draft, probe, voiceCancel;
        private static readonly string[] VoiceTitles = {"Штатная Алиса", "Внешняя модель через Quasar Bridge",
            "ChatGPT Voice на Станции", "ChatGPT Voice через Linux-ПК", "Home Assistant Assist через Pulsar",
            "Свой сервер quasard", "Свой сервер v3rt3p"};
        private static readonly string[] VoiceIds = {"stock","bridge","chatgpt-device","chatgpt-linux","assist","quasard","v3rt3p"};
        private static readonly string[] VoiceHelp = {
            "Микрофон и динамик использует штатная Алиса. Для обычного диалога нужен интернет и включённая Станция.",
            "Микрофон, распознавание и озвучка работают через штатную Алису и Яндекс. Мост выбирает адресата текста. Нужны интернет, Станция и хост моста. Хост локальной LLM тоже должен работать.",
            "Во время открытого голосового чата микрофоны и динамик использует Alisa Voice. Нужны Станция, интернет и отдельный вход в ChatGPT в браузере. После настройки ПК можно выключить.",
            "Станция передаёт звук в Chromium. Требуются интернет и постоянно включённый Linux-ПК с PipeWire/PulseAudio. Этот мост не запускается нативно на Windows 10.",
            "Pulsar занимает микрофоны и динамик, штатный Quasar останавливается. Нужны включённая Станция, Home Assistant и службы распознавания и синтеза. Интернет зависит от выбранных служб.",
            "Отдельный сервер quasard на Windows/Linux с NVIDIA принимает голос, запускает распознавание/модель/озвучку. Требует доступного сервера. Совместимость с 00026 требует испытания.",
            "Отдельный сервер v3rt3p и его контейнеры принимают голос. Требует доступного сервера и выбранных STT/LLM/TTS. Поддержка 00026 в рецепте не подтверждена."
        };
        private static readonly string[] VoiceSteps = {
            "1. Проверьте в обычной Алисе время и таймер.\r\n2. Перед сменой голосового режима сохраните текущие службы и конфигурацию.\r\n3. После восстановления режима повторите вопрос и проверку физического mute.",
            "1. Проверьте текстовый API в соседней вкладке.\r\n2. Сверьте плату, прошивку, ABI и резервную копию quasar.cfg, включая uniProxyUrl и uniProxyQuicUrl. На 00026 сборка ARMv7 требует испытания.\r\n3. Подготовьте фрагмент для установленного Quasar Bridge 0.7.6 и проверьте API с хоста моста.\r\n4. Произнесите префикс и новый вопрос. В диагностике моста проверьте распознанный текст и маршрут llm, услышьте ответ. Проверьте обычный таймер.\r\n5. Для возврата остановите мост, восстановите оба сохранённых URL и штатный процесс. Проверьте голос после перезагрузки.",
            "1. Установщик Alisa Voice при уже настроенном SSH проверяет прошивку. Аппаратный отчёт охватывает Windows 11 и Лайт 2 без часов 00028. Для Windows 10 и 00026 требуется проверка.\r\n2. Войдите в ChatGPT через собственный интерфейс Alisa Voice. В этом приложении браузерные cookies не используются.\r\n3. Откройте чат голосом, создайте новый диалог и проверьте ответ. Голос, историю и фразы настраивает Alisa Voice.\r\n4. Скажите «закрой чат», проверьте обычную Алису, громкость и физическую кнопку микрофона. Для удаления используйте откат Alisa Voice.",
            "1. Проверьте совместимость станции и получите SSH. Установите Alisa Voice на отдельный Linux-ПК.\r\n2. Подключите браузерный микрофон и выход по инструкции моста, войдите в ChatGPT.\r\n3. Проверьте разговор и приостановку микрофона. Вернитесь в штатный режим командой закрытия чата, остановите мост и проверьте Алису.",
            "1. Сначала проверьте Assist на сервере HA с русским распознаванием и синтезом. Рецепт протестирован на одной Plum с прошивкой .133.\r\n2. После резервного копирования включите совместимый Pulsar и привяжите ESPHome по 6053 с его ключом.\r\n3. Проверьте голос, физическую кнопку отключения микрофона и действие HA. Для команды Windows привяжите агент в разделе «Команды ПК».\r\n4. Для проверки автономности испытайте местные STT/агент/TTS без внешнего интернета. Верните штатный режим через ctl.sh disable и проверьте его.",
            "1. Ознакомьтесь с требованиями к серверу, DNS, моделям и звуку (GigaAM и F5-TTS или сетевой Edge TTS).\r\n2. Сначала испытайте сервер и голосовую цепочку на совместимом стенде. Сохраните прежние DNS/сертификаты и конфигурацию станции.\r\n3. Для автономного режима используйте локальную модель и TTS. Проверьте без внешнего интернета. Восстановите прежний DNS и службы для возврата к Алисе.",
            "1. Разверните и отдельно проверьте контейнеры, хранилище, STT, LLM и TTS по документации проекта.\r\n2. До подключения станции установите поддержку вашей платы и сохраните прежние DNS, сертификаты и сервисы.\r\n3. Проверьте управление HA, таймеры и доступность после перезагрузки. Для автономности проверьте без внешнего интернета. Для возврата восстановите исходное разрешение имён и службы."
        };
        private void SetupVoice(TabPage page)
        {
            var grid = Grid(page);
            voice.Items.AddRange(VoiceTitles);
            voice.SelectedIndex = Math.Max(0, Array.IndexOf(VoiceIds, profile.Get("ai.voice", "stock")));
            bridgeHost.Items.AddRange(new string[] {"На Станции", "На отдельном сервере"});
            bridgeHost.SelectedIndex = profile.Get("ai.bridge.host") == "server" ? 1 : 0;
            routing.Items.AddRange(new string[] {"Только по обращению, остальное — Алисе", "Обращение и классификация остальных фраз"});
            routing.SelectedIndex = profile.Get("ai.bridge.route") == "classify" ? 1 : 0;
            hostBase.Text = profile.Get("ai.bridge.base");
            trigger.Text = profile.Get("ai.bridge.prefix","спроси нейросеть");
            trigger.MaxLength = 64;
            Row(grid, "Голосовой режим", voice);
            Row(grid, "", voiceInfo);
            Row(grid, "Размещение моста", bridgeHost);
            Row(grid, "База API с хоста моста", hostBase);
            Row(grid, "Маршрутизация", routing);
            Row(grid, "Начало обращения", trigger);
            draft = ActionButton("Сохранить фрагмент настройки моста…", ExportFragment);
            probe = ActionButton("Проверить API с хоста моста…", ProbeBridge);
            voiceCancel = Btn("Остановить ожидание", Cancel);
            voiceCancel.Enabled = false;
            Row(grid, "", Bar(ActionButton("Сохранить план", SaveVoice), draft, probe, voiceCancel));
            Row(grid, "", voiceResult);
            Row(grid, "Порядок проверки", checklist);
            Row(grid, "", Bar(ActionButton("Открыть руководство проекта", OpenVoiceDocs)));
            locked.AddRange(new Control[] {voice, bridgeHost, routing, hostBase, trigger});
            voice.SelectedIndexChanged += delegate { RenderVoice(); };
            bridgeHost.SelectedIndexChanged += delegate { RenderVoice(); };
            RenderVoice();
        }
        private void RenderVoice()
        {
            int i = voice.SelectedIndex;
            if (i < 0) return;
            voiceInfo.Text = VoiceHelp[i];
            checklist.Text = VoiceSteps[i];
            bool bridge = i == 1;
            foreach (Control c in new Control[] {bridgeHost, routing, hostBase, trigger, draft, probe})
                if (c != null) c.Enabled = bridge;
            voiceResult.Text = "Выбран план. Активный голосовой режим и работа на этой колонке ещё не проверены.";
        }
        private void SaveVoice()
        {
            if (voice.SelectedIndex == 1)
            {
                if (hostBase.Text.Trim().Length > 0) BridgeBase();
                ValidPrefix(trigger.Text);
                if (!String.IsNullOrWhiteSpace(models.Text)) SaveConfig(Endpoint());
                profile.Set("ai.bridge.host", bridgeHost.SelectedIndex == 1 ? "server" : "speaker");
                profile.Set("ai.bridge.base", hostBase.Text.Trim());
                profile.Set("ai.bridge.prefix", trigger.Text.Trim());
                profile.Set("ai.bridge.route", routing.SelectedIndex == 1 ? "classify" : "explicit");
            }
            profile.Set("ai.voice", VoiceIds[voice.SelectedIndex]);
            voiceResult.Text = "План сохранён в профиле. Состояние служб на Станции определяется отдельно.";
        }
        private static string ValidPrefix(string value)
        {
            value = value.Trim();
            if (value.Length == 0 || value.Length > 64 || value.Any(Char.IsControl))
                throw new AiFailure("Укажите начало голосового обращения длиной от 1 до 64 символов.");
            return value;
        }
        private string BridgeBase()
        {
            var textEndpoint = Endpoint();
            string value = hostBase.Text.Trim();
            if (value.Length == 0) value = textEndpoint.Base.AbsoluteUri;
            var target = new AiEndpoint(currentProvider == "deepseek" ? "compatible" : currentProvider, value);
            if (currentProvider == "deepseek" &&
                (target.Base.Scheme != "https" || target.Base.Host != "api.deepseek.com" || !target.Base.IsDefaultPort ||
                 (target.Base.AbsolutePath != "/" && target.Base.AbsolutePath != "/v1/")))
                throw new AiFailure("Для моста DeepSeek укажите https://api.deepseek.com/v1.");
            if (target.Base.IsLoopback && bridgeHost.SelectedIndex == 0)
                throw new AiFailure("Адрес localhost на Станции указывает на саму колонку. Введите адрес сервера модели, видимый колонке.");
            string root = target.Base.AbsoluteUri.TrimEnd('/');
            return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root : root + "/v1";
        }
        internal static string MakeFragment(string baseUrl, string model, string prefix, bool classify)
        {
            prefix = ValidPrefix(prefix);
            var fields = new {
                mode = "hybrid", llm_api = "chat_completions", llm_base_url = baseUrl, llm_model = AiClient.ModelId(model),
                llm_strip_prefixes = new[] {prefix},
                routing = new {rules = new[] {new {name = "explicit_llm", route = "llm", match = new {starts_with_any = new[] {prefix}}}},
                    default_route = classify ? "classify" : "yandex",
                    classifier = new {timeout_ms = 8000, on_error = "yandex"}, refusal = new {enabled = classify}}
            };
            return new JavaScriptSerializer().Serialize(fields);
        }
        private void ExportFragment()
        {
            string json = MakeFragment(BridgeBase(), models.Text, trigger.Text, routing.SelectedIndex == 1);
            using (var file = new SaveFileDialog {Filter = "Фрагмент JSON|*.json", FileName = "quasar-bridge-fragment.json"})
            {
                if (file.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(file.FileName, json, new UTF8Encoding(false));
            }
            SaveVoice();
            voiceResult.Text = "Сохранён фрагмент config.json без ключа. Объедините с действующим конфигом и проверьте правила routing.rules. " +
                "Ключ на хосте задаётся отдельно в runtime.env (QUASAR_LLM_API_KEY). Сохраните оригинал конфигурации.";
        }
        private async void ProbeBridge()
        {
            if (bridgeHost.SelectedIndex == 1)
            {
                voiceResult.Text = "На отдельном сервере используйте проверку -probe-llm-compat на установленном мосте 0.7.6 с его config.json и runtime.env. " +
                    "Команды для станции и Linux-сервера указаны в руководстве по ИИ. Проба делает запросы к провайдеру.";
                return;
            }
            if (MessageBox.Show(this, "На привязанной по SSH Станции будет запущена проверка уже установленного Quasar Bridge. " +
                "Она использует текущие настройки самого моста. Провайдер может списать средства за несколько текстовых и JSON-запросов (до десяти вариантов). Продолжить?",
                "Проверка с самой Станции", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            CancellationTokenSource op = null;
            try
            {
                op = BeginWork();
                voiceResult.Text = "Проверяется API установленного Quasar Bridge…";
                string message = await DevicePage.ProbeBridgeAsync(profile, op.Token);
                if (!IsDisposed) voiceResult.Text = message + " Использована конфигурация самого моста. Распознавание, выбранный маршрут и слышимый ответ проверяются отдельно.";
            }
            catch (OperationCanceledException) { if (!IsDisposed) voiceResult.Text = "Ожидание остановлено. Мост мог выполнить отправленный запрос."; }
            catch (Exception ex) { if (!IsDisposed) voiceResult.Text = ex.Message; }
            finally {
                if (op != null) EndWork(op);
                if (!IsDisposed) { string saved = voiceResult.Text; RenderVoice(); voiceResult.Text = saved; }
            }
        }
        private void OpenVoiceDocs()
        {
            string[] links = {"https://alice.yandex.ru/support/ru/station/meet/characteristics-lite2",
                "https://github.com/skrashevich/quasar-bridge/blob/v0.7.6/docs/guide.md",
                "https://github.com/rison1337/Alisa-ChatGPT-voice-assistant/releases/tag/v0.3.10",
                "https://hackalice.me/recipes/llm/#chatgpt-web","https://hackalice.me/recipes/home-assistant/",
                "https://hackalice.me/recipes/quasard/","https://hackalice.me/recipes/v3rt3p/"};
            Process.Start(new ProcessStartInfo(links[voice.SelectedIndex]) {UseShellExecute = true});
        }
    }
}
