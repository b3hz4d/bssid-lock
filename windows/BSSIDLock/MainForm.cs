// Main window. Lives in the notification area when closed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using BSSIDLock.Native;

namespace BSSIDLock
{
    public sealed class MainForm : Form
    {
        readonly Engine engine;
        readonly Updater updater;
        readonly TrayUi tray;
        readonly Timer timer, eventDebounce, updateTimer;
        string listSignature;
        bool startHidden;
        bool exiting;
        bool updating;

        // Header
        StatusBadge badge;
        Label title, detail;
        CheckBox lockToggle;
        ComboBox lockTo;
        // Banner (location / Wi-Fi problem / choose network)
        Panel banner;
        Label bannerText;
        Button bannerButton;
        Action bannerAction;
        // List and actions
        Panel listHost;
        AccessPointList list;
        ContextMenuStrip listMenu;
        Button btnRename, btnUp, btnDown, btnScan;
        // Footer
        StatusStrip status;
        ToolStripStatusLabel footer, updateLink;

        public MainForm(Engine engine, bool startHidden, bool justUpdated)
        {
            this.engine = engine;
            this.startHidden = startHidden;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "BSSIDLock";
            Icon = AppIcon.Load(small: false);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 580);
            MinimumSize = new Size(680, 480);

            BuildLayout();
            ResumeLayout(false);
            PerformLayout();

            updater = new Updater(engine);
            tray = new TrayUi(engine) { UpdateMenuText = () => updater.MenuText };
            tray.OpenRequested += ShowFromTray;
            tray.SettingsRequested += OpenSettings;
            tray.ExitRequested += ExitApp;
            tray.UpdateRequested += async () => await updater.CheckAsync(userInitiated: true);

            engine.Changed += (s, e) => UpdateUi();
            engine.NetworkChanged += (t, body) => { if (engine.Settings.NotifyChanges) tray.ShowBalloon(t, body); };
            updater.Changed += (s, e) => UpdateUi();
            updater.RestartRequested += RestartInto;
            Theme.Changed += (s, e) => { if (InvokeRequired) BeginInvoke((Action)ApplyTheme); else ApplyTheme(); };

            timer = new Timer { Interval = 500 };
            timer.Tick += async (s, e) =>
            {
                timer.Interval = Math.Max(3, engine.Settings.IntervalSeconds) * 1000;
                if (engine.LastScan == null && !engine.Busy) await engine.ScanAsync();   // first run
                await engine.TickAsync();
            };
            timer.Start();

            // React within ~1.5 s when Windows roams or reconnects, instead of waiting for the next periodic check.
            eventDebounce = new Timer { Interval = 1500 };
            eventDebounce.Tick += async (s, e) => { eventDebounce.Stop(); await engine.TickAsync(); };
            Wlan.ConnectionChanged += OnWifiEvent;
            Wlan.StartMonitoring();

            // Updates: first look a minute after start, then hourly (the updater itself checks at most once a day).
            updateTimer = new Timer { Interval = 60_000 };
            updateTimer.Tick += async (s, e) => { updateTimer.Interval = 3_600_000; await updater.MaybeCheckAsync(); };
            updateTimer.Start();

            if (justUpdated)
            {
                engine.AddLog("Updated to version " + Updater.Current);
                if (startHidden) tray.ShowBalloon("BSSIDLock was updated", "You're now on version " + Updater.Current + ".");
            }

            ApplyTheme();
            UpdateUi();
        }

