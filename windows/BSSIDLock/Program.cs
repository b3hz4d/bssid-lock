// BSSIDLock for Windows: keep your PC connected to the Wi-Fi access point(s) you choose.
// https://github.com/b3hz4d/bssid-lock
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace BSSIDLock
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // After an update, the new copy waits for the old one to exit before taking over.
            int updatedFrom = -1;
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--after-update") int.TryParse(args[i + 1], out updatedFrom);
            if (updatedFrom > 0)
            {
                try { using (var old = System.Diagnostics.Process.GetProcessById(updatedFrom)) old.WaitForExit(15000); }
                catch (ArgumentException) { /* already gone */ }
            }

            // One instance per user. A second launch asks the running one to show its window.
            using (var mutex = new Mutex(true, @"Local\BSSIDLock.SingleInstance", out bool isFirst))
            using (var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BSSIDLock.Show"))
            {
                if (!isFirst)
                {
                    showRequest.Set();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Never vanish silently: log unexpected errors and tell the user where the log is.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => ReportCrash(e.Exception, fatal: false);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash(e.ExceptionObject as Exception, fatal: true);

                var settings = AppSettings.Load();
                Theme.Init(settings.Theme);
                Updater.CleanUpOldVersions();
                var engine = new Engine(settings);
                bool minimized = args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
                var form = new MainForm(engine, startHidden: minimized, justUpdated: updatedFrom > 0);
                if (args.Contains("--open-settings")) form.Shown += (s, e) => form.BeginInvoke((Action)form.OpenSettingsFromCommandLine);

                var listener = new Thread(() =>
                {
                    while (showRequest.WaitOne())
                    {
                        try { form.BeginInvoke((Action)form.ShowFromTray); }
                        catch (InvalidOperationException) { /* window not ready or closing */ }
                    }
                })
                { IsBackground = true, Name = "ShowRequestListener" };
                listener.Start();

                Application.Run(form);
                GC.KeepAlive(mutex);
            }
        }

        static bool reported;

        static void ReportCrash(Exception ex, bool fatal)
        {
            string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BSSIDLock", "error.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log));
                File.AppendAllText(log, DateTime.Now.ToString("u") + (fatal ? " FATAL " : " ") + ex + Environment.NewLine + Environment.NewLine);
            }
            catch (Exception) { /* nothing more we can do */ }

            if (reported && !fatal) return;   // one dialog per session for non-fatal errors
            reported = true;
            MessageBox.Show(
                "BSSIDLock ran into an unexpected problem" + (fatal ? " and has to close" : "") + ":\n\n" + ex?.Message +
                "\n\nDetails were saved to:\n" + log + "\n\nPlease attach that file to an issue at github.com/b3hz4d/bssid-lock.",
                "BSSIDLock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
