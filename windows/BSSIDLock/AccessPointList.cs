// Custom-drawn list of access points (looks the same in light and dark mode, at any display scaling).
using System;
using System.Collections.Generic;
using System.Drawing;
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
        string hoverPart;        // "lock", "star", "up", "down", "more" or null
        bool adjusting;
        Font boldFont, smallFont, glyphFont, headerFont;
        Bitmap buffer;   // one reusable paint buffer instead of a new bitmap per row

        public event Action<string> LockClicked, UnlockClicked, FavoriteClicked;
        public event Action<string, int> MoveClicked;
        public event Action<string, Point> MoreClicked;

        public AccessPointList(Engine engine)
        {
            this.engine = engine;
            DrawMode = DrawMode.OwnerDrawVariable;
            BorderStyle = BorderStyle.None;
            IntegralHeight = false;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        // Sizes follow the font (which already follows Windows display scaling).
        int S(float px) => Ui.S(this, px);

        void EnsureFonts()
        {
            if (boldFont != null) return;
            boldFont = new Font("Segoe UI Semibold", Font.Size + 0.5f);
            smallFont = new Font(Font.FontFamily, Font.Size - 0.5f);
            headerFont = new Font("Segoe UI Semibold", Font.Size - 1f);
            glyphFont = new Font("Segoe MDL2 Assets", Font.Size + 1f);
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
            if (disposing) { DisposeFonts(); buffer?.Dispose(); }
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

        public void Select(string bssid)
        {
            for (int i = 0; i < Items.Count; i++)
                if (((ListRow)Items[i]).Bssid == bssid) { SelectedIndex = i; return; }
        }

        /// <summary>Replace the rows, keeping the selection and scroll position.</summary>
        public void SetRows(IList<ListRow> rows)
        {
            string selected = SelectedBssid;
            int top = TopIndex;
            adjusting = true;
            BeginUpdate();
            Items.Clear();
            foreach (var r in rows) Items.Add(r);
            if (selected != null) Select(selected);
            if (Items.Count > 0) TopIndex = Math.Min(top, Items.Count - 1);
            EndUpdate();
            adjusting = false;
            hoverIndex = -1;
        }

        // ---------------------------------------------------------------- layout

        protected override void OnMeasureItem(MeasureItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            EnsureFonts();
            var row = (ListRow)Items[e.Index];
            switch (row.Type)
            {
                case ListRow.Kind.AccessPoint: e.ItemHeight = Math.Min(255, boldFont.Height + smallFont.Height + S(22)); break;
                case ListRow.Kind.Header: e.ItemHeight = Math.Min(255, headerFont.Height + S(22)); break;
                default: e.ItemHeight = Math.Min(255, Font.Height + S(18)); break;
            }
        }

        // Right-to-left: [more] [Lock] [star] [down] [up]
        Rectangle MoreRect(Rectangle b) { int w = S(30), h = S(30); return new Rectangle(b.Right - S(10) - w, b.Top + (b.Height - h) / 2, w, h); }
        Rectangle LockRect(Rectangle b) { var m = MoreRect(b); int w = S(78); return new Rectangle(m.Left - S(6) - w, m.Top, w, m.Height); }
        Rectangle StarRect(Rectangle b) { var l = LockRect(b); int w = S(32); return new Rectangle(l.Left - S(6) - w, l.Top, w, l.Height); }
        Rectangle DownRect(Rectangle b) { var s = StarRect(b); int w = S(30); return new Rectangle(s.Left - S(2) - w, s.Top, w, s.Height); }
        Rectangle UpRect(Rectangle b) { var d = DownRect(b); int w = S(30); return new Rectangle(d.Left - S(2) - w, d.Top, w, d.Height); }

        // ---------------------------------------------------------------- drawing

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            EnsureFonts();
            var row = (ListRow)Items[e.Index];
            var bounds = e.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            // Draw into a buffer: no flicker, and ClearType text on an opaque background.
            if (buffer == null || buffer.Width < bounds.Width || buffer.Height < bounds.Height)
            {
                int w = Math.Max(bounds.Width, buffer?.Width ?? 0), h = Math.Max(bounds.Height, buffer?.Height ?? 0);
                buffer?.Dispose();
                buffer = new Bitmap(w, h);
            }
            using (var g = Graphics.FromImage(buffer))
            {
                g.SetClip(new Rectangle(0, 0, bounds.Width, bounds.Height));
                var local = new Rectangle(0, 0, bounds.Width, bounds.Height);
                bool isAp = row.Type == ListRow.Kind.AccessPoint;
                bool selected = isAp && (e.State & DrawItemState.Selected) != 0;
                g.Clear(Theme.Surface);
                if (selected || (isAp && e.Index == hoverIndex))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    var hl = new RectangleF(S(4), S(2), local.Width - S(8), local.Height - S(4));
                    Ui.FillRound(g, hl, S(6), selected ? Theme.Selection : Theme.Mix(Theme.Surface, Theme.Hover, 0.7f));
                    if (selected) Ui.FillRound(g, new RectangleF(S(4), S(12), S(3), local.Height - S(24)), S(1.5f), Theme.Accent);
                }

                switch (row.Type)
                {
                    case ListRow.Kind.Header: DrawHeader(g, local, row); break;
                    case ListRow.Kind.Hint: DrawHint(g, local, row); break;
                    default: DrawAccessPoint(g, local, row, e.Index, selected); break;
                }
                e.Graphics.DrawImage(buffer, bounds, new Rectangle(0, 0, bounds.Width, bounds.Height), GraphicsUnit.Pixel);
            }
        }

        void DrawHeader(Graphics g, Rectangle r, ListRow row)
        {
            string text = row.Text.ToUpperInvariant();
            int h = headerFont.Height;
            var textRect = new Rectangle(S(16), r.Bottom - h - S(6), r.Width - S(32), h);
            var size = TextRenderer.MeasureText(g, text, headerFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, text, headerFont, textRect, Theme.Subtle, Ui.Left);
            if (!string.IsNullOrEmpty(row.Caption))
            {
                int x = textRect.X + size.Width + S(8);
                TextRenderer.DrawText(g, row.Caption, smallFont, new Rectangle(x, textRect.Y, Math.Max(0, textRect.Right - x), h),
                    Theme.Mix(Theme.Subtle, Theme.Surface, 0.3f), Ui.Left);
            }
        }

        void DrawHint(Graphics g, Rectangle r, ListRow row)
        {
            TextRenderer.DrawText(g, row.Text, Font, new Rectangle(S(16), 0, r.Width - S(32), r.Height), Theme.Subtle,
                Ui.Left | TextFormatFlags.WordBreak);
        }

        void DrawAccessPoint(Graphics g, Rectangle r, ListRow row, int index, bool selected)
        {
            string b = row.Bssid;
            var ap = engine.Find(b);
            string name = engine.NameOf(b);
            bool isCurrent = engine.Current != null && engine.Current.Bssid == b;
            bool isTarget = engine.Settings.LockEnabled && engine.Settings.Target == b;
            bool isFav = row.Rank >= 0;
            bool hot = index == hoverIndex;
            bool showTools = hot || selected;

            int x = S(16);
            // Priority number (favorites)
            if (isFav)
                TextRenderer.DrawText(g, (row.Rank + 1).ToString(), smallFont, new Rectangle(x, 0, S(16), r.Height), Theme.Subtle, Ui.Center);
            x += S(24);

            // Signal bars
            Theme.DrawBars(g, new Rectangle(x, r.Height / 2 - S(9), S(22), S(18)), ap?.Rssi, Ui.Unit(this));
            x += S(36);

            int textRight = (isFav ? UpRect(r).Left : StarRect(r).Left) - S(8);
            int width = Math.Max(0, textRight - x);

            // Title line: name (or address) + pills
            string title = name ?? b;
            int lineGap = S(3);
            int top = (r.Height - boldFont.Height - smallFont.Height - lineGap) / 2;
            int titleWidth = Math.Min(TextRenderer.MeasureText(g, title, boldFont, Size.Empty, TextFormatFlags.NoPadding).Width, width);
            TextRenderer.DrawText(g, title, boldFont, new Rectangle(x, top, titleWidth + S(2), boldFont.Height),
                ap == null ? Theme.Subtle : Theme.Text, Ui.Left);
            int px = x + titleWidth + S(8);
            int pillCenter = top + boldFont.Height / 2;
            if (isCurrent) px = DrawPill(g, "Connected", Theme.Green, px, pillCenter, textRight);
            if (isTarget) DrawPill(g, "Locked", Theme.Accent, px, pillCenter, textRight);

            // Detail line
            var parts = new List<string>();
            if (name != null) parts.Add(b);
            if (ap != null)
            {
                if (ap.Ssid.Length > 0) parts.Add(ap.Ssid);
                parts.Add(ap.Band + " · channel " + ap.Channel);
                parts.Add(Signal.Quality(ap.Rssi) + " (" + ap.Rssi + " dBm)");
            }
            else parts.Add("Out of range" + (engine.Settings.KnownSsids.TryGetValue(b, out var seen) ? " · last seen on \u201C" + seen + "\u201D" : ""));
            TextRenderer.DrawText(g, string.Join("  ·  ", parts), smallFont,
                new Rectangle(x, top + boldFont.Height + lineGap, width, smallFont.Height), Theme.Subtle, Ui.Left);

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Reorder (favorites, on hover/selection)
            if (isFav && showTools)
            {
                DrawGlyphButton(g, UpRect(r), Ui.GlyphUp, hot && hoverPart == "up", row.Rank > 0);
                DrawGlyphButton(g, DownRect(r), Ui.GlyphDown, hot && hoverPart == "down", row.Rank < engine.Settings.Favorites.Count - 1);
            }

            // Star
            var starRect = StarRect(r);
            bool starHot = hot && hoverPart == "star";
            if (starHot) Ui.FillRound(g, starRect, S(5), Theme.Hover);
            TextRenderer.DrawText(g, isFav ? "" : "", glyphFont, starRect,
                isFav ? Theme.Star : starHot ? Theme.Text : Theme.Subtle, Ui.Center);

            // Lock / Unlock
            var lockRect = LockRect(r);
            bool lockHot = hot && hoverPart == "lock";
            if (!isTarget)
            {
                Ui.FillRound(g, lockRect, S(5), lockHot ? Theme.Mix(Theme.Accent, Color.White, 0.12f) : Theme.Accent);
                TextRenderer.DrawText(g, "Lock", Font, lockRect, Theme.AccentText, Ui.Center);
            }
            else
            {
                Ui.FillRound(g, lockRect, S(5), lockHot ? Theme.Hover : Theme.Surface);
                Ui.DrawRound(g, lockRect, S(5), Theme.Border);
                TextRenderer.DrawText(g, "Unlock", Font, lockRect, Theme.Text, Ui.Center);
            }

            // More (…)
            DrawGlyphButton(g, MoreRect(r), Ui.GlyphMore, hot && hoverPart == "more", true);
        }

        void DrawGlyphButton(Graphics g, Rectangle rect, string glyph, bool hot, bool enabled)
        {
            if (hot && enabled) Ui.FillRound(g, rect, S(5), Theme.Hover);
            TextRenderer.DrawText(g, glyph, glyphFont, rect, enabled ? (hot ? Theme.Text : Theme.Subtle) : Theme.Border, Ui.Center);
        }

        int DrawPill(Graphics g, string text, Color color, int x, int centerY, int maxRight)
        {
            var size = TextRenderer.MeasureText(g, text, smallFont, Size.Empty, TextFormatFlags.NoPadding);
            var rect = new Rectangle(x, centerY - size.Height / 2 - S(2), size.Width + S(14), size.Height + S(4));
            if (rect.Right > maxRight) return x;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Ui.FillRound(g, rect, rect.Height / 2f, Color.FromArgb(Theme.Dark ? 60 : 36, color));
            TextRenderer.DrawText(g, text, smallFont, rect, Theme.Dark ? Theme.Mix(color, Color.White, 0.35f) : color, Ui.Center);
            return rect.Right + S(6);
        }

        // ---------------------------------------------------------------- interaction

        void InvalidateRow(int i)
        {
            if (i >= 0 && i < Items.Count) Invalidate(GetItemRectangle(i));
        }

        string PartAt(int index, Point p)
        {
            var row = (ListRow)Items[index];
            var b = GetItemRectangle(index);
            if (MoreRect(b).Contains(p)) return "more";
            if (LockRect(b).Contains(p)) return "lock";
            if (StarRect(b).Contains(p)) return "star";
            if (row.Rank >= 0 && UpRect(b).Contains(p)) return "up";
            if (row.Rank >= 0 && DownRect(b).Contains(p)) return "down";
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexFromPoint(e.Location);
            string part = null;
            if (i >= 0 && i < Items.Count && ((ListRow)Items[i]).Type == ListRow.Kind.AccessPoint && GetItemRectangle(i).Contains(e.Location))
                part = PartAt(i, e.Location);
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
            switch (hoverPart)
            {
                case "star": FavoriteClicked?.Invoke(row.Bssid); break;
                case "up": if (row.Rank > 0) MoveClicked?.Invoke(row.Bssid, -1); break;
                case "down": if (row.Rank < engine.Settings.Favorites.Count - 1) MoveClicked?.Invoke(row.Bssid, 1); break;
                case "more":
                    var m = MoreRect(GetItemRectangle(hoverIndex));
                    MoreClicked?.Invoke(row.Bssid, new Point(m.Left, m.Bottom));
                    break;
                default:
                    if (engine.Settings.LockEnabled && engine.Settings.Target == row.Bssid) UnlockClicked?.Invoke(row.Bssid); else LockClicked?.Invoke(row.Bssid);
                    break;
            }
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
            else if (e.Alt && e.KeyCode == Keys.Up) { MoveClicked?.Invoke(b, -1); e.Handled = true; }
            else if (e.Alt && e.KeyCode == Keys.Down) { MoveClicked?.Invoke(b, 1); e.Handled = true; }
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
            Invalidate();   // redraw the hover tools on the newly selected row
        }
    }
}
