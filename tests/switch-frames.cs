using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Captures the actual composed window while a page switch executes. The profile
// and all content in the window are temporary. No screenshots are saved if the
// fixture is obscured.
internal static class SwitchFrames
{
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint direction);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    static readonly BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static int mixedFrames;
    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls) {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    static void Settle(int ms)
    {
        var sw = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(5); } while (sw.ElapsedMilliseconds < ms);
        DwmFlush();
    }
    static Bitmap Capture(Form shell, Rectangle rect)
    {
        for (var window = GetWindow(shell.Handle, 3); window != IntPtr.Zero; window = GetWindow(window, 3)) {
            Rect r;
            int cloaked;
            if (IsWindowVisible(window) && GetWindowRect(window, out r) &&
                Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).IntersectsWith(rect) &&
                !(DwmGetWindowAttribute(window, 14, out cloaked, 4) == 0 && cloaked != 0))
                throw new Exception("Test window was covered; capture discarded.");
        }
        foreach (int x in new[] { rect.Left, rect.Left + rect.Width / 2, rect.Right - 1 })
            foreach (int y in new[] { rect.Top, rect.Top + rect.Height / 2, rect.Bottom - 1 })
                if (GetAncestor(WindowFromPoint(new Point(x, y)), 2) != shell.Handle)
                    throw new Exception("Test window was covered; capture discarded.");
        var bitmap = new Bitmap(rect.Width, rect.Height);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(rect.Location, Point.Empty, rect.Size);
        return bitmap;
    }
    static double Different(Bitmap a, Bitmap b)
    {
        int count = 0, changed = 0;
        for (int y = 0; y < a.Height; y += 4) for (int x = 0; x < a.Width; x += 4) {
            Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
            if (Math.Abs(p.R-q.R) + Math.Abs(p.G-q.G) + Math.Abs(p.B-q.B) > 36) changed++;
            count++;
        }
        return count == 0 ? 0 : (double)changed / count;
    }
    static void RunTransition(Form shell, string name, Action action, string output)
    {
        var rect = shell.RectangleToScreen(shell.ClientRectangle);
        rect.Inflate(-12, -12);
        var frames = new List<Bitmap>();
        Exception error = null;
        bool stop = false;
        using (var before = Capture(shell, rect)) {
            Thread worker = new Thread(delegate() {
                try {
                    while (!Volatile.Read(ref stop)) {
                        DwmFlush();
                        frames.Add(Capture(shell, rect));
                        Thread.Sleep(8);
                    }
                } catch (Exception ex) { error = ex; }
            });
            worker.IsBackground = true;
            worker.Start();
            var sw = Stopwatch.StartNew();
            action();
            Application.DoEvents();
            long elapsed = sw.ElapsedMilliseconds;
            Settle(250);
            Volatile.Write(ref stop, true);
            worker.Join();
            try {
                if (error != null) throw error;
                using (var after = Capture(shell, rect)) {
                    double path = Different(before, after);
                    int mixed = 0;
                    double worst = 0;
                    Bitmap worstFrame = null;
                    foreach (var frame in frames) {
                        double fromBefore = Different(before, frame);
                        double fromAfter = Different(after, frame);
                        double gap = Math.Min(fromBefore, fromAfter);
                        if (gap > worst) { worst = gap; worstFrame = frame; }
                        if (gap > .03) mixed++;
                    }
                    Console.WriteLine("{0}: switch={1}ms frames={2}, changed={3:P1}, mixed={4}, max-gap={5:P1}", name, elapsed, frames.Count, path, mixed, worst);
                    mixedFrames += mixed;
                    if (worstFrame != null && output != null && worst > .03)
                        worstFrame.Save(Path.Combine(output, name + "-intermediate.png"), ImageFormat.Png);
                }
            } finally { foreach (var frame in frames) frame.Dispose(); }
        }
    }
    [STAThread] static int Main(string[] args)
    {
        var previous = GetForegroundWindow();
        try {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var assembly = Assembly.LoadFrom(args[0]);
            var store = Activator.CreateInstance(assembly.GetType("AliceDesk.ProfileStore", true), Members, null,
                new object[] {Path.Combine(args[1], "fixture")}, null);
            var shellType = assembly.GetType("AliceDesk.Shell", true);
            using (var shell = (Form)Activator.CreateInstance(shellType, Members, null, new object[] {store, false}, null)) {
                var area = Screen.PrimaryScreen.WorkingArea;
                shell.StartPosition = FormStartPosition.Manual;
                shell.Size = new Size(Math.Min(1160, area.Width), Math.Min(800, area.Height));
                shell.Location = new Point(area.Left + (area.Width-shell.Width)/2, area.Top + (area.Height-shell.Height)/2);
                shell.ShowInTaskbar = false; shell.TopMost = true; shell.Show(); shell.Activate();
                try {
                    Settle(300);
                    string output = args.Length > 2 ? args[2] : null;
                    foreach (string page in new[] {"ai", "station", "station", "extensions", "recovery", "station", "music", "agent", "home", "ai"}) {
                        var button = Descendants(shell).OfType<Button>().Single(b => b.Visible && b.GetType().Name == "SidebarButton" &&
                            (string)b.GetType().GetField("Page", Members).GetValue(b) == page);
                        RunTransition(shell, "page-" + page, button.PerformClick, output);
                    }
                    var tabs = Descendants(shell).OfType<TabControl>().First(t => t.Visible && t.TabCount >= 3);
                    foreach (int index in new[] {1, 2, 0, 2, 1, 0}) {
                        Rectangle tab = tabs.GetTabRect(index);
                        int pos = ((tab.Top + tab.Height / 2) << 16) | ((tab.Left + tab.Width / 2) & 0xffff);
                        RunTransition(shell, "tab-" + index, delegate {
                            SendMessage(tabs.Handle, 0x201, new IntPtr(1), new IntPtr(pos));
                            SendMessage(tabs.Handle, 0x202, IntPtr.Zero, new IntPtr(pos));
                        }, output);
                        if (tabs.SelectedIndex != index) throw new Exception("Tab click did not select index " + index);
                    }
                } finally {
                    bool restore = GetForegroundWindow() == shell.Handle;
                    shell.Close();
                    if (restore && previous != IntPtr.Zero) SetForegroundWindow(previous);
                }
            }
            if (mixedFrames > 0) Console.Error.WriteLine("Detected {0} incomplete transition frames.", mixedFrames);
            return mixedFrames == 0 ? 0 : 1;
        } catch (Exception ex) { Console.Error.WriteLine(ex.GetBaseException().Message); return 2; }
    }
}
