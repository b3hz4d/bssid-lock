// Custom-drawn list of access points (looks the same in light and dark mode).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace BSSIDLock
{
    sealed class ListRow
    {
        public enum Kind { Header, AccessPoint, Hint }

        public Kind Type;
        public string Text;      // header / hint text
        public string Caption;   // small text after a header
        public string Bssid;
        public int Rank = -1;    // favorite position, -1 if not a favorite

        public override string ToString() => Bssid ?? Text ?? "";
    }

    sealed class AccessPointList : ListBox
    {
        readonly Engine engine;
        int hoverIndex = -1;
        string hoverPart;        // "lock", "star" or null
        bool adjusting;
        Font boldFont, smallFont, glyphFont, headerFont;

        public event Action<string> LockClicked, UnlockClicked, FavoriteClicked;

        public AccessPointList(Engine engine)
        {
            this.engine = engine;
            DrawMode = DrawMode.OwnerDrawVariable;
            BorderStyle = BorderStyle.None;
            IntegralHeight = false;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        float DpiScale => DeviceDpi / 96f;
        int S(float px) => (int)Math.Round(px * DpiScale);

        void EnsureFonts()
        {
            if (boldFont != null) return;
            boldFont = new Font("Segoe UI Semibold", Font.Size + 0.75f);
            smallFont = new Font(Font.FontFamily, Font.Size - 0.5f);
            headerFont = new Font("Segoe UI Semibold", Font.Size - 1f);
            glyphFont = new Font("Segoe MDL2 Assets", Font.Size + 2f);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            DisposeFonts();
            base.OnFontChanged(e);
        }

        void DisposeFonts()
        {
            boldFont?.Dispose(); smallFont?.Dispose(); headerFont?.Dispose(); glyphFont?.Dispose();
            boldFont = smallFont = headerFont = glyphFont = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeFonts();
            base.Dispose(disposing);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTheme();
        }

        public void ApplyTheme()
        {
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Theme.NativeTheme(this, "DarkMode_Explorer");   // dark scrollbar
            Invalidate();
        }

        public string SelectedBssid => SelectedItem is ListRow r && r.Type == ListRow.Kind.AccessPoint ? r.Bssid : null;

        /// <summary>Replace the rows, keeping the selection and scroll position.</summary>
        public void SetRows(IList<ListRow> rows)
        {
            string selected = SelectedBssid;
            int top = TopIndex;
            adjusting = true;
            BeginUpdate();
            Items.Clear();
            foreach (var r in rows) Items.Add(r);
            if (selected != null)
                for (int i = 0; i < Items.Count; i++)
                    if (((ListRow)Items[i]).Bssid == selected) { SelectedIndex = i; break; }
            if (Items.Count > 0) TopIndex = Math.Min(top, Items.Count - 1);
            EndUpdate();
            adjusting = false;
            hoverIndex = -1;
        }

        // ---------------------------------------------------------------- layout

        protected override void OnMeasureItem(MeasureItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            var row = (ListRow)Items[e.Index];
            e.ItemHeight = S(row.Type == ListRow.Kind.AccessPoint ? 52 : row.Type == ListRow.Kind.Header ? 32 : 34);
        }

        Rectangle LockRect(Rectangle b) { int w = S(76), h = S(28); return new Rectangle(b.Right - S(12) - w, b.Top + (b.Height - h) / 2, w, h); }
        Rectangle StarRect(Rectangle b) { var l = LockRect(b); int w = S(32); return new Rectangle(l.Left - S(6) - w, l.Top, w, l.Height); }

        // ---------------------------------------------------------------- drawing

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            EnsureFonts();
            var row = (ListRow)Items[e.Index];
            var bounds = e.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            // Draw into a buffer: no flicker, and ClearType text on an opaque background.
            using (var buffer = new Bitmap(bounds.Width, bounds.Height))
            using (var g = Graphics.FromImage(buffer))
            {
                var local = new Rectangle(0, 0, bounds.Width, bounds.Height);
                bool isAp = row.Type == ListRow.Kind.AccessPoint;
                bool selected = isAp && (e.State & DrawItemState.Selected) != 0;
                Color bg = selected ? Theme.Selection
                    : isAp && e.Index == hoverIndex ? Theme.Mix(Theme.Surface, Theme.Hover, 0.6f)
                    : Theme.Surface;
                g.Clear(bg);

                switch (row.Type)
                {
                    case ListRow.Kind.Header: DrawHeader(g, local, row); break;
                    case ListRow.Kind.Hint: DrawHint(g, local, row); break;
                    default: DrawAccessPoint(g, local, row, e.Index, selected); break;
                }
                e.Graphics.DrawImageUnscaled(buffer, bounds.Location);
            }
        }

        void DrawHeader(Graphics g, Rectangle r, ListRow row)
        {
            var textRect = new Rectangle(S(14), r.Bottom - S(22), r.Width - S(28), S(18));
            var size = TextRenderer.MeasureText(g, row.Text.ToUpperInvariant(), headerFont);
            TextRenderer.DrawText(g, row.Text.ToUpperInvariant(), headerFont, textRect, Theme.Subtle,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (!string.IsNullOrEmpty(row.Caption))
            {
                var cap = new Rectangle(textRect.X + size.Width + S(4), textRect.Y, Math.Max(0, textRect.Right - textRect.X - size.Width - S(4)), textRect.Height);
                TextRenderer.DrawText(g, row.Caption, smallFont, cap, Theme.Mix(Theme.Subtle, Theme.Surface, 0.25f),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }

        void DrawHint(Graphics g, Rectangle r, ListRow row)
        {
            TextRenderer.DrawText(g, row.Text, Font, new Rectangle(S(14), 0, r.Width - S(28), r.Height), Theme.Subtle,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }

        void DrawAccessPoint(Graphics g, Rectangle r, ListRow row, int index, bool selected)
        {
            string b = row.Bssid;
            var ap = engine.Find(b);
            string name = engine.NameOf(b);
            bool isCurrent = engine.Current != null && engine.Current.Bssid == b;
            bool isTarget = engine.Settings.Target == b;
            bool isFav = row.Rank >= 0;
            bool hot = index == hoverIndex;

            if (selected)
                using (var accent = new SolidBrush(Theme.Accent))
                    g.FillRectangle(accent, 0, S(8), S(3), r.Height - S(16));

            int x = S(12);
            // Priority number (favorites)
            if (isFav)
                TextRenderer.DrawText(g, (row.Rank + 1).ToString(), smallFont, new Rectangle(x, 0, S(16), r.Height), Theme.Subtle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += S(22);

            // Signal bars
            Theme.DrawBars(g, new Rectangle(x, r.Height / 2 - S(9), S(22), S(18)), ap?.Rssi, DpiScale);
            x += S(34);

            var lockRect = LockRect(r);
            var starRect = StarRect(r);
            int textRight = starRect.Left - S(8);
            int width = Math.Max(0, textRight - x);

            // Title line: name (or address) + pills
            string title = name ?? b;
            var titleSize = TextRenderer.MeasureText(g, title, boldFont, Size.Empty, TextFormatFlags.NoPadding);
            int top = (r.Height - titleSize.Height - S(18)) / 2;
            int titleWidth = Math.Min(titleSize.Width, width);
            TextRenderer.DrawText(g, title, boldFont, new Rectangle(x, top, titleWidth + S(2), titleSize.Height),
                ap == null ? Theme.Subtle : Theme.Text, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            int px = x + titleWidth + S(8);
            if (isCurrent) px = DrawPill(g, "Connected", Theme.Green, px, top + titleSize.Height / 2, textRight);
            if (isTarget) DrawPill(g, "Locked", Theme.Accent, px, top + titleSize.Height / 2, textRight);

            // Detail line
            var parts = new List<string>();
            if (name != null) parts.Add(b);
            if (ap != null)
            {
                parts.Add(ap.Band + " · channel " + ap.Channel);
                parts.Add(Signal.Quality(ap.Rssi) + " (" + ap.Rssi + " dBm)");
            }
            else parts.Add("Out of range");
            TextRenderer.DrawText(g, string.Join("  ·  ", parts), smallFont, new Rectangle(x, top + titleSize.Height + S(3), width, S(16)),
                Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // Star
            bool starHot = hot && hoverPart == "star";
            if (starHot) FillRound(g, starRect, Theme.Hover);
            TextRenderer.DrawText(g, isFav ? "" : "", glyphFont, starRect,
                isFav ? Theme.Star : starHot ? Theme.Text : Theme.Subtle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // Lock / Unlock button
            bool lockHot = hot && hoverPart == "lock";
            bool primary = !isTarget;
            Color fill = primary ? (lockHot ? Theme.Mix(Theme.Accent, Theme.Text, 0.12f) : Theme.Accent)
                                 : (lockHot ? Theme.Hover : Theme.Surface);
            FillRound(g, lockRect, fill);
            if (!primary)
                using (var path = Theme.RoundRect(new RectangleF(lockRect.X + 0.5f, lockRect.Y + 0.5f, lockRect.Width - 1, lockRect.Height - 1), S(4)))
                using (var pen = new Pen(Theme.Border))
                {
                    var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawPath(pen, path);
                    g.SmoothingMode = old;
                }
            TextRenderer.DrawText(g, isTarget ? "Unlock" : "Lock", Font, lockRect, primary ? Theme.AccentText : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        int DrawPill(Graphics g, string text, Color color, int x, int centerY, int maxRight)
        {
            var size = TextRenderer.MeasureText(g, text, smallFont, Size.Empty, TextFormatFlags.NoPadding);
            var rect = new Rectangle(x, centerY - size.Height / 2 - S(2), size.Width + S(14), size.Height + S(4));
            if (rect.Right > maxRight) return x;
            FillRound(g, rect, Color.FromArgb(Theme.Dark ? 60 : 36, color));
            TextRenderer.DrawText(g, text, smallFont, rect, Theme.Dark ? Theme.Mix(color, Color.White, 0.35f) : color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return rect.Right + S(6);
        }

        void FillRound(Graphics g, Rectangle r, Color color)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundRect(r, S(4)))
            using (var brush = new SolidBrush(color))
                g.FillPath(brush, path);
            g.SmoothingMode = old;
        }

        // ---------------------------------------------------------------- interaction

        void InvalidateRow(int i)
        {
            if (i >= 0 && i < Items.Count) Invalidate(GetItemRectangle(i));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexFromPoint(e.Location);
            string part = null;
            if (i >= 0 && i < Items.Count && ((ListRow)Items[i]).Type == ListRow.Kind.AccessPoint)
            {
                var b = GetItemRectangle(i);
                if (!b.Contains(e.Location)) i = -1;
                else if (LockRect(b).Contains(e.Location)) part = "lock";
                else if (StarRect(b).Contains(e.Location)) part = "star";
            }
            else i = -1;

            if (i != hoverIndex || part != hoverPart)
            {
                int old = hoverIndex;
                hoverIndex = i;
                hoverPart = part;
                InvalidateRow(old);
                InvalidateRow(i);
                Cursor = part != null ? Cursors.Hand : Cursors.Default;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            int old = hoverIndex;
            hoverIndex = -1;
            hoverPart = null;
            InvalidateRow(old);
            Cursor = Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || hoverIndex < 0 || hoverPart == null) return;
            var row = (ListRow)Items[hoverIndex];
            if (hoverPart == "star") FavoriteClicked?.Invoke(row.Bssid);
            else if (engine.Settings.Target == row.Bssid) UnlockClicked?.Invoke(row.Bssid);
            else LockClicked?.Invoke(row.Bssid);
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            if (hoverPart == null && SelectedBssid != null) LockClicked?.Invoke(SelectedBssid);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            string b = SelectedBssid;
            if (b == null) return;
            if (e.KeyCode == Keys.Enter) { LockClicked?.Invoke(b); e.Handled = true; }
            else if (e.KeyCode == Keys.Space) { FavoriteClicked?.Invoke(b); e.Handled = true; }
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            // Headers and hints aren't selectable: move to the nearest access point instead.
            if (!adjusting && SelectedIndex >= 0 && ((ListRow)Items[SelectedIndex]).Type != ListRow.Kind.AccessPoint)
            {
                adjusting = true;
                int next = -1;
                for (int i = SelectedIndex + 1; i < Items.Count && next < 0; i++)
                    if (((ListRow)Items[i]).Type == ListRow.Kind.AccessPoint) next = i;
                for (int i = SelectedIndex - 1; i >= 0 && next < 0; i--)
                    if (((ListRow)Items[i]).Type == ListRow.Kind.AccessPoint) next = i;
                SelectedIndex = next;
                adjusting = false;
            }
            base.OnSelectedIndexChanged(e);
        }
    }
}
