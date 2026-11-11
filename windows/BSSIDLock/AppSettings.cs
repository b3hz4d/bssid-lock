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
    [DataContract]
    public sealed class AppSettings
    {
        /// <summary>The network (SSID) to manage. BSSIDLock never touches any other network.</summary>
        [DataMember] public string Ssid { get; set; }
        /// <summary>Favorite BSSIDs in priority order (first = preferred).</summary>
        [DataMember] public List<string> Favorites { get; set; }
        /// <summary>null = any favorite; otherwise lock to exactly this BSSID.</summary>
        [DataMember] public string Target { get; set; }
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

        public AppSettings() { SetDefaults(); }

        [OnDeserializing]
        void OnDeserializing(StreamingContext _) { SetDefaults(); }

        void SetDefaults()
        {
            Ssid = "";
            Favorites = new List<string>();
            Target = null;
            LockEnabled = true;
            IntervalSeconds = 10;
            Names = new Dictionary<string, string>();
            KnownSsids = new Dictionary<string, string>();
            Theme = "system";
            AutoUpdate = true;
            NotifyChanges = true;
        }

        static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BSSIDLock");
        static string FilePath => Path.Combine(Folder, "settings.json");

        static DataContractJsonSerializer Serializer() =>
            new DataContractJsonSerializer(typeof(AppSettings), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var fs = File.OpenRead(FilePath))
                        return ((AppSettings)Serializer().ReadObject(fs)).Normalize();
            }
            catch (Exception) { /* unreadable settings: start fresh */ }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
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
            Favorites = (Favorites ?? new List<string>()).Where(Bssid.IsValid).Select(Bssid.Canonical).Distinct().ToList();
            Names = Names ?? new Dictionary<string, string>();
            KnownSsids = KnownSsids ?? new Dictionary<string, string>();
            if (Target != null && !Favorites.Contains(Target)) Target = null;
            if (IntervalSeconds < 3 || IntervalSeconds > 120) IntervalSeconds = 10;
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