        void OnWifiEvent()
        {
            // Called on a Wi-Fi service thread: hop to the UI thread and restart the debounce timer.
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke((Action)(() => { eventDebounce.Stop(); eventDebounce.Start(); }));
            }
            catch (InvalidOperationException) { /* closing */ }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(this);
        }

        // ---------------------------------------------------------------- layout

        void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(16, 14, 16, 10),
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // banner
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // header
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // list
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons

            root.Controls.Add(BuildBanner(), 0, 0);
            root.Controls.Add(BuildHeader(), 0, 1);
            root.Controls.Add(BuildList(), 0, 2);
            root.Controls.Add(BuildButtons(), 0, 3);

            status = new StatusStrip { SizingGrip = true, Padding = new Padding(8, 0, 4, 0) };
            footer = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            updateLink = new ToolStripStatusLabel { IsLink = true, Visible = false, Margin = new Padding(0, 3, 12, 2) };
            updateLink.Click += async (s, e) => await updater.CheckAsync(userInitiated: true);
            var activity = new ToolStripStatusLabel("Activity") { IsLink = true, Margin = new Padding(0, 3, 12, 2) };
            activity.Click += (s, e) => { using (var f = new ActivityForm(engine)) f.ShowDialog(this); };
            var settings = new ToolStripStatusLabel("Settings") { IsLink = true };
            settings.Click += (s, e) => OpenSettings();
            status.Items.AddRange(new ToolStripItem[] { footer, updateLink, activity, settings });

            Controls.Add(status);
            Controls.Add(root);
            root.BringToFront();
        }

        Control BuildBanner()
        {
            banner = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 8, 8, 8),
                Margin = new Padding(0, 0, 0, 12),
                AutoSize = true,
                Visible = false,
                Tag = "banner",
            };
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bannerText = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Tag = "banner", Margin = new Padding(0, 0, 12, 0) };
            bannerButton = new Button { AutoSize = true, Anchor = AnchorStyles.Right, Padding = new Padding(6, 0, 6, 0) };
            bannerButton.Click += (s, e) => bannerAction?.Invoke();
            row.Controls.Add(bannerText, 0, 0);
            row.Controls.Add(bannerButton, 1, 0);
            row.SizeChanged += (s, e) => bannerText.MaximumSize = new Size(Math.Max(100, row.Width - bannerButton.Width - 24), 0);
            banner.Controls.Add(row);
            return banner;
        }

        Control BuildHeader()
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 12),
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            badge = new StatusBadge { Size = new Size(52, 52), Margin = new Padding(0, 0, 14, 0), Anchor = AnchorStyles.Left };

            var text = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, AutoSize = true };
            title = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 14F), Margin = new Padding(0, 4, 0, 2) };
            detail = new Label { AutoSize = true, Tag = "subtle", Margin = new Padding(1, 0, 0, 0) };
            text.Controls.Add(title, 0, 0);
            text.Controls.Add(detail, 0, 1);
            text.SizeChanged += (s, e) => { title.MaximumSize = detail.MaximumSize = new Size(Math.Max(100, text.Width), 0); };

            var right = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, Anchor = AnchorStyles.Right };
            lockToggle = new CheckBox
            {
                Appearance = Appearance.Button,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(150, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9.5F),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 0, 8),
                Anchor = AnchorStyles.Right,
                Tag = "theme:skip",
            };
            lockToggle.FlatAppearance.BorderSize = 0;
            lockToggle.CheckedChanged += (s, e) => { if (!updating) engine.SetLockEnabled(lockToggle.Checked); };

            var lockToLabel = new Label { Text = "Lock to:", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 6, 6, 0) };
            lockTo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
            lockTo.SelectedIndexChanged += (s, e) =>
            {
                if (updating) return;
                engine.SetTarget((lockTo.SelectedItem as Choice)?.Bssid);
            };
            right.Controls.Add(lockToggle, 1, 0);
            right.Controls.Add(lockToLabel, 0, 1);
            right.Controls.Add(lockTo, 1, 1);

            header.Controls.Add(badge, 0, 0);
            header.Controls.Add(text, 1, 0);
            header.Controls.Add(right, 2, 0);
            return header;
        }

        Control BuildList()
        {
            // A 1px border around the list, drawn by the host panel's background.
            listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = new Padding(0), Tag = "theme:skip" };
            list = new AccessPointList(engine) { Dock = DockStyle.Fill };
            list.LockClicked += b => engine.LockTo(b);
            list.UnlockClicked += b => engine.SetTarget(null);
            list.FavoriteClicked += b => engine.ToggleFavorite(b);
            list.SelectedIndexChanged += (s, e) => UpdateButtons();
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.F2) RenameSelected(); };
            list.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                int i = list.IndexFromPoint(e.Location);
                if (i >= 0 && list.Items[i] is ListRow r && r.Type == ListRow.Kind.AccessPoint) list.SelectedIndex = i;
            };

            listMenu = new ContextMenuStrip { Renderer = new MenuRenderer() };
            listMenu.Opening += (s, e) => { e.Cancel = list.SelectedBssid == null; if (!e.Cancel) BuildListMenu(); };
            listMenu.Opened += (s, e) => Theme.RoundCorners(listMenu.Handle);
            list.ContextMenuStrip = listMenu;

            listHost.Controls.Add(list);
            return listHost;
        }

        Control BuildButtons()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            var tips = new ToolTip();

            Button Make(string text, string tip, EventHandler onClick)
            {
                var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 0, 6, 0) };
                b.Click += onClick;
                tips.SetToolTip(b, tip);
                return b;
            }

            btnRename = Make("Rename…", "Give the selected access point a name you'll recognize (F2)", (s, e) => RenameSelected());
            btnUp = Make("Move up", "Higher priority in \"Any favorite\" mode", (s, e) => { var b = list.SelectedBssid; if (b != null) engine.MoveFavorite(b, -1); });
            btnDown = Make("Move down", "Lower priority in \"Any favorite\" mode", (s, e) => { var b = list.SelectedBssid; if (b != null) engine.MoveFavorite(b, 1); });
            btnScan = Make("Scan", "Look for access points now", async (s, e) => await engine.ScanAsync());
            btnScan.Margin = new Padding(0);

            left.Controls.AddRange(new Control[] { btnRename, btnUp, btnDown });
            row.Controls.Add(left, 0, 0);
            row.Controls.Add(btnScan, 1, 0);
            return row;
        }

        void ApplyTheme()
        {
            if (IsDisposed) return;
            Theme.Apply(this);
            listHost.BackColor = Theme.Border;
            list.ApplyTheme();
            badge.Invalidate();
            listSignature = null;   // force a redraw with the new colors
            UpdateUi();
        }

        // ---------------------------------------------------------------- updates

        sealed class Choice
        {
            public string Bssid;
            public string Text;
            public override string ToString() => Text;
        }

        void UpdateUi()
        {
            if (IsDisposed) return;
            updating = true;
            try
            {
                var st = engine.Settings;

                badge.State = engine.State;
                title.Text = engine.Title;
                detail.Text = engine.Detail;

                lockToggle.Checked = st.LockEnabled;
                lockToggle.Text = st.LockEnabled ? "Lock is ON" : "Lock is OFF";
                lockToggle.BackColor = st.LockEnabled ? Theme.Green : Theme.Gray;
                lockToggle.FlatAppearance.MouseOverBackColor = Theme.Mix(lockToggle.BackColor, Color.White, 0.12f);

                var choices = new List<Choice> { new Choice { Bssid = null, Text = "Any favorite (top first)" } };
                choices.AddRange(st.Favorites.Select(b => new Choice { Bssid = b, Text = engine.Label(b) }));
                if (lockTo.Items.Count != choices.Count || !choices.Select(c => c.Text).SequenceEqual(lockTo.Items.Cast<Choice>().Select(c => c.Text)))
                {
                    lockTo.Items.Clear();
                    lockTo.Items.AddRange(choices.ToArray());
                }
                lockTo.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Bssid == st.Target));
                lockTo.Enabled = st.LockEnabled;

                if (engine.AccessDenied)
                    ShowBanner("Windows is hiding Wi-Fi details from BSSIDLock. Turn on Location services and \"Let desktop apps access your location\".",
                        "Open location settings", () => Process.Start("ms-settings:privacy-location"));
                else if (engine.LastError != null && engine.Current == null)
                    ShowBanner("Wi-Fi problem: " + engine.LastError, "Show activity", () => { using (var f = new ActivityForm(engine)) f.ShowDialog(this); });
                else if (st.Ssid.Length == 0)
                    ShowBanner("Choose which Wi-Fi network BSSIDLock should manage.", "Choose network…", OpenSettings);
                else
                    banner.Visible = false;

                RebuildList();

                footer.Text = engine.Busy ? "Scanning…"
                    : engine.LastScan.HasValue ? "Last scan " + Ago(engine.LastScan.Value) + " · checks every " + st.IntervalSeconds + " s"
                    : "Not scanned yet";
                updateLink.Visible = updater.Busy || updater.AvailableVersion != null;
                updateLink.Text = updater.Busy ? updater.Status : updater.AvailableVersion != null ? "Install version " + updater.AvailableVersion : "";
                btnScan.Enabled = !engine.Busy;

                tray.Update();
                UpdateButtons();
            }
            finally { updating = false; }
        }

        void ShowBanner(string text, string button, Action action)
        {
            bannerText.Text = text;
            bannerButton.Text = button;
            bannerAction = action;
            banner.Visible = true;
        }

        void RebuildList()
        {
            var st = engine.Settings;
            // Skip the rebuild (and any flicker) when nothing visible changed.
            string signature = string.Join("|", st.Favorites) + "#" + string.Join("|", engine.Visible.Select(v => v.Bssid + ":" + v.Rssi / 3))
                + "#" + engine.Current?.Bssid + "#" + st.Target + "#" + st.Ssid + "#" + engine.Busy
                + "#" + string.Join("|", st.Names.Select(kv => kv.Key + "=" + kv.Value));
            if (signature == listSignature) return;
            listSignature = signature;

            var rows = new List<ListRow>
            {
                new ListRow { Type = ListRow.Kind.Header, Text = "Favorites", Caption = st.Favorites.Count > 1 ? "top is preferred" : null }
            };
            if (st.Favorites.Count == 0)
                rows.Add(new ListRow { Type = ListRow.Kind.Hint, Text = "Click the star on access points you're happy to use, or press Lock to stick to just one." });
            for (int i = 0; i < st.Favorites.Count; i++)
                rows.Add(new ListRow { Type = ListRow.Kind.AccessPoint, Bssid = st.Favorites[i], Rank = i });

            rows.Add(new ListRow { Type = ListRow.Kind.Header, Text = st.Ssid.Length > 0 ? "Other access points on \"" + st.Ssid + "\"" : "Other access points" });
            var others = engine.Visible.Where(v => !st.Favorites.Contains(v.Bssid)).ToList();
            if (others.Count == 0)
                rows.Add(new ListRow
                {
                    Type = ListRow.Kind.Hint,
                    Text = engine.Busy ? "Scanning…"
                        : st.Ssid.Length == 0 ? "Choose a network first."
                        : engine.Visible.Count == 0 ? "No access points found yet. Press Scan."
                        : "Every access point in range is already a favorite.",
                });
            rows.AddRange(others.Select(ap => new ListRow { Type = ListRow.Kind.AccessPoint, Bssid = ap.Bssid }));

            list.SetRows(rows);
        }

        void UpdateButtons()
        {
            string b = list.SelectedBssid;
            int rank = b != null ? engine.Settings.Favorites.IndexOf(b) : -1;
            btnRename.Enabled = b != null;
            btnUp.Enabled = rank > 0;
            btnDown.Enabled = rank >= 0 && rank < engine.Settings.Favorites.Count - 1;
        }

        void BuildListMenu()
        {
            string b = list.SelectedBssid;
            int rank = engine.Settings.Favorites.IndexOf(b);
            listMenu.Items.Clear();
            listMenu.Items.Add(engine.Settings.Target == b ? "Unlock (accept any favorite)" : "Lock to this access point", null,
                (s, e) => { if (engine.Settings.Target == b) engine.SetTarget(null); else engine.LockTo(b); });
            listMenu.Items.Add(rank >= 0 ? "Remove from favorites" : "Add to favorites", null, (s, e) => engine.ToggleFavorite(b));
            listMenu.Items.Add("Rename…", null, (s, e) => RenameSelected());
            if (rank >= 0)
            {
                listMenu.Items.Add(new ToolStripSeparator());
                listMenu.Items.Add("Move up", null, (s, e) => engine.MoveFavorite(b, -1)).Enabled = rank > 0;
                listMenu.Items.Add("Move down", null, (s, e) => engine.MoveFavorite(b, 1)).Enabled = rank < engine.Settings.Favorites.Count - 1;
            }
            listMenu.Items.Add(new ToolStripSeparator());
            listMenu.Items.Add("Copy address", null, (s, e) => Clipboard.SetText(b));
            foreach (ToolStripItem it in listMenu.Items) it.Padding = new Padding(0, 2, 0, 2);
        }

        void RenameSelected()
        {
            string b = list.SelectedBssid;
            if (b == null) return;
            string name = Prompt.Show(this, "Name this access point",
                "Give " + b + " a name you'll recognize (e.g. Living room).\nLeave it empty to show the address.",
                engine.NameOf(b) ?? "");
            if (name != null) engine.Rename(b, name);
        }

        void OpenSettings()
        {
            ShowFromTray();
            using (var f = new SettingsForm(engine, updater))
                f.ShowDialog(this);
            UpdateUi();
        }

        // ---------------------------------------------------------------- window / tray

        public void ShowFromTray()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        void RestartInto(string exe)
        {
            try
            {
                string args = "--after-update " + Process.GetCurrentProcess().Id + (Visible ? "" : " --minimized");
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                engine.AddLog("Couldn't restart after updating: " + ex.Message);
                return;
            }
            ExitApp();
        }

        void ExitApp()
        {
            exiting = true;
            Wlan.ConnectionChanged -= OnWifiEvent;
            Wlan.StopMonitoring();
            tray.Dispose();
            Application.Exit();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                // Keep running in the tray; the lock only works while the app runs.
                e.Cancel = true;
                Hide();
                if (!engine.Settings.TrayHintShown)
                {
                    tray.ShowBalloon("BSSIDLock is still running", "It keeps your lock active from the notification area. Click the icon for quick options.");
                    engine.Settings.TrayHintShown = true;
                    engine.Settings.Save();
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer?.Dispose(); eventDebounce?.Dispose(); updateTimer?.Dispose(); }
            base.Dispose(disposing);
        }

        static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 60) return (int)d.TotalSeconds + " s ago";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
            return t.ToString("t");
        }
    }

    /// <summary>Round tinted badge showing the lock state (matches the macOS app).</summary>
    sealed class StatusBadge : Control
    {
        LockState state = LockState.Idle;
        Font glyphFont;

        public StatusBadge()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Tag = "theme:skip";
        }

        public LockState State
        {
            get => state;
            set { if (state != value) { state = value; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var color = Glyphs.StateColor(state);
            using (var fill = new SolidBrush(Color.FromArgb(Theme.Dark ? 70 : 40, color)))
                g.FillEllipse(fill, 0, 0, Width - 1, Height - 1);
            if (glyphFont == null || Math.Abs(glyphFont.Size - Height * 0.36f) > 0.5f)
            {
                glyphFont?.Dispose();
                glyphFont = new Font("Segoe MDL2 Assets", Height * 0.36f, GraphicsUnit.Pixel);
            }
            TextRenderer.DrawText(g, Glyphs.StateGlyph(state), glyphFont, new Rectangle(0, 1, Width, Height), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) glyphFont?.Dispose();
            base.Dispose(disposing);
        }
    }
}
