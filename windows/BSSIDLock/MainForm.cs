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
        string listSignature, emptyKey;
        bool startHidden;
        bool exiting;
        bool updating;

        // Header
        StatusBadge badge;
        Label title, detail;
        ToggleSwitch lockSwitch;
        // Toolbar
        Label lockToLabel;
        DropDownButton lockTo;
        UiButton scanButton;
        // Content
        Panel listHost;
        AccessPointList list;
        EmptyState empty;
        ContextMenuStrip listMenu;
        // Footer
        Label footer;
        LinkLabel updateLink;

        public MainForm(Engine engine, bool startHidden, bool justUpdated)
        {
            this.engine = engine;
            this.startHidden = startHidden;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9.5F);
            Text = "BSSIDLock";
            Icon = AppIcon.Load(small: false);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(840, 600);
            MinimumSize = new Size(700, 480);

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
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(22, 18, 22, 12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // header
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // toolbar
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // list / message
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // footer
            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildToolbar(), 0, 1);
            root.Controls.Add(BuildContent(), 0, 2);
            root.Controls.Add(BuildFooter(), 0, 3);
            Controls.Add(root);
        }

        Control BuildHeader()
        {
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Margin = new Padding(0) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            badge = new StatusBadge { Size = new Size(54, 54), Margin = new Padding(0, 0, 16, 0), Anchor = AnchorStyles.Left };

            var text = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, AutoSize = true, Margin = new Padding(0) };
            title = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 15F), Margin = new Padding(0, 4, 0, 2) };
            detail = new Label { AutoSize = true, Tag = "subtle", Margin = new Padding(2, 0, 0, 0) };
            text.Controls.Add(title, 0, 0);
            text.Controls.Add(detail, 0, 1);
            text.SizeChanged += (s, e) => { title.MaximumSize = detail.MaximumSize = new Size(Math.Max(100, text.Width), 0); };

            lockSwitch = new ToggleSwitch { Text = "Lock", Font = new Font("Segoe UI Semibold", 11F), Anchor = AnchorStyles.Right, Margin = new Padding(12, 0, 0, 0) };
            lockSwitch.CheckedChanged += (s, e) => { if (!updating) engine.SetLockEnabled(lockSwitch.Checked); };
            new ToolTip().SetToolTip(lockSwitch, "When on, BSSIDLock keeps you on the access point(s) you chose");

            header.Controls.Add(badge, 0, 0);
            header.Controls.Add(text, 1, 0);
            header.Controls.Add(lockSwitch, 2, 0);
            return header;
        }

        Control BuildToolbar()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 16, 0, 12) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var left = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
            lockToLabel = new Label { Text = "Lock to", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) };
            lockTo = new DropDownButton { MinWidth = 240, Margin = new Padding(0), EmptyText = "Star access points to add them here" };
            lockTo.SelectionChanged += (s, e) => engine.SetTarget(lockTo.SelectedValue as string);
            left.Controls.Add(lockToLabel);
            left.Controls.Add(lockTo);
            left.Layout += (s, e) => lockToLabel.Margin = new Padding(0, (lockTo.Height - lockToLabel.Height) / 2, 8, 0);

            var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Right };
            scanButton = new UiButton { Glyph = Ui.GlyphRefresh, Text = "Scan" };
            scanButton.Click += async (s, e) => await engine.ScanAsync();
            var activity = new UiButton { Glyph = Ui.GlyphHistory, Text = "Activity" };
            activity.Click += (s, e) => ShowActivity();
            var settings = new UiButton { Glyph = Ui.GlyphSettings, Text = "Settings", Margin = new Padding(0) };
            settings.Click += (s, e) => OpenSettings();
            right.Controls.AddRange(new Control[] { scanButton, activity, settings });

            bar.Controls.Add(left, 0, 0);
            bar.Controls.Add(right, 1, 0);
            return bar;
        }

        Control BuildContent()
        {
            var host = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };

            // A 1 px border around the list, drawn by the host panel's background.
            listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), Tag = "theme:skip" };
            list = new AccessPointList(engine) { Dock = DockStyle.Fill };
            list.LockClicked += b => engine.LockTo(b);
            list.UnlockClicked += b => engine.SetTarget(null);
            list.FavoriteClicked += b => engine.ToggleFavorite(b);
            list.MoveClicked += (b, d) => engine.MoveFavorite(b, d);
            list.MoreClicked += (b, p) => { list.Select(b); listMenu.Show(list, p); };
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

            empty = new EmptyState { Dock = DockStyle.Fill, Visible = false };

            host.Controls.Add(listHost);
            host.Controls.Add(empty);
            return host;
        }

        Control BuildFooter()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer = new Label { AutoSize = true, Tag = "subtle", Anchor = AnchorStyles.Left, Margin = new Padding(2, 0, 0, 0) };
            updateLink = new LinkLabel { AutoSize = true, Anchor = AnchorStyles.Right, Visible = false, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0) };
            updateLink.LinkClicked += async (s, e) => await updater.CheckAsync(userInitiated: true);
            bar.Controls.Add(footer, 0, 0);
            bar.Controls.Add(updateLink, 1, 0);
            return bar;
        }

        void ApplyTheme()
        {
            if (IsDisposed) return;
            Theme.Apply(this);
            listHost.BackColor = Theme.Border;
            list.ApplyTheme();
            listSignature = null;   // redraw rows with the new colors
            UpdateUi();
        }

        // ---------------------------------------------------------------- updates

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
                lockSwitch.Checked = st.LockEnabled;

                var options = new List<KeyValuePair<string, object>> { new KeyValuePair<string, object>("Any favorite (top first)", null) };
                options.AddRange(st.Favorites.Select(b => new KeyValuePair<string, object>(engine.Label(b), b)));
                lockTo.SetOptions(options);
                lockTo.SelectedValue = st.Target;
                lockTo.Enabled = lockToLabel.Enabled = st.LockEnabled;
                scanButton.Enabled = !engine.Busy;
                scanButton.Text = engine.Busy ? "Scanning…" : "Scan";

                UpdateContent();

                footer.Text = engine.LastScan.HasValue
                    ? "Last scan " + Ago(engine.LastScan.Value) + "  ·  Wi-Fi changes are handled instantly, with a safety check every " + st.IntervalSeconds + " s"
                    : engine.Busy ? "Scanning…" : "";
                updateLink.Visible = updater.Busy || updater.AvailableVersion != null;
                updateLink.Text = updater.Busy ? updater.Status : updater.AvailableVersion != null ? "Install version " + updater.AvailableVersion : "";

                tray.Update();
            }
            finally { updating = false; }
        }

        /// <summary>Show the access point list, or a friendly message when something is needed first.</summary>
        void UpdateContent()
        {
            var st = engine.Settings;
            string key =
                engine.AccessDenied ? "denied"
                : engine.LastError != null && engine.Current == null ? "error:" + engine.LastError
                : st.Ssid.Length == 0 ? "choose:" + engine.Current?.Ssid
                : null;

            if (key != emptyKey)
            {
                emptyKey = key;
                if (key == "denied")
                {
                    empty.SetContent(Ui.GlyphLocation, Theme.Orange, "Allow location access",
                        "Windows hides Wi-Fi details from apps until you allow location access. BSSIDLock never uses your actual location; it only needs the access point addresses.",
                        MakeButton("Open location settings", UiButton.Kind.Primary, () => Process.Start("ms-settings:privacy-location")),
                        MakeButton("Try again", UiButton.Kind.Secondary, TryAgain));
                }
                else if (key != null && key.StartsWith("error:"))
                {
                    bool noAdapter = engine.LastError.StartsWith("No Wi-Fi adapter");
                    empty.SetContent(noAdapter ? Ui.GlyphWifi : Ui.GlyphWarning, noAdapter ? Theme.Gray : Theme.Orange,
                        noAdapter ? "No Wi-Fi adapter found" : "Wi-Fi isn't available",
                        noAdapter
                            ? "BSSIDLock needs a Wi-Fi adapter. If Windows is running in a virtual machine (Parallels, VMware, VirtualBox), it can't see your computer's Wi-Fi; run BSSIDLock on a PC with Wi-Fi instead."
                            : engine.LastError,
                        MakeButton("Try again", UiButton.Kind.Primary, TryAgain),
                        MakeButton("Show activity", UiButton.Kind.Secondary, ShowActivity));
                }
                else if (key != null)
                {
                    var current = engine.Current?.Ssid;
                    var buttons = new List<UiButton>();
                    if (!string.IsNullOrEmpty(current))
                    {
                        buttons.Add(MakeButton("Use “" + current + "”", UiButton.Kind.Primary, () => engine.SetSsid(current)));
                        buttons.Add(MakeButton("Choose another…", UiButton.Kind.Secondary, OpenSettings));
                    }
                    else buttons.Add(MakeButton("Choose network…", UiButton.Kind.Primary, OpenSettings));
                    empty.SetContent(Ui.GlyphWifi, Theme.Accent, "Which network should BSSIDLock manage?",
                        "It only acts while you're connected to that network and never touches any other.", buttons.ToArray());
                }
                Theme.Apply(this);   // style the new message
                listHost.BackColor = Theme.Border;
            }

            empty.Visible = key != null;
            listHost.Visible = key == null;
            if (key == null) RebuildList();
        }

        UiButton MakeButton(string text, UiButton.Kind kind, Action onClick)
        {
            var b = new UiButton { Text = text, Style = kind, MinWidth = 120 };
            b.Click += (s, e) => onClick();
            return b;
        }

        async void TryAgain()
        {
            await engine.ScanAsync();
            await engine.TickAsync();
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
                new ListRow { Type = ListRow.Kind.Header, Text = "Favorites", Caption = st.Favorites.Count > 1 ? "the top one is preferred" : null }
            };
            if (st.Favorites.Count == 0)
                rows.Add(new ListRow { Type = ListRow.Kind.Hint, Text = "Click the star on access points you're happy to use, or press Lock to stick to just one." });
            for (int i = 0; i < st.Favorites.Count; i++)
                rows.Add(new ListRow { Type = ListRow.Kind.AccessPoint, Bssid = st.Favorites[i], Rank = i });

            rows.Add(new ListRow { Type = ListRow.Kind.Header, Text = "Other access points on “" + st.Ssid + "”" });
            var others = engine.Visible.Where(v => !st.Favorites.Contains(v.Bssid)).ToList();
            if (others.Count == 0)
                rows.Add(new ListRow
                {
                    Type = ListRow.Kind.Hint,
                    Text = engine.Busy ? "Scanning…"
                        : engine.Visible.Count == 0 ? "No access points found yet. Press Scan."
                        : "Every access point in range is already a favorite.",
                });
            rows.AddRange(others.Select(ap => new ListRow { Type = ListRow.Kind.AccessPoint, Bssid = ap.Bssid }));

            list.SetRows(rows);
        }

        void BuildListMenu()
        {
            string b = list.SelectedBssid;
            int rank = engine.Settings.Favorites.IndexOf(b);
            listMenu.Items.Clear();
            listMenu.Items.Add(engine.Settings.Target == b ? "Unlock (accept any favorite)" : "Lock to this access point", null,
                (s, e) => { if (engine.Settings.Target == b) engine.SetTarget(null); else engine.LockTo(b); });
            listMenu.Items.Add(rank >= 0 ? "Remove from favorites" : "Add to favorites", null, (s, e) => engine.ToggleFavorite(b));
            ((ToolStripMenuItem)listMenu.Items.Add("Rename…", null, (s, e) => RenameSelected())).ShortcutKeyDisplayString = "F2";
            if (rank >= 0)
            {
                listMenu.Items.Add(new ToolStripSeparator());
                var up = listMenu.Items.Add("Move up", null, (s, e) => engine.MoveFavorite(b, -1));
                up.Enabled = rank > 0;
                ((ToolStripMenuItem)up).ShortcutKeyDisplayString = "Alt+Up";
                var down = listMenu.Items.Add("Move down", null, (s, e) => engine.MoveFavorite(b, 1));
                down.Enabled = rank < engine.Settings.Favorites.Count - 1;
                ((ToolStripMenuItem)down).ShortcutKeyDisplayString = "Alt+Down";
            }
            listMenu.Items.Add(new ToolStripSeparator());
            listMenu.Items.Add("Copy address", null, (s, e) => Clipboard.SetText(b));
            foreach (ToolStripItem it in listMenu.Items) it.Padding = new Padding(0, 3, 0, 3);
        }

        void RenameSelected()
        {
            string b = list.SelectedBssid;
            if (b == null) return;
            string name = Prompt.Show(this, "Name this access point",
                "Give " + b + " a name you'll recognize, like “Living room”. Leave it empty to show the address.",
                engine.NameOf(b) ?? "");
            if (name != null) engine.Rename(b, name);
        }

        void ShowActivity()
        {
            using (var f = new ActivityForm(engine)) f.ShowDialog(this);
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
            return "at " + t.ToString("t");
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
