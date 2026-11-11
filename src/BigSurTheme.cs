using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace AliceDesk
{
    internal static class BigSurTheme
    {
        internal static readonly Color Canvas = Color.FromArgb(246,246,249);
        internal static readonly Color Surface = Color.White;
        internal static readonly Color Ink = Color.FromArgb(36,36,43);
        internal static readonly Color Muted = Color.FromArgb(85,86,99);
        internal static readonly Color Line = Color.FromArgb(209,210,218);
        internal static readonly Color Blue = Color.FromArgb(0,103,213);

        internal static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width,r.Height));
            if(d < 2) { p.AddRectangle(r); return p; }
            p.AddArc(r.Left,r.Top,d,d,180,90);
            p.AddArc(r.Right-d,r.Top,d,d,270,90);
            p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);
            p.AddArc(r.Left,r.Bottom-d,d,d,90,90);
            p.CloseFigure();
            return p;
        }
        internal static void Box(Graphics g, RectangleF rect, float radius, Color fill, Color border)
        {
            if(rect.Width < 2 || rect.Height < 2) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using(var shape=Rounded(rect,radius))
            using(var brush=new SolidBrush(fill)) {
                g.FillPath(brush,shape);
                if(border.A > 0) using(var stroke=new Pen(border)) g.DrawPath(stroke,shape);
            }
        }

        internal static void Apply(Control root)
        {
            var box=root as TextBoxBase;
            if(box!=null) {
                box.ForeColor=Ink;
                box.BackColor=box.ReadOnly ? Color.FromArgb(248,249,251) : Surface;
                if(!(box.Parent is FieldFrame) && (box.Parent is FlowLayoutPanel || box.Parent is TableLayoutPanel)) {
                    FieldFrame.Wrap(box);
                }
            } else if(root is ComboBox || root is ListBox || root is NumericUpDown) {
                root.ForeColor=Ink; root.BackColor=Surface;
            } else if(root is Button && !(root is SettingsButton)) {
                var ordinary=(Button)root;
                ordinary.UseVisualStyleBackColor=false; ordinary.FlatStyle=FlatStyle.Flat;
                ordinary.BackColor=Surface; ordinary.ForeColor=Ink; ordinary.FlatAppearance.BorderColor=Line;
            } else if(root is CheckBox) {
                root.ForeColor=Ink; ((CheckBox)root).UseVisualStyleBackColor=false;
            } else if(root is TabPage) {
                ((TabPage)root).UseVisualStyleBackColor=false; root.BackColor=Canvas; root.ForeColor=Ink;
            } else if(root is UserControl || root is Form) {
                root.BackColor=Canvas; root.ForeColor=Ink;
            } else if(root is Label) {
                if(root.BackColor.GetBrightness()>.5f || root.BackColor==Color.Transparent)
                    root.ForeColor=root.Font.Bold ? Ink : Muted;
            }
            var list=root as ListBox;
            if(list!=null && list.DrawMode==DrawMode.Normal) {
                list.BorderStyle=BorderStyle.None;
                list.DrawMode=DrawMode.OwnerDrawFixed; list.ItemHeight=Math.Max(list.Font.Height+8,25);
                list.DrawItem+=DrawListRow;
            }
            foreach(Control child in root.Controls.Cast<Control>().ToArray()) Apply(child);
            var body=root as SettingsBody;
            if(body!=null) body.GroupSections();
        }
        static void DrawListRow(object sender,DrawItemEventArgs e)
        {
            var list=(ListBox)sender;
            if(e.Index<0 || e.Index>=list.Items.Count)return;
            bool selected=(e.State & DrawItemState.Selected)!=0;
            Color bg=selected ? Color.FromArgb(225,237,255) : Surface;
            using(var b=new SolidBrush(Surface)) e.Graphics.FillRectangle(b,e.Bounds);
            var r=Rectangle.Inflate(e.Bounds,-2,-1);
            Box(e.Graphics,r,5,bg,Color.Transparent);
            var text=new Rectangle(r.X+7,r.Y,Math.Max(0,r.Width-13),r.Height);
            TextRenderer.DrawText(e.Graphics,list.GetItemText(list.Items[e.Index]),e.Font,text,selected?Color.FromArgb(12,69,132):Ink,
                TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine);
            if((e.State & DrawItemState.Focus)!=0)ControlPaint.DrawFocusRectangle(e.Graphics,r);
        }
    }

    // All behavior (mnemonics, AcceptButton, UI Automation and PerformClick) stays with Button.
    internal sealed class SettingsButton : PaintedButton
    {
        internal bool Primary {get;set;}
        bool hover,down;
        internal SettingsButton()
        {
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
            AutoSize=true; FlatStyle=FlatStyle.Flat;
            FlatAppearance.BorderSize=0;
            Padding=new Padding(10,3,10,3); Margin=new Padding(3,3,4,3); Cursor=Cursors.Hand;
        }
        public override Size GetPreferredSize(Size proposal)
        {
            var measure=TextRenderer.MeasureText(Text??"",Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine);
            return new Size(measure.Width+Padding.Horizontal+10,Math.Max(Font.Height+Padding.Vertical+7,28));
        }
        protected override void OnMouseEnter(EventArgs e) {base.OnMouseEnter(e);hover=true;Invalidate();}
        protected override void OnMouseLeave(EventArgs e) {base.OnMouseLeave(e);hover=false;down=false;Invalidate();}
        protected override void OnMouseDown(MouseEventArgs e) {base.OnMouseDown(e);if(e.Button==MouseButtons.Left)down=true;Invalidate();}
        protected override void OnMouseUp(MouseEventArgs e) {base.OnMouseUp(e);down=false;Invalidate();}
        protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(e.KeyCode==Keys.Space){down=true;Invalidate();}}
        protected override void OnKeyUp(KeyEventArgs e){base.OnKeyUp(e);down=false;Invalidate();}
        protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);down=false;Invalidate();}
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;
            g.Clear(Parent==null?BigSurTheme.Canvas:Parent.BackColor);
            var r=new RectangleF(1.5f,1.5f,Width-3,Height-4);
            Color fill=!Enabled?Color.FromArgb(240,240,244):Primary?(down?Color.FromArgb(0,83,180):hover?Color.FromArgb(5,120,234):BigSurTheme.Blue)
                :(down?Color.FromArgb(228,229,235):hover?Color.FromArgb(246,248,252):Color.White);
            BigSurTheme.Box(g,new RectangleF(r.X,r.Y+1,r.Width,r.Height),7,Color.FromArgb(16,30,35,56),Color.Transparent);
            BigSurTheme.Box(g,r,7,fill,Primary&&Enabled?Color.FromArgb(0,89,196):BigSurTheme.Line);
            if(Focused && ShowFocusCues) {
                using(var path=BigSurTheme.Rounded(new RectangleF(1,1,Width-3,Height-3),8))
                using(var pen=new Pen(Color.FromArgb(70,149,255),2))g.DrawPath(pen,path);
            }
            var flags=TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis;
            if(!ShowKeyboardCues)flags|=TextFormatFlags.HidePrefix;
            var text=Rectangle.Inflate(ClientRectangle,-3,-1);
            TextRenderer.DrawText(g,Text,Font,text,!Enabled?Color.FromArgb(124,125,135):Primary?Color.White:BigSurTheme.Ink,flags);
        }
    }

    internal sealed class SettingsCombo : ComboBox
    {
        internal SettingsCombo()
        {
            FlatStyle=FlatStyle.Flat;DrawMode=DrawMode.OwnerDrawFixed;
            ForeColor=BigSurTheme.Ink;BackColor=Color.White;ItemHeight=Math.Max(22,Font.Height+5);
        }
        protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);ItemHeight=Math.Max(22,Font.Height+5);}
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool selected=(e.State&DrawItemState.Selected)!=0;
            string value=e.Index>=0&&e.Index<Items.Count?GetItemText(Items[e.Index]):Text;
            Color bg=selected?BigSurTheme.Blue:Color.White;
            using(var b=new SolidBrush(bg))e.Graphics.FillRectangle(b,e.Bounds);
            var r=e.Bounds;r.X+=5;r.Width=Math.Max(r.Width-7,0);
            TextRenderer.DrawText(e.Graphics,value,Font,r,selected?Color.White:BigSurTheme.Ink,
                TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.EndEllipsis);
        }
        protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
        protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
    }

    internal sealed class SettingsTabs : TabControl
    {
        internal SettingsTabs()
        {
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
            Dock=DockStyle.Fill; Padding=new Point(15,7);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;g.Clear(BigSurTheme.Canvas);
            if(TabCount==0)return;
            var baseRect=Rectangle.Union(GetTabRect(0),GetTabRect(TabCount-1));
            BigSurTheme.Box(g,baseRect,8,Color.FromArgb(228,228,234),Color.Transparent);
            for(int i=0;i<TabCount;i++) {
                var r=GetTabRect(i);bool active=i==SelectedIndex;
                if(active)BigSurTheme.Box(g,new RectangleF(r.X+1.5f,r.Y+1.5f,r.Width-3,r.Height-3),6,Color.White,Focused?Color.FromArgb(80,154,247):BigSurTheme.Line);
                TextRenderer.DrawText(g,TabPages[i].Text,Font,r,active?BigSurTheme.Ink:BigSurTheme.Muted,
                    TextFormatFlags.VerticalCenter|TextFormatFlags.HorizontalCenter|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine);
            }
        }
        protected override void OnSelectedIndexChanged(EventArgs e){base.OnSelectedIndexChanged(e);Invalidate();}
        protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
        protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
    }

    // Native text editing, password behavior, selection and screen-reader state remain on the edit child.
    internal sealed class FieldFrame : Panel
    {
        readonly TextBoxBase edit;
        FieldFrame(TextBoxBase edit)
        {
            this.edit=edit; SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint,true);
            Padding=edit.Multiline ? new Padding(8,6,8,6) : new Padding(8,4,8,4);
            TabStop=false;
            edit.Enter+=(s,e)=>Invalidate();edit.Leave+=(s,e)=>Invalidate();
        }
        internal static void Wrap(TextBoxBase edit)
        {
            var parent=edit.Parent;var grid=parent as TableLayoutPanel;
            var oldDock=edit.Dock;var size=edit.Size;
            var margin=edit.Margin;int order=parent.Controls.GetChildIndex(edit);
            int tabIndex=edit.TabIndex;
            var frame=new FieldFrame(edit) {Dock=oldDock,Anchor=edit.Anchor,Margin=margin,TabIndex=tabIndex,BackColor=parent.BackColor};
            var cell=grid==null?new TableLayoutPanelCellPosition():grid.GetCellPosition(edit);
            int rowSpan=grid==null?1:grid.GetRowSpan(edit),colSpan=grid==null?1:grid.GetColumnSpan(edit);
            parent.SuspendLayout();
            try {
                parent.Controls.Remove(edit);
                frame.Size=new Size(size.Width,edit.Multiline?Math.Max(size.Height,60):edit.Font.Height+11);
                edit.BorderStyle=BorderStyle.None;edit.Margin=Padding.Empty;
                if(edit.Multiline)edit.Dock=DockStyle.Fill;
                else {edit.Dock=DockStyle.Top;edit.AutoSize=true;}
                frame.Controls.Add(edit);
                if(grid!=null) {
                    grid.Controls.Add(frame,cell.Column,cell.Row);grid.SetColumnSpan(frame,colSpan);grid.SetRowSpan(frame,rowSpan);
                } else parent.Controls.Add(frame);
                parent.Controls.SetChildIndex(frame,Math.Min(order,parent.Controls.Count-1));
            } finally {parent.ResumeLayout(true);}
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            BigSurTheme.Box(e.Graphics,new RectangleF(.5f,.5f,Width-1,Height-1),6,edit.BackColor,edit.Focused?BigSurTheme.Blue:BigSurTheme.Line);
        }
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(edit.CanFocus)edit.Focus();}
    }

    // Group related actions on a shared surface. Field and row widths track the viewport in both DPI modes.
    internal sealed class SettingsBody : FlowLayoutPanel
    {
        readonly List<SettingsCard> cards=new List<SettingsCard>();
        readonly Dictionary<Control,int> fieldWidths=new Dictionary<Control,int>();
        bool grouped,arranging;
        internal SettingsBody()
        {
            AutoScroll=true; Dock=DockStyle.Fill; FlowDirection=FlowDirection.TopDown;WrapContents=false;
            Padding=new Padding(12,10,12,12);BackColor=BigSurTheme.Canvas;
        }
        internal void GroupSections()
        {
            if(grouped)return;
            grouped=true;
            var items=Controls.Cast<Control>().ToArray();
            SettingsCard card=null;
            SuspendLayout();
            foreach(var item in items) {
                if(card==null || (item is Label && item.Font.Bold)) {
                    card=new SettingsCard();cards.Add(card);Controls.Add(card);
                }
                card.Controls.Add(item);
                if(!item.AutoSize && !(item is Label))fieldWidths[item]=item.Width;
            }
            ResumeLayout(true);Fit();
            ClientSizeChanged+=(s,e)=>Fit();
        }
        void Fit()
        {
            if(arranging||ClientSize.Width<310)return;arranging=true;
            try {
                // Reserve the same gutter when a vertical scrollbar appears to prevent resize oscillations.
                int width=Math.Max(175,ClientSize.Width-Padding.Horizontal-SystemInformation.VerticalScrollBarWidth-3);
                foreach(var card in cards) {
                    card.MaximumSize=new Size(width,0);card.MinimumSize=new Size(width,0);
                    var inner=Math.Max(125,width-card.Padding.Horizontal-10);
                    foreach(Control c in card.Controls) {
                        var row=c as FlowLayoutPanel;
                        if(c is Label || row!=null)c.MaximumSize=new Size(inner,0);
                        else {
                            int desiredWidth;
                            if(fieldWidths.TryGetValue(c,out desiredWidth))c.Width=Math.Min(inner,desiredWidth);
                        }
                    }
                }
            } finally {arranging=false;}
        }
    }

    internal sealed class SettingsCard : FlowLayoutPanel
    {
        internal SettingsCard()
        {
            AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;FlowDirection=FlowDirection.TopDown;WrapContents=false;
            Margin=new Padding(0,0,0,12);Padding=new Padding(17,10,14,12);
            BackColor=Color.White;SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint,true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BigSurTheme.Canvas);
            BigSurTheme.Box(e.Graphics,new RectangleF(.5f,.5f,Width-1,Height-1),12,Color.White,Color.FromArgb(221,221,229));
        }
    }
    internal sealed class SettingsGrid : TableLayoutPanel
    {
        internal SettingsGrid() {BackColor=Color.White;SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint,true);}
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BigSurTheme.Canvas);
            BigSurTheme.Box(e.Graphics,new RectangleF(.5f,.5f,Width-1,Height-1),12,Color.White,Color.FromArgb(221,221,229));
        }
    }
}
