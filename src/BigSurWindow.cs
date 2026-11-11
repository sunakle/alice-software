using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AliceDesk
{
    internal abstract class BigSurWindow : Form, IMessageFilter
    {
        const int WmNcCalcSize = 0x0083, WmNcHitTest = 0x0084;
        const int WmNcLeftDown = 0x00A1, WmNcDoubleClick = 0x00A3, WmSysCommand = 0x0112;
        const int WmMouseMove = 0x0200, WmLeftDown = 0x0201, HtCaption = 2;
        const int HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13, HtTopRight = 14;
        const int HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;
        const int ScSize = 0xF000, ScMove = 0xF010, ScMaximize = 0xF030, ScRestore = 0xF120;
        const int NativeFrame = 0x00C00000 | 0x00040000 | 0x00080000 | 0x00010000 | 0x00020000;
        readonly HashSet<Control> dragSurfaces = new HashSet<Control>();
        Control pendingDrag;
        Point dragOrigin;
        Size clipSize;
        int dpi = 96;

        [StructLayout(LayoutKind.Sequential)]
        struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] static extern IntPtr GetSystemMenu(IntPtr window, bool revert);
        [DllImport("user32.dll")] static extern int EnableMenuItem(IntPtr menu, int cmd, int flags);
        [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);

        protected BigSurWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            DoubleBuffered = true;
        }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // Keep the system's move, resize, taskbar, Snap and system-menu behavior.
                // Keep WinForms' later client-to-outer size calculations borderless.
                if (!IsHandleCreated) cp.Style |= NativeFrame;
                return cp;
            }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (var g = CreateGraphics()) dpi = (int)Math.Round(g.DpiX);
            Application.AddMessageFilter(this);
            UpdateClip();
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            Application.RemoveMessageFilter(this);
            base.OnHandleDestroyed(e);
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateClip();
        }
        int ScalePx(int value) { return (value * dpi + 48) / 96; }
        void UpdateClip()
        {
            if (!IsHandleCreated || IsDisposed || Disposing || WindowState == FormWindowState.Minimized) return;
            Size wanted = WindowState == FormWindowState.Maximized ? Size.Empty : Size;
            if (wanted == clipSize) return;
            clipSize = wanted;
            Region previous = Region;
            if (wanted.IsEmpty) Region = null;
            else {
                int d = Math.Min(ScalePx(19), Math.Min(Width - 1, Height - 1));
                if (d < 2) return;
                using (var path = new GraphicsPath()) {
                    path.AddArc(0, 0, d, d, 180, 90);
                    path.AddArc(Width - d, 0, d, d, 270, 90);
                    path.AddArc(Width - d, Height - d, d, d, 0, 90);
                    path.AddArc(0, Height - d, d, d, 90, 90);
                    path.CloseFigure();
                    Region = new Region(path);
                }
            }
            if (previous != null) previous.Dispose();
        }
        internal Control CreateWindowButtons()
        {
            var dots = new WindowButtons();
            dots.CloseButton.Click += delegate { Close(); };
            dots.MinimizeButton.Click += delegate { WindowState = FormWindowState.Minimized; };
            dots.ZoomButton.Click += delegate { ToggleZoom(); };
            EventHandler stateChanged = delegate {
                dots.ZoomButton.Restore = WindowState == FormWindowState.Maximized;
                dots.SetZoomHint();
            };
            Resize += stateChanged;
            dots.Disposed += delegate { Resize -= stateChanged; };
            stateChanged(this, EventArgs.Empty);
            return dots;
        }
        void ToggleZoom()
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }
        internal void EnableTitleDrag(Control surface)
        {
            if (!dragSurfaces.Add(surface)) return;
            surface.MouseDown += TitleMouseDown;
            surface.MouseMove += TitleMouseMove;
            surface.MouseUp += TitleMouseUp;
            surface.Disposed += delegate { dragSurfaces.Remove(surface); };
            // A caption label has its own HWND, while text fields and buttons must keep their input.
            foreach (Control child in surface.Controls) if (child is Label) EnableTitleDrag(child);
            surface.ControlAdded += delegate(object sender, ControlEventArgs e) {
                if (e.Control is Label) EnableTitleDrag(e.Control);
            };
        }
        void TitleMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.Clicks == 2) { pendingDrag = null; ToggleZoom(); return; }
            pendingDrag = (Control)sender;
            dragOrigin = pendingDrag.PointToScreen(e.Location);
        }
        void TitleMouseMove(object sender, MouseEventArgs e)
        {
            if (pendingDrag != sender || e.Button != MouseButtons.Left) return;
            Point current = pendingDrag.PointToScreen(e.Location);
            Size threshold = SystemInformation.DragSize;
            if (Math.Abs(current.X - dragOrigin.X) < Math.Max(1, threshold.Width / 2) &&
                Math.Abs(current.Y - dragOrigin.Y) < Math.Max(1, threshold.Height / 2)) return;
            pendingDrag = null;
            ReleaseCapture();
            SendMessage(Handle, WmNcLeftDown, new IntPtr(HtCaption), Pack(current));
        }
        void TitleMouseUp(object sender, MouseEventArgs e)
        {
            pendingDrag = null;
            if (e.Button == MouseButtons.Right) ShowSystemMenu(((Control)sender).PointToScreen(e.Location));
        }
        void ShowSystemMenu(Point at)
        {
            IntPtr menu = GetSystemMenu(Handle, false);
            if (menu == IntPtr.Zero) return;
            bool maximized = WindowState == FormWindowState.Maximized;
            EnableMenuItem(menu, ScRestore, maximized ? 0 : 1);
            foreach (int command in new[] { ScMove, ScSize, ScMaximize }) EnableMenuItem(menu, command, maximized ? 1 : 0);
            int selected = TrackPopupMenuEx(menu, 0x0102, at.X, at.Y, Handle, IntPtr.Zero);
            if (selected != 0) PostMessage(Handle, WmSysCommand, new IntPtr(selected), IntPtr.Zero);
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Alt | Keys.Space)) {
                ShowSystemMenu(PointToScreen(new Point(ScalePx(16), ScalePx(35))));
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        static Point Unpack(IntPtr packed)
        {
            int n = unchecked((int)packed.ToInt64());
            return new Point(unchecked((short)n), unchecked((short)(n >> 16)));
        }
        static IntPtr Pack(Point p) { return new IntPtr(unchecked(((p.Y & 0xffff) << 16) | (p.X & 0xffff))); }
        int EdgeAt(Point local)
        {
            if (WindowState != FormWindowState.Normal || !ClientRectangle.Contains(local)) return 0;
            int margin = ScalePx(6);
            bool left = local.X < margin, right = local.X >= ClientSize.Width - margin;
            bool top = local.Y < margin, bottom = local.Y >= ClientSize.Height - margin;
            if (top) return left ? HtTopLeft : right ? HtTopRight : HtTop;
            if (bottom) return left ? HtBottomLeft : right ? HtBottomRight : HtBottom;
            return left ? HtLeft : right ? HtRight : 0;
        }
        static Cursor EdgeCursor(int edge)
        {
            if (edge == HtLeft || edge == HtRight) return Cursors.SizeWE;
            if (edge == HtTop || edge == HtBottom) return Cursors.SizeNS;
            return edge == HtTopLeft || edge == HtBottomRight ? Cursors.SizeNWSE : Cursors.SizeNESW;
        }
        bool IMessageFilter.PreFilterMessage(ref Message m)
        {
            // Docked controls also cover the resize border. Forward clicks there to the native size loop.
            if ((m.Msg != WmMouseMove && m.Msg != WmLeftDown) || !IsHandleCreated || !Visible || !IsChild(Handle, m.HWnd)) return false;
            Point screen = Unpack(m.LParam);
            if (!ClientToScreen(m.HWnd, ref screen)) return false;
            int edge = EdgeAt(PointToClient(screen));
            if (edge == 0) return false;
            if (m.Msg == WmMouseMove) { Cursor.Current = EdgeCursor(edge); return false; }
            ReleaseCapture();
            SendMessage(Handle, WmNcLeftDown, new IntPtr(edge), Pack(screen));
            return true;
        }
        protected override void WndProc(ref Message m)
        {
            const int wmStyleChanging = 0x007C, gwlStyle = -16;
            if (m.Msg == wmStyleChanging && m.WParam.ToInt64() == gwlStyle) {
                int proposed = Marshal.ReadInt32(m.LParam, sizeof(int));
                Marshal.WriteInt32(m.LParam, sizeof(int), proposed | NativeFrame);
            }
            if (m.Msg == WmNcCalcSize) {
                // When maximized Win32 puts the sizing frame outside the work area. Keep pixels
                // under that frame out of the client area, including on a non-primary monitor.
                if (IsZoomed(m.HWnd)) {
                    var r = (NativeRect)Marshal.PtrToStructure(m.LParam, typeof(NativeRect));
                    var proposed = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    var work = Screen.FromRectangle(proposed).WorkingArea;
                    if (proposed.Contains(work)) {
                        r.Left = work.Left; r.Top = work.Top; r.Right = work.Right; r.Bottom = work.Bottom;
                        Marshal.StructureToPtr(r, m.LParam, false);
                    }
                }
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WmNcHitTest) {
                int edge = EdgeAt(PointToClient(Unpack(m.LParam)));
                if (edge != 0) { m.Result = new IntPtr(edge); return; }
                // The normal Win32 caption, including its invisible close hotspot, has been replaced.
                m.Result = new IntPtr(1);
                return;
            }
            if (m.Msg == WmNcDoubleClick && m.WParam.ToInt32() == HtCaption) { ToggleZoom(); return; }
            base.WndProc(ref m);
        }
    }

    internal sealed class WindowButtons : FlowLayoutPanel
    {
        internal readonly WindowDot CloseButton = new WindowDot(0, Color.FromArgb(255, 96, 87), "Закрыть окно");
        internal readonly WindowDot MinimizeButton = new WindowDot(1, Color.FromArgb(255, 190, 47), "Свернуть окно");
        internal readonly WindowDot ZoomButton = new WindowDot(2, Color.FromArgb(40, 200, 64), "Развернуть окно");
        readonly ToolTip hints = new ToolTip();
        internal bool Highlight { get; private set; }

        internal WindowButtons()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(66, 30);
            WrapContents = false;
            Margin = Padding.Empty;
            foreach (WindowDot dot in new[] { CloseButton, MinimizeButton, ZoomButton }) {
                Controls.Add(dot);
                hints.SetToolTip(dot, dot.AccessibleName);
                dot.MouseEnter += delegate { Highlight = true; Invalidate(true); };
                dot.MouseLeave += delegate { Highlight = ClientRectangle.Contains(PointToClient(MousePosition)); Invalidate(true); };
            }
            MouseLeave += delegate { Highlight = false; Invalidate(true); };
        }
        internal void SetZoomHint()
        {
            ZoomButton.AccessibleName = ZoomButton.Restore ? "Вернуть размер окна" : "Развернуть окно";
            hints.SetToolTip(ZoomButton, ZoomButton.AccessibleName);
            ZoomButton.Invalidate();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) hints.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class WindowDot : PaintedButton
    {
        readonly int kind;
        readonly Color color;
        internal bool Restore;
        bool pressed;
        internal WindowDot(int kind, Color color, string caption)
        {
            this.kind = kind;
            this.color = color;
            Size = new Size(22, 30);
            Margin = Padding.Empty;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            UseVisualStyleBackColor = false;
            BackColor = Color.Transparent;
            AccessibleName = caption;
            AccessibleRole = AccessibleRole.PushButton;
            TabStop = true;
        }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) { pressed = false; Invalidate(); } base.OnMouseCaptureChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = g.DpiX / 96f, d = 12 * scale;
            float x = (ClientSize.Width - 1) / 2f, y = (ClientSize.Height - 1) / 2f;
            using (var fill = new SolidBrush(pressed ? ControlPaint.Dark(color, .15f) : color))
            using (var edge = new Pen(Color.FromArgb(50, 61, 39, 42), Math.Max(1, .7f * scale))) {
                g.FillEllipse(fill, x - d / 2, y - d / 2, d, d);
                g.DrawEllipse(edge, x - d / 2, y - d / 2, d, d);
            }
            var group = Parent as WindowButtons;
            if (Focused && ShowFocusCues) {
                using (var focus = new Pen(Color.FromArgb(29, 100, 205), 1.3f * scale))
                    g.DrawEllipse(focus, x - d / 2 - 2 * scale, y - d / 2 - 2 * scale, d + 4 * scale, d + 4 * scale);
            }
            if ((group == null || !group.Highlight) && !(Focused && ShowFocusCues)) return;
            using (var pen = new Pen(Color.FromArgb(157, 52, 39, 39), Math.Max(1, 1.2f * scale))) {
                float r = 2.55f * scale;
                if (kind == 0) { g.DrawLine(pen, x-r, y-r, x+r, y+r); g.DrawLine(pen, x+r, y-r, x-r, y+r); }
                else if (kind == 1) g.DrawLine(pen, x-r, y, x+r, y);
                else {
                    float gap = Restore ? .3f * scale : 1.1f * scale;
                    g.DrawLines(pen, new[]{new PointF(x-r,y+gap),new PointF(x-r,y+r),new PointF(x-gap,y+r)});
                    g.DrawLines(pen, new[]{new PointF(x+gap,y-r),new PointF(x+r,y-r),new PointF(x+r,y-gap)});
                    g.DrawLine(pen, x-r, y+r, x+r, y-r);
                }
            }
        }
    }
}
