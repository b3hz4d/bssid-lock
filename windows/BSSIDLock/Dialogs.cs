// Settings, activity log, a text prompt, and the app icon loader.
using System;
using System.Drawing;
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
        protected DialogBase(string text)
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = text;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.Apply(this);
        }

        protected static FlowLayoutPanel ButtonRow(params Button[] buttons)
        {
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
            foreach (var b in buttons) { b.AutoSize = true; b.MinimumSize = new Size(88, 30); b.Margin = new Padding(6, 0, 0, 0); row.Controls.Add(b); }
            return row;
        }

        protected static Label Hint(string text) =>
            new Label { Text = text, AutoSize = true, Tag = "subtle", MaximumSize = new Size(460, 0), Margin = new Padding(3, 2, 3, 8) };

        protected static Label Heading(string text) =>
            new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 10F), Margin = new Padding(2, 12, 3, 6) };

        protected static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            row.Controls.AddRange(controls);
            return row;
        }
    }

    sealed class SettingsForm : DialogBase
    {
        readonly Engine engine;
        readonly Updater updater;
        readonly ComboBox ssid, appearance;
        readonly NumericUpDown interval;
        readonly CheckBox startup, autoUpdate, notify;
        readonly TextBox manual;
        readonly Label updateStatus;
        readonly Button checkNow;

        static readonly string[] ThemeKeys = { "system", "light", "dark" };

        public SettingsForm(Engine engine, Updater updater) : base("BSSIDLock Settings")
        {
            this.engine = engine;
            this.updater = updater;
            var st = engine.Settings;
            var grid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };

            // Network
            grid.Controls.Add(Heading("Network to manage"));
            ssid = new ComboBox { Width = 290, DropDownStyle = ComboBoxStyle.DropDown, Text = st.Ssid };
            ssid.Items.AddRange(engine.Nearby.Select(n => (object)n.Ssid).ToArray());
            var useCurrent = new Button { Text = "Use current network", AutoSize = true, Enabled = engine.Current != null, Margin = new Padding(6, 0, 0, 0) };
            useCurrent.Click += (s, e) => { if (engine.Current != null) ssid.Text = engine.Current.Ssid; };
            grid.Controls.Add(Row(ssid, useCurrent));
            string why =
                engine.AccessDenied ? "Windows is hiding Wi-Fi details until you allow location access, so the list is empty. You can still type the name."
                : engine.Current == null ? "You're not connected to Wi-Fi right now, so \"Use current network\" is unavailable. You can type the name."
                : engine.Nearby.Count == 0 ? "No scan results yet. Pick from the list after a scan, or type the name."
                : null;
            if (why != null) grid.Controls.Add(Hint(why));
            grid.Controls.Add(Hint("BSSIDLock only acts while you're connected to this network. Windows must already know the network (connect to it once normally)."));

            // Behavior
            grid.Controls.Add(Heading("Behavior"));
            interval = new NumericUpDown { Minimum = 3, Maximum = 120, Value = st.IntervalSeconds, Width = 60 };
            grid.Controls.Add(Row(
                new Label { Text = "Safety check every", AutoSize = true, Margin = new Padding(3, 6, 3, 0) },
                interval,
                new Label { Text = "seconds (Wi-Fi changes are handled instantly)", AutoSize = true, Tag = "subtle", Margin = new Padding(3, 6, 3, 0) }));
            startup = new CheckBox { Text = "Start BSSIDLock when I sign in to Windows (in the notification area)", AutoSize = true, Checked = StartWithWindows.IsEnabled, Margin = new Padding(3, 8, 3, 3) };
            grid.Controls.Add(startup);
            notify = new CheckBox { Text = "Notify me when the Wi-Fi network or access point changes", AutoSize = true, Checked = st.NotifyChanges, Margin = new Padding(3, 4, 3, 3) };
            grid.Controls.Add(notify);

            // Appearance
            grid.Controls.Add(Heading("Appearance"));
            appearance = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
            appearance.Items.AddRange(new object[] { "Match Windows", "Light", "Dark" });
            appearance.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeKeys, st.Theme));
            appearance.SelectedIndexChanged += (s, e) => Theme.SetPreference(ThemeKeys[appearance.SelectedIndex]);   // live preview
            grid.Controls.Add(Row(new Label { Text = "Theme", AutoSize = true, Margin = new Padding(3, 6, 8, 0) }, appearance));

            // Updates
            grid.Controls.Add(Heading("Updates"));
            autoUpdate = new CheckBox { Text = "Keep BSSIDLock up to date automatically", AutoSize = true, Checked = st.AutoUpdate, Margin = new Padding(3, 0, 3, 3) };
            grid.Controls.Add(autoUpdate);
            checkNow = new Button { Text = "Check now", AutoSize = true, Margin = new Padding(3, 4, 8, 0) };
            checkNow.Click += async (s, e) => await updater.CheckAsync(userInitiated: true);
            updateStatus = new Label { AutoSize = true, Tag = "subtle", MaximumSize = new Size(340, 0), Margin = new Padding(0, 10, 3, 0) };
            grid.Controls.Add(Row(checkNow, updateStatus));
            grid.Controls.Add(Hint("Version " + Updater.Current + ". Updates come only from this project's GitHub releases, are checked against the release checksum, and install in the background."));

            // Manual favorite
            grid.Controls.Add(Heading("Add a favorite by address"));
            manual = new TextBox { Width = 210, Font = new Font("Consolas", 9.5F) };
            var add = new Button { Text = "Add", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            add.Click += (s, e) =>
            {
                if (engine.AddFavorite(manual.Text)) manual.Clear();
                else MessageBox.Show(this, "That doesn't look like an access point address (e.g. aa:bb:cc:dd:ee:ff).", "BSSIDLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            grid.Controls.Add(Row(manual, add));
            grid.Controls.Add(Hint("Useful for an access point that's out of range right now."));

            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            grid.Controls.Add(ButtonRow(cancel, ok));
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(grid);

            updater.Changed += OnUpdaterChanged;
            OnUpdaterChanged(null, EventArgs.Empty);
        }

        void OnUpdaterChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            checkNow.Enabled = !updater.Busy;
            updateStatus.Text = updater.Status.Length > 0 ? updater.Status : "";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            updater.Changed -= OnUpdaterChanged;
            var st = engine.Settings;
            if (DialogResult == DialogResult.OK)
            {
                st.IntervalSeconds = (int)interval.Value;
                st.AutoUpdate = autoUpdate.Checked;
                st.NotifyChanges = notify.Checked;
                st.Theme = ThemeKeys[appearance.SelectedIndex];
                try { StartWithWindows.Set(startup.Checked); }
                catch (Exception ex) { engine.AddLog("Couldn't change \"start with Windows\": " + ex.Message); }
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
            AutoSize = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = true;
            ClientSize = new Size(660, 420);
            var log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9F),
                Text = engine.Log.Count == 0 ? "Nothing yet." : string.Join(Environment.NewLine, Enumerable.Reverse(engine.Log)),
            };
            var copy = new Button { Text = "Copy all" };
            copy.Click += (s, e) => { if (engine.Log.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, engine.Log)); };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel };
            CancelButton = close;
            var buttons = ButtonRow(close, copy);
            buttons.Dock = DockStyle.Bottom;
            Controls.Add(log);
            Controls.Add(buttons);
            log.BringToFront();
            Shown += (s, e) => { log.SelectionStart = 0; log.SelectionLength = 0; Theme.NativeTheme(log, "DarkMode_Explorer"); };
        }
    }

    sealed class Prompt : DialogBase
    {
        readonly TextBox box;

        Prompt(string title, string message, string value) : base(title)
        {
            var grid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            grid.Controls.Add(new Label { Text = message, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(3, 0, 3, 10) });
            box = new TextBox { Text = value, Width = 400 };
            grid.Controls.Add(box);
            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            grid.Controls.Add(ButtonRow(cancel, ok));
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(grid);
        }

        /// <summary>Returns the entered text, or null if cancelled.</summary>
        public static string Show(IWin32Window owner, string title, string message, string value)
        {
            using (var p = new Prompt(title, message, value))
                return p.ShowDialog(owner) == DialogResult.OK ? p.box.Text : null;
        }
    }
}
