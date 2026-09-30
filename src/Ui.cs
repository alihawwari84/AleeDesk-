using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteDesk
{
    // Colours, icon glyphs, the logo mark and small formatting helpers shared by every window.
    static class Theme
    {
        public static readonly Color Primary = Color.FromArgb(20, 98, 245);
        public static readonly Color PrimaryHover = Color.FromArgb(52, 120, 255);
        public static readonly Color PrimaryPressed = Color.FromArgb(13, 78, 208);
        public static readonly Color PrimarySoft = Color.FromArgb(234, 241, 255);
        public static readonly Color Ink = Color.FromArgb(11, 18, 32);
        public static readonly Color Text = Color.FromArgb(38, 48, 66);
        public static readonly Color Muted = Color.FromArgb(100, 112, 133);
        public static readonly Color Faint = Color.FromArgb(160, 170, 186);
        public static readonly Color Border = Color.FromArgb(228, 232, 240);
        public static readonly Color Surface = Color.FromArgb(245, 247, 251);
        public static readonly Color Card = Color.White;
        public static readonly Color Hover = Color.FromArgb(241, 244, 249);
        public static readonly Color Green = Color.FromArgb(22, 163, 74);
        public static readonly Color Amber = Color.FromArgb(217, 119, 6);
        public static readonly Color Red = Color.FromArgb(220, 38, 38);

        public const string IconFont = "Segoe MDL2 Assets";
        public const string GCopy = "", GRefresh = "", GSettings = "", GMonitor = "",
            GArrow = "", GBack = "", GLock = "", GGlobe = "", GCheck = "", GInfo = "",
            GWarn = "", GShield = "", GClock = "", GDevices = "", GContact = "",
            GBolt = "", GLaptop = "", GAdd = "", GDelete = "";

        public const TextFormatFlags Line = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        public const TextFormatFlags Centered = TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;

        // The AleeDesk "A": two overlapping blue strokes, the darker left one in front.
        public static void DrawMark(Graphics g, RectangleF r)
        {
            if (r.Width < 2 || r.Height < 2) return;
            SmoothingMode sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            PointF[] right = { P(r, 0.40f, 0.03f), P(r, 0.64f, 0.03f), P(r, 0.98f, 0.97f), P(r, 0.73f, 0.97f) };
            PointF[] left = { P(r, 0.36f, 0.03f), P(r, 0.60f, 0.03f), P(r, 0.27f, 0.97f), P(r, 0.02f, 0.97f) };
            using (LinearGradientBrush b = new LinearGradientBrush(r, Color.FromArgb(59, 130, 246), Color.FromArgb(56, 189, 248), 90f))
                g.FillPolygon(b, right);
            using (LinearGradientBrush b = new LinearGradientBrush(r, Color.FromArgb(30, 64, 175), Color.FromArgb(37, 99, 235), 90f))
                g.FillPolygon(b, left);
            g.SmoothingMode = sm;
        }

        static PointF P(RectangleF r, float x, float y) { return new PointF(r.X + x * r.Width, r.Y + y * r.Height); }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static string FormatId(string id)
        {
            return id.Length == 9 ? id.Substring(0, 3) + " " + id.Substring(3, 3) + " " + id.Substring(6) : id;
        }

        public static string Ago(DateTime utc)
        {
            TimeSpan d = DateTime.UtcNow - utc;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
            if (d.TotalHours < 24) return (int)d.TotalHours + " h ago";
            if (d.TotalDays < 30) return (int)d.TotalDays + ((int)d.TotalDays == 1 ? " day ago" : " days ago");
            return "on " + utc.ToLocalTime().ToString("d MMM yyyy");
        }

        public static string When(DateTime utc)
        {
            DateTime t = utc.ToLocalTime();
            if (t.Date == DateTime.Today) return "Today, " + t.ToString("HH:mm");
            if (t.Date == DateTime.Today.AddDays(-1)) return "Yesterday, " + t.ToString("HH:mm");
            return t.ToString("d MMM yyyy, HH:mm");
        }

        public static string Clock(TimeSpan t)
        {
            if (t.TotalHours >= 1) return string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
            return string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);
        }
    }

    // Base for owner-drawn controls: double-buffered, paints the parent colour behind rounded shapes.
    class Painted : Control
    {
        public Painted()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        protected Color Outside { get { return Parent != null ? Parent.BackColor : BackColor; } }
    }

    class RoundedPanel : Panel
    {
        public Color Stroke = Color.Empty;
        public int Radius = 12;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Radius))
            {
                using (SolidBrush b = new SolidBrush(BackColor)) g.FillPath(b, p);
                if (Stroke != Color.Empty) using (Pen pen = new Pen(Stroke)) g.DrawPath(pen, p);
            }
        }
    }

    // Rounded button with hover/pressed states and an optional icon glyph before the text.
    class FlatButton : Painted
    {
        public Color Back = Theme.Primary, Hover = Theme.PrimaryHover, Pressed = Theme.PrimaryPressed, Fore = Color.White;
        public Color Stroke = Color.Empty;
        public string Glyph;
        public Font GlyphFont = new Font(Theme.IconFont, 11f);
        public int Radius = 8;
        bool hover, down;

        public FlatButton()
        {
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI Semibold", 10f);
        }

        public static FlatButton Ghost(string text, string glyph)
        {
            return new FlatButton { Text = text, Glyph = glyph, Back = Theme.Card, Hover = Theme.Hover, Pressed = Theme.Border, Fore = Theme.Text, Stroke = Theme.Border };
        }

        public static FlatButton IconOnly(string glyph)
        {
            return new FlatButton { Glyph = glyph, Back = Theme.Surface, Hover = Theme.PrimarySoft, Pressed = Theme.Border, Fore = Theme.Text };
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Outside);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = !Enabled ? Color.FromArgb(110, Back) : down ? Pressed : hover ? Hover : Back;
            using (GraphicsPath p = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Radius))
            {
                if (fill.A > 0) using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                if (Stroke != Color.Empty) using (Pen pen = new Pen(Stroke)) g.DrawPath(pen, p);
            }
            TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            Size ts = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(g, Text, Font, Size.Empty, fl);
            Size gs = string.IsNullOrEmpty(Glyph) ? Size.Empty : TextRenderer.MeasureText(g, Glyph, GlyphFont, Size.Empty, fl);
            int gap = ts.Width > 0 && gs.Width > 0 ? Height / 4 : 0;
            int x = (Width - (gs.Width + gap + ts.Width)) / 2;
            Color fg = Enabled ? Fore : Color.FromArgb(150, Fore);
            if (gs.Width > 0) { TextRenderer.DrawText(g, Glyph, GlyphFont, new Rectangle(x, 0, gs.Width + 1, Height), fg, fl); x += gs.Width + gap; }
            if (ts.Width > 0) TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, ts.Width + 2, Height), fg, fl);
        }
    }

    // Rounded text field: a borderless TextBox inside a painted frame that turns blue when focused.
    class InputBox : Painted
    {
        public readonly TextBox Box = new TextBox();
        public Color Stroke = Theme.Border, FocusStroke = Theme.Primary;
        public int Radius = 8;
        readonly string cue;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        public InputBox(string placeholder, float size)
        {
            BackColor = Theme.Card;
            cue = placeholder;
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Card;
            Box.ForeColor = Theme.Ink;
            Box.Font = new Font("Segoe UI", size);
            Box.HandleCreated += delegate { SendMessage(Box.Handle, 0x1501, (IntPtr)1, cue); }; // EM_SETCUEBANNER
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Box.TextChanged += delegate { if (Stroke == Theme.Red) { Stroke = Theme.Border; Invalidate(); } };
            Controls.Add(Box);
            Cursor = Cursors.IBeam;
        }

        public string Value { get { return Box.Text; } set { Box.Text = value; } }

        public void Flag() { Stroke = Theme.Red; Invalidate(); Box.Focus(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = Height / 3;
            Box.SetBounds(pad, (Height - Box.PreferredHeight) / 2, Width - pad * 2, Box.PreferredHeight);
        }

        protected override void OnMouseDown(MouseEventArgs e) { Box.Focus(); base.OnMouseDown(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Outside);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool f = Box.Focused;
            using (GraphicsPath p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Radius))
            {
                using (SolidBrush b = new SolidBrush(BackColor)) g.FillPath(b, p);
                using (Pen pen = new Pen(f && Stroke != Theme.Red ? FocusStroke : Stroke, f ? 1.8f : 1f)) g.DrawPath(pen, p);
            }
        }
    }

    // On/off switch with a label to its right.
    class Toggle : Painted
    {
        bool on;
        public event EventHandler CheckedChanged;
        public Color OnColor = Theme.Primary, OffColor = Color.FromArgb(203, 211, 224);

        public Toggle()
        {
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 9.5f);
            ForeColor = Theme.Text;
        }

        public bool Checked
        {
            get { return on; }
            set
            {
                if (on == value) return;
                on = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Outside);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int th = (int)(Height * 0.8f), tw = (int)(th * 1.8f), ty = (Height - th) / 2;
            using (GraphicsPath p = Theme.Round(new RectangleF(0, ty, tw, th), th / 2f))
            using (SolidBrush b = new SolidBrush(on ? OnColor : OffColor))
                g.FillPath(b, p);
            float k = th - 6;
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, on ? tw - k - 3 : 3, ty + 3, k, k);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tw + th / 2, 0, Width - tw - th / 2, Height), ForeColor, Theme.Line);
        }
    }

    // Small coloured status badge: dot + short text.
    class StatusPill : Painted
    {
        public Color Dot = Theme.Amber;

        public StatusPill() { Font = new Font("Segoe UI Semibold", 9f); }

        public void FitWidth() { Width = TextRenderer.MeasureText(Text, Font).Width + Height + 6; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Outside);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), (Height - 1) / 2f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(34, Dot)))
                g.FillPath(b, p);
            float d = Height * 0.32f;
            using (SolidBrush b = new SolidBrush(Dot)) g.FillEllipse(b, Height * 0.45f - d / 2, (Height - d) / 2f, d, d);
            TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(Height * 0.8f), 0, Width, Height), Theme.Ink, Theme.Line);
        }
    }

    // The logo mark on its own.
    class MarkLogo : Painted
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Outside);
            int s = Math.Min(Width, Height);
            Theme.DrawMark(e.Graphics, new RectangleF((Width - s) / 2f, (Height - s) / 2f, s, s));
        }
    }

    // "Less waiting. More fixing." - two colours on one line.
    class TwoTone : Painted
    {
        public string A = "", B = "";

        public TwoTone() { Font = new Font("Segoe UI Semibold", 10f); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Outside);
            TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            int w = TextRenderer.MeasureText(g, A, Font, Size.Empty, f).Width + Font.Height / 4;
            TextRenderer.DrawText(g, A, Font, new Rectangle(0, 0, w + 2, Height), Theme.Ink, f);
            TextRenderer.DrawText(g, B, Font, new Rectangle(w, 0, Math.Max(1, Width - w), Height), Theme.Primary, f);
        }
    }

    // Round icon badge: soft tinted circle with a coloured glyph.
    class GlyphBadge : Painted
    {
        public string Glyph;
        public Color Accent = Theme.Primary;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Outside);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(30, Accent))) g.FillEllipse(b, 0, 0, Width - 1, Height - 1);
            using (Font f = new Font(Theme.IconFont, Height * 0.4f, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, Glyph, f, ClientRectangle, Accent, Theme.Centered);
        }
    }

    // Left-hand navigation entry.
    class NavItem : Painted
    {
        public string Glyph;
        bool selected, hover;
        static readonly Font GlyphFont = new Font(Theme.IconFont, 12f);
        static readonly Font Normal = new Font("Segoe UI", 10f), Bold = new Font("Segoe UI Semibold", 10f);

        public NavItem() { Cursor = Cursors.Hand; }

        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Outside);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (selected || hover)
                using (GraphicsPath p = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), 10))
                using (SolidBrush b = new SolidBrush(selected ? Theme.PrimarySoft : Theme.Hover))
                    g.FillPath(b, p);
            TextRenderer.DrawText(g, Glyph, GlyphFont, new Rectangle(Height / 3, 0, Height, Height), selected ? Theme.Primary : Theme.Muted, Theme.Line);
            TextRenderer.DrawText(g, Text, selected ? Bold : Normal, new Rectangle((int)(Height * 1.1f), 0, Width, Height), selected ? Theme.Primary : Theme.Text, Theme.Line);
        }
    }

    // Image drawn with high-quality scaling (the stock PictureBox looks jagged when shrinking).
    class SmoothPicture : Control
    {
        public Image Image;

        public SmoothPicture()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Image == null) return;
            double sc = Math.Min((double)Width / Image.Width, (double)Height / Image.Height);
            int w = (int)(Image.Width * sc), h = (int)(Image.Height * sc);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(Image, new Rectangle((Width - w) / 2, (Height - h) / 2, w, h));
        }
    }

    // Connection state card on the Remote Access page: big status circle, title, and while a
    // session runs, the device and a live connection timer.
    class HeroPanel : RoundedPanel
    {
        string glyph = Theme.GMonitor, title = "", subtitle = "", devTitle, devSub, time;
        Color accent = Theme.Primary;
        readonly FlatButton action = new FlatButton();
        Action onAction;
        static readonly Font TitleFont = new Font("Segoe UI Semibold", 20f), SubFont = new Font("Segoe UI", 10.5f),
            StatFont = new Font("Segoe UI Semibold", 9.5f), StatSub = new Font("Segoe UI", 8.5f), StatGlyph = new Font(Theme.IconFont, 16f);

        public HeroPanel()
        {
            BackColor = Theme.Card;
            Stroke = Theme.Border;
            Radius = 14;
            action.Click += delegate { if (onAction != null) onAction(); };
            Controls.Add(action);
        }

        public void Set(string glyph, Color accent, string title, string subtitle, string devTitle, string devSub, string time, string actionText, Action act)
        {
            this.glyph = glyph; this.accent = accent; this.title = title; this.subtitle = subtitle;
            this.devTitle = devTitle; this.devSub = devSub; this.time = time;
            onAction = act;
            action.Visible = actionText != null;
            if (actionText != null)
            {
                action.Text = actionText;
                if (actionText == "Disconnect")
                {
                    action.Back = Theme.Red; action.Hover = Color.FromArgb(239, 68, 68); action.Pressed = Color.FromArgb(185, 28, 28);
                    action.Fore = Color.White; action.Stroke = Color.Empty;
                }
                else
                {
                    action.Back = Theme.Card; action.Hover = Theme.Hover; action.Pressed = Theme.Border;
                    action.Fore = Theme.Text; action.Stroke = Theme.Border;
                }
                action.Invalidate();
            }
            PerformLayout();
            Invalidate();
        }

        public void SetTime(string t) { time = t; Invalidate(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            float k = Width / 738f;
            action.SetBounds((int)(Width - 170 * k), (int)((Height - 42 * k) / 2), (int)(146 * k), (int)(42 * k));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = Width / 738f;
            bool stats = devTitle != null;
            float top = stats ? 22 * k : (Height - 66 * k) / 2;
            RectangleF c = new RectangleF(30 * k, top, 66 * k, 66 * k);
            using (SolidBrush b = new SolidBrush(accent)) g.FillEllipse(b, c);
            using (Font gf = new Font(Theme.IconFont, c.Height * 0.4f, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, glyph, gf, Rectangle.Round(c), Color.White, Theme.Centered);
            int tx = (int)(118 * k), tw = (int)(Width - tx - (action.Visible ? 190 : 30) * k);
            TextRenderer.DrawText(g, title, TitleFont, new Rectangle(tx, (int)(top - 4 * k), tw, (int)(40 * k)), Theme.Ink, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            int sh0 = stats ? (int)(40 * k) : Height - (int)(top + 42 * k) - (int)(8 * k);
            TextRenderer.DrawText(g, subtitle, SubFont, new Rectangle(tx + (int)(2 * k), (int)(top + 42 * k), tw, sh0), Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            if (!stats) return;
            int sy = (int)(104 * k), sh = (int)(18 * k);
            TextRenderer.DrawText(g, Theme.GMonitor, StatGlyph, new Rectangle(tx, sy, (int)(30 * k), 2 * sh), Theme.Text, Theme.Line);
            TextRenderer.DrawText(g, devTitle, StatFont, new Rectangle(tx + (int)(36 * k), sy, (int)(170 * k), sh), Theme.Ink, Theme.Line);
            TextRenderer.DrawText(g, devSub, StatSub, new Rectangle(tx + (int)(36 * k), sy + sh, (int)(170 * k), sh), devSub.StartsWith("Online") ? Theme.Green : Theme.Muted, Theme.Line);
            int dx = tx + (int)(222 * k);
            using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, dx, sy + 2, dx, sy + 2 * sh - 2);
            TextRenderer.DrawText(g, Theme.GClock, StatGlyph, new Rectangle(dx + (int)(22 * k), sy, (int)(30 * k), 2 * sh), Theme.Text, Theme.Line);
            TextRenderer.DrawText(g, time, StatFont, new Rectangle(dx + (int)(56 * k), sy, (int)(150 * k), sh), Theme.Ink, Theme.Line);
            TextRenderer.DrawText(g, "Connection time", StatSub, new Rectangle(dx + (int)(56 * k), sy + sh, (int)(150 * k), sh), Theme.Muted, Theme.Line);
        }
    }

    // "Instant connections - Secure and reliable - Support from anywhere".
    class FeatureStrip : Painted
    {
        static readonly string[] Glyphs = { Theme.GBolt, Theme.GShield, Theme.GLaptop };
        static readonly string[] Lines = { "Instant\nconnections", "Secure\nand reliable", "Support\nfrom anywhere" };
        static readonly Font GlyphFont = new Font(Theme.IconFont, 20f), TextFont = new Font("Segoe UI", 9.5f);

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Outside);
            int cw = Width / 3;
            for (int i = 0; i < 3; i++)
            {
                TextRenderer.DrawText(g, Glyphs[i], GlyphFont, new Rectangle(i * cw, 0, cw, Height * 44 / 100), Theme.Ink,
                    TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom);
                TextRenderer.DrawText(g, Lines[i], TextFont, new Rectangle(i * cw, Height * 52 / 100, cw, Height * 48 / 100), Theme.Text,
                    TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter);
            }
        }
    }

    // A device card: blue band with a monitor icon (plus a green dot when it's on this
    // network), then a title and a small line under it.
    class DeviceTile : RoundedPanel
    {
        readonly string title, sub;
        readonly bool online;
        bool hover;
        static readonly Font GlyphFont = new Font(Theme.IconFont, 22f);
        static readonly Font IdFont = new Font("Segoe UI Semibold", 12f);
        static readonly Font SmallFont = new Font("Segoe UI", 8.5f);

        public DeviceTile(string title, string sub, bool online)
        {
            this.title = title;
            this.sub = sub;
            this.online = online;
            BackColor = Theme.Card;
            Radius = 14;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int band = (int)(Height * 0.52f);
            using (GraphicsPath p = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Radius))
            {
                g.SetClip(p);
                using (SolidBrush b = new SolidBrush(Theme.PrimarySoft)) g.FillRectangle(b, 0, 0, Width, band);
                g.ResetClip();
                using (Pen pen = new Pen(hover ? Theme.Primary : Theme.Border, hover ? 2f : 1f)) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, Theme.GMonitor, GlyphFont, new Rectangle(0, 0, Width, band), Theme.Primary, Theme.Centered);
            if (online)
            {
                float d = band * 0.18f;
                RectangleF dot = new RectangleF(Width / 2f + band * 0.2f, band * 0.26f, d, d);
                using (SolidBrush b = new SolidBrush(Theme.PrimarySoft)) g.FillEllipse(b, RectangleF.Inflate(dot, 2, 2));
                using (SolidBrush b = new SolidBrush(Theme.Green)) g.FillEllipse(b, dot);
            }
            int rest = Height - band, pad = (int)(Width * 0.07f);
            TextRenderer.DrawText(g, title, IdFont, new Rectangle(pad, band + (int)(rest * 0.12f), Width - 2 * pad, (int)(rest * 0.44f)), Theme.Ink, Theme.Line);
            TextRenderer.DrawText(g, sub, SmallFont, new Rectangle(pad, band + (int)(rest * 0.56f), Width - 2 * pad, (int)(rest * 0.32f)), online ? Theme.Green : Theme.Muted, Theme.Line);
        }
    }

    // Address-book entry: initials, name, ID, Connect and delete buttons.
    class ContactRow : RoundedPanel
    {
        readonly string name, id;
        readonly FlatButton connect = new FlatButton(), remove = FlatButton.IconOnly(Theme.GDelete);
        static readonly Font NameFont = new Font("Segoe UI Semibold", 10.5f), IdFont = new Font("Segoe UI", 9f);

        public ContactRow(string name, string id, Action onConnect, Action onRemove)
        {
            this.name = name;
            this.id = id;
            BackColor = Theme.Card;
            Stroke = Theme.Border;
            Radius = 12;
            connect.Text = "Connect";
            connect.Glyph = Theme.GArrow;
            connect.Font = new Font("Segoe UI Semibold", 9.5f);
            connect.Click += delegate { onConnect(); };
            remove.Click += delegate { onRemove(); };
            Controls.Add(connect);
            Controls.Add(remove);
        }

        public Control RemoveButton { get { return remove; } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int h = Height, bh = h * 60 / 100, by = (h - bh) / 2;
            remove.SetBounds(Width - bh - h / 4, by, bh, bh);
            connect.SetBounds(remove.Left - h * 2 - h / 6, by, h * 2, bh);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = Height;
            float d = h * 0.62f;
            RectangleF c = new RectangleF(h * 0.26f, (h - d) / 2, d, d);
            using (SolidBrush b = new SolidBrush(Theme.PrimarySoft)) g.FillEllipse(b, c);
            using (Font f = new Font("Segoe UI Semibold", d * 0.36f, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, Initials(name), f, Rectangle.Round(c), Theme.Primary, Theme.Centered);
            int tx = (int)(h * 1.12f), tw = connect.Left - tx - 10;
            TextRenderer.DrawText(g, name, NameFont, new Rectangle(tx, h * 14 / 100, tw, h * 38 / 100), Theme.Ink, Theme.Line);
            TextRenderer.DrawText(g, Theme.FormatId(id), IdFont, new Rectangle(tx, h * 52 / 100, tw, h * 32 / 100), Theme.Muted, Theme.Line);
        }

        static string Initials(string n)
        {
            string[] parts = n.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            string s = parts[0].Substring(0, 1);
            if (parts.Length > 1) s += parts[parts.Length - 1].Substring(0, 1);
            return s.ToUpperInvariant();
        }
    }

    // One line of session history.
    class SessionRow : RoundedPanel
    {
        readonly bool incoming;
        readonly string who;
        readonly DateTime start;
        readonly int seconds;
        static readonly Font TitleFont = new Font("Segoe UI Semibold", 10f), SubFont = new Font("Segoe UI", 8.75f), TimeFont = new Font("Segoe UI Semibold", 10.5f);

        public SessionRow(bool incoming, string who, DateTime start, int seconds)
        {
            this.incoming = incoming; this.who = who; this.start = start; this.seconds = seconds;
            BackColor = Theme.Card;
            Stroke = Theme.Border;
            Radius = 12;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = Height;
            float d = h * 0.6f;
            RectangleF c = new RectangleF(h * 0.26f, (h - d) / 2, d, d);
            Color accent = incoming ? Theme.Primary : Theme.Green;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(30, accent))) g.FillEllipse(b, c);
            using (Font f = new Font(Theme.IconFont, d * 0.42f, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, incoming ? Theme.GBack : Theme.GArrow, f, Rectangle.Round(c), accent, Theme.Centered);
            int tx = (int)(h * 1.12f), tw = Width - tx - h * 2;
            TextRenderer.DrawText(g, (incoming ? "Incoming from " : "Outgoing to ") + who, TitleFont, new Rectangle(tx, h * 14 / 100, tw, h * 38 / 100), Theme.Ink, Theme.Line);
            TextRenderer.DrawText(g, Theme.When(start), SubFont, new Rectangle(tx, h * 52 / 100, tw, h * 32 / 100), Theme.Muted, Theme.Line);
            TextRenderer.DrawText(g, Theme.Clock(TimeSpan.FromSeconds(seconds)), TimeFont, new Rectangle(Width - h * 2, 0, h * 2 - h / 3, h), Theme.Ink,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }
    }

    // Borderless dialog with a white header (icon badge + title); drag it by the header.
    class BrandDialog : Form
    {
        protected readonly Panel Body = new Panel();

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public BrandDialog(string title, string glyph, int bodyHeight)
        {
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            BackColor = Theme.Border; // shows as a 1px frame around the docked content
            Padding = new Padding(1);
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(440, 66 + bodyHeight);
            Icon = Brand.Icon;
            Text = Brand.Name;

            Body.Dock = DockStyle.Fill;
            Body.BackColor = Theme.Card;
            Controls.Add(Body);
            Panel head = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = Theme.Card };
            head.Paint += (s, e) => { using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, head.Height - 1, head.Width, head.Height - 1); };
            head.Controls.Add(new GlyphBadge { Glyph = glyph, Location = new Point(22, 15), Size = new Size(36, 36) });
            head.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI Semibold", 13f), ForeColor = Theme.Ink, AutoSize = true, Location = new Point(68, 18) });
            Controls.Add(head);
            head.MouseDown += DragMove;
            foreach (Control c in head.Controls) c.MouseDown += DragMove;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
        }

        void DragMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN on the caption
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // CS_DROPSHADOW
        }

        protected FlatButton Primary(string text, string glyph, int x, int y)
        {
            FlatButton b = new FlatButton { Text = text, Glyph = glyph, Location = new Point(x, y), Size = new Size(110, 38) };
            Body.Controls.Add(b);
            return b;
        }

        protected FlatButton Secondary(string text, int x, int y)
        {
            FlatButton b = FlatButton.Ghost(text, null);
            b.Location = new Point(x, y);
            b.Size = new Size(100, 38);
            Body.Controls.Add(b);
            return b;
        }
    }

    // Asks for the remote computer's password after the ID was entered (AnyDesk-style).
    class PasswordDialog : BrandDialog
    {
        readonly InputBox pwd;

        readonly Toggle remember;

        public PasswordDialog(string formattedId) : base("Enter password", Theme.GLock, 226)
        {
            Body.Controls.Add(new Label { Text = "Connecting to remote desk", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(24, 16) });
            Body.Controls.Add(new Label { Text = formattedId, Font = new Font("Segoe UI Semibold", 14f), ForeColor = Theme.Ink, AutoSize = true, Location = new Point(22, 36) });
            pwd = new InputBox("Password shown on the other computer", 12f) { Location = new Point(24, 78), Size = new Size(390, 42) };
            pwd.Box.UseSystemPasswordChar = true;
            Body.Controls.Add(pwd);
            remember = new Toggle { Text = "Remember password for this desk", Location = new Point(24, 132), Size = new Size(390, 22) };
            Body.Controls.Add(remember);
            Secondary("Cancel", 194, 170).Click += delegate { DialogResult = DialogResult.Cancel; };
            Primary("Connect", Theme.GArrow, 304, 170).Click += delegate { Submit(); };
            pwd.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Submit(); } };
            Shown += delegate { pwd.Box.Focus(); };
        }

        // Passwords are not case-sensitive (session passwords are shown in capitals and the
        // unattended password is stored the same way).
        public string Password { get { return pwd.Value.Trim().ToUpperInvariant(); } }

        public bool Remember { get { return remember.Checked; } }

        void Submit()
        {
            if (Password.Length == 0) { pwd.Flag(); return; }
            DialogResult = DialogResult.OK;
        }
    }

    // Sets the permanent password used for unattended access.
    class SetPasswordDialog : BrandDialog
    {
        readonly InputBox first, second;
        readonly Label error;

        public SetPasswordDialog() : base("Unattended access password", Theme.GLock, 270)
        {
            Body.Controls.Add(new Label { Text = "Anyone with this password can connect without anyone accepting. Use at least 8 characters.", ForeColor = Theme.Muted, Location = new Point(24, 12), Size = new Size(392, 52) });
            first = new InputBox("New password", 11.5f) { Location = new Point(24, 68), Size = new Size(390, 40) };
            second = new InputBox("Type it again", 11.5f) { Location = new Point(24, 116), Size = new Size(390, 40) };
            first.Box.UseSystemPasswordChar = true;
            second.Box.UseSystemPasswordChar = true;
            Body.Controls.Add(first);
            Body.Controls.Add(second);
            error = new Label { ForeColor = Theme.Red, Location = new Point(24, 164), Size = new Size(392, 36) };
            Body.Controls.Add(error);
            Secondary("Cancel", 194, 210).Click += delegate { DialogResult = DialogResult.Cancel; };
            Primary("Save", Theme.GCheck, 304, 210).Click += delegate { Submit(); };
            second.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Submit(); } };
            Shown += delegate { first.Box.Focus(); };
        }

        public string Password { get { return first.Value.Trim().ToUpperInvariant(); } }

        void Submit()
        {
            if (first.Value.Trim().Length < 8) { error.Text = "Use at least 8 characters."; first.Flag(); return; }
            if (first.Value != second.Value) { error.Text = "The two passwords don't match."; second.Flag(); return; }
            DialogResult = DialogResult.OK;
        }
    }

    // Shown on the shared computer when someone with the right password wants in.
    class ConsentDialog : BrandDialog
    {
        public ConsentDialog(string ip) : base("Incoming connection", Theme.GShield, 224)
        {
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            Body.Controls.Add(new Label { Text = "Someone wants to control this computer", Font = new Font("Segoe UI Semibold", 11f), ForeColor = Theme.Ink, AutoSize = true, Location = new Point(22, 18) });
            Body.Controls.Add(new Label { Text = "From  " + ip, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(24, 48) });
            Body.Controls.Add(new Label { Text = "They entered your password. Only accept if you know who this is — they will be able to use your mouse and keyboard.", ForeColor = Theme.Muted, Location = new Point(24, 74), Size = new Size(392, 64) });
            Secondary("Dismiss", 194, 164).Click += delegate { DialogResult = DialogResult.Cancel; };
            Primary("Accept", Theme.GCheck, 304, 164).Click += delegate { DialogResult = DialogResult.OK; };
            Shown += delegate { Activate(); };
        }
    }

    // Always-on-top strip on the shared computer while someone is connected.
    class HostBanner : Form
    {
        public HostBanner(string who, Action disconnect)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Card;
            float k;
            using (Graphics g = CreateGraphics()) k = g.DpiX / 96f;
            Size = new Size((int)(500 * k), (int)(46 * k));
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y);

            Panel accent = new Panel { Dock = DockStyle.Bottom, Height = Math.Max(3, (int)(3 * k)), BackColor = Theme.Primary };
            Panel right = new Panel { Dock = DockStyle.Right, Width = (int)(126 * k), Padding = new Padding((int)(7 * k)), BackColor = Theme.Card };
            FlatButton b = new FlatButton { Text = "Disconnect", Dock = DockStyle.Fill, Back = Theme.Red, Hover = Color.FromArgb(239, 68, 68), Pressed = Color.FromArgb(185, 28, 28), Fore = Color.White, Radius = 6 };
            b.Click += delegate { disconnect(); };
            right.Controls.Add(b);
            Controls.Add(new MarkLogo { Location = new Point((int)(12 * k), (int)(10 * k)), Size = new Size((int)(24 * k), (int)(24 * k)) });
            Controls.Add(new Label { Text = "●", ForeColor = Theme.Red, Font = new Font("Segoe UI", 9f), AutoSize = true, Location = new Point((int)(44 * k), (int)(13 * k)) });
            Controls.Add(new Label { Text = "Remote session active  ·  " + who, ForeColor = Theme.Ink, Font = new Font("Segoe UI Semibold", 9.5f), AutoSize = true, Location = new Point((int)(60 * k), (int)(12 * k)) });
            Controls.Add(right);
            Controls.Add(accent);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // CS_DROPSHADOW
        }
    }

    // White toolbar for the viewer window.
    class LightToolRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !e.Item.Pressed) return;
            using (SolidBrush b = new SolidBrush(e.Item.Pressed ? Theme.Border : Theme.PrimarySoft))
                e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size));
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int x = e.Item.Width / 2;
            using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, x, 4, x, e.Item.Height - 4);
        }
    }
}
