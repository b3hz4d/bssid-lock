// BSSIDLock for Windows: lock logic and app state. All public members are used from the UI thread;
// Wi-Fi calls run on the thread pool and their results are applied back on the UI thread.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BSSIDLock.Native;

namespace BSSIDLock
{
    public enum LockState { Locked, Working, Attention, Idle, Off }

    public sealed class NetworkSummary
    {
        public string Ssid;
        public int Rssi;
        public int Count;
    }

    public static class Signal
    {
        public static string Quality(int rssi) =>
            rssi >= -55 ? "Excellent" : rssi >= -67 ? "Good" : rssi >= -75 ? "Fair" : "Weak";

    }

    public sealed class Engine
    {
        static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(60);

        public AppSettings Settings { get; }
        public event EventHandler Changed;

        public ConnectionInfo Current { get; private set; }
        public List<BssEntry> Visible { get; private set; } = new List<BssEntry>();
        public List<NetworkSummary> Nearby { get; private set; } = new List<NetworkSummary>();
        public DateTime? LastScan { get; private set; }
        public LockState State { get; private set; } = LockState.Idle;
        public string Title { get; private set; } = "Starting…";
        public string Detail { get; private set; } = "";
        public bool IsLocked { get; private set; }
        public bool Busy { get; private set; }
        public bool AccessDenied { get; private set; }
        public List<string> Log { get; } = new List<string>();

        DateTime retryAfter = DateTime.MinValue;
        bool ticking;

        public Engine(AppSettings settings) { Settings = settings; }

        void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public void AddLog(string message)
        {
            Log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
            if (Log.Count > 300) Log.RemoveRange(0, Log.Count - 300);
        }

        // ---- Names and favorites ----

        public string NameOf(string bssid) =>
            Settings.Names.TryGetValue(bssid, out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : null;

        public string Label(string bssid) => NameOf(bssid) ?? bssid;

        public IReadOnlyList<string> Wanted =>
            Settings.Target != null ? new List<string> { Settings.Target } : Settings.Favorites;

        public BssEntry Find(string bssid) => Visible.FirstOrDefault(v => v.Bssid == bssid);

        public void Rename(string bssid, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) Settings.Names.Remove(bssid); else Settings.Names[bssid] = name;
            Settings.Save();
            UpdateStatusOnly();
        }

        public void ToggleFavorite(string bssid)
        {
            if (Settings.Favorites.Contains(bssid))
            {
                Settings.Favorites.Remove(bssid);
                if (Settings.Target == bssid) Settings.Target = null;
            }
            else Settings.Favorites.Add(bssid);
            Settings.Save();
            _ = TickAsync();
        }

        public bool AddFavorite(string raw)
        {
            if (!Bssid.IsValid(raw)) return false;
            var b = Bssid.Canonical(raw);
            if (!Settings.Favorites.Contains(b)) Settings.Favorites.Add(b);
            Settings.Save();
            RaiseChanged();
            return true;
        }

        public void MoveFavorite(string bssid, int delta)
        {
            int i = Settings.Favorites.IndexOf(bssid), j = i + delta;
            if (i < 0 || j < 0 || j >= Settings.Favorites.Count) return;
            Settings.Favorites[i] = Settings.Favorites[j];
            Settings.Favorites[j] = bssid;
            Settings.Save();
            RaiseChanged();
        }

        /// <summary>Lock to exactly this access point (adds it to favorites) and switch now.</summary>
        public void LockTo(string bssid)
        {
            if (!Settings.Favorites.Contains(bssid)) Settings.Favorites.Add(bssid);
            Settings.Target = bssid;
            Settings.LockEnabled = true;
            Settings.Save();
            retryAfter = DateTime.MinValue;
            _ = TickAsync();
        }

        public void SetTarget(string bssid)
        {
            if (Settings.Target == bssid) return;
            Settings.Target = bssid;
            Settings.Save();
            retryAfter = DateTime.MinValue;
            _ = TickAsync();
        }

