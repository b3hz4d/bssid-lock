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

                var engine = new Engine(AppSettings.Load());
                bool minimized = args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
                var form = new MainForm(engine, startHidden: minimized);

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
