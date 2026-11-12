// Settings, activity log, a text prompt, and the app icon loader.
using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BSSIDLock
{
    static class AppIcon
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        /// <summary>
        /// The app icon from the exe's own icon resource, at the system's small (tray) or large (window) size.
        /// Reading it from the exe means the icon is stored only once in the file.
        /// </summary>
        public static Icon Load(bool small)
        {
            var large = new IntPtr[1];
            var smallIcons = new IntPtr[1];
            try
            {
                if (ExtractIconEx(Application.ExecutablePath, 0, large, smallIcons, 1) == 0) return SystemIcons.Application;
                IntPtr handle = small ? smallIcons[0] : large[0];
                if (handle == IntPtr.Zero) return SystemIcons.Application;
                using (var temp = Icon.FromHandle(handle)) return (Icon)temp.Clone();   // Clone copies, so we can free the handle
            }
            finally
            {
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (smallIcons[0] != IntPtr.Zero) DestroyIcon(smallIcons[0]);
            }
        }
    }

    abstract class DialogBase : Form
    {
        EventHandler themeHandler;

        protected DialogBase(string text)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9.5F);
            Text = text;
            Icon = AppIcon.Load(small: true);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
        }

        /// <summary>Call at the end of the subclass constructor.</summary>
        protected void Finish()
        {
            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.Apply(this);
            OnThemeApplied();
            themeHandler = (s, a) => { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => { Theme.Apply(this); OnThemeApplied(); })); };
            Theme.Changed += themeHandler;
        }

        /// <summary>Called after the theme is applied (first show and every theme change).</summary>
        protected virtual void OnThemeApplied() { }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (themeHandler != null) Theme.Changed -= themeHandler;
            base.OnHandleDestroyed(e);
        }

        protected static FlowLayoutPanel Buttons(params UiButton[] buttons)
        {
            var bar = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
            foreach (var b in buttons) { b.MinWidth = 96; b.Margin = new Padding(8, 0, 0, 0); bar.Controls.Add(b); }
            return bar;
        }
    }

    sealed class SettingsForm : DialogBase
    {
        readonly Engine engine;
        readonly Updater updater;
        readonly ScrollHost host;
        readonly StackPanel stack;
        readonly Panel footer;
        readonly FieldBox ssid, manual;
        readonly DropDownButton nearby, interval, weak;
        readonly SettingsCard networksCard;
        readonly SettingRow addNetworkRow;
        readonly UiButton addNetwork;
        readonly ToggleSwitch startup, notify, autoUpdate;
        readonly SegmentedControl appearance;
        readonly SettingRow versionRow;
        readonly UiButton checkNow, save, cancel;

        static readonly string[] ThemeKeys = { "system", "light", "dark" };

        public SettingsForm(Engine engine, Updater updater) : base("BSSIDLock Settings")
        {
            this.engine = engine;
            this.updater = updater;
            var st = engine.Settings;
            AutoScaleMode = AutoScaleMode.None;   // everything here is sized from the font, which already follows display scaling
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.Manual;

            stack = new StackPanel { Location = Point.Empty };
            stack.SuspendLayout();
            host = new ScrollHost(stack) { Dock = DockStyle.Fill };

            // ---- Networks
            ssid = new FieldBox { DesignWidth = 180, Placeholder = "Network name" };
            nearby = new DropDownButton { Placeholder = "Nearby", EmptyText = "No networks found yet" };
            nearby.SetOptions(engine.Nearby.Select(n => new KeyValuePair<string, object>(n.Ssid, n.Ssid)));
            nearby.SelectedValue = null;
            nearby.SelectionChanged += (s, e) => { if (nearby.SelectedValue is string picked) ssid.Text = picked; nearby.SelectedValue = null; };
            nearby.ExtraItems = () =>
            {
                if (engine.Current == null) return Enumerable.Empty<ToolStripItem>();
                var current = engine.Current.Ssid;
                var item = new ToolStripMenuItem("Use the network I'm on (“" + current + "”)") { Padding = new Padding(0, 4, 0, 4) };
                item.Click += (s, e) => ssid.Text = current;
                return new ToolStripItem[] { item };
            };
            string why =
                engine.AccessDenied ? " Windows is hiding nearby networks until you allow location access; you can still type the name."
                : engine.Current == null ? " You're not connected to Wi-Fi right now; you can type the name."
                : "";
            addNetwork = new UiButton { Text = "Add", MinWidth = 64, Enabled = false };
            ssid.Box.TextChanged += (s, e) => addNetwork.Enabled = ssid.Text.Trim().Length > 0 && !engine.Settings.Manages(ssid.Text.Trim());
            ssid.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { addNetwork.PerformClick(); e.SuppressKeyPress = true; } };
            addNetwork.Click += (s, e) =>
            {
                if (!addNetwork.Enabled) return;
                engine.AddNetwork(ssid.Text);
                ssid.Text = "";
                RebuildNetworks();
            };
            addNetworkRow = new SettingRow("Add a network",
                "Windows must already know it (connect to it once normally)." + why, new HStack(ssid, nearby, addNetwork));
            stack.Controls.Add(new SectionHeading("Networks") { Margin = new Padding(0, 10, 0, 8) });
            networksCard = new SettingsCard { Margin = new Padding(0, 0, 0, 10) };
            stack.Controls.Add(networksCard);
            FillNetworks();

            // ---- Behavior
            interval = new DropDownButton { MinWidth = 170 };
            var seconds = new List<int> { 5, 10, 15, 30, 60 };
            if (!seconds.Contains(st.IntervalSeconds)) seconds.Add(st.IntervalSeconds);
            interval.SetOptions(seconds.OrderBy(x => x).Select(x => new KeyValuePair<string, object>("Every " + x + " seconds", x)));
            interval.SelectedValue = st.IntervalSeconds;
            interval.ExtraItems = () =>
            {
                var custom = new ToolStripMenuItem("Custom…") { Padding = new Padding(0, 4, 0, 4) };
                custom.Click += (s, e) => AskCustomInterval();
                return new ToolStripItem[] { custom };
            };
            weak = new DropDownButton { MinWidth = 190 };
            var floors = new List<int> { 0, -70, -75, -80 };
            if (!floors.Contains(st.WeakThreshold)) floors.Add(st.WeakThreshold);
            weak.SetOptions(floors.Select(f => new KeyValuePair<string, object>(f == 0 ? "Stay locked" : "Let go below " + f + " dBm", f)));
            weak.SelectedValue = st.WeakThreshold;
            startup = Toggle(StartWithWindows.IsEnabled);
            notify = Toggle(st.NotifyChanges);
            Section("Behavior",
                new SettingRow("Safety check", "Wi-Fi changes are handled instantly. This extra check is only a backup.", interval),
                new SettingRow("When the signal is weak", "“Let go” moves you to a stronger favorite (or lets Windows choose) while your chosen access point is weak, and comes back once it recovers.", weak),
                new SettingRow("Start with Windows", "Runs quietly in the notification area after you sign in.", startup),
                new SettingRow("Notifications", "Tell me when the Wi-Fi network or access point changes.", notify));

            // ---- Appearance
            appearance = new SegmentedControl { Options = new[] { "System", "Light", "Dark" } };
            appearance.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeKeys, st.Theme));
            appearance.SelectedIndexChanged += (s, e) => Theme.SetPreference(ThemeKeys[appearance.SelectedIndex]);   // live preview
            Section("Appearance",
                new SettingRow("Theme", "“System” follows your Windows light or dark setting.", appearance));

            // ---- Updates
            autoUpdate = Toggle(st.AutoUpdate);
            checkNow = new UiButton { Text = "Check now" };
            checkNow.Click += async (s, e) => await updater.CheckAsync(userInitiated: true);
            versionRow = new SettingRow("Version " + Updater.Current, "", checkNow);
            Section("Updates",
                new SettingRow("Automatic updates", Updater.InstalledByWinget
                    ? "Checks this project's GitHub releases once a day and updates through winget (you installed BSSIDLock with winget)."
                    : "Checks this project's GitHub releases once a day and installs new versions in the background. Every download is checked against the release checksum.", autoUpdate),
                versionRow);

            // ---- Favorites by address
            manual = new FieldBox { DesignWidth = 190, Placeholder = "aa:bb:cc:dd:ee:ff" };
            manual.Box.Font = new Font("Consolas", 10F);
            manual.Box.MaxLength = 17;
            var add = new UiButton { Text = "Add", Enabled = false, MinWidth = 70 };
            manual.Box.KeyPress += (s, e) =>
            {
                // Only hex digits and separators.
                if (e.KeyChar == '\r') { e.Handled = true; return; }
                if (!char.IsControl(e.KeyChar) && !Uri.IsHexDigit(e.KeyChar) && e.KeyChar != ':' && e.KeyChar != '-') e.Handled = true;
            };
            manual.Box.TextChanged += (s, e) => { add.Text = "Add"; add.Enabled = Bssid.IsValid(manual.Text); };
            manual.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && add.Enabled) { add.PerformClick(); e.SuppressKeyPress = true; } };
            add.Click += (s, e) =>
            {
                if (!engine.AddFavorite(manual.Text)) return;
                manual.Text = "";
                add.Text = "Added";
            };
            Section("Favorites",
                new SettingRow("Add by address", (st.Networks.Count > 0 ? "Adds it to “" + st.Ssid + "”. " : "") + "For an access point that's out of range right now.", new HStack(manual, add)));

            stack.ResumeLayout(false);

            // ---- Footer
            save = new UiButton { Text = "Save", Style = UiButton.Kind.Primary, DialogResult = DialogResult.OK, MinWidth = 100 };
            cancel = new UiButton { Text = "Cancel", DialogResult = DialogResult.Cancel, MinWidth = 100 };
            AcceptButton = save;
            CancelButton = cancel;
            footer = new Panel { Dock = DockStyle.Bottom };
            footer.Controls.Add(save);
            footer.Controls.Add(cancel);
            footer.Layout += (s, e) =>
            {
                var bs = save.GetPreferredSize(Size.Empty);
                var cs = cancel.GetPreferredSize(Size.Empty);
                int pad = Ui.S(this, 22), gap = Ui.S(this, 10), top = Ui.S(this, 12);
                footer.Height = top + Math.Max(bs.Height, cs.Height) + Ui.S(this, 16);
                save.SetBounds(footer.Width - pad - bs.Width, top, bs.Width, bs.Height);
                cancel.SetBounds(save.Left - gap - cs.Width, top, cs.Width, cs.Height);
            };

            Controls.Add(host);
            Controls.Add(footer);
            host.BringToFront();
            ClientSize = new Size(Ui.S(this, 680), Ui.S(this, 600));
            MinimumSize = new Size(Ui.S(this, 560), Ui.S(this, 380));

            updater.Changed += OnUpdaterChanged;
            OnUpdaterChanged(null, EventArgs.Empty);
            Finish();
        }

        /// <summary>One row per managed network (with Remove), then the "Add a network" row.</summary>
        void FillNetworks()
        {
            networksCard.SuspendLayout();
            foreach (var old in networksCard.Controls.Cast<Control>().Where(c => c != addNetworkRow).ToList())
            {
                networksCard.Controls.Remove(old);
                old.Dispose();
            }
            networksCard.Controls.Remove(addNetworkRow);
            foreach (var n in engine.Settings.Networks)
            {
                string name = n.Ssid;
                var remove = new UiButton { Text = "Remove", MinWidth = 80 };
                remove.Click += (s, e) =>
                {
                    var answer = MessageBox.Show(this, "Stop managing “" + name + "”? BSSIDLock stops acting on it and forgets its favorites.",
                        "BSSIDLock", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes) return;
                    engine.RemoveNetwork(name);
                    BeginInvoke((Action)RebuildNetworks);   // not inside the clicked button's own Click
                };
                networksCard.Controls.Add(new SettingRow(name, Summary(n), remove));
            }
            networksCard.Controls.Add(addNetworkRow);
            networksCard.ResumeLayout();
        }

        void RebuildNetworks()
        {
            if (IsDisposed) return;
            FillNetworks();
            Theme.Apply(this);
            OnThemeApplied();
            stack.PerformLayout();
            host.PerformLayout();
        }

        /// <summary>"Connected now · 3 favorites · locked to Living room"</summary>
        string Summary(NetworkSetup n)
        {
            var parts = new List<string>();
            if (engine.Current != null && engine.Current.Ssid == n.Ssid) parts.Add("Connected now");
            parts.Add(n.Favorites.Count == 0 ? "No favorites yet" : n.Favorites.Count == 1 ? "1 favorite" : n.Favorites.Count + " favorites");
            if (n.Target != null) parts.Add("locked to " + engine.Label(n.Target));
            return string.Join(" · ", parts);
        }

        void Section(string title, params SettingRow[] rows)
        {
            stack.Controls.Add(new SectionHeading(title) { Margin = new Padding(0, 10, 0, 8) });
            var card = new SettingsCard { Margin = new Padding(0, 0, 0, 10) };
            card.Controls.AddRange(rows);
            stack.Controls.Add(card);
        }

        static ToggleSwitch Toggle(bool value)
        {
            var t = new ToggleSwitch { Checked = value };
            t.Text = value ? "On" : "Off";
            t.CheckedChanged += (s, e) => t.Text = t.Checked ? "On" : "Off";
            return t;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Fit the window to its content (no scrolling unless the screen is too small), centered on the main window.
            var area = Screen.FromControl(Owner ?? this).WorkingArea;
            int chrome = Height - ClientSize.Height;
            int content = stack.GetPreferredSize(new Size(ClientSize.Width, 0)).Height + footer.Height;
            ClientSize = new Size(ClientSize.Width, Math.Min(content, area.Height - chrome - Ui.S(this, 40)));
            var anchor = Owner != null && Owner.Visible ? Owner.Bounds : area;
            Location = new Point(
                Math.Max(area.Left, anchor.Left + (anchor.Width - Width) / 2),
                Math.Max(area.Top + Ui.S(this, 10), anchor.Top + (anchor.Height - Height) / 2));
        }

        protected override void OnThemeApplied() => host.Invalidate(true);

        void AskCustomInterval()
        {
            string current = interval.SelectedValue is int v ? v.ToString() : engine.Settings.IntervalSeconds.ToString();
            string answer = Prompt.Show(this, "Custom safety check", "Check every how many seconds? Enter a number from 3 to 120.", current);
            if (answer == null) return;
            if (!int.TryParse(answer.Trim(), out int sec) || sec < 3 || sec > 120)
            {
                MessageBox.Show(this, "Please enter a whole number from 3 to 120.", "BSSIDLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var opts = interval.Options.Where(o => !(o.Value is int x) || x != sec).ToList();
            opts.Add(new KeyValuePair<string, object>("Every " + sec + " seconds", sec));
            interval.SetOptions(opts.OrderBy(o => o.Value is int x ? x : 0));
            interval.SelectedValue = sec;
        }

        void OnUpdaterChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            checkNow.Enabled = !updater.Busy && !updater.Installing;
            versionRow.Description = updater.Status.Length > 0 ? updater.Status : "Press “Check now” to look for a new version.";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            updater.Changed -= OnUpdaterChanged;
            var st = engine.Settings;
            if (DialogResult == DialogResult.OK)
            {
                st.IntervalSeconds = interval.SelectedValue is int sec ? sec : st.IntervalSeconds;
                if (weak.SelectedValue is int floor) engine.SetWeakThreshold(floor);
                st.AutoUpdate = autoUpdate.Checked;
                st.NotifyChanges = notify.Checked;
                st.Theme = ThemeKeys[appearance.SelectedIndex];
                try { StartWithWindows.Set(startup.Checked); }
                catch (Exception ex) { engine.AddLog("Couldn't change “start with Windows”: " + ex.Message); }
                st.Save();
                // A name typed into "Add a network" but not added yet: add it on Save too.
                if (ssid.Text.Trim().Length > 0 && !st.Manages(ssid.Text.Trim())) engine.AddNetwork(ssid.Text);
            }
            else
            {
                Theme.SetPreference(st.Theme);   // undo the live preview
            }
            base.OnFormClosing(e);
        }
    }

    sealed class ActivityForm : DialogBase
    {
        public ActivityForm(Engine engine) : base("BSSIDLock Activity")
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(700, 460);
            MinimumSize = new Size(480, 300);

            var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 6, 12) };
            var log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.5F),
                Text = engine.Log.Count == 0 ? "Nothing yet." : string.Join(Environment.NewLine, Enumerable.Reverse(engine.Log)),
            };
            card.Controls.Add(log);
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 18, 20, 0) };
            host.Controls.Add(card);

            var copy = new UiButton { Text = "Copy all" };
            copy.Click += (s, e) => { if (engine.Log.Count > 0) { Clipboard.SetText(string.Join(Environment.NewLine, engine.Log)); copy.Text = "Copied"; } };
            var file = new UiButton { Glyph = Ui.GlyphFolder, Text = "Show file" };
            file.Click += (s, e) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + Engine.LogPath + "\""); }
                catch (Exception ex) { engine.AddLog("Couldn't open the folder: " + ex.Message); }
            };
            new ToolTip().SetToolTip(file, "The full history is saved in " + Engine.LogPath);
            var close = new UiButton { Text = "Close", Style = UiButton.Kind.Primary, DialogResult = DialogResult.Cancel };
            CancelButton = close;
            var footer = Buttons(close, copy, file);
            footer.Dock = DockStyle.Bottom;
            footer.Padding = new Padding(20, 12, 20, 16);

            Controls.Add(host);
            Controls.Add(footer);
            host.BringToFront();
            Shown += (s, e) =>
            {
                log.BackColor = Theme.Surface;
                log.SelectionStart = 0;
                log.SelectionLength = 0;
                Theme.NativeTheme(log, "DarkMode_Explorer");
            };
            Finish();
        }
    }

    sealed class Prompt : DialogBase
    {
        readonly FieldBox box;
        readonly Label label;

        Prompt(string title, string message, string value) : base(title)
        {
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var grid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(22, 18, 22, 16) };
            label = new Label { Text = message, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
            box = new FieldBox { DesignWidth = 380, Margin = new Padding(0, 0, 0, 18) };
            box.Text = value;
            var ok = new UiButton { Text = "Save", Style = UiButton.Kind.Primary, DialogResult = DialogResult.OK };
            var cancel = new UiButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
            AcceptButton = ok;
            CancelButton = cancel;
            grid.Controls.Add(label, 0, 0);
            grid.Controls.Add(box, 0, 1);
            grid.Controls.Add(Buttons(cancel, ok), 0, 2);
            Controls.Add(grid);
            Finish();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            label.MaximumSize = new Size(box.Width, 0);
            box.Box.SelectAll();
            box.Box.Focus();
        }

        /// <summary>Returns the entered text, or null if cancelled.</summary>
        public static string Show(IWin32Window owner, string title, string message, string value)
        {
            using (var p = new Prompt(title, message, value))
                return p.ShowDialog(owner) == DialogResult.OK ? p.box.Text : null;
        }
    }
}