        public void SetLockEnabled(bool on)
        {
            if (Settings.LockEnabled == on) return;
            Settings.LockEnabled = on;
            Settings.Save();
            retryAfter = DateTime.MinValue;
            _ = TickAsync();
        }

        public void SetSsid(string ssid)
        {
            ssid = (ssid ?? "").Trim();
            if (ssid == Settings.Ssid) return;
            Settings.Ssid = ssid;
            Settings.Save();
            Visible = new List<BssEntry>();
            LastScan = null;
            retryAfter = DateTime.MinValue;
            if (ssid.Length > 0) AddLog("Now managing \"" + ssid + "\"");
            _ = ScanThenTickAsync();
        }

        async Task ScanThenTickAsync()
        {
            await ScanAsync();
            await TickAsync();
        }

        // ---- Wi-Fi ----

        async Task RefreshAsync()
        {
            try
            {
                Current = await Task.Run(() => Wlan.GetCurrentConnection());
                AccessDenied = false;
            }
            catch (WlanException ex)
            {
                Current = null;
                if (ex.AccessDenied) AccessDenied = true; else AddLog(ex.Message);
            }
        }

        void ApplyScan(List<BssEntry> all)
        {
            Nearby = all.Where(a => a.Ssid.Length > 0)
                .GroupBy(a => a.Ssid)
                .Select(g => new NetworkSummary { Ssid = g.Key, Rssi = g.Max(a => a.Rssi), Count = g.Select(a => a.Bssid).Distinct().Count() })
                .OrderByDescending(n => n.Rssi)
                .ToList();
            Visible = all.Where(a => a.Ssid == Settings.Ssid)
                .GroupBy(a => a.Bssid)
                .Select(g => g.OrderByDescending(a => a.Rssi).First())
                .OrderByDescending(a => a.Rssi)
                .ToList();
            LastScan = DateTime.Now;
        }

        /// <summary>Scan for access points (all networks; the managed one is shown in the list).</summary>
        public async Task ScanAsync()
        {
            if (Busy) return;
            Busy = true;
            RaiseChanged();
            try
            {
                var all = await Task.Run(() => Wlan.GetAccessPoints(scanFirst: true));
                AccessDenied = false;
                ApplyScan(all);
                if (Settings.Ssid.Length > 0)
                    AddLog("Scan: found " + Visible.Count + " access point(s) for \"" + Settings.Ssid + "\"");
            }
            catch (WlanException ex)
            {
                if (ex.AccessDenied) AccessDenied = true;
                AddLog(ex.Message);
            }
            finally
            {
                Busy = false;
                await RefreshAsync();
                UpdateStatusOnly();
            }
        }

        async Task<bool> ConnectAsync(IReadOnlyList<string> order)
        {
            if (Busy || Settings.Ssid.Length == 0) return false;
            Busy = true;
            RaiseChanged();
            try
            {
                string profile = Current != null && Current.Ssid == Settings.Ssid && !string.IsNullOrEmpty(Current.ProfileName)
                    ? Current.ProfileName : Settings.Ssid;
                ApplyScan(await Task.Run(() => Wlan.GetAccessPoints(scanFirst: true)));

                bool anyInRange = false;
                foreach (var b in order)
                {
                    var ap = Find(b);
                    if (ap == null) continue;
                    anyInRange = true;
                    try
                    {
                        await Task.Run(() => Wlan.ConnectToAccessPoint(profile, b));
                        for (int i = 0; i < 12; i++)   // wait up to ~12 s for Windows to switch
                        {
                            await Task.Delay(1000);
                            await RefreshAsync();
                            if (Current != null && Current.Bssid == b) break;
                        }
                        if (Current != null && Current.Bssid == b)
                        {
                            AddLog("Connected to " + Label(b) + " (" + ap.Rssi + " dBm)");
                            return true;
                        }
                        AddLog("Windows didn't switch to " + Label(b));
                    }
                    catch (WlanException ex) { AddLog(ex.Message); }
                }
                if (!anyInRange) AddLog("None of your chosen access points are in range");
                return false;
            }
            catch (WlanException ex)
            {
                if (ex.AccessDenied) AccessDenied = true;
                AddLog(ex.Message);
                return false;
            }
            finally
            {
                Busy = false;
            }
        }

