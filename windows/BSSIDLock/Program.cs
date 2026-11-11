// BSSIDLock for Windows: keep your PC connected to the Wi-Fi access point(s) you choose.
// https://github.com/b3hz4d/bssid-lock
using System;
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
    }
}
