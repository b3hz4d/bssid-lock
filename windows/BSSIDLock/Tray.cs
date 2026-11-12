// Notification-area (tray) icon with a status dot, and its quick menu.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BSSIDLock
{
    /// <summary>Small images shared by the window and the tray menu.</summary>
    static class Glyphs
    {
        public static Color StateColor(LockState s)
        {
            switch (s)
            {
                case LockState.Locked: return Theme.Green;
                case LockState.Working: return Theme.Blue;
                case LockState.Attention: return Theme.Orange;
                default: return Theme.Gray;
            }
        }

        // Segoe MDL2 Assets: Lock, Sync, Warning, Unlock, Pause
        public static string StateGlyph(LockState s)
        {
            switch (s)
            {
                case LockState.Locked: return "";
                case LockState.Working: return "";
                case LockState.Attention: return "";
                case LockState.Off: return "";
                default: return "";
            }
        }

        /// <summary>Round tinted badge with the state glyph.</summary>
        public static Bitmap Badge(LockState s, int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            using (var fill = new SolidBrush(Color.FromArgb(Theme.Dark ? 70 : 40, StateColor(s))))
            using (var font = new Font("Segoe MDL2 Assets", size * 0.36f, GraphicsUnit.Pixel))
            using (var text = new SolidBrush(StateColor(s)))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.FillEllipse(fill, 0, 0, size - 1, size - 1);
                g.DrawString(StateGlyph(s), font, text, new RectangleF(0, 1, size, size), fmt);
            }
            return bmp;
        }

        /// <summary>Radio button, optionally followed by signal bars.</summary>
        public static Bitmap Choice(bool selected, int? rssi, bool withBars, float scale)
        {
            int w = (int)(40 * scale), h = (int)(20 * scale);
            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float d = 14 * scale;
                var ring = new RectangleF(2 * scale, (h - d) / 2, d, d);
                using (var pen = new Pen(selected ? Theme.Accent : Theme.Subtle, 1.4f * scale))
                    g.DrawEllipse(pen, ring);
                if (selected)
                    using (var dot = new SolidBrush(Theme.Accent))
                        g.FillEllipse(dot, RectangleF.Inflate(ring, -3.5f * scale, -3.5f * scale));
                if (withBars)
                    Theme.DrawBars(g, new Rectangle((int)(21 * scale), (int)(h / 2f - 8 * scale), (int)(18 * scale), (int)(16 * scale)), rssi, scale * 0.85f);
            }
            return bmp;
        }
    }

    sealed class TrayUi : IDisposable
    {
        readonly Engine engine;
        readonly NotifyIcon icon;
        readonly ContextMenuStrip menu;
        readonly Dictionary<LockState, Icon> icons = new Dictionary<LockState, Icon>();
        readonly List<Image> menuImages = new List<Image>();
        Font menuFont, boldFont, smallFont;

        public event Action OpenRequested, SettingsRequested, ExitRequested, UpdateRequested;
        /// <summary>What clicking the latest notification does (null = nothing).</summary>
        Action balloonClick;
        /// <summary>Text for the update menu item ("Check for updates", "Install version 1.3.0", …).</summary>
        public Func<string> UpdateMenuText = () => "Check for updates";

        public TrayUi(Engine engine)
        {
            this.engine = engine;
            menuFont = new Font("Segoe UI", 9.5f);
            boldFont = new Font("Segoe UI Semibold", 9.5f);
            smallFont = new Font("Segoe UI", 8.5f);

            menu = new ThemedMenu { Font = menuFont, ShowImageMargin = true, ShowCheckMargin = false };
            menu.Opening += (s, e) => Build();

            icon = new NotifyIcon { Text = "BSSIDLock", ContextMenuStrip = menu };
            // Left click opens the quick menu too (like the macOS menu bar), not just right click.
            icon.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left) ShowMenu(); };
            icon.BalloonTipClicked += (s, e) => { var a = balloonClick; balloonClick = null; a?.Invoke(); };

            BuildIcons();
            Update();
            icon.Visible = true;
        }

        float Scale
        {
            get { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }
        }

        void ShowMenu()
        {
            // NotifyIcon has no public way to open its menu on left click; this is the same call it uses for right click.
            typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(icon, null);
        }

        /// <summary>Show a notification; <paramref name="onClick"/> runs if the user clicks it.</summary>
        public void ShowBalloon(string title, string text, Action onClick = null)
        {
            balloonClick = onClick;
            icon.ShowBalloonTip(5000, string.IsNullOrEmpty(title) ? "BSSIDLock" : title, string.IsNullOrEmpty(text) ? " " : text, ToolTipIcon.Info);
        }

        /// <summary>Refresh the tray icon (status dot) and tooltip.</summary>
        public void Update()
        {
            if (icons.TryGetValue(engine.State, out var ic) && icon.Icon != ic) icon.Icon = ic;
            string tip = "BSSIDLock: " + engine.Title;
            tip = tip.Length <= 63 ? tip : tip.Substring(0, 62) + "…";
            if (icon.Text != tip) icon.Text = tip;
        }

        // ---------------------------------------------------------------- icons

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        void BuildIcons()
        {
            using (var baseIcon = AppIcon.Load(small: true))
            using (var baseBmp = baseIcon.ToBitmap())
                foreach (LockState s in Enum.GetValues(typeof(LockState)))
                {
                    using (var bmp = new Bitmap(baseBmp))
                    {
                        if (s != LockState.Idle)
                            using (var g = Graphics.FromImage(bmp))
                            {
                                g.SmoothingMode = SmoothingMode.AntiAlias;
                                float d = bmp.Width * 0.5f;
                                var r = new RectangleF(bmp.Width - d, bmp.Height - d, d - 0.5f, d - 0.5f);
                                using (var ring = new SolidBrush(Color.FromArgb(32, 32, 32))) g.FillEllipse(ring, r);
                                using (var dot = new SolidBrush(Glyphs.StateColor(s)))
                                    g.FillEllipse(dot, RectangleF.Inflate(r, -bmp.Width * 0.07f, -bmp.Width * 0.07f));
                            }
                        IntPtr h = bmp.GetHicon();
                        using (var tmp = Icon.FromHandle(h)) icons[s] = (Icon)tmp.Clone();
                        DestroyIcon(h);
                    }
                }
        }

        // ---------------------------------------------------------------- menu

        void Build()
        {
            var st = engine.Settings;
            float sc = Scale;
            menu.SuspendLayout();
            foreach (var img in menuImages) img.Dispose();
            menuImages.Clear();
            menu.Items.Clear();
            menu.ImageScalingSize = new Size((int)(40 * sc), (int)(20 * sc));

            Image Keep(Image i) { menuImages.Add(i); return i; }
            ToolStripMenuItem Item(string text, Action onClick, Image image = null)
            {
                var it = new ToolStripMenuItem(text) { Image = image, ImageScaling = ToolStripItemImageScaling.None, Padding = new Padding(0, 3, 0, 3) };
                if (onClick != null) it.Click += (s, e) => onClick();
                return it;
            }

            // Status header
            var header = Item(Truncate(engine.Title, 36), null, Keep(Glyphs.Badge(engine.State, (int)(20 * sc))));
            header.Font = boldFont;
            header.Tag = "header";
            header.Enabled = false;
            menu.Items.Add(header);
            // Keep the menu narrow: the full status sentence lives in the main window.
            string detail = engine.Current != null ? engine.Current.Ssid : "Not connected to Wi-Fi";
            menu.Items.Add(new ToolStripMenuItem(Truncate(detail, 40)) { Enabled = false, Tag = "subtle", Font = smallFont });
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(Item(st.LockEnabled ? "Turn lock off" : "Turn lock on", () => engine.SetLockEnabled(!st.LockEnabled)));
            if (st.LockEnabled && engine.IsPaused)
            {
                var resume = Item("Resume lock", engine.Resume);
                resume.ShortcutKeyDisplayString = engine.PauseText.Replace("until you switch networks", "until network change");
                menu.Items.Add(resume);
            }
            else if (st.LockEnabled)
            {
                var pause = Item("Pause", null);
                pause.DropDown.Renderer = new MenuRenderer();
                pause.DropDown.Font = menuFont;
                pause.DropDown.HandleCreated += (s, e) => Theme.RoundCorners(((Control)s).Handle);
                pause.DropDownItems.Add(Item("For 15 minutes", () => engine.Pause(15)));
                pause.DropDownItems.Add(Item("For 1 hour", () => engine.Pause(60)));
                pause.DropDownItems.Add(Item("Until I switch networks", () => engine.Pause(0)));
                menu.Items.Add(pause);
            }

            // Lock targets, one click each
            if (st.Networks.Count > 0 && !engine.AccessDenied)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("LOCK TO") { Enabled = false, Tag = "subtle", Font = smallFont, ShortcutKeyDisplayString = Truncate(st.Ssid, 24) });

                var any = Item("Any favorite", () => { engine.SetLockEnabled(true); engine.SetTarget(null); engine.Resume(); },
                    Keep(Glyphs.Choice(st.LockEnabled && st.Target == null, null, false, sc)));
                any.ShortcutKeyDisplayString = st.Favorites.Count > 0 ? "top first" : "no favorites yet";
                any.Enabled = st.Favorites.Count > 0;
                menu.Items.Add(any);

                var candidates = st.Favorites.Concat(engine.Visible.Where(v => v.Ssid == st.Ssid).Select(v => v.Bssid).Where(b => !st.Favorites.Contains(b))).Take(8);
                foreach (var b in candidates)
                {
                    var ap = engine.Find(b);
                    string bssid = b;
                    string label = Truncate(engine.Label(b), 28) + (st.Favorites.Contains(b) ? "  ★" : "");
                    var it = Item(label, () => engine.LockTo(bssid), Keep(Glyphs.Choice(st.LockEnabled && st.Target == b, ap?.Rssi, true, sc)));
                    it.ShortcutKeyDisplayString = ap != null ? ap.Rssi + " dBm" : "out of range";
                    if (engine.Current != null && engine.Current.Bssid == b) it.Font = boldFont;
                    menu.Items.Add(it);
                }
            }

            menu.Items.Add(new ToolStripSeparator());
            var scan = Item(engine.Busy ? "Scanning…" : "Scan now", async () => await engine.ScanAsync());
            scan.Enabled = !engine.Busy;
            menu.Items.Add(scan);
            menu.Items.Add(Item(UpdateMenuText(), () => UpdateRequested?.Invoke()));

            menu.Items.Add(new ToolStripSeparator());
            var open = Item("Open BSSIDLock", () => OpenRequested?.Invoke());
            open.Font = boldFont;
            menu.Items.Add(open);
            menu.Items.Add(Item("Settings…", () => SettingsRequested?.Invoke()));
            menu.Items.Add(Item("Exit", () => ExitRequested?.Invoke()));
            menu.ResumeLayout();
        }

        static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
            foreach (var img in menuImages) img.Dispose();
            foreach (var ic in icons.Values) ic.Dispose();
            menuFont.Dispose(); boldFont.Dispose(); smallFont.Dispose();
        }
    }
}
