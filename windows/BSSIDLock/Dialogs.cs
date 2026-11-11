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
            themeHandler = (s, a) => { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => Theme.Apply(this))); };
            Theme.Changed += themeHandler;
        }

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
        readonly FieldBox ssid, manual;
        readonly DropDownButton nearby, interval;
        readonly ToggleSwitch startup, notify, autoUpdate;
        readonly SegmentedControl appearance;
        readonly Label updateStatus;
        readonly UiButton checkNow;

        static readonly string[] ThemeKeys = { "system", "light", "dark" };

        public SettingsForm(Engine engine, Updater updater) : base("BSSIDLock Settings")
        {
            this.engine = engine;
            this.updater = updater;
            var st = engine.Settings;
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(660, 660);
            MinimumSize = new Size(560, 420);

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22, 16, 22, 8) };
            var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(stack);

            // ---- Network
            ssid = new FieldBox { Width = 230, Margin = new Padding(0, 0, 8, 0), Placeholder = "Network name" };
            ssid.Text = st.Ssid;
            nearby = new DropDownButton { Placeholder = "Choose", Margin = new Padding(0), EmptyText = "No networks found yet" };
            nearby.SetOptions(engine.Nearby.Select(n => new KeyValuePair<string, object>(n.Ssid, n.Ssid)));
            nearby.SelectedValue = null;
            nearby.SelectionChanged += (s, e) => { ssid.Text = nearby.SelectedValue as string ?? ssid.Text; nearby.SelectedValue = null; };
            nearby.ExtraItems = () =>
            {
                if (engine.Current == null) return Enumerable.Empty<ToolStripItem>();
                var current = engine.Current.Ssid;
                var item = new ToolStripMenuItem("Use the network I'm on (“" + current + "”)") { Padding = new Padding(0, 4, 0, 4) };
                item.Click += (s, e) => ssid.Text = current;
                return new ToolStripItem[] { item };
            };
            string why =
                engine.AccessDenied ? " Windows is hiding Wi-Fi details until you allow location access, so nearby networks can't be listed; you can still type the name."
                : engine.Current == null ? " You're not connected to Wi-Fi right now; you can type the name."
                : "";
            AddSection(stack, "Network",
                Row("Wi-Fi network", "BSSIDLock only acts while you're connected to this network. Windows must already know it (connect once normally)." + why,
                    Flow(ssid, nearby)));

            // ---- Lock behavior
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
            startup = Toggle(StartWithWindows.IsEnabled);
            notify = Toggle(st.NotifyChanges);
            AddSection(stack, "Behavior",
                Row("Safety check", "Wi-Fi changes are handled instantly. This extra check is only a backup.", interval),
                Row("Start with Windows", "Runs quietly in the notification area after you sign in.", startup),
                Row("Notifications", "Tell me when the Wi-Fi network or access point changes.", notify));

            // ---- Appearance
            appearance = new SegmentedControl { Options = new[] { "System", "Light", "Dark" } };
            appearance.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeKeys, st.Theme));
            appearance.SelectedIndexChanged += (s, e) => Theme.SetPreference(ThemeKeys[appearance.SelectedIndex]);   // live preview
            AddSection(stack, "Appearance",
                Row("Theme", "“System” follows your Windows light or dark setting.", appearance));

            // ---- Updates
            autoUpdate = Toggle(st.AutoUpdate);
            checkNow = new UiButton { Text = "Check now", Margin = new Padding(0) };
            checkNow.Click += async (s, e) => await updater.CheckAsync(userInitiated: true);
            var versionRow = Row("Version " + Updater.Current, "", checkNow);
            updateStatus = (Label)versionRow.Tag;
            AddSection(stack, "Updates",
                Row("Automatic updates", "Checks this project's GitHub releases once a day and installs new versions in the background. Every download is checked against the release checksum.", autoUpdate),
                versionRow);

            // ---- Favorites by address
            manual = new FieldBox { Width = 200, Margin = new Padding(0, 0, 8, 0), Placeholder = "aa:bb:cc:dd:ee:ff" };
            manual.Box.Font = new Font("Consolas", 10F);
            manual.Box.MaxLength = 17;
            manual.Box.KeyPress += (s, e) =>
            {
                // Only hex digits and separators; Enter adds.
                if (e.KeyChar == '\r') { e.Handled = true; return; }
                if (!char.IsControl(e.KeyChar) && !Uri.IsHexDigit(e.KeyChar) && e.KeyChar != ':' && e.KeyChar != '-') e.Handled = true;
            };
            var add = new UiButton { Text = "Add", Margin = new Padding(0), Enabled = false };
            add.Click += (s, e) =>
            {
                if (engine.AddFavorite(manual.Text)) { manual.Text = ""; add.Text = "Added"; }
                else MessageBox.Show(this, "That doesn't look like an access point address (for example aa:bb:cc:dd:ee:ff).", "BSSIDLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            manual.Box.TextChanged += (s, e) => { add.Text = "Add"; add.Enabled = Bssid.IsValid(manual.Text); };
            manual.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && add.Enabled) { add.PerformClick(); e.SuppressKeyPress = true; } };
            AddSection(stack, "Favorites",
                Row("Add by address", "For an access point that's out of range right now.", Flow(manual, add)));

            // ---- Footer
            var save = new UiButton { Text = "Save", Style = UiButton.Kind.Primary, DialogResult = DialogResult.OK };
            var cancel = new UiButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
            AcceptButton = save;
            CancelButton = cancel;
            var footer = Buttons(cancel, save);
            footer.Dock = DockStyle.Bottom;
            footer.Padding = new Padding(22, 12, 22, 16);

            Controls.Add(scroll);
            Controls.Add(footer);
            scroll.BringToFront();

            updater.Changed += OnUpdaterChanged;
            OnUpdaterChanged(null, EventArgs.Empty);
            Finish();
        }

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

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var area = Screen.FromControl(Owner ?? this).WorkingArea;
            if (Height > area.Height - 40) { Height = area.Height - 40; Top = Math.Max(area.Top + 20, Top); }
        }

        // ---------------------------------------------------------------- building blocks

        static ToggleSwitch Toggle(bool value)
        {
            var t = new ToggleSwitch { Checked = value, Margin = new Padding(0) };
            t.Text = value ? "On" : "Off";
            t.CheckedChanged += (s, e) => t.Text = t.Checked ? "On" : "Off";
            return t;
        }

        static FlowLayoutPanel Flow(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            f.Controls.AddRange(controls);
            return f;
        }

        /// <summary>A settings row: title and description on the left, the control on the right. Tag = the description label.</summary>
        static TableLayoutPanel Row(string title, string description, Control control)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var text = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 16, 0) };
            var t = new Label { Text = title, AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
            var d = new Label { Text = description, AutoSize = true, Tag = "subtle", Margin = new Padding(0), Visible = description.Length > 0 };
            text.Controls.Add(t, 0, 0);
            text.Controls.Add(d, 0, 1);
            control.Anchor = AnchorStyles.Right;
            row.Controls.Add(text, 0, 0);
            row.Controls.Add(control, 1, 0);
            row.SizeChanged += (s, e) =>
            {
                int w = Math.Max(120, row.ClientSize.Width - row.Padding.Horizontal - control.Width - 24);
                t.MaximumSize = d.MaximumSize = new Size(w, 0);
            };
            row.Tag = d;
            return row;
        }

        static void AddSection(TableLayoutPanel stack, string heading, params Control[] rows)
        {
            stack.Controls.Add(new Label { Text = heading, AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5F), Margin = new Padding(2, 6, 0, 8) });
            var card = new Card { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 16), Padding = new Padding(1) };
            var inner = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) inner.Controls.Add(new Panel { Height = 1, Dock = DockStyle.Fill, Margin = new Padding(16, 0, 16, 0), Tag = "separator" });
                inner.Controls.Add(rows[i]);
            }
            card.Controls.Add(inner);
            stack.Controls.Add(card);
        }

        void OnUpdaterChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            checkNow.Enabled = !updater.Busy;
            updateStatus.Text = updater.Status.Length > 0 ? updater.Status : "Press “Check now” to look for a new version.";
            updateStatus.Visible = true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            updater.Changed -= OnUpdaterChanged;
            var st = engine.Settings;
            if (DialogResult == DialogResult.OK)
            {
                st.IntervalSeconds = interval.SelectedValue is int sec ? sec : st.IntervalSeconds;
                st.AutoUpdate = autoUpdate.Checked;
                st.NotifyChanges = notify.Checked;
                st.Theme = ThemeKeys[appearance.SelectedIndex];
                try { StartWithWindows.Set(startup.Checked); }
                catch (Exception ex) { engine.AddLog("Couldn't change “start with Windows”: " + ex.Message); }
                st.Save();
                engine.SetSsid(ssid.Text);
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
            var close = new UiButton { Text = "Close", Style = UiButton.Kind.Primary, DialogResult = DialogResult.Cancel };
            CancelButton = close;
            var footer = Buttons(close, copy);
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
            box = new FieldBox { Width = 420, Margin = new Padding(0, 0, 0, 18) };
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
