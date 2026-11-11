using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AliceDesk
{
    internal sealed class BigSurHome : UserControl
    {
        static readonly Color Canvas = Color.FromArgb(248, 248, 251);
        static readonly Color Ink = Color.FromArgb(32, 33, 43);
        static readonly Color Secondary = Color.FromArgb(92, 95, 110);
        readonly Panel sheet = new Panel();
        readonly Hero banner;
        readonly Label computerHeading, stationHeading;
        readonly Tile[] computer, station;
        float scale = 1f;
        bool arranging;

        internal BigSurHome(Profile profile, Action<string> navigate)
        {
            AutoScaleMode = AutoScaleMode.None;
            AutoScroll = true;
            BackColor = Canvas;
            AccessibleName = "Обзор Станции";
            sheet.BackColor = Canvas;
            Controls.Add(sheet);
            string model = profile.Get("station.expected", "Яндекс Станция Лайт 2 с часами · YNDX-00026");
            banner = new Hero(model, delegate { navigate("station"); });
            sheet.Controls.Add(banner);

            computerHeading = Heading("Без подключения к Станции");
            computer = new[] {
                AddTile("ai", "Нейросети", "DeepSeek и другие модели", navigate),
                AddTile("agent", "Команды ПК", "Приложения и фразы", navigate),
                AddTile("music", "Музыка", "Spotify и Bluetooth", navigate)
            };
            stationHeading = Heading("Настройка Станции");
            station = new[] {
                AddTile("extensions", "Расширения", "Каталог возможностей", navigate),
                AddTile("recovery", "Диагностика", "Проверки и резервные копии", navigate),
                AddTile("profiles", "Профили", "Открыть сохранённый профиль", navigate)
            };
        }

        Label Heading(string text)
        {
            var label = new Label { Text = text, ForeColor = Ink, BackColor = Canvas, Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = Padding.Empty };
            sheet.Controls.Add(label);
            return label;
        }
        Tile AddTile(string id, string caption, string detail, Action<string> navigate)
        {
            var tile = new Tile(id, caption, detail);
            tile.Click += delegate { navigate(id); };
            sheet.Controls.Add(tile);
            return tile;
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            PerformLayout();
        }
        int S(float value) { return (int)Math.Round(value * scale); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (arranging || station == null || ClientSize.Width < 1) return;
            arranging = true;
            try {
                int width = Math.Min(ClientSize.Width, S(1220));
                int pad = S(width < S(600) ? 15 : 28);
                int available = Math.Max(1, width - pad * 2);
                int columns = available >= S(594) ? 3 : available >= S(390) ? 2 : 1;
                int gap = S(12);
                int y = S(22);
                int heroHeight = S(available >= S(525) ? 229 : 184);
                banner.SetBounds(pad, y, available, heroHeight);
                y += heroHeight + S(19);
                y = PlaceGroup(computerHeading, computer, y, pad, available, columns, gap);
                y = PlaceGroup(stationHeading, station, y + S(7), pad, available, columns, gap);
                Point scroll = AutoScrollPosition;
                sheet.SetBounds(Math.Max(0, (ClientSize.Width - width) / 2) + scroll.X, scroll.Y, width, y + S(22));
                AutoScrollMinSize = new Size(0, sheet.Height);
            } finally { arranging = false; }
        }
        int PlaceGroup(Label heading, Tile[] tiles, int y, int pad, int width, int count, int gap)
        {
            heading.SetBounds(pad, y, width, S(34));
            y += S(42);
            int h = S(count == 1 ? 78 : 108);
            for (int i = 0; i < tiles.Length; i++) {
                int col = i % count, row = i / count;
                int left = (width + gap) * col / count;
                int right = (width + gap) * (col + 1) / count - gap;
                tiles[i].SetBounds(pad + left, y + row * (h + gap), right - left, h);
            }
            return y + ((tiles.Length + count - 1) / count) * (h + gap);
        }

        static GraphicsPath Round(float x, float y, float width, float height, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Max(1, Math.Min(radius * 2, Math.Min(width, height)));
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + width - d, y, d, d, 270, 90);
            p.AddArc(x + width - d, y + height - d, d, d, 0, 90);
            p.AddArc(x, y + height - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        static void Shape(Graphics g, GraphicsPath path, RectangleF space, Color first, Color last, float angle)
        {
            using (var brush = new LinearGradientBrush(space, first, last, angle)) g.FillPath(brush, path);
        }

        sealed class Hero : Panel
        {
            readonly string model;
            readonly HeroButton connect;
            readonly Font titleFont = new Font("Segoe UI", 28.5f, FontStyle.Bold);
            readonly Font modelFont = new Font("Segoe UI", 10.5f, FontStyle.Regular);
            public Hero(string model, Action action)
            {
                this.model = model;
                AccessibleName = "Моя Станция";
                AccessibleDescription = "Ожидаемая модель — " + model + ". Иллюстрация колонки.";
                BackColor = Canvas;
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
                connect = new HeroButton();
                connect.Click += delegate { action(); };
                Controls.Add(connect);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { titleFont.Dispose(); modelFont.Dispose(); }
                base.Dispose(disposing);
            }
            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (connect == null || !IsHandleCreated) return;
                float d;
                using (var g = CreateGraphics()) d = g.DpiX / 96f;
                int left = (int)((Width >= 525*d ? 27 : 18) * d);
                connect.SetBounds(left, Height - (int)(54*d), (int)(181*d), (int)(31*d));
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                int w = Width, h = Height;
                if (w < 4 || h < 4) return;
                float d = g.DpiX / 96f;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var mask = Round(1, 1, w-2, h-2, 15*d)) {
                    GraphicsState clip = g.Save();
                    g.SetClip(mask);
                    GraphicsState waves = g.Save();
                    g.ScaleTransform(w/920f, h/236f);
                    Landscape(g);
                    g.Restore(waves);
                    bool wide = w >= 525*d;
                    if (wide) {
                        float s = Math.Min(h/198f, w/560f);
                        float cx = Math.Min(w*.817f, w - 115*s);
                        GraphicsState product = g.Save();
                        g.TranslateTransform(cx - 103*s, h*.48f - 70*s);
                        g.ScaleTransform(s, s);
                        Speaker(g);
                        g.Restore(product);
                    }
                    int left = (int)((wide ? 27 : 18)*d);
                    int top = (int)(h*(wide ? .225f : .145f));
                    int textWidth = wide ? (int)(w*.58f)-left : w-left*2;
                    var titleBox = new Rectangle(left, top, textWidth, (int)(53*d));
                    var modelBox = new Rectangle(left+1, titleBox.Bottom, textWidth, (int)(39*d));
                    TextRenderer.DrawText(g, "Моя Станция", titleFont, titleBox, Color.White, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                    TextRenderer.DrawText(g, model, modelFont, modelBox, Color.White, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                    g.Restore(clip);
                }
            }
            static void Landscape(Graphics g)
            {
                var scene = new RectangleF(0,0,920,236);
                using (var sky = new LinearGradientBrush(scene, Color.FromArgb(105,207,238), Color.FromArgb(15,104,188), 10f))
                    g.FillRectangle(sky, scene);
                using (var p = new GraphicsPath()) {
                    p.StartFigure(); p.AddBezier(-10,-22,187,-18,266,-20,388,35);
                    p.AddBezier(388,35,523,98,594,64,700,94); p.AddBezier(700,94,789,120,845,108,935,129);
                    p.AddLine(935,129,935,241); p.AddLine(935,241,-10,241); p.CloseFigure();
                    Shape(g,p,scene,Color.FromArgb(81,173,219),Color.FromArgb(45,133,209),10f);
                }
                using (var p = new GraphicsPath()) {
                    p.AddBezier(-12,-25,100,-18,151,5,279,75); p.AddBezier(279,75,399,137,488,88,596,124);
                    p.AddBezier(596,124,712,164,807,128,935,169); p.AddLine(935,169,935,244);
                    p.AddLine(935,244,-12,244);p.CloseFigure();
                    Shape(g,p,scene,Color.FromArgb(169,144,221),Color.FromArgb(222,232,252),4f);
                }
                using (var p = new GraphicsPath()) {
                    p.AddBezier(-12,-17,112,-10,164,17,260,72);p.AddBezier(260,72,373,139,402,118,512,155);
                    p.AddBezier(512,155,627,192,708,141,804,170);p.AddBezier(804,170,860,183,894,195,935,190);
                    p.AddLine(935,190,935,244);p.AddLine(935,244,-12,244);p.CloseFigure();
                    Shape(g,p,scene,Color.FromArgb(254,102,86),Color.FromArgb(255,191,85),8f);
                }
                using (var p = new GraphicsPath()) {
                    p.AddBezier(-15,-8,135,-1,164,59,290,102);p.AddBezier(290,102,378,132,410,125,491,171);
                    p.AddBezier(491,171,602,235,713,174,816,206);p.AddBezier(816,206,879,224,896,226,936,226);
                    p.AddLine(936,226,936,244);p.AddLine(936,244,-15,244);p.CloseFigure();
                    Shape(g,p,scene,Color.FromArgb(219,53,112),Color.FromArgb(255,107,92),0f);
                }
                using (var p = new GraphicsPath()) {
                    p.AddBezier(-10,101,133,104,199,171,339,185); p.AddBezier(339,185,424,194,474,205,552,244);
                    p.AddLine(552,244,-10,244);p.CloseFigure();
                    Shape(g,p,scene,Color.FromArgb(112,67,151),Color.FromArgb(228,69,118),12f);
                }
                using (var wash = new LinearGradientBrush(new RectangleF(0,0,693,237),Color.White,Color.Black,0f)) {
                    wash.InterpolationColors = new ColorBlend {
                        Positions=new[]{0f,.55f,.76f,1f},
                        Colors=new[]{Color.FromArgb(151,77,26,113),Color.FromArgb(115,83,26,119),
                            Color.FromArgb(92,65,35,111),Color.FromArgb(0,62,32,109)}
                    };
                    g.FillRectangle(wash,0,0,692,236);
                }
            }

            static void Speaker(Graphics g)
            {
                var box = new RectangleF(0,0,211,151);
                using(var shadow = new GraphicsPath()) {
                    shadow.AddEllipse(-9,112,232,43);
                    using(var fade = new PathGradientBrush(shadow)) {
                        fade.CenterColor=Color.FromArgb(90,44,27,71); fade.SurroundColors=new[]{Color.FromArgb(0,45,24,65)};
                        g.FillPath(fade,shadow);
                    }
                }
                using(var body = new GraphicsPath()) {
                    body.AddBezier(7,43,12,26,46,20,76,18); body.AddLine(76,18,173,8);
                    body.AddBezier(173,8,194,8,204,15,205,31); body.AddLine(205,31,203,102);
                    body.AddBezier(203,102,202,118,190,128,172,132); body.AddLine(172,132,43,145);
                    body.AddBezier(43,145,21,146,10,137,9,121);body.CloseFigure();
                    using(var coating=new LinearGradientBrush(box,Color.White,Color.Black,4f)) {
                        coating.InterpolationColors=new ColorBlend {
                            Positions=new[]{0f,.22f,.63f,.83f,1f}, Colors=new[]{
                                Color.FromArgb(138,133,201),Color.FromArgb(170,160,224),Color.FromArgb(148,137,210),
                                Color.FromArgb(121,114,188),Color.FromArgb(79,81,152)}
                        };g.FillPath(coating,body);
                    }
                    using(var outline=new Pen(Color.FromArgb(95,78,78,148),.8f))g.DrawPath(outline,body);
                }
                using(var lid=new GraphicsPath()) {
                    lid.AddBezier(7,44,6,38,13,27,22,18);lid.AddBezier(22,18,27,12,33,10,43,9);
                    lid.AddLine(43,9,164,1);lid.AddBezier(164,1,181,0,191,3,198,9);
                    lid.AddBezier(198,9,207,17,204,24,197,31);lid.AddLine(197,31,181,44);
                    lid.AddBezier(181,44,176,48,169,51,158,52);lid.AddLine(158,52,36,63);
                    lid.AddBezier(36,63,19,65,9,58,7,48);lid.CloseFigure();
                    Shape(g,lid,box,Color.FromArgb(218,204,251),Color.FromArgb(159,157,224),30f);
                    using(var line=new Pen(Color.FromArgb(142,236,231,255),1f))g.DrawPath(line,lid);
                }
                using(var seam=new Pen(Color.FromArgb(45,71,69,138),1.2f)) {
                    g.DrawBezier(seam,11,120,11,135,23,143,44,141);
                    g.DrawLine(seam,44,141,171,128);g.DrawBezier(seam,171,128,190,125,199,118,202,109);
                }
                using(var glow=new SolidBrush(Color.FromArgb(76,236,235,255)))g.FillEllipse(glow,89,23,28,11);
                using(var ring=new Pen(Color.FromArgb(242,237,252,255),2.25f))g.DrawEllipse(ring,89,23,28,10);
                using(var key=new Pen(Color.FromArgb(197,247,246,255),1.35f)) {
                    key.StartCap=LineCap.Round;key.EndCap=LineCap.Round;
                    g.DrawLine(key,44,36,52,35);g.DrawLine(key,153,26,161,25.5f);g.DrawLine(key,157,23,157,28);
                }
                using(var mic=new SolidBrush(Color.FromArgb(97,90,107,166))) {
                    g.FillEllipse(mic,64,18,2,1.3f);g.FillEllipse(mic,134,42,2,1.3f);
                }
                GraphicsState face=g.Save();
                g.TranslateTransform(47,87);g.RotateTransform(-5);
                int[] numbers={1,2,3,5};
                for(int i=0;i<numbers.Length;i++)
                    Digit(g, i*18+(i>=2?8:0), numbers[i]);
                using(var light=new SolidBrush(Color.FromArgb(241,238,250,255))) {
                    g.FillEllipse(light,37,8,2.8f,2.8f);g.FillEllipse(light,37,17,2.8f,2.8f);
                }
                g.Restore(face);
            }
            static void Digit(Graphics g,int x,int n)
            {
                int[] masks={0x3f,0x06,0x5b,0x4f,0x66,0x6d,0x7d,0x07,0x7f,0x6f};
                float[,] bars={{3,0,10,0},{12,2,12,10},{12,15,12,23},{3,25,10,25},{1,15,1,23},{1,2,1,10},{3,12.5f,10,12.5f}};
                using(var halo=new Pen(Color.FromArgb(62,226,219,255),5.7f))
                using(var led=new Pen(Color.FromArgb(247,244,247,255),2.3f)) {
                    led.StartCap=led.EndCap=LineCap.Round;halo.StartCap=halo.EndCap=LineCap.Round;
                    for(int i=0;i<7;i++)if((masks[n] & (1<<i))!=0) {
                        g.DrawLine(halo,x+bars[i,0],bars[i,1],x+bars[i,2],bars[i,3]);
                        g.DrawLine(led,x+bars[i,0],bars[i,1],x+bars[i,2],bars[i,3]);
                    }
                }
            }
        }

        // Using buttons keeps the illustrated controls accessible to keyboards and screen readers.
        abstract class SurfaceButton : Button
        {
            protected bool hover, pressed;
            protected SurfaceButton()
            {
                FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0;
                UseVisualStyleBackColor=false; Cursor=Cursors.Hand;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }
            protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
            protected override void OnMouseLeave(EventArgs e){hover=false;pressed=false;Invalidate();base.OnMouseLeave(e);}
            protected override void OnMouseDown(MouseEventArgs e){pressed=e.Button==MouseButtons.Left;Invalidate();base.OnMouseDown(e);}
            protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
            protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
            protected override void OnLostFocus(EventArgs e){Invalidate();base.OnLostFocus(e);}
        }
        sealed class HeroButton : SurfaceButton
        {
            public HeroButton()
            {
                Text="Подключить Станцию"; AccessibleName=Text;
                Font=new Font("Segoe UI",9.25f,FontStyle.Bold);
                SetStyle(ControlStyles.SupportsTransparentBackColor,true);BackColor=Color.Transparent;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g=e.Graphics;float d=g.DpiX/96f;
                g.SmoothingMode=SmoothingMode.AntiAlias;
                using(var p=Round(1,1,Width-2,Height-2,8*d))
                using(var fill=new SolidBrush(pressed?Color.FromArgb(224,219,243):hover?Color.FromArgb(247,244,255):Color.White)) {
                    g.FillPath(fill,p);
                    if(Focused&&ShowFocusCues)using(var focus=new Pen(Color.FromArgb(55,106,226),2*d))g.DrawPath(focus,p);
                }
                var text=new Rectangle((int)(12*d),0,Width-(int)(36*d),Height);
                TextRenderer.DrawText(g,Text,Font,text,Color.FromArgb(70,50,106),TextFormatFlags.NoPadding|TextFormatFlags.VerticalCenter|TextFormatFlags.HorizontalCenter|TextFormatFlags.SingleLine);
                float x=Width-18*d, y=Height/2f;
                using(var line=new Pen(Color.FromArgb(91,64,131),1.4f*d)) {
                    line.StartCap=line.EndCap=LineCap.Round;g.DrawLine(line,x-2*d,y-3*d,x+d,y);g.DrawLine(line,x+d,y,x-2*d,y+3*d);
                }
            }
        }
        sealed class Tile : SurfaceButton
        {
            readonly string id, detail;
            readonly Font caption = new Font("Segoe UI",11f,FontStyle.Bold);
            readonly Font description = new Font("Segoe UI",9.25f);
            public Tile(string id,string text,string detail)
            {
                this.id=id;this.detail=detail;Text=text;AccessibleName=text;AccessibleDescription=detail;BackColor=Canvas;
            }
            protected override void Dispose(bool disposing)
            {
                if(disposing){caption.Dispose();description.Dispose();}base.Dispose(disposing);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g=e.Graphics;float d=g.DpiX/96f;
                g.SmoothingMode=SmoothingMode.AntiAlias;
                using(var shade=Round(1,3*d,Width-2,Height-3*d,12*d))
                using(var brush=new SolidBrush(Color.FromArgb(18,49,47,76)))g.FillPath(brush,shade);
                using(var card=Round(1,1,Width-2,Height-4*d,12*d))
                using(var brush=new SolidBrush(pressed?Color.FromArgb(234,236,248):hover?Color.FromArgb(252,252,255):Color.White))
                using(var border=new Pen(Focused&&ShowFocusCues?Color.FromArgb(36,116,230):hover?Color.FromArgb(177,182,209):Color.FromArgb(225,225,233),Focused&&ShowFocusCues?2*d:1*d)) {
                    g.FillPath(brush,card);g.DrawPath(border,card);
                }
                bool compact=Height<88*d;
                float left=15*d;
                float iconY=compact?(Height-37*d)/2f:12*d;
                DrawIcon(g,id,left,iconY,35*d);
                int textX=compact?(int)(left+48*d):(int)left;
                int titleY=compact?(int)((Height-40*d)/2):(int)(51*d);
                int textW=Width-textX-(int)(12*d);
                TextRenderer.DrawText(g,Text,caption,new Rectangle(textX,titleY,textW,(int)(24*d)),Ink,
                    TextFormatFlags.NoPadding|TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g,detail,description,new Rectangle(textX,titleY+(int)(24*d),textW,(int)(26*d)),Secondary,
                    TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
                if(!compact) {
                    float x=Width-17*d,y=23*d;
                    using(var line=new Pen(Color.FromArgb(162,163,177),1.25f*d)) {
                        line.StartCap=line.EndCap=LineCap.Round;g.DrawLine(line,x-2*d,y-3*d,x+d,y);g.DrawLine(line,x+d,y,x-2*d,y+3*d);
                    }
                }
            }
            static void DrawIcon(Graphics g,string id,float x,float y,float side)
            {
                GraphicsState state=g.Save();
                g.TranslateTransform(x,y);g.ScaleTransform(side/40f,side/40f);
                Color a,b;
                switch(id) {
                    case "ai":a=Color.FromArgb(150,123,251);b=Color.FromArgb(105,67,225);break;
                    case "agent":a=Color.FromArgb(50,188,244);b=Color.FromArgb(21,107,231);break;
                    case "music":a=Color.FromArgb(255,124,150);b=Color.FromArgb(232,57,107);break;
                    case "extensions":a=Color.FromArgb(255,181,92);b=Color.FromArgb(237,126,31);break;
                    case "recovery":a=Color.FromArgb(98,209,194);b=Color.FromArgb(34,153,146);break;
                    default:a=Color.FromArgb(131,150,181);b=Color.FromArgb(88,107,141);break;
                }
                using(var tile=Round(1,1,38,38,9))
                using(var edge=new Pen(Color.FromArgb(44,38,36,72),.8f)) {
                    Shape(g,tile,new RectangleF(0,0,40,40),a,b,75);g.DrawPath(edge,tile);
                }
                using(var line=new Pen(Color.White,2f))
                using(var white=new SolidBrush(Color.White)) {
                    line.StartCap=line.EndCap=LineCap.Round;line.LineJoin=LineJoin.Round;
                    switch(id) {
                        case "ai":
                            Star(g,white,17,21,8);Star(g,white,26.5f,13,4);break;
                        case "agent":
                            using(var monitor=Round(8,10.5f,24,17,2.5f))g.DrawPath(line,monitor);
                            g.DrawLine(line,18,28,17,31);g.DrawLine(line,22,28,23,31);g.DrawLine(line,15,31,25,31);break;
                        case "music":
                            g.DrawLine(line,17,25,17,14);g.DrawLine(line,17,14,27.5f,11);g.DrawLine(line,27.5f,11,27.5f,23);
                            g.DrawLine(line,18,17,27,14.5f);g.FillEllipse(white,10.5f,23.4f,7,5.9f);g.FillEllipse(white,21,21,7,5.9f);break;
                        case "extensions":
                            g.DrawPolygon(line,new[]{new PointF(20,8.8f),new PointF(30,14),new PointF(20,19),new PointF(10,14)});
                            g.DrawLines(line,new[]{new PointF(10,19.6f),new PointF(20,25),new PointF(30,19.6f)});
                            g.DrawLines(line,new[]{new PointF(10,25.3f),new PointF(20,30.4f),new PointF(30,25.3f)});break;
                        case "recovery":
                            g.DrawArc(line,9,9,22,22,203,305);
                            g.DrawLines(line,new[]{new PointF(7.8f,10.8f),new PointF(9.5f,16.5f),new PointF(14.4f,13.8f)});
                            g.DrawLine(line,20,13.7f,20,20);g.DrawLine(line,20,20,24.4f,22.5f);break;
                        default:
                            g.DrawEllipse(line,17,9,8,8);g.DrawArc(line,11,20,19,14,185,172);
                            g.DrawArc(line,10,12,7,7,95,185);g.DrawArc(line,6.5f,21,10.7f,9,187,100);break;
                    }
                }
                g.Restore(state);
            }
            static void Star(Graphics g,Brush white,float x,float y,float r)
            {
                using(var p=new GraphicsPath()){
                    p.AddBezier(x,y-r,x+r*.18f,y-r*.24f,x+r*.21f,y-r*.18f,x+r,y);
                    p.AddBezier(x+r,y,x+r*.25f,y+r*.17f,x+r*.18f,y+r*.23f,x,y+r);
                    p.AddBezier(x,y+r,x-r*.19f,y+r*.22f,x-r*.22f,y+r*.17f,x-r,y);
                    p.AddBezier(x-r,y,x-r*.24f,y-r*.18f,x-r*.16f,y-r*.21f,x,y-r);
                    g.FillPath(white,p);
                }
            }
        }
    }
}
