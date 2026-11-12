using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class TabStripTest
{
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr handle, uint flag);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr handle, uint attr, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }
    static readonly BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static int failures, checkedStrips;

    static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls) {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
    static void Settle()
    {
        var timer = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(15); } while (timer.ElapsedMilliseconds < 180);
        DwmFlush();
    }
    static bool Clear(Form shell, Rectangle rect)
    {
        if (!Screen.FromControl(shell).Bounds.Contains(rect)) return false;
        for (IntPtr w = GetWindow(shell.Handle, 3); w != IntPtr.Zero; w = GetWindow(w, 3)) {
            Rect r; int cloaked;
            if (IsWindowVisible(w) && GetWindowRect(w, out r) &&
                Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom).IntersectsWith(rect) &&
                !(DwmGetWindowAttribute(w,14,out cloaked,4)==0 && cloaked!=0)) return false;
        }
        foreach (int x in new[] { rect.Left, rect.Left+rect.Width/2, rect.Right-1 })
            foreach (int y in new[] { rect.Top, rect.Top+rect.Height/2, rect.Bottom-1 })
                if (GetAncestor(WindowFromPoint(new Point(x,y)), 2) != shell.Handle) return false;
        return true;
    }
    [STAThread] static int Main(string[] args)
    {
        IntPtr previous = GetForegroundWindow();
        try {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var asm = Assembly.LoadFrom(args[0]);
            var store = Activator.CreateInstance(asm.GetType("AliceDesk.ProfileStore",true), Members, null,
                new object[] {Path.Combine(args[1],"profile")}, null);
            using (var shell = (Form)Activator.CreateInstance(asm.GetType("AliceDesk.Shell",true), Members, null,
                new object[] {store, false}, null)) {
                var area=Screen.PrimaryScreen.WorkingArea;
                shell.StartPosition=FormStartPosition.Manual;
                shell.Size=new Size(Math.Min(1160,area.Width),Math.Min(800,area.Height));
                shell.Location=new Point(area.Left+(area.Width-shell.Width)/2,area.Top+(area.Height-shell.Height)/2);
                shell.ShowInTaskbar=false; shell.TopMost=true; shell.Show(); shell.Activate();
                try {
                    Settle();
                    foreach(string page in new[] {"ai","agent"}) {
                    var button=Descendants(shell).OfType<Button>().Single(b=>b.Visible && b.GetType().Name=="SidebarButton" &&
                        (string)b.GetType().GetField("Page",Members).GetValue(b)==page);
                    button.PerformClick(); Settle();
                    var tabs=Descendants(shell).OfType<TabControl>().Single(t=>t.Visible && t.GetType().Name=="SettingsTabs");
                    int expected=page=="ai"?3:2;
                    if(tabs.TabCount!=expected)throw new Exception("Unexpected number of tabs in "+page);
                    for(int index=0;index<tabs.TabCount;index++) {
                        Rectangle click=tabs.GetTabRect(index);
                        int pos=((click.Top+click.Height/2)<<16)|((click.Left+click.Width/2)&0xffff);
                        SendMessage(tabs.Handle,0x201,new IntPtr(1),new IntPtr(pos));
                        SendMessage(tabs.Handle,0x202,IntPtr.Zero,new IntPtr(pos));
                        Settle();
                        if(tabs.SelectedIndex!=index)throw new Exception("Tab click did not select "+index+" in "+page);
                        var tab=tabs.GetTabRect(0);
                        int tabHeight=tab.Bottom+4;
                        var screen=tabs.RectangleToScreen(new Rectangle(0,0,Math.Min(tabs.Width,tabs.GetTabRect(tabs.TabCount-1).Right+14),tabHeight));
                        var wait=Stopwatch.StartNew();
                        while(!Clear(shell,screen)) {
                            if(wait.ElapsedMilliseconds>2500)throw new Exception("Test window covered, capture discarded.");
                            Settle();
                        }
                        using(var image=new Bitmap(screen.Width,screen.Height)) {
                            using(var g=Graphics.FromImage(image))g.CopyFromScreen(screen.Location,Point.Empty,screen.Size);
                            if(!Clear(shell,screen))throw new Exception("Test window covered, capture discarded.");
                            int top=tab.Top+tab.Height/4, bottom=tab.Bottom-tab.Height/4;
                            // Scan vertical edges in the middle half of the tabs. Native
                            // dark-theme borders showed up as near-black, full-height lines.
                            int maxRun=0, worstX=-1;
                            for(int x=0;x<image.Width;x++) {
                                int run=0;
                                for(int y=top;y<bottom;y++) {
                                    Color pixel=image.GetPixel(x,y);
                                    if(pixel.R<20 && pixel.G<20 && pixel.B<20)run++;
                                }
                                if(run>maxRun){maxRun=run;worstX=x;}
                            }
                            checkedStrips++;
                            if(maxRun>=(bottom-top)*.7f) {
                                failures++;
                                Console.WriteLine("{0} tab {1}: dark vertical stripe at x={2} ({3}/{4} pixels).",
                                    page,index,worstX,maxRun,bottom-top);
                            }
                            if(args.Length>2)image.Save(Path.Combine(args[2],page+"-"+index+".png"),ImageFormat.Png);
                        }
                    }
                    }
                } finally {
                    bool restore=GetForegroundWindow()==shell.Handle;
                    shell.Close();
                    if(restore && previous!=IntPtr.Zero)SetForegroundWindow(previous);
                }
            }
            Console.WriteLine("Checked {0} tab strips, {1} with dark stripes.",checkedStrips,failures);
            return failures==0?0:1;
        } catch(Exception ex) {Console.Error.WriteLine(ex.GetBaseException().Message);return 2;}
    }
}
