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

// Run via live-paint.ps1 on an unlocked Windows desktop. Read the composed
// window from the display, including child HWND paints and buffer reuse.
internal static class LivePaint
{
    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint direction);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    static readonly BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static int failures, samples, scrollErrors;

    static void Settle()
    {
        var timer = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(15); } while (timer.ElapsedMilliseconds < 150);
        DwmFlush();
    }

    static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls) {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    static Button PageButton(Form shell, string page)
    {
        return Descendants(shell).OfType<Button>().Single(b => b.Visible &&
            b.GetType().Name == "SidebarButton" && (string)b.GetType().GetField("Page",Members).GetValue(b) == page);
    }

    static bool IsUnobscured(Form shell, Rectangle screen)
    {
        if (!Screen.FromControl(shell).Bounds.Contains(screen)) throw new Exception("Window is outside the display.");
        for (var window = GetWindow(shell.Handle, 3); window != IntPtr.Zero; window = GetWindow(window, 3)) {
            Rect r;
            int cloaked;
            if (IsWindowVisible(window) && GetWindowRect(window, out r) &&
                Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom).IntersectsWith(screen) &&
                !(DwmGetWindowAttribute(window, 14, out cloaked, 4) == 0 && cloaked != 0))
                return false;
        }
        foreach (int x in new[] { screen.Left, screen.Left+screen.Width/2, screen.Right-1 })
            foreach (int y in new[] { screen.Top, screen.Top+screen.Height/2, screen.Bottom-1 })
                if (GetAncestor(WindowFromPoint(new Point(x,y)),2) != shell.Handle)
                    return false;
        return true;
    }

    static void Compare(string stage, Control control, Bitmap image, Rectangle screen, Point a, Color expected)
    {
        var p = control.PointToScreen(a);
        var actual = image.GetPixel(p.X-screen.X,p.Y-screen.Y);
        samples++;
        if (Math.Abs(expected.R-actual.R) <= 8 && Math.Abs(expected.G-actual.G) <= 8 &&
            Math.Abs(expected.B-actual.B) <= 8) return;
        if (++failures <= 12) Console.WriteLine("{0} {1}/{2} ({3},{4}) expected {5:X6}, found {6:X6}",
            stage, control.GetType().Name,control.AccessibleName, a.X,a.Y, expected.ToArgb() & 0xffffff, actual.ToArgb() & 0xffffff);
    }

    static void CapturePage(Form shell, string stage, string output)
    {
        if (output == null) return;
        var screen = shell.RectangleToScreen(shell.ClientRectangle);
        screen.Inflate(-9,-9);
        var wait = Stopwatch.StartNew();
        while (!IsUnobscured(shell,screen)) {
            if (wait.ElapsedMilliseconds >= 2500) throw new Exception("The window is obscured or the desktop is locked. Capture discarded.");
            Settle();
        }
        using (var image = new Bitmap(screen.Width,screen.Height)) {
            using (var g = Graphics.FromImage(image)) g.CopyFromScreen(screen.Location,Point.Empty,screen.Size);
            if (!IsUnobscured(shell,screen)) throw new Exception("The window was obscured during capture. Capture discarded.");
            image.Save(Path.Combine(output,stage+".png"),ImageFormat.Png);
        }
    }

    static void Inspect(Form shell, string stage, string output, bool seed)
    {
        var controls = Descendants(shell).Where(c => c.Visible &&
            (c.GetType().Name == "Tile" || c.GetType().Name == "SidebarButton")).ToList();
        if (controls.Count(c=>c.GetType().Name=="Tile") != 6 || controls.Count(c=>c.GetType().Name=="SidebarButton") < 4)
            throw new Exception("The expected overview controls were not visible.");
        foreach (Control c in controls) {
            if (seed) {
                // A shared buffer may contain arbitrary prior paints. Give it a
                // known prior color, then execute the control's ordinary WM_PAINT.
                using (var g = c.CreateGraphics())
                using (var buffer = BufferedGraphicsManager.Current.Allocate(g,c.ClientRectangle))
                    buffer.Graphics.Clear(Color.Magenta);
            }
            c.Invalidate(); c.Update();
        }
        Settle();
        var home = (ScrollableControl)Descendants(shell).Single(c => c.Visible && c.GetType().Name == "BigSurHome");
        if (home.HorizontalScroll.Visible) {
            scrollErrors++;
            Console.WriteLine("{0}: unexpected horizontal scrollbar (client {1}, sheet {2}, display {3}).",
                stage, home.ClientSize, home.Controls[0].Bounds, home.DisplayRectangle);
        }
        // Capture the application client area without the system window border.
        var screen = shell.RectangleToScreen(shell.ClientRectangle);
        screen.Inflate(-9,-9);
        var wait = Stopwatch.StartNew();
        while (!IsUnobscured(shell,screen)) {
            if (wait.ElapsedMilliseconds >= 2500) throw new Exception("The window is obscured or the desktop is locked. Capture discarded.");
            Settle();
        }
        using (var image = new Bitmap(screen.Width,screen.Height)) {
            using (var g = Graphics.FromImage(image)) g.CopyFromScreen(screen.Location,Point.Empty,screen.Size);
            if (!IsUnobscured(shell,screen)) throw new Exception("The window was obscured during capture. Capture discarded.");
            foreach (var c in controls) {
                if (!screen.Contains(c.RectangleToScreen(c.ClientRectangle))) throw new Exception("A control is clipped at this screen size.");
                foreach (var point in new[] {new Point(1,1),new Point(c.Width-2,1),new Point(1,c.Height-2),new Point(c.Width-2,c.Height-2)}) {
                    Color expected = c.Parent.BackColor;
                    if (c.GetType().Name == "SidebarButton") {
                        var reference = c.PointToScreen(new Point(c.Width + 2,point.Y));
                        expected = image.GetPixel(reference.X-screen.X,reference.Y-screen.Y);
                    }
                    Compare(stage,c,image,screen,point,expected);
                }
            }
            if (output != null) image.Save(Path.Combine(output,stage+".png"),ImageFormat.Png);
        }
    }

    [STAThread] static int Main(string[] args)
    {
        var previous = GetForegroundWindow();
        try {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            var assembly = Assembly.LoadFrom(args[0]);
            var store = Activator.CreateInstance(assembly.GetType("AliceDesk.ProfileStore",true),Members,null,
                new object[] { Path.Combine(args[1],"fixture") },null);
            var type = assembly.GetType("AliceDesk.Shell",true);
            using (var shell = (Form)Activator.CreateInstance(type,Members,null,new object[] {store,false},null)) {
                var area = Screen.PrimaryScreen.WorkingArea;
                shell.StartPosition=FormStartPosition.Manual;
                shell.Size=new Size(Math.Min(1160,area.Width),Math.Min(800,area.Height));
                shell.Location=new Point(area.Left+(area.Width-shell.Width)/2,area.Top+(area.Height-shell.Height)/2);
                shell.ShowInTaskbar=false; shell.TopMost=true; shell.Show(); shell.Activate();
                try {
                    Settle();
                    string output=args.Length>2?args[2]:null;
                    Inspect(shell,"overview",output,false);
                    var heading = (Label)type.GetField("title",Members).GetValue(shell);
                    foreach (var page in new[] {"music","ai","station","extensions","recovery","agent","music"}) {
                        var button = PageButton(shell,page);
                        button.PerformClick();
                        Settle();
                        if (heading.Text != button.Text)
                            throw new Exception("The page did not open: " + page);
                        CapturePage(shell,page,output);
                    }
                    shell.Width=Math.Max(shell.MinimumSize.Width,shell.Width-54);
                    shell.Height=Math.Max(shell.MinimumSize.Height,shell.Height-24);
                    shell.Location=new Point(Math.Min(shell.Left+12,area.Right-shell.Width),Math.Min(shell.Top+12,area.Bottom-shell.Height));
                    var link=PageButton(shell,"home");
                    link.PerformClick(); Settle();
                    SendMessage(link.Handle,0x200,IntPtr.Zero,new IntPtr((8<<16)|8)); Settle();
                    SendMessage(link.Handle,0x2a3,IntPtr.Zero,IntPtr.Zero);
                    shell.Invalidate(true); shell.Update();
                    Inspect(shell,"after-navigation",output,true);
                } finally {
                    bool restore = GetForegroundWindow() == shell.Handle;
                    shell.Close();
                    if (restore && previous != IntPtr.Zero) SetForegroundWindow(previous);
                }
            }
            Console.WriteLine("Visible paint: {0} samples, {1} pixel mismatches, {2} scrollbar errors.",
                samples,failures,scrollErrors);
            return failures==0 && scrollErrors==0 ? 0 : 1;
        } catch (Exception error) {
            Console.Error.WriteLine(error.GetBaseException().Message); return 2;
        }
    }
}
