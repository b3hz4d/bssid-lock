// Main window + tray icon.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BSSIDLock
{
    public sealed class MainForm : Form
    {
        readonly Engine engine;
        readonly NotifyIcon tray;
        readonly Timer timer;
        bool startHidden;
        bool exiting;
        bool updating;

        // Header
        Label badge, title, detail;
        CheckBox lockToggle;
        ComboBox lockTo;
        // Banner (location / choose network)
        Panel banner;
        Label bannerText;
        Button bannerButton;
        Action bannerAction;
        // List and actions
        ListView list;
        ListViewGroup favGroup, otherGroup;
        Button btnLock, btnFav, btnRename, btnUp, btnDown, btnScan;
        ContextMenuStrip listMenu;
        // Footer
        ToolStripStatusLabel footer;
        Font boldFont;

        static readonly Color Green = Color.FromArgb(22, 163, 74);
        static readonly Color Blue = Color.FromArgb(37, 99, 235);
        static readonly Color Orange = Color.FromArgb(217, 119, 6);
        static readonly Color Gray = Color.FromArgb(107, 114, 128);

        public MainForm(Engine engine, bool startHidden)
        {
            this.engine = engine;
            this.startHidden = startHidden;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "BSSIDLock";
            Icon = AppIcon.Load(32);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 560);
            MinimumSize = new Size(680, 460);

            BuildLayout();
            ResumeLayout(false);
            PerformLayout();

            tray = new NotifyIcon { Icon = AppIcon.Load(16), Text = "BSSIDLock", Visible = true };
            tray.ContextMenuStrip = new ContextMenuStrip();
            tray.ContextMenuStrip.Opening += (s, e) => BuildTrayMenu(tray.ContextMenuStrip);
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };

            engine.Changed += (s, e) => UpdateUi();

            timer = new Timer { Interval = 500 };
            timer.Tick += async (s, e) =>
            {
                timer.Interval = Math.Max(3, engine.Settings.IntervalSeconds) * 1000;
                if (engine.LastScan == null && !engine.Busy) await engine.ScanAsync();   // first run
                await engine.TickAsync();
            };
            timer.Start();
            UpdateUi();
        }

        int S(int px) => (int)Math.Round(px * DeviceDpi / 96f);

        // ---------------------------------------------------------------- layout

        void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(14, 12, 14, 8),
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // banner
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // header
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // list
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons

            root.Controls.Add(BuildBanner(), 0, 0);
            root.Controls.Add(BuildHeader(), 0, 1);
            root.Controls.Add(BuildList(), 0, 2);
            root.Controls.Add(BuildButtons(), 0, 3);

            var status = new StatusStrip { SizingGrip = true };
            footer = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            var activity = new ToolStripStatusLabel("Activity") { IsLink = true };
            activity.Click += (s, e) => new ActivityForm(engine).ShowDialog(this);
            var settings = new ToolStripStatusLabel("Settings") { IsLink = true };
            settings.Click += (s, e) => OpenSettings();
            status.Items.AddRange(new ToolStripItem[] { footer, activity, settings });

            Controls.Add(status);
            Controls.Add(root);
            root.BringToFront();
        }

        Control BuildBanner()
        {
            banner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(255, 247, 214),
                Padding = new Padding(10, 8, 10, 8),
                Margin = new Padding(0, 0, 0, 10),
                AutoSize = true,
                Visible = false,
            };
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bannerText = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.FromArgb(120, 53, 15) };
            bannerButton = new Button { AutoSize = true, Anchor = AnchorStyles.Right };
            bannerButton.Click += (s, e) => bannerAction?.Invoke();
            row.Controls.Add(bannerText, 0, 0);
            row.Controls.Add(bannerButton, 1, 0);
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
                Margin = new Padding(0, 0, 0, 10),
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            badge = new Label
            {
                Font = new Font("Segoe MDL2 Assets", 20F),
                Size = new Size(52, 52),
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0, 0, 12, 0),
            };

            var text = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right };
            title = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 13F), Margin = new Padding(0, 4, 0, 2) };
            detail = new Label { AutoSize = true, ForeColor = Gray, Margin = new Padding(0) };
            text.Controls.Add(title, 0, 0);
            text.Controls.Add(detail, 0, 1);
            text.SizeChanged += (s, e) => { title.MaximumSize = detail.MaximumSize = new Size(Math.Max(100, text.Width), 0); };

            var right = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, Anchor = AnchorStyles.Right };
            lockToggle = new CheckBox
            {
                Appearance = Appearance.Button,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(150, 32),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9.5F),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 0, 6),
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
            lockToggle.Anchor = AnchorStyles.Right;

            header.Controls.Add(badge, 0, 0);
            header.Controls.Add(text, 1, 0);
            header.Controls.Add(right, 2, 0);
            return header;
        }

        Control BuildList()
        {
            list = new BufferedListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                ShowGroups = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
            };
            list.Columns.Add("Access point", 210);
            list.Columns.Add("Address (BSSID)", 140);
            list.Columns.Add("Signal", 150);
            list.Columns.Add("Band", 70);
            list.Columns.Add("Channel", 64);
            list.Columns.Add("Status", 140);
            favGroup = new ListViewGroup("fav", "Favorites");
            otherGroup = new ListViewGroup("other", "Other access points");
            list.Groups.Add(favGroup);
            list.Groups.Add(otherGroup);
            list.SelectedIndexChanged += (s, e) => UpdateButtons();
            list.DoubleClick += (s, e) => { var b = SelectedBssid(); if (b != null) engine.LockTo(b); };
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.F2) RenameSelected(); };

            listMenu = new ContextMenuStrip();
            listMenu.Opening += (s, e) => { e.Cancel = SelectedBssid() == null; if (!e.Cancel) BuildListMenu(); };
            list.ContextMenuStrip = listMenu;
            HandleCreated += (s, e) =>
            {
                int[] widths = { 210, 140, 150, 70, 64, 140 };
                for (int i = 0; i < widths.Length; i++) list.Columns[i].Width = S(widths[i]);
            };
            return list;
        }

        Control BuildButtons()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };

            Button Make(string text, string tip, EventHandler onClick)
            {
                var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 0, 6, 0) };
                b.Click += onClick;
                new ToolTip().SetToolTip(b, tip);
                return b;
            }

            btnLock = Make("Lock to selected", "Stick to this access point only (double-click a row does the same)", (s, e) => { var b = SelectedBssid(); if (b != null) engine.LockTo(b); });
            btnFav = Make("☆ Add to favorites", "Favorites are accepted in \"Any favorite\" mode, top first", (s, e) => { var b = SelectedBssid(); if (b != null) engine.ToggleFavorite(b); });
            btnRename = Make("Rename…", "Give this access point a name you'll recognize (F2)", (s, e) => RenameSelected());
            btnUp = Make("↑ Up", "Move up (higher priority)", (s, e) => { var b = SelectedBssid(); if (b != null) engine.MoveFavorite(b, -1); });
            btnDown = Make("↓ Down", "Move down (lower priority)", (s, e) => { var b = SelectedBssid(); if (b != null) engine.MoveFavorite(b, 1); });
            btnScan = Make("Scan", "Look for access points now", async (s, e) => await engine.ScanAsync());
            btnScan.Margin = new Padding(0);

            left.Controls.AddRange(new Control[] { btnLock, btnFav, btnRename, btnUp, btnDown });
            row.Controls.Add(left, 0, 0);
            row.Controls.Add(btnScan, 1, 0);
            return row;
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

                // Header
                (string glyph, Color color) look;
                switch (engine.State)
                {
                    case LockState.Locked: look = ("", Green); break;
                    case LockState.Working: look = ("", Blue); break;
                    case LockState.Attention: look = ("", Orange); break;
                    case LockState.Off: look = ("", Gray); break;
                    default: look = ("", Gray); break;
                }
                badge.Text = look.glyph;
                badge.ForeColor = look.color;
                title.Text = engine.Title;
                detail.Text = engine.Detail;

                lockToggle.Checked = st.LockEnabled;
                lockToggle.Text = st.LockEnabled ? "Lock is ON" : "Lock is OFF";
                lockToggle.BackColor = st.LockEnabled ? Green : Gray;

                var choices = new List<Choice> { new Choice { Bssid = null, Text = "Any favorite (top first)" } };
                choices.AddRange(st.Favorites.Select(b => new Choice { Bssid = b, Text = engine.Label(b) }));
                if (lockTo.Items.Count != choices.Count || !choices.Select(c => c.Text).SequenceEqual(lockTo.Items.Cast<Choice>().Select(c => c.Text)))
                {
                    lockTo.Items.Clear();
                    lockTo.Items.AddRange(choices.ToArray());
                }
                lockTo.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Bssid == st.Target));
                lockTo.Enabled = st.LockEnabled;

                // Banner
                if (engine.AccessDenied)
                    ShowBanner("Windows is hiding Wi-Fi details. Turn on Location services and \"Let desktop apps access your location\".",
                        "Open location settings", () => Process.Start("ms-settings:privacy-location"));
                else if (st.Ssid.Length == 0)
                    ShowBanner("Choose which Wi-Fi network BSSIDLock should manage.", "Choose network…", OpenSettings);
                else
                    banner.Visible = false;

                otherGroup.Header = st.Ssid.Length > 0 ? "Other access points on \"" + st.Ssid + "\"" : "Other access points";
                RebuildList();

                footer.Text = engine.Busy ? "Scanning…"
                    : engine.LastScan.HasValue ? "Last scan " + Ago(engine.LastScan.Value) + " · checks every " + st.IntervalSeconds + " s"
                    : "Not scanned yet";
                btnScan.Enabled = !engine.Busy;

                tray.Text = Truncate("BSSIDLock: " + engine.Title, 63);
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
            string selected = SelectedBssid();
            var st = engine.Settings;
            list.BeginUpdate();
            list.Items.Clear();
            for (int i = 0; i < st.Favorites.Count; i++)
                list.Items.Add(MakeItem(st.Favorites[i], i, favGroup));
            foreach (var ap in engine.Visible.Where(v => !st.Favorites.Contains(v.Bssid)))
                list.Items.Add(MakeItem(ap.Bssid, -1, otherGroup));
            foreach (ListViewItem it in list.Items)
                if ((string)it.Tag == selected) { it.Selected = true; it.Focused = true; }
            list.EndUpdate();
        }

        ListViewItem MakeItem(string bssid, int rank, ListViewGroup group)
        {
            var ap = engine.Find(bssid);
            string name = engine.NameOf(bssid);
            string first = (rank >= 0 ? (rank + 1) + ".  ★ " : "☆ ") + (name ?? "(unnamed)");
            var status = new List<string>();
            if (engine.Current != null && engine.Current.Bssid == bssid) status.Add("Connected");
            if (engine.Settings.Target == bssid) status.Add("Locked");

            var it = new ListViewItem(first) { Tag = bssid, Group = group };
            it.SubItems.Add(bssid);
            it.SubItems.Add(ap != null ? Signal.Quality(ap.Rssi) + "  (" + ap.Rssi + " dBm)" : "Out of range");
            it.SubItems.Add(ap?.Band ?? "");
            it.SubItems.Add(ap != null ? ap.Channel.ToString() : "");
            it.SubItems.Add(string.Join(", ", status));
            if (ap == null) it.ForeColor = Gray;
            if (status.Contains("Connected")) it.Font = boldFont ?? (boldFont = new Font(list.Font, FontStyle.Bold));
            return it;
        }

        string SelectedBssid() => list.SelectedItems.Count > 0 ? (string)list.SelectedItems[0].Tag : null;

        void UpdateButtons()
        {
            string b = SelectedBssid();
            int rank = b != null ? engine.Settings.Favorites.IndexOf(b) : -1;
            btnLock.Enabled = b != null && !engine.Busy;
            btnFav.Enabled = btnRename.Enabled = b != null;
            btnFav.Text = rank >= 0 ? "★ Remove from favorites" : "☆ Add to favorites";
            btnUp.Enabled = rank > 0;
            btnDown.Enabled = rank >= 0 && rank < engine.Settings.Favorites.Count - 1;
        }

        void BuildListMenu()
        {
            string b = SelectedBssid();
            int rank = engine.Settings.Favorites.IndexOf(b);
            listMenu.Items.Clear();
            listMenu.Items.Add("Lock to this access point", null, (s, e) => engine.LockTo(b));
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
        }

        void RenameSelected()
        {
            string b = SelectedBssid();
            if (b == null) return;
            string name = Prompt.Show(this, "Name this access point",
                "Give " + b + " a name you'll recognize (e.g. Living room).\nLeave it empty to show the address.",
                engine.NameOf(b) ?? "");
            if (name != null) engine.Rename(b, name);
        }

        void OpenSettings()
        {
            ShowFromTray();
            using (var f = new SettingsForm(engine))
                if (f.ShowDialog(this) == DialogResult.OK) UpdateUi();
        }

        // ---------------------------------------------------------------- tray

        void BuildTrayMenu(ContextMenuStrip menu)
        {
            var st = engine.Settings;
            menu.Items.Clear();
            menu.Items.Add(new ToolStripMenuItem(Truncate(engine.Title, 60)) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());

            var lockItem = new ToolStripMenuItem("Lock enabled") { Checked = st.LockEnabled };
            lockItem.Click += (s, e) => engine.SetLockEnabled(!st.LockEnabled);
            menu.Items.Add(lockItem);

            var to = new ToolStripMenuItem("Lock to");
            var any = new ToolStripMenuItem("Any favorite (top first)") { Checked = st.Target == null, Enabled = st.Favorites.Count > 0 };
            any.Click += (s, e) => { engine.SetLockEnabled(true); engine.SetTarget(null); };
            to.DropDownItems.Add(any);
            to.DropDownItems.Add(new ToolStripSeparator());
            var candidates = st.Favorites.Concat(engine.Visible.Select(v => v.Bssid).Where(b => !st.Favorites.Contains(b))).Take(10);
            foreach (var b in candidates)
            {
                var ap = engine.Find(b);
                var item = new ToolStripMenuItem(engine.Label(b) + (ap != null ? "   " + Signal.Quality(ap.Rssi) : "   (out of range)"))
                { Checked = st.Target == b };
                string bssid = b;
                item.Click += (s, e) => engine.LockTo(bssid);
                to.DropDownItems.Add(item);
            }
            to.Enabled = st.Ssid.Length > 0;
            menu.Items.Add(to);
            menu.Items.Add("Scan now", null, async (s, e) => await engine.ScanAsync()).Enabled = !engine.Busy;
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open BSSIDLock", null, (s, e) => ShowFromTray()).Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add("Settings…", null, (s, e) => OpenSettings());
            menu.Items.Add("Exit", null, (s, e) => ExitApp());
        }

        public void ShowFromTray()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        void ExitApp()
        {
            exiting = true;
            tray.Visible = false;
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
                    tray.ShowBalloonTip(5000, "BSSIDLock is still running",
                        "It keeps your lock active from the notification area. Right-click the icon to exit.", ToolTipIcon.Info);
                    engine.Settings.TrayHintShown = true;
                    engine.Settings.Save();
                }
                return;
            }
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer?.Dispose(); tray?.Dispose(); boldFont?.Dispose(); }
            base.Dispose(disposing);
        }

        static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 60) return (int)d.TotalSeconds + " s ago";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
            return t.ToString("t");
        }

        static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }

    /// <summary>ListView with double buffering (no flicker when rows refresh).</summary>
    sealed class BufferedListView : ListView
    {
        public BufferedListView() { DoubleBuffered = true; }
    }
}
