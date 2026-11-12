// Made-up Wi-Fi for "--demo": lets the app (and the CI screenshots) show a realistic window on machines
// without Wi-Fi, such as virtual machines and build servers. Never used otherwise.
using System.Collections.Generic;
using System.Linq;

namespace BSSIDLock.Native
{
    static class DemoWifi
    {
        public static bool On;

        static readonly List<BssEntry> Aps = new List<BssEntry>
        {
            new BssEntry { Ssid = "Office", Bssid = "a0:b1:c2:d3:e4:01", Rssi = -48, Channel = 36, Band = "5 GHz" },
            new BssEntry { Ssid = "Office", Bssid = "a0:b1:c2:d3:e4:02", Rssi = -63, Channel = 149, Band = "5 GHz" },
            new BssEntry { Ssid = "Office", Bssid = "a0:b1:c2:d3:e4:03", Rssi = -71, Channel = 6, Band = "2.4 GHz" },
            new BssEntry { Ssid = "Office", Bssid = "a0:b1:c2:d3:e4:04", Rssi = -82, Channel = 11, Band = "2.4 GHz" },
            new BssEntry { Ssid = "Guest", Bssid = "a0:b1:c2:d3:e4:11", Rssi = -55, Channel = 36, Band = "5 GHz" },
            new BssEntry { Ssid = "Cafe", Bssid = "70:4f:57:12:34:56", Rssi = -77, Channel = 1, Band = "2.4 GHz" },
        };

        static string current = "a0:b1:c2:d3:e4:02";

        public static ConnectionInfo Current()
        {
            var ap = Aps.First(a => a.Bssid == current);
            return new ConnectionInfo { Ssid = ap.Ssid, Bssid = ap.Bssid, ProfileName = ap.Ssid, SignalQuality = (ap.Rssi + 100) * 2 };
        }

        public static List<BssEntry> AccessPoints() =>
            Aps.Select(a => new BssEntry { Ssid = a.Ssid, Bssid = a.Bssid, Rssi = a.Rssi, Channel = a.Channel, Band = a.Band, LinkQuality = (a.Rssi + 100) * 2 }).ToList();

        public static void Connect(string bssid)
        {
            if (Aps.Any(a => a.Bssid == bssid)) current = bssid;
        }
    }
}
