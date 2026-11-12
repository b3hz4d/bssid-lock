// Settings persisted to %APPDATA%\BSSIDLock\settings.json, plus small helpers.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BSSIDLock
{
    /// <summary>One managed network's choices: favorite access points and an optional lock target.</summary>
    [DataContract]
    public sealed class NetworkSetup
    {
        [DataMember] public string Ssid { get; set; }
        /// <summary>Favorite BSSIDs in priority order (first = preferred).</summary>
        [DataMember] public List<string> Favorites { get; set; }
        /// <summary>null = any favorite; otherwise lock to exactly this BSSID.</summary>
        [DataMember] public string Target { get; set; }

        public NetworkSetup(string ssid) { Ssid = ssid; Favorites = new List<string>(); }

        /// <summary>The BSSIDs the lock accepts on this network.</summary>
        public IReadOnlyList<string> Wanted => Target != null ? new List<string> { Target } : Favorites;
    }

    [DataContract]
    public sealed class AppSettings
    {
        /// <summary>The networks (SSIDs) to manage, each with its own favorites. BSSIDLock never touches any other network.</summary>
        [DataMember] public List<NetworkSetup> Networks { get; set; }
        /// <summary>The network shown in the window. Follows the PC to whichever managed network it joins.</summary>
        [DataMember] public string Ssid { get; set; }
        // Version 1.3 and earlier managed one network; these were its favorites and target. Still written
        // (for the shown network) so going back to an older version keeps working.
        [DataMember(Name = "Favorites")] List<string> legacyFavorites;
        [DataMember(Name = "Target")] string legacyTarget;
        [DataMember] public bool LockEnabled { get; set; }
        [DataMember] public int IntervalSeconds { get; set; }
        /// <summary>The network (SSID) each access point was last seen on, keyed by BSSID.</summary>
        [DataMember] public Dictionary<string, string> KnownSsids { get; set; }
        /// <summary>Friendly names keyed by BSSID.</summary>
        [DataMember] public Dictionary<string, string> Names { get; set; }
        [DataMember] public bool TrayHintShown { get; set; }
        /// <summary>"system", "light" or "dark".</summary>
        [DataMember] public string Theme { get; set; }
        /// <summary>Check GitHub once a day and install new versions automatically.</summary>
        [DataMember] public bool AutoUpdate { get; set; }
        [DataMember] public long LastUpdateCheckTicks { get; set; }
        /// <summary>Show a notification when the Wi-Fi network or access point changes.</summary>
        [DataMember] public bool NotifyChanges { get; set; }
        /// <summary>Let go of a chosen access point whose signal drops below this many dBm. 0 = always stay locked.</summary>
        [DataMember] public int WeakThreshold { get; set; }
        /// <summary>While paused the lock doesn't move you: UTC ticks, 0 = not paused, DateTime.MaxValue = until the network changes.</summary>
        [DataMember] public long PausedUntilTicks { get; set; }
        /// <summary>For "until I switch networks": the network the pause started on ("" = not connected then).</summary>
        [DataMember] public string PauseNetwork { get; set; }

        public AppSettings() { SetDefaults(); }

        [OnDeserializing]
        void OnDeserializing(StreamingContext _)
        {
            SetDefaults();
            Networks = null;   // missing in files from version 1.3 and earlier: migrated in Normalize
        }

        void SetDefaults()
        {
            Networks = new List<NetworkSetup>();
            Ssid = "";
            legacyFavorites = null;
            legacyTarget = null;
            LockEnabled = true;
            IntervalSeconds = 10;
            Names = new Dictionary<string, string>();
            KnownSsids = new Dictionary<string, string>();
            Theme = "system";
            AutoUpdate = true;
            NotifyChanges = true;
            WeakThreshold = 0;
            PausedUntilTicks = 0;
            PauseNetwork = null;
        }

        // ---- networks

        public NetworkSetup Find(string ssid) => string.IsNullOrEmpty(ssid) ? null : Networks.FirstOrDefault(n => n.Ssid == ssid);
        public bool Manages(string ssid) => Find(ssid) != null;

        /// <summary>The network shown in the window (an empty, unsaved one if there are no networks yet).</summary>
        public NetworkSetup Shown => Find(Ssid) ?? new NetworkSetup(Ssid ?? "");
        /// <summary>Favorites of the shown network.</summary>
        public List<string> Favorites => Shown.Favorites;
        /// <summary>Lock target of the shown network.</summary>
        public string Target
        {
            get => Shown.Target;
            set { var n = Find(Ssid); if (n != null) n.Target = value; }
        }

        internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BSSIDLock");
        static string FilePath => Path.Combine(Folder, "settings.json");

        static DataContractJsonSerializer Serializer() =>
            new DataContractJsonSerializer(typeof(AppSettings), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var bytes = File.ReadAllBytes(FilePath);
                    int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;   // UTF-8 BOM
                    using (var ms = new MemoryStream(bytes, skip, bytes.Length - skip))
                        return ((AppSettings)Serializer().ReadObject(ms)).Normalize();
                }
            }
            catch (Exception) { /* unreadable settings: start fresh */ }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                legacyFavorites = Shown.Favorites;
                legacyTarget = Shown.Target;
                string tmp = FilePath + ".tmp";
                using (var fs = File.Create(tmp)) Serializer().WriteObject(fs, this);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception) { /* not fatal; settings just won't persist */ }
        }

        AppSettings Normalize()
        {
            Ssid = Ssid ?? "";
            if (Networks == null)
            {
                // Version 1.3 and earlier: one network.
                Networks = new List<NetworkSetup>();
                if (Ssid.Length > 0) Networks.Add(new NetworkSetup(Ssid) { Favorites = legacyFavorites, Target = legacyTarget });
            }
            Networks = Networks.Where(n => n != null && !string.IsNullOrEmpty(n.Ssid)).GroupBy(n => n.Ssid).Select(g => g.First()).ToList();
            foreach (var n in Networks)
            {
                n.Favorites = (n.Favorites ?? new List<string>()).Where(Bssid.IsValid).Select(Bssid.Canonical).Distinct().ToList();
                if (n.Target != null) n.Target = Bssid.Canonical(n.Target);
                if (n.Target != null && !n.Favorites.Contains(n.Target)) n.Target = null;
            }
            if (!Manages(Ssid)) Ssid = Networks.Count > 0 ? Networks[0].Ssid : "";
            Names = Names ?? new Dictionary<string, string>();
            KnownSsids = KnownSsids ?? new Dictionary<string, string>();
            if (IntervalSeconds < 3 || IntervalSeconds > 120) IntervalSeconds = 10;
            if (WeakThreshold > 0 || WeakThreshold < -95) WeakThreshold = 0;
            if (Theme != "light" && Theme != "dark") Theme = "system";
            return this;
        }
    }

    public static class Bssid
    {
        /// <summary>Normalize "AA-BB-CC-DD-E-FF" or "aa:bb:..." to "aa:bb:cc:dd:0e:ff".</summary>
        public static string Canonical(string s)
        {
            var parts = (s ?? "").Trim().ToLowerInvariant().Replace('-', ':').Split(':');
            return string.Join(":", parts.Select(p => p.Length == 1 ? "0" + p : p));
        }

        public static bool IsValid(string s)
        {
            var parts = Canonical(s).Split(':');
            return parts.Length == 6 && parts.All(p => p.Length == 2 && p.All(Uri.IsHexDigit));
        }
    }

    /// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
    public static class StartWithWindows
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "BSSIDLock";

        public static bool IsEnabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue(ValueName) is string;
            }
        }

        public static void Set(bool enabled)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" --minimized");
                else k.DeleteValue(ValueName, false);
            }
        }
    }
}