        string ConnectionDetail
        {
            get
            {
                if (Current == null) return "Not connected to Wi-Fi";
                var parts = new List<string> { Current.Ssid };
                if (NameOf(Current.Bssid) != null) parts.Add(Current.Bssid);
                var ap = Find(Current.Bssid);
                parts.Add(ap != null ? Signal.Quality(ap.Rssi) + " signal (" + ap.Rssi + " dBm)" : "Signal " + Current.SignalQuality + "%");
                return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
            }
        }

        void Set(LockState state, string title, string detail)
        {
            State = state;
            Title = title;
            Detail = detail;
        }

        /// <summary>Recompute the status line without touching Wi-Fi.</summary>
        void UpdateStatusOnly()
        {
            IsLocked = false;
            if (!Settings.LockEnabled) Set(LockState.Off, "Lock is off", "Windows picks the access point. " + ConnectionDetail);
            else if (AccessDenied) Set(LockState.Attention, "Location access needed", "Windows hides Wi-Fi details from apps without it.");
            else if (Settings.Ssid.Length == 0) Set(LockState.Attention, "Choose a network", "Open Settings to pick the Wi-Fi network to manage.");
            else if (Current == null) Set(LockState.Idle, "Not connected to Wi-Fi", "Waiting for \"" + Settings.Ssid + "\".");
            else if (Current.Ssid != Settings.Ssid) Set(LockState.Idle, "Idle on \"" + Current.Ssid + "\"", "BSSIDLock only acts on \"" + Settings.Ssid + "\".");
            else if (Wanted.Count == 0) Set(LockState.Attention, "Pick an access point", "Select one below and press Lock, or star a few as favorites.");
            else if (Wanted.Contains(Current.Bssid)) { IsLocked = true; Set(LockState.Locked, "Locked to " + Label(Current.Bssid), ConnectionDetail); }
            else if (State != LockState.Working) Set(LockState.Attention, "On " + Label(Current.Bssid), "Not one of your chosen access points.");
            RaiseChanged();
        }

        /// <summary>One check: called by the timer and after every change.</summary>
        public async Task TickAsync()
        {
            if (ticking || Busy) return;
            ticking = true;
            try
            {
                await RefreshAsync();

                // First run: manage the network the PC is on now.
                if (Settings.Ssid.Length == 0 && Current != null && Current.Ssid.Length > 0)
                {
                    Settings.Ssid = Current.Ssid;
                    Settings.Save();
                    AddLog("Now managing \"" + Current.Ssid + "\" (the network you're on). You can change this in Settings.");
                    await ScanAsync();
                }

                UpdateStatusOnly();
                if (!Settings.LockEnabled || AccessDenied || Current == null || Current.Ssid != Settings.Ssid
                    || Wanted.Count == 0 || Wanted.Contains(Current.Bssid))
                    return;

                var wait = retryAfter - DateTime.Now;
                if (wait > TimeSpan.Zero)
                {
                    Set(LockState.Attention, "On " + Label(Current.Bssid),
                        "Your chosen access point isn't in range. Trying again in " + (int)Math.Ceiling(wait.TotalSeconds) + " s.");
                    return;
                }

                Set(LockState.Working, "Switching…",
                    "Moving from " + Label(Current.Bssid) + " to " + (Settings.Target != null ? Label(Settings.Target) : "your favorite") + ".");
                RaiseChanged();
                if (await ConnectAsync(Wanted))
                {
                    UpdateStatusOnly();
                }
                else
                {
                    retryAfter = DateTime.Now + RetryBackoff;
                    string last = Log.Count > 0 ? Log[Log.Count - 1].Substring(10) : "";
                    Set(LockState.Attention, "Couldn't switch", last);
                }
            }
            finally
            {
                ticking = false;
                RaiseChanged();
            }
        }
    }
}
