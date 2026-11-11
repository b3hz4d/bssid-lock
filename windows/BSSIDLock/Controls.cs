// Small custom-drawn controls that look right in light and dark mode at any display scaling.
// Sizes are derived from the control's font height, which already follows Windows display scaling.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace BSSIDLock
{
    static class Ui
    {
        /// <summary>One design pixel for this control (Segoe UI 9 pt is about 16 px tall at 100 %).</summary>
        public static float Unit(Control c) => c.Font.Height / 16f;
        public static int S(Control c, float px) => (int)Math.Round(px * Unit(c));

        // Segoe MDL2 Assets
        public const string GlyphRefresh = "", GlyphHistory = "", GlyphSettings = "", GlyphMore = "",
            GlyphUp = "", GlyphDown = "", GlyphChevron = "", GlyphWarning = "", GlyphWifi = "",
            GlyphLocation = "";

        public static void FillRound(Graphics g, RectangleF r, float radius, Color color)
        {
            using (var path = Theme.RoundRect(r, radius))
            using (var b = new SolidBrush(color))
                g.FillPath(b, path);
        }

        public static void DrawRound(Graphics g, RectangleF r, float radius, Color color, float width = 1f)
        {
            var inner = new RectangleF(r.X + width / 2, r.Y + width / 2, r.Width - width, r.Height - width);
            using (var path = Theme.RoundRect(inner, radius))
            using (var p = new Pen(color, width))
                g.DrawPath(p, path);
        }

        public static Color Back(Control c) => c.Parent?.BackColor ?? Theme.Window;

        public const TextFormatFlags Left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        public const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    }

    /// <summary>Base for custom-painted, focusable controls.</summary>
    abstract class PaintedControl : Control
    {
        protected bool Hover, Pressed;

        protected PaintedControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
            TabStop = true;
            AutoSize = true;
        }

        protected void ResizeToFit() { if (AutoSize) Size = GetPreferredSize(Size.Empty); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FontsChanged(); ResizeToFit(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); ResizeToFit(); Invalidate(); }
        protected virtual void FontsChanged() { }

        protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hover = Pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override bool IsInputKey(Keys k) => k == Keys.Space || k == Keys.Enter || base.IsInputKey(k);

        protected Graphics Begin(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Back(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            return g;
        }

        protected void FocusRing(Graphics g, RectangleF r, float radius)
        {
            if (Focused && ShowFocusCues) Ui.DrawRound(g, r, radius, Theme.Accent, Math.Max(2f, Ui.Unit(this) * 1.5f));
        }
    }

    /// <summary>Rounded button: primary (accent), secondary (outlined) or ghost (flat), with an optional icon.</summary>
    class UiButton : PaintedControl, IButtonControl
    {
        public enum Kind { Secondary, Primary, Ghost }

        Kind kind = Kind.Secondary;
        string glyph;
        Font glyphFont;
        protected bool Chevron;

        public UiButton() { Margin = new Padding(0, 0, 6, 0); }

        public Kind Style { get => kind; set { kind = value; Invalidate(); } }
        public string Glyph { get => glyph; set { glyph = value; ResizeToFit(); Invalidate(); } }
        /// <summary>Minimum width in design pixels.</summary>
        public int MinWidth { get; set; }
        public DialogResult DialogResult { get; set; }
        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled && Visible) OnClick(EventArgs.Empty); }

        protected Font GlyphFont => glyphFont ?? (glyphFont = new Font("Segoe MDL2 Assets", Font.Size));
        protected override void FontsChanged() { glyphFont?.Dispose(); glyphFont = null; }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (DialogResult != DialogResult.None) { var f = FindForm(); if (f != null) f.DialogResult = DialogResult; }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) PerformClick();
            base.OnKeyUp(e);
        }

        int Pad => Ui.S(this, string.IsNullOrEmpty(Text) ? 9 : 14);
        int Gap => Ui.S(this, 8);

        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = Pad * 2;
            if (!string.IsNullOrEmpty(glyph))
                w += TextRenderer.MeasureText(glyph, GlyphFont, Size.Empty, TextFormatFlags.NoPadding).Width + (Text.Length > 0 ? Gap : 0);
            if (Text.Length > 0) w += TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            if (Chevron) w += Gap + TextRenderer.MeasureText(Ui.GlyphChevron, GlyphFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            int h = Math.Max(Ui.S(this, 32), Font.Height + Ui.S(this, 14));
            return new Size(Math.Max(w, Ui.S(this, MinWidth)), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var r = new RectangleF(0, 0, Width, Height);
            float radius = Ui.S(this, 5);
            Color fill = Color.Empty, border = Color.Empty, text;
            switch (kind)
            {
                case Kind.Primary:
                    fill = !Enabled ? Theme.Mix(Theme.Accent, Ui.Back(this), 0.55f)
                        : Pressed ? Theme.Mix(Theme.Accent, Color.Black, 0.15f)
                        : Hover ? Theme.Mix(Theme.Accent, Color.White, 0.12f)
                        : Theme.Accent;
                    text = Theme.AccentText;
                    break;
                case Kind.Ghost:
                    if (Pressed) fill = Theme.Border; else if (Hover) fill = Theme.Hover;
                    text = Theme.Text;
                    break;
                default:
                    fill = Pressed ? Theme.Border : Hover ? Theme.Hover : Theme.Surface;
                    border = Theme.Border;
                    text = Theme.Text;
                    break;
            }
            if (!Enabled && kind != Kind.Primary) text = Theme.Mix(Theme.Subtle, Ui.Back(this), 0.4f);
            if (fill != Color.Empty) Ui.FillRound(g, r, radius, fill);
            if (border != Color.Empty) Ui.DrawRound(g, r, radius, border);
            FocusRing(g, r, radius);

            var glyphSize = string.IsNullOrEmpty(glyph) ? Size.Empty : TextRenderer.MeasureText(g, glyph, GlyphFont, Size.Empty, TextFormatFlags.NoPadding);
            var textSize = Text.Length == 0 ? Size.Empty : TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            var chevSize = Chevron ? TextRenderer.MeasureText(g, Ui.GlyphChevron, GlyphFont, Size.Empty, TextFormatFlags.NoPadding) : Size.Empty;
            int content = glyphSize.Width + (glyphSize.Width > 0 && textSize.Width > 0 ? Gap : 0) + textSize.Width;
            int x = Chevron ? Pad : (Width - content) / 2;
            if (glyphSize.Width > 0)
            {
                TextRenderer.DrawText(g, glyph, GlyphFont, new Rectangle(x, 0, glyphSize.Width, Height), text, Ui.Left);
                x += glyphSize.Width + (textSize.Width > 0 ? Gap : 0);
            }
            if (textSize.Width > 0)
            {
                int right = Chevron ? Width - Pad - Gap - chevSize.Width : Width - Ui.S(this, 4);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Max(0, right - x), Height), text, Ui.Left);
            }
            if (Chevron)
                TextRenderer.DrawText(g, Ui.GlyphChevron, GlyphFont, new Rectangle(Width - Pad - chevSize.Width, 0, chevSize.Width, Height),
                    Theme.Subtle, Ui.Left);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) glyphFont?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A button that opens a themed menu of choices and shows the selected one.</summary>
    sealed class DropDownButton : UiButton
    {
        readonly ContextMenuStrip menu = new ContextMenuStrip { Renderer = new MenuRenderer() };
        readonly List<KeyValuePair<string, object>> options = new List<KeyValuePair<string, object>>();
        object selected;

        public event EventHandler SelectionChanged;
        /// <summary>Extra menu items added after the options (e.g. "Use current network").</summary>
        public Func<IEnumerable<ToolStripItem>> ExtraItems;
        public string EmptyText = "Nothing to choose yet";
        public string Placeholder = "";

        public DropDownButton()
        {
            Chevron = true;
            menu.Opened += (s, e) => Theme.RoundCorners(menu.Handle);
        }

        public object SelectedValue
        {
            get => selected;
            set
            {
                selected = value;
                var match = options.FirstOrDefault(o => Equals(o.Value, value));
                Text = match.Key ?? Placeholder;
            }
        }

        public void SetOptions(IEnumerable<KeyValuePair<string, object>> items)
        {
            options.Clear();
            options.AddRange(items);
            SelectedValue = selected;
        }

        public IReadOnlyList<KeyValuePair<string, object>> Options => options;

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            menu.Items.Clear();
            menu.Font = Font;
            foreach (var o in options)
            {
                var option = o;
                var item = new ToolStripMenuItem(o.Key) { Checked = Equals(o.Value, selected), Padding = new Padding(0, 4, 0, 4) };
                item.Click += (s, a) => { SelectedValue = option.Value; SelectionChanged?.Invoke(this, EventArgs.Empty); };
                menu.Items.Add(item);
            }
            var extra = ExtraItems?.Invoke()?.ToList();
            if (extra != null && extra.Count > 0)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
                foreach (var it in extra) menu.Items.Add(it);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new ToolStripMenuItem(EmptyText) { Enabled = false });
            menu.MinimumSize = new Size(Width, 0);
            menu.Show(this, new Point(0, Height + Ui.S(this, 2)));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) menu.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>On/off switch (Windows 11 style), with an optional label on its left.</summary>
    sealed class ToggleSwitch : PaintedControl
    {
        bool on;
        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get => on;
            set { if (on == value) return; on = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
        }

        Size Track => new Size(Ui.S(this, 40), Ui.S(this, 20));

        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = Track.Width + Ui.S(this, 4);
            if (Text.Length > 0) w += TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width + Ui.S(this, 10);
            return new Size(w, Math.Max(Track.Height, Font.Height) + Ui.S(this, 10));
        }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var t = Track;
            var track = new RectangleF(Width - t.Width - Ui.S(this, 2), (Height - t.Height) / 2f, t.Width, t.Height);
            if (Text.Length > 0)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, (int)track.Left - Ui.S(this, 10), Height),
                    Enabled ? Theme.Text : Theme.Subtle, Ui.Left);

            float radius = t.Height / 2f;
            float knob = t.Height - Ui.S(this, 8) + (Hover ? Ui.S(this, 2) : 0);
            if (on)
            {
                Ui.FillRound(g, track, radius, Enabled ? (Hover ? Theme.Mix(Theme.Accent, Color.White, 0.1f) : Theme.Accent) : Theme.Subtle);
                using (var b = new SolidBrush(Theme.AccentText))
                    g.FillEllipse(b, track.Right - Ui.S(this, 4) - knob - (Hover ? -Ui.S(this, 1) : 0), track.Top + (t.Height - knob) / 2, knob, knob);
            }
            else
            {
                if (Hover) Ui.FillRound(g, track, radius, Theme.Hover);
                Ui.DrawRound(g, track, radius, Theme.Subtle, Math.Max(1f, Ui.Unit(this)));
                using (var b = new SolidBrush(Theme.Subtle))
                    g.FillEllipse(b, track.Left + Ui.S(this, 4) - (Hover ? Ui.S(this, 1) : 0), track.Top + (t.Height - knob) / 2, knob, knob);
            }
            FocusRing(g, RectangleF.Inflate(track, Ui.S(this, 2), Ui.S(this, 2)), radius + Ui.S(this, 2));
        }
    }

    /// <summary>A row of mutually exclusive options (e.g. System / Light / Dark).</summary>
    sealed class SegmentedControl : PaintedControl
    {
        string[] options = new string[0];
        int selected, hover = -1;
        public event EventHandler SelectedIndexChanged;

        public string[] Options { get => options; set { options = value ?? new string[0]; ResizeToFit(); Invalidate(); } }

        public int SelectedIndex
        {
            get => selected;
            set { if (selected == value || value < 0 || value >= options.Length) return; selected = value; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
        }

        int SegmentWidth => options.Length == 0 ? 0 : options.Max(o => TextRenderer.MeasureText(o, Font, Size.Empty, TextFormatFlags.NoPadding).Width) + Ui.S(this, 30);

        public override Size GetPreferredSize(Size proposedSize) =>
            new Size(SegmentWidth * options.Length + Ui.S(this, 6), Font.Height + Ui.S(this, 16));

        int IndexAt(int x) => SegmentWidth == 0 ? -1 : Math.Max(-1, Math.Min(options.Length - 1, (x - Ui.S(this, 3)) / SegmentWidth));

        protected override void OnMouseMove(MouseEventArgs e) { int i = IndexAt(e.X); if (i != hover) { hover = i; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { int i = IndexAt(e.X); if (i >= 0) SelectedIndex = i; base.OnMouseClick(e); }
        protected override bool IsInputKey(Keys k) => k == Keys.Left || k == Keys.Right || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) SelectedIndex = Math.Max(0, selected - 1);
            if (e.KeyCode == Keys.Right) SelectedIndex = Math.Min(options.Length - 1, selected + 1);
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var r = new RectangleF(0, 0, Width, Height);
            float radius = Ui.S(this, 6);
            Ui.FillRound(g, r, radius, Theme.Field);
            Ui.DrawRound(g, r, radius, Theme.Border);
            int w = SegmentWidth, pad = Ui.S(this, 3);
            for (int i = 0; i < options.Length; i++)
            {
                var seg = new RectangleF(pad + i * w, pad, w, Height - pad * 2);
                if (i == selected) Ui.FillRound(g, seg, radius - 2, Theme.Accent);
                else if (i == hover) Ui.FillRound(g, seg, radius - 2, Theme.Hover);
                TextRenderer.DrawText(g, options[i], Font, Rectangle.Round(seg), i == selected ? Theme.AccentText : Theme.Text, Ui.Center);
            }
            FocusRing(g, r, radius);
        }
    }

    /// <summary>Rounded panel used to group related settings.</summary>
    sealed class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Back(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float radius = Ui.S(this, 8);
            var r = new RectangleF(0, 0, Width, Height);
            Ui.FillRound(g, r, radius, Theme.Surface);
            Ui.DrawRound(g, r, radius, Theme.Border);
        }
    }

    /// <summary>Text field with a rounded border that highlights when focused.</summary>
    sealed class FieldBox : Panel
    {
        public readonly TextBox Box = new TextBox { BorderStyle = BorderStyle.None };
        bool focused;
        string placeholder = "";

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        const int EM_SETCUEBANNER = 0x1501;

        /// <summary>Grey hint shown inside the empty field (stays visible while focused).</summary>
        public string Placeholder
        {
            get => placeholder;
            set { placeholder = value ?? ""; ApplyPlaceholder(); }
        }

        void ApplyPlaceholder()
        {
            if (Box.IsHandleCreated) SendMessage(Box.Handle, EM_SETCUEBANNER, (IntPtr)1, placeholder);
        }

        public FieldBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Controls.Add(Box);
            Box.HandleCreated += (s, e) => ApplyPlaceholder();
            Box.GotFocus += (s, e) => { focused = true; Invalidate(); };
            Box.LostFocus += (s, e) => { focused = false; Invalidate(); };
            Cursor = Cursors.IBeam;
        }

        public override string Text { get => Box.Text; set => Box.Text = value; }

        protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int h = Box.PreferredHeight;
            int target = h + Ui.S(this, 14);
            if (Height != target) Height = target;
            int px = Ui.S(this, 10);
            Box.SetBounds(px, (Height - h) / 2, Math.Max(10, Width - px * 2), h);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Back(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float radius = Ui.S(this, 5);
            var r = new RectangleF(0, 0, Width, Height);
            Ui.FillRound(g, r, radius, Theme.Field);
            Ui.DrawRound(g, r, radius, focused ? Theme.Accent : Theme.Border, focused ? Math.Max(1.5f, Ui.Unit(this) * 1.5f) : 1f);
        }
    }

    /// <summary>Centered message with an icon and actions, shown instead of the list when something is needed first.</summary>
    sealed class EmptyState : Panel
    {
        readonly Label glyph, title, message;
        readonly FlowLayoutPanel buttons;

        public EmptyState()
        {
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var stack = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Anchor = AnchorStyles.None };
            glyph = new Label { AutoSize = true, Font = new Font("Segoe MDL2 Assets", 30f), Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 14), Tag = "theme:skip" };
            title = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 8) };
            message = new Label { AutoSize = true, Tag = "subtle", Anchor = AnchorStyles.None, TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 0, 0, 18) };
            buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false, Margin = new Padding(0) };
            stack.Controls.Add(glyph, 0, 0);
            stack.Controls.Add(title, 0, 1);
            stack.Controls.Add(message, 0, 2);
            stack.Controls.Add(buttons, 0, 3);
            outer.Controls.Add(stack, 0, 1);
            Controls.Add(outer);
            SizeChanged += (s, e) => message.MaximumSize = new Size(Math.Max(Ui.S(this, 200), Math.Min(Width - Ui.S(this, 60), Ui.S(this, 560))), 0);
        }

        public void SetContent(string glyphText, Color glyphColor, string titleText, string messageText, params UiButton[] actions)
        {
            glyph.Text = glyphText;
            glyph.ForeColor = glyphColor;
            title.Text = titleText;
            message.Text = messageText;
            foreach (Control old in buttons.Controls.Cast<Control>().ToList()) old.Dispose();
            buttons.Controls.Clear();
            foreach (var b in actions) { b.Margin = new Padding(4, 0, 4, 0); buttons.Controls.Add(b); }
        }
    }
}
