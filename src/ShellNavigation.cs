using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AliceDesk
{
    internal static class ShellArt
    {
        internal static readonly Color Ink=Color.FromArgb(43,42,54), Muted=Color.FromArgb(93,88,106), Blue=Color.FromArgb(47,110,213);

        internal static GraphicsPath Shape(RectangleF bounds, float radius)
        {
            float d=Math.Min(2*radius,Math.Min(bounds.Width,bounds.Height));
            var path=new GraphicsPath();
            path.AddArc(bounds.X,bounds.Y,d,d,180,90);path.AddArc(bounds.Right-d,bounds.Y,d,d,270,90);
            path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90);path.AddArc(bounds.X,bounds.Bottom-d,d,d,90,90);
            path.CloseFigure();return path;
        }

        internal static Color Tint(string id)
        {
            switch(id) {
            case "station": return Color.FromArgb(119,83,202);
            case "ai": return Color.FromArgb(147,76,205);
            case "music": return Color.FromArgb(212,63,107);
            case "extensions": return Color.FromArgb(89,91,198);
            case "recovery": return Color.FromArgb(33,134,120);
            case "profiles": return Color.FromArgb(116,90,183);
            default: return Blue;
            }
        }

        // Draw line icons at a fixed design size and scale the pen together with each glyph.
        internal static void Glyph(Graphics g, string id, RectangleF box, Color color)
        {
            var state=g.Save();
            try {
                g.TranslateTransform(box.X,box.Y);g.ScaleTransform(box.Width/24f,box.Height/24f);
                g.SmoothingMode=SmoothingMode.AntiAlias;
                using(var p=new Pen(color,1.7f) {StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round}) {
                    switch(id) {
                    case "home":
                        g.DrawLines(p,new[]{new Point(2,11),new Point(12,3),new Point(22,11)});
                        g.DrawLines(p,new[]{new Point(5,10),new Point(5,20),new Point(10,20),new Point(10,14),new Point(14,14),new Point(14,20),new Point(19,20),new Point(19,10)});break;
                    case "station":
                        using(var shape=Shape(new RectangleF(4,5,16,15),4))g.DrawPath(p,shape);
                        g.DrawArc(p,4,3,16,8,0,180);g.DrawLine(p,8,15,10,15);g.DrawLine(p,14,15,16,15);break;
                    case "ai":
                        g.DrawLines(p,new[]{new Point(10,3),new Point(12,9),new Point(18,11),new Point(12,13),new Point(10,20),new Point(8,13),new Point(2,11),new Point(8,9),new Point(10,3)});
                        g.DrawLine(p,18,3,18,7);g.DrawLine(p,16,5,20,5);break;
                    case "agent":
                        using(var shape=Shape(new RectangleF(2.5f,4.5f,19,15),3))g.DrawPath(p,shape);
                        g.DrawLines(p,new[]{new Point(6,9),new Point(9,12),new Point(6,15)});g.DrawLine(p,12,15,16,15);break;
                    case "music":
                        g.DrawLine(p,8,17,8,6);g.DrawLine(p,8,6,19,3);g.DrawLine(p,19,3,19,15);g.DrawLine(p,8,9,19,6);
                        g.DrawEllipse(p,3,16,5,4);g.DrawEllipse(p,14,14,5,4);break;
                    case "extensions":
                        foreach(var x in new[]{3,13})foreach(var y in new[]{3,13})
                            using(var shape=Shape(new RectangleF(x,y,7,7),1.8f))g.DrawPath(p,shape);
                        break;
                    case "recovery":
                        g.DrawArc(p,4,4,16,16,192,306);g.DrawLines(p,new[]{new Point(2,5),new Point(3,10),new Point(8,9)});
                        g.DrawLine(p,12,8,12,12);g.DrawLine(p,12,12,15,14);break;
                    case "profiles":
                        g.DrawEllipse(p,9,4,6,6);g.DrawArc(p,5,13,14,12,192,156);break;
                    case "help":
                        g.DrawEllipse(p,3,3,18,18);g.DrawArc(p,9,7,6,6,190,245);g.DrawLine(p,12,13,12,14);g.DrawLine(p,12,17,12,17.2f);break;
                    case "search":
                        g.DrawEllipse(p,4,4,12,12);g.DrawLine(p,14.5f,14.5f,20,20);break;
                    case "forward":case "back":
                        int a=id=="back"?15:9,b=id=="back"?9:15;
                        g.DrawLines(p,new[]{new Point(a,5),new Point(b,12),new Point(a,19)});break;
                    }
                }
            } finally {g.Restore(state);}
        }
    }

    internal sealed class ShellSidebar : Panel
    {
        internal ShellSidebar() {DoubleBuffered=true;BackColor=Color.FromArgb(239,232,245);}
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var rect=ClientRectangle;if(rect.Width<1||rect.Height<1)return;
            using(var fill=new LinearGradientBrush(rect,Color.White,Color.White,76f)) {
                fill.InterpolationColors=new ColorBlend{Positions=new[]{0f,.42f,.77f,1f},
                    Colors=new[]{Color.FromArgb(246,230,235),Color.FromArgb(239,230,243),Color.FromArgb(227,228,246),Color.FromArgb(217,227,245)}};
                e.Graphics.FillRectangle(fill,rect);
            }
            using(var edge=new Pen(Color.FromArgb(208,204,218)))e.Graphics.DrawLine(edge,rect.Right-1,0,rect.Right-1,rect.Bottom);
        }
    }

    internal sealed class SidebarButton : Button
    {
        internal readonly string Page;
        internal bool Selected;
        bool hot;
        internal SidebarButton(string id,string text)
        {
            Page=id;Text=text;AccessibleName=text;AccessibleRole=AccessibleRole.PushButton;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.SupportsTransparentBackColor,true);
            FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;BackColor=Color.Transparent;
            Cursor=Cursors.Hand;TextAlign=ContentAlignment.MiddleLeft;
        }
        protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hot=true;Invalidate();}
        protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hot=false;Invalidate();}
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;float s=g.DpiX/96f;g.SmoothingMode=SmoothingMode.AntiAlias;
            var r=new RectangleF(1,1,Width-2,Height-2);
            using(var shape=ShellArt.Shape(r,7*s)) {
                if(Selected||hot)using(var brush=new SolidBrush(Selected?Color.FromArgb(223,211,233):Color.FromArgb(113,255,255,255)))g.FillPath(brush,shape);
                if(Focused&&ShowFocusCues)using(var pen=new Pen(ShellArt.Blue,1.5f*s))g.DrawPath(pen,shape);
            }
            int size=(int)(18*s),x=(int)(10*s);
            ShellArt.Glyph(g,Page,new RectangleF(x,(Height-size)/2f,size,size),ShellArt.Tint(Page));
            TextRenderer.DrawText(g,Text,Font,new Rectangle(x+size+(int)(9*s),0,Width-x-size-(int)(13*s),Height),ShellArt.Ink,
                TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding|TextFormatFlags.SingleLine);
        }
    }

    internal sealed class HistoryButton : Button
    {
        readonly string id;
        bool hot;
        internal HistoryButton(string id,string name)
        {
            this.id=id;AccessibleName=name;AccessibleRole=AccessibleRole.PushButton;TabStop=true;Cursor=Cursors.Hand;
            Size=new Size(32,32);FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Margin=Padding.Empty;
        }
        protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hot=true;Invalidate();}
        protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hot=false;Invalidate();}
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;float s=g.DpiX/96f;
            g.SmoothingMode=SmoothingMode.AntiAlias;
            if(Enabled && (hot || (Focused&&ShowFocusCues)))using(var r=ShellArt.Shape(new RectangleF(1,1,Width-2,Height-2),6*s))
                using(var fill=new SolidBrush(Color.FromArgb(230,229,236)))g.FillPath(fill,r);
            int k=(int)(19*s);
            ShellArt.Glyph(g,id,new RectangleF((Width-k)/2f,(Height-k)/2f,k,k),Enabled?ShellArt.Ink:Color.FromArgb(175,173,181));
        }
    }

    internal sealed class ProfileButton : Button
    {
        readonly Font nameFont=new Font("Segoe UI Semibold",9.5f);
        readonly Font captionFont=new Font("Segoe UI",8.25f);
        internal ProfileButton()
        {
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.SupportsTransparentBackColor,true);
            FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;BackColor=Color.Transparent;Cursor=Cursors.Hand;
        }
        protected override void Dispose(bool disposing){if(disposing){nameFont.Dispose();captionFont.Dispose();}base.Dispose(disposing);}
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;float s=g.DpiX/96f;
            using(var edge=new Pen(Color.FromArgb(100,158,151,173)))g.DrawLine(edge,13*s,0,Width-13*s,0);
            float size=32*s;var box=new RectangleF(14*s,(Height-size)/2,size,size);
            using(var color=new LinearGradientBrush(box,Color.FromArgb(173,150,217),Color.FromArgb(103,99,177),55f))
                g.FillEllipse(color,box);
            ShellArt.Glyph(g,"station",RectangleF.Inflate(box,-6*s,-6*s),Color.White);
            int x=(int)(54*s),w=Math.Max(10,Width-x-(int)(24*s));
            var flags=TextFormatFlags.NoPadding|TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(g,Text,nameFont,new Rectangle(x,Height/2-(int)(17*s),w,(int)(19*s)),ShellArt.Ink,flags);
            TextRenderer.DrawText(g,"Текущий профиль",captionFont,new Rectangle(x,Height/2+(int)(1*s),w,(int)(17*s)),ShellArt.Muted,flags);
            using(var pen=new Pen(ShellArt.Muted,1.2f*s)) {
                float cx=Width-16*s,cy=Height*.5f;
                g.DrawLines(pen,new[]{new PointF(cx-3*s,cy-s),new PointF(cx,cy-4*s),new PointF(cx+3*s,cy-s)});
                g.DrawLines(pen,new[]{new PointF(cx-3*s,cy+s),new PointF(cx,cy+4*s),new PointF(cx+3*s,cy+s)});
            }
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(ClientRectangle,-3,-3));
        }
    }

    internal sealed class SectionSearch : Panel
    {
        readonly Label placeholder=new Label();
        internal readonly TextBox Input=new TextBox();
        internal SectionSearch()
        {
            DoubleBuffered=true;BackColor=Color.FromArgb(248,248,250);Size=new Size(194,29);
            Input.AccessibleName="Поиск по разделам";Input.BorderStyle=BorderStyle.None;Input.BackColor=Color.White;Input.ForeColor=ShellArt.Ink;
            Input.Font=new Font("Segoe UI",9.5f);Input.MaxLength=100;
            placeholder.Text="Поиск по разделам";placeholder.ForeColor=ShellArt.Muted;placeholder.BackColor=Color.White;placeholder.Font=Input.Font;
            placeholder.TextAlign=ContentAlignment.MiddleLeft;placeholder.Cursor=Cursors.IBeam;placeholder.TabStop=false;
            Controls.Add(Input);Controls.Add(placeholder);placeholder.BringToFront();
            placeholder.MouseDown+=(s,e)=>Input.Focus();MouseDown+=(s,e)=>Input.Focus();
            Input.TextChanged+=(s,e)=>Sync();Input.GotFocus+=(s,e)=>Sync();Input.LostFocus+=(s,e)=>Sync();
        }
        void Sync(){placeholder.Visible=!Input.Focused && Input.TextLength==0;PerformLayout();Invalidate();}
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);if(Input==null || placeholder==null)return;
            int x=Height;int h=Input.PreferredHeight;
            var bounds=new Rectangle(x,(Height-h)/2,Math.Max(8,Width-x-9),h);
            placeholder.Bounds=bounds;
            if(!Input.Focused && Input.TextLength==0)bounds.Width=1;
            Input.Bounds=bounds;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            float s=e.Graphics.DpiX/96f;e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=ShellArt.Shape(new RectangleF(1,1,Width-3,Height-3),7*s))
            using(var bg=new SolidBrush(Color.White))using(var pen=new Pen(Input.Focused?ShellArt.Blue:Color.FromArgb(209,207,216),Input.Focused?2*s:1*s)) {
                e.Graphics.FillPath(bg,path);e.Graphics.DrawPath(pen,path);
            }
            var k=15*s;ShellArt.Glyph(e.Graphics,"search",new RectangleF(8*s,(Height-k)/2,k,k),ShellArt.Muted);
        }
    }
}
