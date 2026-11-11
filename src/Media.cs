using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AliceDesk {
    internal static class MediaUi {
        internal static FlowLayoutPanel Body(Control parent) {
            var body = new SettingsBody();
            parent.Controls.Add(body);
            return body;
        }
        internal static Label Line(Control p, string s, bool title = false) {
            var label = new Label { Text = s, AutoSize = true, MaximumSize = new Size(690,0),
                Margin = new Padding(3, title ? 4 : 5, 3, 5) };
            if (title) label.Font = new Font("Segoe UI", 12, FontStyle.Bold);
            p.Controls.Add(label); return label;
        }
        internal static FlowLayoutPanel Row(Control p) {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(700,0), Margin = Padding.Empty };
            p.Controls.Add(row); return row;
        }
        internal static Button Btn(Control p, string s, EventHandler click, bool primary = false) {
            var b = new SettingsButton { Text = s, Primary = primary };
            b.Click += click; p.Controls.Add(b); return b;
        }
        internal static TextBox Field(Control p, string value, int width) {
            var t = new TextBox { Width = width, Text = value ?? "" }; p.Controls.Add(t); return t;
        }
        internal static void Open(string uri) {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
    }

    public sealed class MediaPage : UserControl {
        readonly Profile profile;
        readonly ComboBox outputs;
        readonly Label status;
        readonly Button tone, confirmSpotify;
        bool updating;
        public MediaPage(Profile p) {
            profile = p; Dock = DockStyle.Fill;
            var body = MediaUi.Body(this);
            MediaUi.Line(body, "Музыка с компьютера", true);
            MediaUi.Line(body,"Скажите «Алиса, включи Bluetooth». В Windows добавьте колонку в списке Bluetooth. Во время музыки ПК и Станция должны быть включены.");
            var row = MediaUi.Row(body);
            MediaUi.Btn(row,"Bluetooth Windows",delegate { Open("ms-settings:bluetooth"); });
            MediaUi.Btn(row,"Открыть Spotify",delegate { Spotify(); },true);
            MediaUi.Btn(row,"Громкость приложений",delegate { Open("ms-settings:apps-volume"); });
            MediaUi.Line(body,"Выход для пробного сигнала",true);
            outputs = new SettingsCombo { Width = 385, DropDownStyle = ComboBoxStyle.DropDownList };
            body.Controls.Add(outputs);
            row = MediaUi.Row(body);
            MediaUi.Btn(row,"Обновить выходы", delegate { RefreshSinks(); });
            tone = MediaUi.Btn(row,"Проиграть короткий сигнал", async delegate { await Tone(); },true);
            MediaUi.Line(body,"Запустите трек в Spotify и назначьте ему тот же выход в разделе Windows «Параметры устройств и громкости приложений». Выбор здесь относится к пробному сигналу.");
            confirmSpotify = MediaUi.Btn(body,"Подтверждаю, слышу Spotify на Станции",delegate {
                var selected = outputs.SelectedItem as MediaSink;
                if (selected == null) return;
                profile.Set("local.media.confirmed", selected.Id + "|" + DateTime.Now.ToString("s"));
                status.Text = "Вы подтвердили слышимость Spotify на Станции через «" + selected.Name + "».";
            });
            status = MediaUi.Line(body,"");
            outputs.SelectedIndexChanged += delegate {
                if (updating) return;
                var sink = outputs.SelectedItem as MediaSink;
                if (sink != null) {
                    profile.Set("media.output", sink.Id);
                    status.Text="Выбран выход «"+sink.Name+"». Для проверки проиграйте короткий сигнал.";
                }
                SetControls();
            };
            MediaUi.Line(body,"Spotify Connect на самой Лайт 2 с часами",true);
            MediaUi.Line(body,"Рецепт для Станции 2 и скрипт для первой Лайт рассчитаны на другие устройства. Слушать официальный Spotify через Bluetooth можно до прошивки. Для этого режима пароль или ключ Spotify здесь не требуются.");
            RefreshSinks();
        }
        void SetControls() {
            bool chosen = outputs.SelectedItem != null;
            tone.Enabled = chosen; confirmSpotify.Enabled = chosen;
        }
        void RefreshSinks() {
            updating = true;
            try {
                string wanted = profile.Get("media.output","");
                var list = MediaAudio.Enumerate();
                outputs.Items.Clear(); outputs.Items.AddRange(list.ToArray());
                for (int i = 0; i < list.Count; i++) if (list[i].Id == wanted) outputs.SelectedIndex = i;
                if (outputs.SelectedIndex < 0 && wanted.Length == 0 && list.Count > 0) {
                    outputs.SelectedIndex = 0; profile.Set("media.output",list[0].Id);
                }
                status.Text = list.Count == 0 ? "Windows не вернула доступных выходов. Проверьте подключение и обновите список." :
                    outputs.SelectedIndex < 0 ? "Сохранённый выход отключён. Подключите его или выберите другой." :
                    "Выходы получены из Windows. Проверьте выбранный коротким сигналом.";
                string previous = profile.Get("local.media.confirmed","");
                var selected = outputs.SelectedItem as MediaSink;
                if (selected != null && previous.StartsWith(selected.Id + "|",StringComparison.Ordinal))
                    status.Text += " Ранее вы подтвердили Spotify: " + previous.Substring(selected.Id.Length + 1) + ".";
            } catch (Exception ex) { status.Text = ex.Message; }
            finally { updating = false; SetControls(); }
        }
        async Task Tone() {
            var sink = outputs.SelectedItem as MediaSink; if (sink == null) return;
            tone.Enabled = false; outputs.Enabled = false;
            try {
                await Task.Run(delegate { MediaAudio.Play(sink.Id); });
                if (!IsDisposed) status.Text = "Windows завершила вывод сигнала на «" + sink.Name + "». Теперь можно отдельно проверить трек Spotify.";
            } catch (Exception ex) { if (!IsDisposed) status.Text = "Сигнал не завершён. " + ex.Message; }
            finally { if (!IsDisposed) { outputs.Enabled = true; SetControls(); } }
        }
        void Open(string target) { try { MediaUi.Open(target); } catch (Exception ex) { status.Text = ex.Message; } }
        void Spotify() {
            try {
                using (var key = Registry.ClassesRoot.OpenSubKey("spotify")) {
                    if (key != null && key.GetValue("URL Protocol") != null) {
                        try { MediaUi.Open("spotify:"); return; } catch (System.ComponentModel.Win32Exception) { }
                    }
                }
                MediaUi.Open("https://open.spotify.com/");
            } catch (Exception ex) { status.Text = ex.Message; }
        }
        public static void SelfTest() { MediaAudio.Test(); MediaCommands.Test(); MediaHome.Test(); }
    }
    internal sealed class MediaSink {
        internal string Id, Name; internal int Index;
        public override string ToString() { return Name; }
    }
    internal static class MediaAudio {
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct Caps { public ushort Maker, Product; public uint Version;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name;
            public uint Formats; public ushort Channels, Reserved; public uint Support; }
        [StructLayout(LayoutKind.Sequential,Pack=2)]
        struct Format { public ushort Tag, Channels; public uint Rate, ByteRate; public ushort Block, Bits, Extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct Header { public IntPtr Data; public uint Length, Recorded; public IntPtr User;
            public uint Flags, Loops; public IntPtr Next, Reserved; }
        [DllImport("winmm.dll")] static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll",CharSet=CharSet.Unicode)] static extern int waveOutGetDevCapsW(IntPtr id,out Caps caps,int size);
        [DllImport("winmm.dll")] static extern int waveOutMessage(IntPtr id,uint message,IntPtr value,IntPtr length);
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h,uint id,ref Format f,IntPtr callback,IntPtr instance,uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h,IntPtr header,int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h,IntPtr header,int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h,IntPtr header,int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        static string EndpointId(int index) {
            IntPtr size = Marshal.AllocHGlobal(4), buffer = IntPtr.Zero;
            try {
                if (waveOutMessage((IntPtr)index,0x812,size,IntPtr.Zero) != 0) return null;
                int n = Marshal.ReadInt32(size);
                if (n < 4 || n > 32768 || (n % 2) != 0) return null;
                buffer = Marshal.AllocHGlobal(n);
                return waveOutMessage((IntPtr)index,0x811,buffer,(IntPtr)n) == 0 ? Marshal.PtrToStringUni(buffer) : null;
            } finally { Marshal.FreeHGlobal(size); if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
        }
        internal static List<MediaSink> Enumerate() {
            var list = new List<MediaSink>();
            int count = waveOutGetNumDevs();
            for (int i=0; i < count; i++) {
                Caps caps; string id = EndpointId(i);
                if (!String.IsNullOrEmpty(id) && waveOutGetDevCapsW((IntPtr)i,out caps,Marshal.SizeOf(typeof(Caps))) == 0)
                    list.Add(new MediaSink { Id = id, Name = caps.Name + "  [" + (i+1) + "]", Index = i });
            } return list;
        }
        static short[] Samples() {
            const int rate = 44100, count = 22050;
            var data = new short[count];
            for (int i=0; i<count; i++) {
                double envelope = Math.Min(1.0,Math.Min(i,count-1-i) / 800.0);
                data[i] = (short)(3800 * envelope * Math.Sin(2 * Math.PI * 523.25 * i / rate));
            } return data;
        }
        static void Check(int error) { if (error != 0) throw new InvalidOperationException("Код аудиодрайвера Windows: " + error + ". Обновите список выходов."); }
        internal static void Play(string id) {
            var selected = Enumerate().FirstOrDefault(s => s.Id == id);
            if (selected == null) throw new InvalidOperationException("Выбранное устройство отключено.");
            var format = new Format { Tag=1,Channels=1,Rate=44100,ByteRate=88200,Block=2,Bits=16 };
            IntPtr handle;
            using (var signal = new AutoResetEvent(false)) {
                Check(waveOutOpen(out handle,(uint)selected.Index,ref format,signal.SafeWaitHandle.DangerousGetHandle(),IntPtr.Zero,0x50000));
                int size = Marshal.SizeOf(typeof(Header)); var data = Samples();
                IntPtr samples = Marshal.AllocHGlobal(data.Length * 2), hdr = Marshal.AllocHGlobal(size);
                bool prepared = false;
                try {
                    Marshal.Copy(data,0,samples,data.Length);
                    Marshal.StructureToPtr(new Header { Data=samples, Length=(uint)data.Length*2 },hdr,false);
                    Check(waveOutPrepareHeader(handle,hdr,size)); prepared = true;
                    Check(waveOutWrite(handle,hdr,size));
                    var elapsed = Stopwatch.StartNew();
                    while ((((Header)Marshal.PtrToStructure(hdr,typeof(Header))).Flags & 1) == 0) {
                        if (elapsed.ElapsedMilliseconds > 4000) throw new TimeoutException("Устройство перестало отвечать.");
                        signal.WaitOne(100);
                    }
                } finally {
                    waveOutReset(handle);
                    if (prepared) waveOutUnprepareHeader(handle,hdr,size);
                    waveOutClose(handle); Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(samples);
                }
            }
        }
        internal static void Test() {
            var data = Samples();
            if (data.Length != 22050 || data.Max() < 3600 || data.Max() > 3800 || data[0] != 0 || data[data.Length-1] != 0)
                throw new Exception("Тест генератора PCM не прошёл.");
            // Enumeration is real, including the valid headless case. Never play sound during unattended tests.
            var devices = Enumerate();
            if (devices.Select(s => s.Id).Distinct().Count() != devices.Count) throw new Exception("Повторяющийся ID аудиовыхода.");
        }
    }
}
