// BSSIDLock for Windows: lock logic and app state. All public members are used from the UI thread;
// Wi-Fi calls run on the thread pool and their results are applied back on the UI thread.
using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>The Wi-Fi network or access point changed and stayed changed.</summary>
    public sealed class NetworkChange
    {
        public string Title, Body;
        public string Ssid, Bssid, PreviousBssid;
        /// <summary>The network is one BSSIDLock manages.</summary>
        public bool Managed;
        /// <summary>Joined a different network (not just another access point).</summary>
        public bool NewNetwork;
        /// <summary>Same network, and Windows moved the PC (not BSSIDLock).</summary>
        public bool Roamed;
    }

    public static class Signal
    {
        public static string Quality(int rssi) =>
            rssi >= -55 ? "Excellent" : rssi >= -67 ? "Good" : rssi >= -75 ? "Fair" : "Weak";

    }

    public sealed class Engine
    {
        static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(60);
        /// <summary>Extra signal needed before moving back after letting go, so it doesn't flip-flop.</summary>
        const int WeakMargin = 5;
        const int ShownLogLines = 300, KeptLogLines = 2000;

        public AppSettings Settings { get; }
        public event EventHandler Changed;
        public event Action<NetworkChange> NetworkChanged;

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
        /// <summary>The last Wi-Fi error other than "access denied", shown in the window until a call succeeds.</summary>
        public string LastError { get; private set; }
        /// <summary>The newest activity lines (also saved to <see cref="LogPath"/>).</summary>
        public List<string> Log { get; } = new List<string>();
        /// <summary>The newest activity message, without its time.</summary>
        public string LastMessage { get; private set; } = "";
        public static string LogPath => Path.Combine(AppSettings.Folder, "activity.log");

        DateTime retryAfter = DateTime.MinValue;
        DateTime lastActiveScan = DateTime.MinValue;
        bool ticking, refreshing;
        (string Ssid, string Bssid)? announced;
        bool changeCheckPending;
        DateTime lastSwitchByUs = DateTime.MinValue;
        string lastSsid;
        /// <summary>The chosen access points were too weak, so Windows is choosing until one recovers.</summary>
        bool letGo;

        public Engine(AppSettings settings)
        {
            Settings = settings;
            LoadLog();
        }

        void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        // ---- Activity log

        void LoadLog()
        {
            try
            {
                if (!File.Exists(LogPath)) return;
                var all = File.ReadAllLines(LogPath).Where(l => l.Length > 0).ToList();
                if (all.Count > KeptLogLines)
                {
                    all = all.Skip(all.Count - KeptLogLines).ToList();
                    File.WriteAllLines(LogPath, all);
                }
                Log.AddRange(all.Skip(Math.Max(0, all.Count - ShownLogLines)));
            }
            catch (Exception) { /* not fatal: start with an empty log */ }
        }

        public void AddLog(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;
            LastMessage = message;
            Log.Add(line);
            if (Log.Count > ShownLogLines) Log.RemoveRange(0, Log.Count - ShownLogLines);
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch (Exception) { /* not fatal */ }
        }

        // ---- Networks

        /// <summary>The managed network the PC is on right now (null when offline or on another network).</summary>
        public string ActiveSsid => Current != null && Settings.Manages(Current.Ssid) ? Current.Ssid : null;
        public NetworkSetup ActiveSetup => Settings.Find(ActiveSsid);

        /// <summary>The BSSIDs the lock currently accepts (on the network the PC is on).</summary>
        public IReadOnlyList<string> Wanted => ActiveSetup?.Wanted ?? new List<string>();

        /// <summary>"“MEDIANA”", "“MEDIANA” and “PbLove”", "“A”, “B” and “C”".</summary>
        public string NetworkList
        {
            get
            {
                var q = Settings.Networks.Select(n => "“" + n.Ssid + "”").ToList();
                if (q.Count <= 1) return q.FirstOrDefault() ?? "";
                return string.Join(", ", q.Take(q.Count - 1)) + " and " + q[q.Count - 1];
            }
        }

        /// <summary>Start managing a network (or show it, if it's already managed).</summary>
        public void AddNetwork(string ssid)
        {
            ssid = (ssid ?? "").Trim();
            if (ssid.Length == 0) return;
            if (!Settings.Manages(ssid))
            {
                Settings.Networks.Add(new NetworkSetup(ssid));
                AddLog("Now managing \"" + ssid + "\"");
            }
            Settings.Ssid = ssid;
            Settings.Save();
            letGo = false;
            retryAfter = DateTime.MinValue;
            _ = ScanThenTickAsync();
        }

        /// <summary>Stop managing a network; forgets its favorites.</summary>
        public void RemoveNetwork(string ssid)
        {
            var n = Settings.Find(ssid);
            if (n == null) return;
            Settings.Networks.Remove(n);
            AddLog("Stopped managing \"" + ssid + "\"");
            if (Settings.Ssid == ssid) Settings.Ssid = ActiveSsid ?? (Settings.Networks.Count > 0 ? Settings.Networks[0].Ssid : "");
            Settings.Save();
            RestartLock();
        }

        /// <summary>Show another managed network's access points in the window.</summary>
        public void ShowNetwork(string ssid)
        {
            if (!Settings.Manages(ssid) || Settings.Ssid == ssid) return;
            Settings.Ssid = ssid;
            Settings.Save();
            RaiseChanged();
        }

        // ---- Pause

        public bool IsPaused => Settings.PausedUntilTicks > 0 && DateTime.UtcNow.Ticks < Settings.PausedUntilTicks;

        /// <summary>"until 14:30" or "until you switch networks".</summary>
        public string PauseText =>
            !IsPaused ? ""
            : Settings.PauseNetwork != null ? "until you switch networks"
            : "until " + new DateTime(Settings.PausedUntilTicks, DateTimeKind.Utc).ToLocalTime().ToString("t");

        /// <summary>Pause the lock for some minutes, or (minutes = 0) until the PC joins another network.</summary>
        public void Pause(int minutes)
        {
            if (minutes > 0)
            {
                Settings.PauseNetwork = null;
                Settings.PausedUntilTicks = DateTime.UtcNow.AddMinutes(minutes).Ticks;
                AddLog("Lock paused for " + (minutes < 60 ? minutes + " minutes" : minutes == 60 ? "1 hour" : minutes / 60 + " hours"));
            }
            else
            {
                Settings.PauseNetwork = Current?.Ssid ?? "";
                Settings.PausedUntilTicks = DateTime.MaxValue.Ticks;
                AddLog("Lock paused until you switch networks");
            }
            Settings.Save();
            _ = TickAsync();
        }

        public void Resume()
        {
            if (Settings.PausedUntilTicks == 0) return;
            ClearPause();
            Settings.Save();
            AddLog("Lock resumed");
            RestartLock();
        }

        void ClearPause()
        {
            Settings.PausedUntilTicks = 0;
            Settings.PauseNetwork = null;
        }

        /// <summary>End the pause when its time is up, or when the PC joins another network.</summary>
        void CheckPause()
        {
            if (Settings.PausedUntilTicks == 0) return;
            if (DateTime.UtcNow.Ticks >= Settings.PausedUntilTicks) AddLog("The pause is over; the lock is on again");
            else if (Settings.PauseNetwork != null && Current != null && Current.Ssid != Settings.PauseNetwork) AddLog("You switched networks, so the pause is over");
            else return;
            ClearPause();
            Settings.Save();
            letGo = false;
            retryAfter = DateTime.MinValue;
        }

        // ---- Names and favorites (of the shown network)

        public string NameOf(string bssid) =>
            Settings.Names.TryGetValue(bssid, out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : null;

        public string Label(string bssid) => NameOf(bssid) ?? bssid;

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
            if (!Settings.Manages(Settings.Ssid)) return;
            if (Settings.Favorites.Contains(bssid))
            {
                Settings.Favorites.Remove(bssid);
                if (Settings.Target == bssid) Settings.Target = null;
            }
            else Settings.Favorites.Add(bssid);
            Settings.Save();
            RestartLock();
        }

        public bool AddFavorite(string raw)
        {
            if (!Bssid.IsValid(raw) || !Settings.Manages(Settings.Ssid)) return false;
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
            if (!Settings.Manages(Settings.Ssid)) return;
            if (!Settings.Favorites.Contains(bssid)) Settings.Favorites.Add(bssid);
            Settings.Target = bssid;
            Settings.LockEnabled = true;
            ClearPause();
            Settings.Save();
            RestartLock();
        }

        public void SetTarget(string bssid)
        {
            if (Settings.Target == bssid) return;
            Settings.Target = bssid;
            Settings.Save();
            RestartLock();
        }

        public void SetLockEnabled(bool on)
        {
            if (Settings.LockEnabled == on) return;
            Settings.LockEnabled = on;
            ClearPause();
            Settings.Save();
            RestartLock();
        }

        public void SetWeakThreshold(int dbm)
        {
            if (Settings.WeakThreshold == dbm) return;
            Settings.WeakThreshold = dbm;
            Settings.Save();
            RestartLock();
        }

        /// <summary>Something about the lock changed: forget earlier back-off and decide again now.</summary>
        void RestartLock()
        {
            letGo = false;
            retryAfter = DateTime.MinValue;
            _ = TickAsync();
        }

        async Task ScanThenTickAsync()
        {
            await ScanAsync();
            await TickAsync();
        }

        // ---- Wi-Fi

        async Task RefreshAsync()
        {
            try
            {
                Current = await Task.Run(() => Wlan.GetCurrentConnection());
                // Only a real answer proves we have access; "not connected" doesn't touch the protected API.
                if (Current != null) { AccessDenied = false; LastError = null; }
                TrackChange();
            }
            catch (WlanException ex)
            {
                Current = null;
                NoteError(ex);
            }

            string ssidNow = Current?.Ssid;
            if (ssidNow != lastSsid)
            {
                lastSsid = ssidNow;
                letGo = false;
                // Follow the PC to whichever managed network it joins.
                if (ssidNow != null && Settings.Manages(ssidNow) && Settings.Ssid != ssidNow)
                {
                    Settings.Ssid = ssidNow;
                    Settings.Save();
                }
            }
        }

        void NoteError(WlanException ex)
        {
            if (ex.AccessDenied)
            {
                if (!AccessDenied) AddLog(ex.Message);
                AccessDenied = true;
                return;
            }
            if (LastError != ex.Message) AddLog(ex.Message);   // log each new problem once, not every few seconds
            LastError = ex.Message;
        }

        /// <summary>Signal of the current connection in dBm (Windows reports 0–100 %, linear from −100 to −50 dBm).</summary>
        public int CurrentRssi => Current == null ? 0 : Current.SignalQuality / 2 - 100;

        /// <summary>Signal of an access point on a network: live for the one we're on, else the latest scan.</summary>
        int SignalOf(string bssid, string ssid)
        {
            if (Current != null && Current.Bssid == bssid) return CurrentRssi;
            return Visible.FirstOrDefault(v => v.Bssid == bssid && v.Ssid == ssid)?.Rssi ?? -200;
        }

        // ---- Change notifications

        /// <summary>
        /// Notice a new network / access point; announce it only if it's still the same ~4 s later,
        /// so a roam that BSSIDLock immediately corrects doesn't produce two notifications.
        /// </summary>
        void TrackChange()
        {
            if (Current == null) return;
            var now = (Current.Ssid, Current.Bssid);
            if (announced == null) { announced = now; return; }   // starting point, not a change
            if (now == announced.Value || changeCheckPending) return;
            changeCheckPending = true;
            _ = ConfirmChangeAsync();
        }

        async Task ConfirmChangeAsync()
        {
            await Task.Delay(4000);
            ConnectionInfo c;
            try { c = await Task.Run(() => Wlan.GetCurrentConnection()); }
            catch (WlanException) { changeCheckPending = false; return; }
            changeCheckPending = false;
            if (c == null || announced == null) return;
            var prev = announced.Value;
            var now = (c.Ssid, c.Bssid);
            if (now == prev) return;
            announced = now;

            var change = new NetworkChange { Ssid = c.Ssid, Bssid = c.Bssid, PreviousBssid = prev.Bssid, Managed = Settings.Manages(c.Ssid) };
            if (c.Ssid != prev.Ssid)
            {
                change.NewNetwork = true;
                change.Title = "Connected to \"" + c.Ssid + "\"";
                change.Body = change.Managed ? "BSSIDLock is managing this network · " + Label(c.Bssid) + "."
                    : Settings.Networks.Count > 0 ? "BSSIDLock doesn't manage this network, so it's idle here."
                    : "Wi-Fi network changed.";
            }
            else
            {
                bool byUs = (DateTime.Now - lastSwitchByUs).TotalSeconds < 20;
                change.Roamed = !byUs;
                change.Title = "Switched to " + Label(c.Bssid);
                var ap = Find(c.Bssid);
                change.Body = c.Ssid + (ap != null ? " · " + Signal.Quality(ap.Rssi) + " (" + ap.Rssi + " dBm)" : " · signal " + c.SignalQuality + "%")
                    + (byUs ? " · moved by BSSIDLock" : " · Windows roamed");
            }
            AddLog(change.Title + ". " + change.Body);
            NetworkChanged?.Invoke(change);
            RaiseChanged();
        }

        void ApplyScan(List<BssEntry> all)
        {
            Nearby = all.Where(a => a.Ssid.Length > 0)
                .GroupBy(a => a.Ssid)
                .Select(g => new NetworkSummary { Ssid = g.Key, Rssi = g.Max(a => a.Rssi), Count = g.Select(a => a.Bssid).Distinct().Count() })
                .OrderByDescending(n => n.Rssi)
                .ToList();
            // The managed networks' access points, plus favorites on any network (e.g. a separate 5 GHz name).
            var favorites = new HashSet<string>(Settings.Networks.SelectMany(n => n.Favorites));
            var managed = new HashSet<string>(Settings.Networks.Select(n => n.Ssid));
            Visible = all.Where(a => managed.Contains(a.Ssid) || favorites.Contains(a.Bssid))
                .GroupBy(a => a.Bssid)
                .Select(g => g.OrderByDescending(a => a.Rssi).First())
                .OrderByDescending(a => a.Rssi)
                .ToList();
            LastScan = DateTime.Now;

            // Remember which network each access point is on (shown even when it's out of range). Save only on change.
            bool changed = false;
            foreach (var ap in Visible)
                if (ap.Ssid.Length > 0 && (!Settings.KnownSsids.TryGetValue(ap.Bssid, out var known) || known != ap.Ssid))
                { Settings.KnownSsids[ap.Bssid] = ap.Ssid; changed = true; }
            if (changed) Settings.Save();
        }

        /// <summary>Scan for access points (all networks; the shown one is listed in the window).</summary>
        public async Task ScanAsync()
        {
            if (Busy) return;
            Busy = true;
            RaiseChanged();
            try
            {
                var all = await Task.Run(() => Wlan.GetAccessPoints(scanFirst: true));
                lastActiveScan = DateTime.Now;
                AccessDenied = false;
                LastError = null;
                ApplyScan(all);
                if (Settings.Ssid.Length > 0)
                    AddLog("Scan: found " + Visible.Count(v => v.Ssid == Settings.Ssid) + " access point(s) for \"" + Settings.Ssid + "\"");
            }
            catch (WlanException ex)
            {
                NoteError(ex);
            }
            finally
            {
                Busy = false;
                await RefreshAsync();
                UpdateStatusOnly();
            }
        }

        /// <summary>
        /// While the window is open: refresh signal strengths once per interval from Windows' cached results
        /// (no radio scan), with a real scan at most every 30 s. Quiet: no log lines, no "Scanning…" state.
        /// </summary>
        public async Task RefreshSignalsAsync(int intervalSeconds, int staleAfterSeconds = -1)
        {
            if (Busy || refreshing || AccessDenied) return;
            double age = LastScan.HasValue ? (DateTime.Now - LastScan.Value).TotalSeconds : double.MaxValue;
            if (age < (staleAfterSeconds >= 0 ? staleAfterSeconds : Math.Max(3, intervalSeconds) - 0.5)) return;
            bool active = (DateTime.Now - lastActiveScan).TotalSeconds >= Math.Max(30, intervalSeconds);
            await QuietScanAsync(active);
            UpdateStatusOnly();
        }

        /// <summary>Before deciding whether to move: fresher readings, mostly from Windows' cached results;
        /// a real radio scan at most every 2 minutes.</summary>
        async Task RefreshForDecisionAsync()
        {
            if (LastScan.HasValue && (DateTime.Now - LastScan.Value).TotalSeconds < 8) return;
            await QuietScanAsync((DateTime.Now - lastActiveScan).TotalSeconds >= 120);
        }

        async Task QuietScanAsync(bool active)
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                var all = await Task.Run(() => Wlan.GetAccessPoints(scanFirst: active));
                if (active) lastActiveScan = DateTime.Now;
                AccessDenied = false;
                LastError = null;
                ApplyScan(all);
            }
            catch (WlanException ex) { NoteError(ex); }
            finally { refreshing = false; }
        }

        /// <summary>Join the first access point of <paramref name="order"/> that's in range on <paramref name="ssid"/>
        /// (and at least <paramref name="minRssi"/> strong).</summary>
        async Task<bool> ConnectAsync(IReadOnlyList<string> order, string ssid, int? minRssi = null)
        {
            if (Busy || string.IsNullOrEmpty(ssid)) return false;
            Busy = true;
            RaiseChanged();
            try
            {
                string profile = Current != null && Current.Ssid == ssid && !string.IsNullOrEmpty(Current.ProfileName)
                    ? Current.ProfileName : ssid;
                BssEntry InRange(string b) => Visible.FirstOrDefault(v => v.Bssid == b && v.Ssid == ssid);
                // Skip the scan when the top choice was seen in the last 30 s (switching then starts immediately).
                bool fresh = LastScan.HasValue && (DateTime.Now - LastScan.Value).TotalSeconds < 30 && order.Count > 0 && InRange(order[0]) != null;
                if (!fresh) ApplyScan(await Task.Run(() => Wlan.GetAccessPoints(scanFirst: true)));

                bool anyInRange = false, attempted = false;
                foreach (var b in order)
                {
                    var ap = InRange(b);
                    if (ap == null) continue;
                    anyInRange = true;
                    if (minRssi.HasValue && ap.Rssi < minRssi.Value) continue;
                    attempted = true;
                    try
                    {
                        await Task.Run(() => Wlan.ConnectToAccessPoint(profile, b));
                        for (int i = 0; i < 24; i++)   // wait up to ~12 s for Windows to switch
                        {
                            await Task.Delay(500);
                            await RefreshAsync();
                            if (Current != null && Current.Bssid == b) break;
                        }
                        if (Current != null && Current.Bssid == b)
                        {
                            lastSwitchByUs = DateTime.Now;
                            AddLog("Connected to " + Label(b) + " (" + ap.Rssi + " dBm)");
                            return true;
                        }
                        AddLog("Windows didn't switch to " + Label(b));
                    }
                    catch (WlanException ex) { AddLog(ex.Message); }
                }
                if (!anyInRange) AddLog("None of your chosen access points are in range");
                else if (!attempted) AddLog("Your chosen access points are too weak right now");
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
            var setup = ActiveSetup;
            if (!Settings.LockEnabled) Set(LockState.Off, "Lock is off", "Windows picks the access point. " + ConnectionDetail);
            else if (IsPaused) Set(LockState.Idle, "Paused " + PauseText, "Windows picks the access point for now. " + ConnectionDetail);
            else if (AccessDenied) Set(LockState.Attention, "Location access needed", "Windows hides Wi-Fi details from apps without it.");
            else if (LastError != null && Current == null && LastError.StartsWith("No Wi-Fi adapter")) Set(LockState.Attention, "No Wi-Fi adapter", "BSSIDLock needs Wi-Fi to work.");
            else if (LastError != null && Current == null) Set(LockState.Attention, "Wi-Fi isn't available", LastError);
            else if (Settings.Networks.Count == 0) Set(LockState.Attention, "Choose a network", "Open Settings to pick the Wi-Fi network to manage.");
            else if (Current == null) Set(LockState.Idle, "Not connected to Wi-Fi", "Waiting for " + NetworkList + ".");
            else if (setup == null) Set(LockState.Idle, "Idle on \"" + Current.Ssid + "\"", "BSSIDLock only acts on " + NetworkList + ".");
            else if (setup.Wanted.Count == 0)
                Set(LockState.Attention, "Pick an access point", Current.Ssid == Settings.Ssid
                    ? "Select one below and press Lock, or star a few as favorites." : "Choose favorites for \"" + Current.Ssid + "\".");
            else if (setup.Wanted.Contains(Current.Bssid))
            {
                IsLocked = true;
                bool weak = Settings.WeakThreshold < 0 && CurrentRssi < Settings.WeakThreshold;
                Set(LockState.Locked, "Locked to " + Label(Current.Bssid),
                    weak ? "Weak signal (" + CurrentRssi + " dBm), and nothing stronger is in range." : ConnectionDetail);
            }
            else if (letGo)
                Set(LockState.Attention, "On " + Label(Current.Bssid),
                    "Your chosen access point is weak or out of range, so Windows chooses for now. BSSIDLock moves back when the signal recovers.");
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
                if (Settings.Networks.Count == 0 && Current != null && Current.Ssid.Length > 0)
                {
                    Settings.Networks.Add(new NetworkSetup(Current.Ssid));
                    Settings.Ssid = Current.Ssid;
                    Settings.Save();
                    AddLog("Now managing \"" + Current.Ssid + "\" (the network you're on). You can change this in Settings.");
                    await ScanAsync();
                }

                CheckPause();
                UpdateStatusOnly();
                var setup = ActiveSetup;
                if (!Settings.LockEnabled || IsPaused || AccessDenied || Current == null || setup == null || setup.Wanted.Count == 0)
                    return;
                if (Settings.WeakThreshold < 0)
                {
                    await TickLettingGoWhenWeakAsync(setup);
                    return;
                }
                if (setup.Wanted.Contains(Current.Bssid)) return;

                var wait = retryAfter - DateTime.Now;
                if (wait > TimeSpan.Zero)
                {
                    Set(LockState.Attention, "On " + Label(Current.Bssid),
                        "Your chosen access point isn't in range. Trying again in " + (int)Math.Ceiling(wait.TotalSeconds) + " s.");
                    return;
                }

                Set(LockState.Working, "Switching…",
                    "Moving from " + Label(Current.Bssid) + " to " + (setup.Target != null ? Label(setup.Target) : "your favorite") + ".");
                RaiseChanged();
                if (await ConnectAsync(setup.Wanted, setup.Ssid))
                {
                    UpdateStatusOnly();
                }
                else
                {
                    retryAfter = DateTime.Now + RetryBackoff;
                    Set(LockState.Attention, "Couldn't switch", LastMessage);
                }
            }
            finally
            {
                ticking = false;
                RaiseChanged();
            }
        }

        /// <summary>
        /// The lock with "let go when the signal is weak": stay on a chosen access point while its signal is good
        /// enough; otherwise move to a chosen one (or another favorite) that's strong, or let Windows choose until
        /// one recovers.
        /// </summary>
        async Task TickLettingGoWhenWeakAsync(NetworkSetup setup)
        {
            int floor = Settings.WeakThreshold;
            string ssid = setup.Ssid, b = Current.Bssid;
            var wanted = setup.Wanted;
            bool onWanted = wanted.Contains(b);
            int rssi = CurrentRssi;
            if (onWanted && rssi >= floor) { letGo = false; return; }   // the status already says "Locked"

            var wait = retryAfter - DateTime.Now;
            if (wait <= TimeSpan.Zero) await RefreshForDecisionAsync();
            // Moving back after letting go needs a bit more signal than the floor.
            int need = letGo ? floor + WeakMargin : floor;
            var order = wanted.Where(x => x != b && SignalOf(x, ssid) >= need && (!onWanted || SignalOf(x, ssid) >= rssi + WeakMargin)).ToList();
            bool onGoodFavorite = setup.Favorites.Contains(b) && rssi >= floor;
            if (order.Count == 0 && !onGoodFavorite)
                order = setup.Favorites.Where(x => !wanted.Contains(x) && x != b && SignalOf(x, ssid) >= floor + WeakMargin).ToList();

            if (order.Count == 0)
            {
                // Nothing better in range: stay where we are.
                if (onWanted) letGo = false;
                else if (!letGo)
                {
                    letGo = true;
                    AddLog("Your chosen access point is weak or out of range, so Windows chooses until it's back");
                }
                UpdateStatusOnly();
                return;
            }
            if (wait > TimeSpan.Zero)
            {
                Set(LockState.Attention, "On " + Label(b), "Couldn't switch. Trying again in " + (int)Math.Ceiling(wait.TotalSeconds) + " s.");
                return;
            }

            string to = Label(order[0]);
            if (onWanted) AddLog("Weak signal on " + Label(b) + " (" + rssi + " dBm); moving to a stronger access point");
            Set(LockState.Working, "Switching…",
                onWanted ? "The signal on " + Label(b) + " is weak; moving to " + to + "." : "Moving from " + Label(b) + " to " + to + ".");
            RaiseChanged();
            if (await ConnectAsync(order, ssid, floor))
            {
                letGo = Current == null || !wanted.Contains(Current.Bssid);
                UpdateStatusOnly();
            }
            else
            {
                retryAfter = DateTime.Now + RetryBackoff;
                Set(LockState.Attention, "Couldn't switch", LastMessage);
            }
        }
    }
}
