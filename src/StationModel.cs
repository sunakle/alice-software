using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AliceDesk
{
    // A small software-rendered mesh keeps the preview responsive without a GPU dependency.
    internal sealed class StationModel : Control
    {
        const int Segments = 64;
        float angle;
        int lastX;
        bool dragging;
        internal bool ShowClock { get; set; }

        internal StationModel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.FromArgb(35, 37, 48);
            Cursor = Cursors.SizeWE;
            AccessibleName = "Трёхмерная модель Станции";
            AccessibleDescription = "Перетащите мышью, чтобы повернуть модель";
            ShowClock = true;
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { dragging = true; lastX = e.X; Capture = true; }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            angle += (e.X - lastX) * .012f;
            lastX = e.X;
            Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Capture = false; base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) dragging = false; base.OnMouseCaptureChanged(e); }

        PointF Project(float theta, float height, float radius)
        {
            float a = theta + angle;
            float x = (float)Math.Sin(a) * radius;
            float z = -(float)Math.Cos(a) * radius;
            float size = Math.Min(Width / 3.2f, Height / 2.25f);
            return new PointF(Width / 2f + x * size, Height / 2f + (height - z * .25f) * size);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shadow = new SolidBrush(Color.FromArgb(65, 0, 0, 0)))
                g.FillEllipse(shadow, Width * .19f, Height * .78f, Width * .62f, Height * .09f);

            // Draw the visible half of the cylindrical body, from its sides toward the viewer.
            g.SmoothingMode = SmoothingMode.None;
            for (int i = 0; i < Segments; i++) {
                float world = (float)(-Math.PI / 2 + Math.PI * i / Segments);
                float next = (float)(-Math.PI / 2 + Math.PI * (i + 1) / Segments);
                float t = world - angle, n = next - angle;
                float light = (float)Math.Cos(world);
                int red = (int)(85 + 54 * light), green = (int)(82 + 48 * light), blue = (int)(153 + 57 * light);
                using (var brush = new SolidBrush(Color.FromArgb(red, green, blue)))
                    g.FillPolygon(brush, new[] { Project(t, -.55f, 1), Project(n, -.55f, 1), Project(n, .58f, 1), Project(t, .58f, 1) });
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var top = new PointF[Segments];
            for (int i = 0; i < Segments; i++) top[i] = Project((float)(2 * Math.PI * i / Segments), -.55f, 1);
            using (var brush = new SolidBrush(Color.FromArgb(170, 159, 224))) g.FillPolygon(brush, top);
            using (var pen = new Pen(Color.FromArgb(185, 221, 215, 255), 2)) {
                var center = Project(0, -.56f, 0);
                float size = Math.Min(Width / 3.2f, Height / 2.25f);
                g.DrawEllipse(pen, center.X - size * .13f, center.Y - size * .035f, size * .26f, size * .07f);
            }
            if (ShowClock && Math.Cos(angle) > .15) DrawClock(g);
        }
        void DrawClock(Graphics g)
        {
            int[] masks = { 0x06, 0x5b, 0x4f, 0x6d };
            float[,] bars = { {0,0,.12f,0}, {.12f,0,.12f,.13f}, {.12f,.13f,.12f,.26f}, {0,.26f,.12f,.26f}, {0,.13f,0,.26f}, {0,0,0,.13f}, {0,.13f,.12f,.13f} };
            using (var pen = new Pen(Color.FromArgb(244, 242, 255), Math.Max(2, Width / 150f))) {
                pen.StartCap = pen.EndCap = LineCap.Round;
                for (int digit = 0; digit < 4; digit++) {
                    float offset = -.37f + digit * .18f + (digit >= 2 ? .04f : 0);
                    for (int b = 0; b < 7; b++) if ((masks[digit] & (1 << b)) != 0) {
                        float x1 = offset + bars[b,0], x2 = offset + bars[b,2];
                        g.DrawLine(pen, Project((float)Math.Asin(x1), -.03f + bars[b,1], 1.012f), Project((float)Math.Asin(x2), -.03f + bars[b,3], 1.012f));
                    }
                }
                var a = Project(0, .05f, 1.012f); var btm = Project(0, .15f, 1.012f);
                g.DrawLine(pen, a.X, a.Y, a.X + .1f, a.Y); g.DrawLine(pen, btm.X, btm.Y, btm.X + .1f, btm.Y);
            }
        }
    }
}
