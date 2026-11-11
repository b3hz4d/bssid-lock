// Light / dark theme for WinForms: follows the Windows "app mode" setting live, or a user override.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BSSIDLock
{
    static class Theme
    {
        public static bool Dark { get; private set; }
        public static Color Window, Surface, Field, Hover, Border, Text, Subtle, Accent, AccentText, Selection, BannerBack, BannerText;

        public static readonly Color Green = Color.FromArgb(22, 163, 74);
        public static readonly Color Orange = Color.FromArgb(217, 119, 6);
        public static readonly Color Blue = Color.FromArgb(37, 99, 235);
        public static readonly Color Red = Color.FromArgb(220, 38, 38);
        public static readonly Color Gray = Color.FromArgb(120, 120, 128);
        public static readonly Color Star = Color.FromArgb(234, 179, 8);

        /// <summary>Raised when the effective theme changes (system switch or user choice).</summary>
        public static event EventHandler Changed;

        static string preference = "system";
        static bool listening;

        /// <param name="pref">"system", "light" or "dark".</param>
        public static void Init(string pref)
        {
            preference = pref ?? "system";
            Compute();
            if (!listening)
            {
                listening = true;
                SystemEvents.UserPreferenceChanged += (s, e) =>
                {
                    if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle) Refresh();
                };
            }
        }

        public static void SetPreference(string pref)
        {
            preference = pref ?? "system";
            Refresh();
        }

        static void Refresh()
        {
            bool was = Dark;
            Compute();
            if (was != Dark) Changed?.Invoke(null, EventArgs.Empty);
        }

        static void Compute()
        {
            Dark = preference == "dark" || (preference == "system" && SystemPrefersDark());
            if (Dark)
            {
                Window = Color.FromArgb(32, 32, 32);
                Surface = Color.FromArgb(43, 43, 43);
                Field = Color.FromArgb(31, 31, 31);
                Hover = Color.FromArgb(58, 58, 58);
                Border = Color.FromArgb(70, 70, 70);
                Text = Color.FromArgb(243, 243, 243);
                Subtle = Color.FromArgb(166, 166, 166);
                Accent = Color.FromArgb(76, 194, 255);
                AccentText = Color.FromArgb(0, 30, 50);
                Selection = Color.FromArgb(52, 64, 79);
                BannerBack = Color.FromArgb(61, 50, 20);
                BannerText = Color.FromArgb(248, 210, 124);
            }
            else
            {
                Window = Color.FromArgb(243, 243, 243);
                Surface = Color.White;
                Field = Color.White;
                Hover = Color.FromArgb(234, 234, 234);
                Border = Color.FromArgb(214, 214, 214);
                Text = Color.FromArgb(27, 27, 27);
                Subtle = Color.FromArgb(100, 100, 108);
                Accent = Color.FromArgb(0, 103, 192);
                AccentText = Color.White;
                Selection = Color.FromArgb(229, 241, 251);
                BannerBack = Color.FromArgb(255, 247, 214);
                BannerText = Color.FromArgb(120, 53, 15);
            }
        }

        static bool SystemPrefersDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- native touches

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        /// <summary>Dark or light title bar (Windows 10 2004+ / 11).</summary>
        public static void ApplyTitleBar(Form f)
        {
            if (!f.IsHandleCreated) return;
            int on = Dark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0)       // DWMWA_USE_IMMERSIVE_DARK_MODE
                    DwmSetWindowAttribute(f.Handle, 19, ref on, 4);           // same, before Windows 10 20H1
            }
            catch (Exception) { /* older Windows: keep the default title bar */ }
        }

        /// <summary>Rounded corners for popups on Windows 11 (ignored elsewhere).</summary>
        public static void RoundCorners(IntPtr hwnd)
        {
            int round = 2;   // DWMWCP_ROUND
            try { DwmSetWindowAttribute(hwnd, 33, ref round, 4); } catch (Exception) { }
        }

        /// <summary>Native dark scrollbars / dropdowns where Windows supports them.</summary>
        public static void NativeTheme(Control c, string darkName)
        {
            if (!c.IsHandleCreated) return;
            try { SetWindowTheme(c.Handle, Dark ? darkName : "Explorer", null); } catch (Exception) { }
        }

        // ---------------------------------------------------------------- controls

        /// <summary>Apply the theme to a form and all its controls. Safe to call again after a theme change.</summary>
        public static void Apply(Form form)
        {
            form.BackColor = Window;
            form.ForeColor = Text;
            ApplyTitleBar(form);
            foreach (Control c in form.Controls) ApplyTo(c);
            form.Invalidate(true);
        }

        static void ApplyTo(Control c)
        {
            string tag = c.Tag as string;
            if (tag == "theme:skip") return;

            switch (c)
            {
                case Button b:
                    StyleButton(b);
                    break;
                case FieldBox fb:
                    fb.BackColor = Field;
                    fb.Box.BackColor = Field;
                    fb.Box.ForeColor = Text;
                    fb.Invalidate();
                    return;
                case Card card:
                    card.BackColor = Surface;
                    break;
                case LinkLabel link:
                    link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = Accent;
                    break;
                case TextBox t:
                    t.BackColor = Field; t.ForeColor = Text;
                    break;
                case NumericUpDown n:
                    n.BackColor = Surface; n.ForeColor = Text;
                    break;
                case ComboBox cb:
                    cb.BackColor = Surface; cb.ForeColor = Text;
                    cb.FlatStyle = Dark ? FlatStyle.Flat : FlatStyle.Standard;
                    NativeTheme(cb, "DarkMode_CFD");
                    break;
                case Label l:
                    l.ForeColor = tag == "subtle" ? Subtle : tag == "banner" ? BannerText : Text;
                    break;
                case CheckBox ch when ch.Appearance == Appearance.Normal:
                    ch.ForeColor = Text;
                    break;
                case StatusStrip ss:
                    ss.Renderer = new MenuRenderer();
                    ss.BackColor = Window;
                    foreach (ToolStripItem item in ss.Items)
                    {
                        if (item is ToolStripStatusLabel sl && sl.IsLink)
                            sl.LinkColor = sl.ActiveLinkColor = sl.VisitedLinkColor = Accent;
                        else
                            item.ForeColor = Subtle;
                    }
                    break;
                case Panel p when tag == "banner":
                    p.BackColor = BannerBack;
                    break;
                case Panel sep when tag == "separator":
                    sep.BackColor = Border;
                    break;
            }
            foreach (Control child in c.Controls) ApplyTo(child);
        }

        public static void StyleButton(Button b)
        {
            if (Dark)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                b.BackColor = Surface;
                b.ForeColor = Text;
                b.FlatAppearance.BorderColor = Border;
                b.FlatAppearance.MouseOverBackColor = Hover;
                b.FlatAppearance.MouseDownBackColor = Border;
            }
            else
            {
                b.FlatStyle = FlatStyle.Standard;
                b.BackColor = SystemColors.Control;
                b.ForeColor = SystemColors.ControlText;
                b.UseVisualStyleBackColor = true;
            }
        }

        // ---------------------------------------------------------------- drawing helpers

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Mix(Color a, Color b, float t) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        public static Color QualityColor(int rssi) => rssi >= -67 ? Green : rssi >= -75 ? Orange : Red;

        public static int QualityBars(int rssi) => rssi >= -55 ? 4 : rssi >= -67 ? 3 : rssi >= -75 ? 2 : 1;

        /// <summary>Four ascending signal bars (grey when out of range).</summary>
        public static void DrawBars(Graphics g, Rectangle r, int? rssi, float scale)
        {
            int filled = rssi.HasValue ? QualityBars(rssi.Value) : 0;
            Color on = rssi.HasValue ? QualityColor(rssi.Value) : Border;
            float w = 3.5f * scale, gap = 2f * scale;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < 4; i++)
            {
                float h = (5 + i * 3.5f) * scale;
                var bar = new RectangleF(r.X + i * (w + gap), r.Bottom - h, w, h);
                using (var path = RoundRect(bar, 1.2f * scale))
                using (var brush = new SolidBrush(i < filled ? on : Border))
                    g.FillPath(brush, path);
            }
            g.SmoothingMode = old;
        }
    }

    /// <summary>Menu / status bar renderer that follows the theme (flat, no gradients).</summary>
    sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new ThemeColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool isShortcut = e.Item is ToolStripMenuItem mi && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString) && e.Text == mi.ShortcutKeyDisplayString;
            if (e.Item is ToolStripStatusLabel sl && sl.IsLink) { base.OnRenderItemText(e); return; }
            string tag = e.Item.Tag as string;
            e.TextColor = tag == "header" ? Theme.Text
                : !e.Item.Enabled || isShortcut || tag == "subtle" ? Theme.Subtle
                : Theme.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            // The status header is a disabled item (not clickable) but its badge should keep its color.
            if ((e.Item.Tag as string) == "header" && e.Image != null) { e.Graphics.DrawImage(e.Image, e.ImageRectangle); return; }
            base.OnRenderItemImage(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Subtle;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(Theme.Border))
                e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // A clean check mark in the text color (the default one is invisible on dark backgrounds).
            var r = e.ImageRectangle;
            var g = e.Graphics;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Theme.Text, Math.Max(1.6f, r.Height / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[]
                {
                    new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
                    new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                    new PointF(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.30f),
                });
            g.SmoothingMode = old;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is StatusStrip)
            {
                using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, e.ToolStrip.Width, 0);
                return;
            }
            base.OnRenderToolStripBorder(e);
        }
    }

    sealed class ThemeColors : ProfessionalColorTable
    {
        public ThemeColors() { UseSystemColors = false; }
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.Hover;
        public override Color MenuItemSelected => Theme.Hover;
        public override Color MenuItemSelectedGradientBegin => Theme.Hover;
        public override Color MenuItemSelectedGradientEnd => Theme.Hover;
        public override Color MenuItemPressedGradientBegin => Theme.Hover;
        public override Color MenuItemPressedGradientMiddle => Theme.Hover;
        public override Color MenuItemPressedGradientEnd => Theme.Hover;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color StatusStripGradientBegin => Theme.Window;
        public override Color StatusStripGradientEnd => Theme.Window;
        public override Color ToolStripBorder => Theme.Border;
    }
}
