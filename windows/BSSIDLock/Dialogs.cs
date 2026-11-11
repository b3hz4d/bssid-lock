// Settings, activity log, a text prompt, and the app icon loader.
using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace BSSIDLock
{
    static class AppIcon
    {
        /// <summary>The embedded multi-size icon at the requested size (scaled for the current DPI).</summary>
        public static Icon Load(int size)
        {
            int px = (int)Math.Round(size * DpiScale());
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("BSSIDLock.ico"))
                return s != null ? new Icon(s, px, px) : SystemIcons.Application;
        }

        static float DpiScale()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
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
            Padding = new Padding(14);
        }

        protected static FlowLayoutPanel ButtonRow(params Button[] buttons)
        {
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
            foreach (var b in buttons) { b.AutoSize = true; b.MinimumSize = new Size(84, 0); row.Controls.Add(b); }
            return row;
        }

        protected static Label Hint(string text) =>
            new Label { Text = text, AutoSize = true, ForeColor = Color.FromArgb(107, 114, 128), MaximumSize = new Size(440, 0), Margin = new Padding(3, 0, 3, 10) };

        protected static Label Heading(string text) =>
            new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5F), Margin = new Padding(3, 8, 3, 4) };
    }

    sealed class SettingsForm : DialogBase
    {
        readonly Engine engine;
        readonly ComboBox ssid;
        readonly NumericUpDown interval;
        readonly CheckBox startup;
        readonly TextBox manual;

        public SettingsForm(Engine engine) : base("BSSIDLock Settings")
        {
            this.engine = engine;
            var st = engine.Settings;

            var grid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };

            grid.Controls.Add(Heading("Network to manage"));
            var ssidRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            ssid = new ComboBox { Width = 280, DropDownStyle = ComboBoxStyle.DropDown, Text = st.Ssid };
            ssid.Items.AddRange(engine.Nearby.Select(n => (object)n.Ssid).ToArray());
            var useCurrent = new Button { Text = "Use current network", AutoSize = true, Enabled = engine.Current != null };
            useCurrent.Click += (s, e) => { if (engine.Current != null) ssid.Text = engine.Current.Ssid; };
            ssidRow.Controls.Add(ssid);
            ssidRow.Controls.Add(useCurrent);
            grid.Controls.Add(ssidRow);
            grid.Controls.Add(Hint("BSSIDLock only acts while you're connected to this network. Windows must already know the network (connect to it once normally)."));

            grid.Controls.Add(Heading("Behavior"));
            var intervalRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            intervalRow.Controls.Add(new Label { Text = "Check every", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
            interval = new NumericUpDown { Minimum = 3, Maximum = 120, Value = st.IntervalSeconds, Width = 60 };
            intervalRow.Controls.Add(interval);
            intervalRow.Controls.Add(new Label { Text = "seconds", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
            grid.Controls.Add(intervalRow);
            startup = new CheckBox { Text = "Start BSSIDLock when I sign in to Windows (in the notification area)", AutoSize = true, Checked = StartWithWindows.IsEnabled, Margin = new Padding(3, 6, 3, 3) };
            grid.Controls.Add(startup);

            grid.Controls.Add(Heading("Add a favorite by address"));
            var manualRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            manual = new TextBox { Width = 200, Font = new Font("Consolas", 9.5F) };
            var add = new Button { Text = "Add", AutoSize = true };
            add.Click += (s, e) =>
            {
                if (engine.AddFavorite(manual.Text)) { manual.Clear(); }
                else MessageBox.Show(this, "That doesn't look like an access point address (e.g. aa:bb:cc:dd:ee:ff).", "BSSIDLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            manualRow.Controls.Add(manual);
            manualRow.Controls.Add(add);
            grid.Controls.Add(manualRow);
            grid.Controls.Add(Hint("Useful for an access point that's out of range right now."));

            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            grid.Controls.Add(ButtonRow(cancel, ok));
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(grid);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                engine.Settings.IntervalSeconds = (int)interval.Value;
                try { StartWithWindows.Set(startup.Checked); }
                catch (Exception ex) { engine.AddLog("Couldn't change \"start with Windows\": " + ex.Message); }
                engine.Settings.Save();
                engine.SetSsid(ssid.Text);
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
            ClientSize = new Size(640, 400);
            var log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
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
            Shown += (s, e) => { log.SelectionStart = 0; log.SelectionLength = 0; };
        }
    }

    sealed class Prompt : DialogBase
    {
        readonly TextBox box;

        Prompt(string title, string message, string value) : base(title)
        {
            var grid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            grid.Controls.Add(new Label { Text = message, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(3, 0, 3, 8) });
            box = new TextBox { Text = value, Width = 380 };
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
