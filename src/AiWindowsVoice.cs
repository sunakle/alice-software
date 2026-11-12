using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AliceDesk
{
    public sealed partial class AiPage
    {
        private readonly ComboBox windowsVoices = Choice(false);
        private readonly NumericUpDown windowsRate = new NumericUpDown { Minimum = -10, Maximum = 10, Width = 110 };
        private readonly NumericUpDown windowsVolume = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 110 };
        private readonly CheckBox windowsVoiceAuto = new CheckBox { Text = "Озвучивать ответы нейросети на этом ПК", AutoSize = true };
        private readonly TextBox windowsVoiceText = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 65, MaxLength = 2000 };
        private readonly Label windowsVoiceResult = Note("");
        private readonly List<string> windowsVoiceIds = new List<string>();
        private dynamic windowsSpeech;

        private void SetupWindowsVoice(TabPage page)
        {
            var grid = Grid(page);
            Row(grid, "", Note("Озвучка использует установленные в Windows голоса и текущий звуковой выход ПК. Чтобы слышать её на колонке, подключите Станцию как Bluetooth-аудиоустройство и выберите её выход в Windows."));
            Row(grid, "Голос", windowsVoices);
            Row(grid, "Скорость", windowsRate);
            Row(grid, "Громкость", windowsVolume);
            Row(grid, "", windowsVoiceAuto);
            Row(grid, "Тестовый текст", windowsVoiceText);
            Row(grid, "", Bar(ActionButton("Сохранить настройки", SaveWindowsVoice),
                ActionButton("Прослушать", PreviewWindowsVoice), ActionButton("Остановить", StopWindowsVoice)));
            Row(grid, "", windowsVoiceResult);
            windowsRate.Value = ReadWindowsVoiceNumber("ai.windowsVoice.rate", 0, -10, 10);
            windowsVolume.Value = ReadWindowsVoiceNumber("ai.windowsVoice.volume", 100, 0, 100);
            windowsVoiceAuto.Checked = profile.Get("ai.windowsVoice.auto", "0") == "1";
            windowsVoiceText.Text = "Привет! Это проверка голоса.";
            LoadWindowsVoices();
        }
        private int ReadWindowsVoiceNumber(string key, int fallback, int min, int max)
        {
            int value;
            if (!Int32.TryParse(profile.Get(key), out value) || value < min || value > max) return fallback;
            return value;
        }
        private void LoadWindowsVoices()
        {
            try {
                Type speechType = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (speechType == null) throw new InvalidOperationException();
                windowsSpeech = Activator.CreateInstance(speechType);
                dynamic tokens = windowsSpeech.GetVoices("", "");
                string saved = profile.Get("ai.windowsVoice.id");
                int chosen = 0;
                try {
                    for (int i = 0; i < (int)tokens.Count; i++) {
                        dynamic token = tokens.Item(i);
                        try {
                            string id = token.Id;
                            windowsVoiceIds.Add(id);
                            windowsVoices.Items.Add((string)token.GetDescription(0));
                            if (id == saved) chosen = i;
                        } finally { Marshal.ReleaseComObject(token); }
                    }
                } finally { Marshal.ReleaseComObject(tokens); }
                if (windowsVoices.Items.Count == 0) throw new InvalidOperationException();
                windowsVoices.SelectedIndex = chosen;
                windowsVoiceResult.Text = saved.Length > 0 && !windowsVoiceIds.Contains(saved)
                    ? "Сохранённый голос не установлен. Выбран первый доступный голос Windows."
                    : "Выберите голос, скорость и громкость, затем проверьте звучание.";
            } catch (Exception) {
                windowsVoices.Enabled = false;
                windowsVoiceAuto.Checked = false;
                windowsVoiceAuto.Enabled = false;
                windowsVoiceResult.Text = "Голоса Windows недоступны. Проверьте установленные компоненты синтеза речи в параметрах Windows.";
            }
        }
        private void SaveWindowsVoice()
        {
            if (windowsVoices.SelectedIndex < 0) throw new AiFailure("Установите голос Windows и выберите его.");
            profile.SetMany(new Dictionary<string,string> {
                {"ai.windowsVoice.id", windowsVoiceIds[windowsVoices.SelectedIndex]},
                {"ai.windowsVoice.rate", ((int)windowsRate.Value).ToString()},
                {"ai.windowsVoice.volume", ((int)windowsVolume.Value).ToString()},
                {"ai.windowsVoice.auto", windowsVoiceAuto.Checked ? "1" : "0"}
            });
            windowsVoiceResult.Text = "Настройки голоса Windows сохранены в профиле.";
        }
        private void PreviewWindowsVoice()
        {
            if (String.IsNullOrWhiteSpace(windowsVoiceText.Text)) throw new AiFailure("Введите текст для проверки голоса.");
            SpeakWindows(windowsVoiceText.Text);
        }
        private void SpeakWindows(string text)
        {
            try {
                if (windowsSpeech == null || windowsVoices.SelectedIndex < 0) {
                    windowsVoiceResult.Text = "Голос Windows недоступен.";
                    return;
                }
                dynamic tokens = windowsSpeech.GetVoices("", "");
                try {
                    dynamic token = tokens.Item(windowsVoices.SelectedIndex);
                    try { windowsSpeech.Voice = token; }
                    finally { Marshal.ReleaseComObject(token); }
                } finally { Marshal.ReleaseComObject(tokens); }
                windowsSpeech.Rate = (int)windowsRate.Value;
                windowsSpeech.Volume = (int)windowsVolume.Value;
                windowsSpeech.Speak(text, 19);
                windowsVoiceResult.Text = "Текст отправлен на выбранный в Windows аудиовыход.";
            } catch (Exception) {
                windowsVoiceResult.Text = "Не удалось запустить озвучку. Проверьте голос и звуковой выход Windows.";
            }
        }
        private void StopWindowsVoice()
        {
            if (windowsSpeech == null) return;
            try { windowsSpeech.Speak("", 3); } catch (Exception) { }
        }
        private void DisposeWindowsVoice()
        {
            StopWindowsVoice();
            if (windowsSpeech == null) return;
            Marshal.ReleaseComObject(windowsSpeech);
            windowsSpeech = null;
        }
    }
}
