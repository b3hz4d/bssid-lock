// Thin wrapper over the Windows Native Wi-Fi API (wlanapi.dll).
// Struct layouts are read by offset to avoid marshalling surprises; offsets follow wlanapi.h / windot11.h.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BSSIDLock.Native
{
    public sealed class WlanException : Exception
    {
        public const int ErrorAccessDenied = 5;

        public int Code { get; }
        public bool AccessDenied => Code == ErrorAccessDenied;

        public WlanException(string operation, int code) : base(Describe(operation, code)) { Code = code; }
        public WlanException(string message) : base(message) { Code = -1; }

        static string Describe(string operation, int code)
        {
            if (code == ErrorAccessDenied)
                return "Windows blocked access to Wi-Fi details. Turn on Location services and " +
                       "\"Let desktop apps access your location\" in Settings > Privacy & security > Location.";
            return operation + " failed: " + new Win32Exception(code).Message + " (error " + code + ")";
        }
    }

    public sealed class BssEntry
    {
        public string Ssid;
        public string Bssid;       // "aa:bb:cc:dd:ee:ff"
        public int Rssi;           // dBm
        public int LinkQuality;    // 0-100
        public int Channel;
        public string Band;
    }

    public sealed class ConnectionInfo
    {
        public string Ssid;
        public string Bssid;
        public string ProfileName;
        public int SignalQuality;  // 0-100
    }

    public static class Wlan
    {
        const uint ClientVersion = 2;                     // Windows Vista and later
        const int WlanIntfOpcodeCurrentConnection = 7;
        const int Dot11BssTypeInfrastructure = 1;
        const int WlanConnectionModeProfile = 0;
        const int WlanInterfaceStateConnected = 1;
        const int ErrorInvalidState = 5023;               // not connected
        const int ErrorServiceNotActive = 1062;           // WLAN AutoConfig service stopped

        // Notification sources and codes (wlanapi.h: WLAN_NOTIFICATION_ACM / WLAN_NOTIFICATION_MSM)
        const uint NotificationSourceAcm = 0x08;
        const uint NotificationSourceMsm = 0x10;
        const int AcmScanComplete = 7, AcmScanFail = 8, AcmConnectionComplete = 10, AcmDisconnected = 21;
        const int MsmConnected = 4, MsmRoamingEnd = 6, MsmDisconnected = 10;

        const int InterfaceInfoSize = 16 + 512 + 4;       // GUID + WCHAR[256] + state
        const int BssEntrySize = 360;                     // sizeof(WLAN_BSS_ENTRY)

        [DllImport("wlanapi.dll")]
        static extern int WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("wlanapi.dll")]
        static extern int WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        static extern int WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll")]
        static extern int WlanScan(IntPtr hClientHandle, [In] ref Guid pInterfaceGuid, IntPtr pDot11Ssid, IntPtr pIeData, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        static extern int WlanGetNetworkBssList(IntPtr hClientHandle, [In] ref Guid pInterfaceGuid, IntPtr pDot11Ssid,
            int dot11BssType, [MarshalAs(UnmanagedType.Bool)] bool bSecurityEnabled, IntPtr pReserved, out IntPtr ppWlanBssList);

        [DllImport("wlanapi.dll")]
        static extern int WlanQueryInterface(IntPtr hClientHandle, [In] ref Guid pInterfaceGuid, int opCode, IntPtr pReserved,
            out int pdwDataSize, out IntPtr ppData, out int pWlanOpcodeValueType);

        [DllImport("wlanapi.dll")]
        static extern int WlanConnect(IntPtr hClientHandle, [In] ref Guid pInterfaceGuid, [In] ref WlanConnectionParameters pConnectionParameters, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        static extern void WlanFreeMemory(IntPtr pMemory);

        delegate void WlanNotificationCallback(IntPtr data, IntPtr context);

        [DllImport("wlanapi.dll")]
        static extern int WlanRegisterNotification(IntPtr hClientHandle, uint dwNotifSource, [MarshalAs(UnmanagedType.Bool)] bool bIgnoreDuplicate,
            WlanNotificationCallback funcCallback, IntPtr pCallbackContext, IntPtr pReserved, out uint pdwPrevNotifSource);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WlanConnectionParameters
        {
            public int wlanConnectionMode;
            [MarshalAs(UnmanagedType.LPWStr)] public string strProfile;
            public IntPtr pDot11Ssid;
            public IntPtr pDesiredBssidList;
            public int dot11BssType;
            public uint dwFlags;
        }

        /// <summary>An open WLAN client handle plus the Wi-Fi interface to use.</summary>
        sealed class Session : IDisposable
        {
            public readonly IntPtr Handle;
            public Guid Interface;
            public readonly bool Connected;

            public Session()
            {
                int rc;
                try { rc = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out Handle); }
                catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
                {
                    // wlanapi.dll is missing on PCs without the Wireless LAN service (e.g. some Windows Server editions).
                    throw new WlanException("Wi-Fi isn't available on this PC (the Wireless LAN service isn't installed).");
                }
                if (rc == ErrorServiceNotActive)
                    throw new WlanException("The Wi-Fi service (WLAN AutoConfig) isn't running. Turn on Wi-Fi, or start the service in services.msc.");
                if (rc != 0) throw new WlanException("Opening the Wi-Fi service", rc);

                rc = WlanEnumInterfaces(Handle, IntPtr.Zero, out IntPtr list);
                if (rc != 0) { WlanCloseHandle(Handle, IntPtr.Zero); throw new WlanException("Listing Wi-Fi adapters", rc); }
                try
                {
                    int count = Marshal.ReadInt32(list, 0);
                    if (count == 0) { WlanCloseHandle(Handle, IntPtr.Zero); throw new WlanException("No Wi-Fi adapter found."); }
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr item = list + 8 + i * InterfaceInfoSize;
                        var guid = (Guid)Marshal.PtrToStructure(item, typeof(Guid));
                        int state = Marshal.ReadInt32(item, 16 + 512);
                        // Prefer the first connected adapter, otherwise the first adapter.
                        if (i == 0 || (state == WlanInterfaceStateConnected && !Connected))
                        {
                            Interface = guid;
                            Connected = state == WlanInterfaceStateConnected;
                        }
                    }
                }
                finally { WlanFreeMemory(list); }
            }

            public void Dispose() => WlanCloseHandle(Handle, IntPtr.Zero);
        }

        /// <summary>The current Wi-Fi connection, or null when not connected.</summary>
        public static ConnectionInfo GetCurrentConnection()
        {
            using (var s = new Session())
            {
                if (!s.Connected) return null;
                int rc = WlanQueryInterface(s.Handle, ref s.Interface, WlanIntfOpcodeCurrentConnection, IntPtr.Zero,
                    out _, out IntPtr data, out _);
                if (rc == ErrorInvalidState) return null;
                if (rc != 0) throw new WlanException("Reading the current connection", rc);
                try
                {
                    // WLAN_CONNECTION_ATTRIBUTES: state(0) mode(4) strProfileName[256](8)
                    // WLAN_ASSOCIATION_ATTRIBUTES at 520: DOT11_SSID(520) bssType(556) bssid(560) phyType(568)
                    //                                     phyIndex(572) signalQuality(576)
                    if (Marshal.ReadInt32(data, 0) != WlanInterfaceStateConnected) return null;
                    var info = new ConnectionInfo
                    {
                        ProfileName = Marshal.PtrToStringUni(data + 8),
                        Ssid = ReadSsid(data, 520),
                        Bssid = ReadMac(data, 560),
                        SignalQuality = Marshal.ReadInt32(data, 576),
                    };
                    // Without location permission (Windows 11 24H2+) Windows may hand back a blank address.
                    if (info.Bssid == "00:00:00:00:00:00")
                        throw new WlanException("Reading the current connection", WlanException.ErrorAccessDenied);
                    return info;
                }
                finally { WlanFreeMemory(data); }
            }
        }

        /// <summary>All access points Windows knows about, optionally after a fresh scan (takes ~4 s).</summary>
        public static List<BssEntry> GetAccessPoints(bool scanFirst)
        {
            using (var s = new Session())
            {
                if (scanFirst)
                {
                    ScanDone.Reset();
                    int src = WlanScan(s.Handle, ref s.Interface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (src == WlanException.ErrorAccessDenied) throw new WlanException("Scanning", src);
                    // Wait until Windows reports the scan finished (usually 1-3 s), or at most 4 s without notifications.
                    if (src == 0) ScanDone.WaitOne(monitorHandle != IntPtr.Zero ? 5000 : 4000);
                }

                int rc = WlanGetNetworkBssList(s.Handle, ref s.Interface, IntPtr.Zero, Dot11BssTypeInfrastructure,
                    false, IntPtr.Zero, out IntPtr list);
                if (rc != 0) throw new WlanException("Reading access points", rc);
                try
                {
                    // WLAN_BSS_LIST: dwTotalSize(0) dwNumberOfItems(4) entries(8)
                    int count = Marshal.ReadInt32(list, 4);
                    var result = new List<BssEntry>(count);
                    for (int i = 0; i < count; i++)
                    {
                        // WLAN_BSS_ENTRY: DOT11_SSID(0) uPhyId(36) bssid(40) bssType(48) phyType(52) lRssi(56)
                        //                 uLinkQuality(60) ... ulChCenterFrequency(92, kHz)
                        IntPtr e = list + 8 + i * BssEntrySize;
                        FromFrequency(Marshal.ReadInt32(e, 92) / 1000, out int channel, out string band);
                        result.Add(new BssEntry
                        {
                            Ssid = ReadSsid(e, 0),
                            Bssid = ReadMac(e, 40),
                            Rssi = Marshal.ReadInt32(e, 56),
                            LinkQuality = Marshal.ReadInt32(e, 60),
                            Channel = channel,
                            Band = band,
                        });
                    }
                    return result;
                }
                finally { WlanFreeMemory(list); }
            }
        }

        /// <summary>
        /// Asks Windows to connect using a saved profile, restricted to one access point.
        /// Returns once Windows accepted the request; the switch itself takes a few seconds.
        /// </summary>
        public static void ConnectToAccessPoint(string profileName, string bssid)
        {
            byte[] mac = ParseMac(bssid);
            // DOT11_BSSID_LIST with one entry: NDIS_OBJECT_HEADER(4) uNumOfEntries(4) uTotalNumOfEntries(4) BSSIDs[6] (+2 padding)
            const int listSize = 20;
            IntPtr list = Marshal.AllocHGlobal(listSize);
            try
            {
                for (int i = 0; i < listSize; i++) Marshal.WriteByte(list, i, 0);
                Marshal.WriteByte(list, 0, 0x80);              // NDIS_OBJECT_TYPE_DEFAULT
                Marshal.WriteByte(list, 1, 1);                 // DOT11_BSSID_LIST_REVISION_1
                Marshal.WriteInt16(list, 2, (short)listSize);  // sizeof(DOT11_BSSID_LIST)
                Marshal.WriteInt32(list, 4, 1);
                Marshal.WriteInt32(list, 8, 1);
                Marshal.Copy(mac, 0, list + 12, 6);

                var p = new WlanConnectionParameters
                {
                    wlanConnectionMode = WlanConnectionModeProfile,
                    strProfile = profileName,
                    pDot11Ssid = IntPtr.Zero,
                    pDesiredBssidList = list,
                    dot11BssType = Dot11BssTypeInfrastructure,
                    dwFlags = 0,
                };
                using (var s = new Session())
                {
                    int rc = WlanConnect(s.Handle, ref s.Interface, ref p, IntPtr.Zero);
                    if (rc != 0) throw new WlanException("Connecting to " + bssid, rc);
                }
            }
            finally { Marshal.FreeHGlobal(list); }
        }

        // ---- Change notifications ----

        static readonly AutoResetEvent ScanDone = new AutoResetEvent(false);
        static IntPtr monitorHandle;
        static WlanNotificationCallback callback;   // kept in a field so the GC can't collect it while Windows holds it

        /// <summary>Raised (on a Wi-Fi service thread) when the connection changes: connected, roamed or disconnected.</summary>
        public static event Action ConnectionChanged;

        /// <summary>Start listening for Wi-Fi events. Returns false if notifications aren't available.</summary>
        public static bool StartMonitoring()
        {
            if (monitorHandle != IntPtr.Zero) return true;
            try
            {
                if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out IntPtr handle) != 0) return false;
                callback = OnNotification;
                if (WlanRegisterNotification(handle, NotificationSourceAcm | NotificationSourceMsm, true, callback,
                        IntPtr.Zero, IntPtr.Zero, out _) != 0)
                {
                    WlanCloseHandle(handle, IntPtr.Zero);
                    return false;
                }
                monitorHandle = handle;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return false;
            }
        }

        public static void StopMonitoring()
        {
            if (monitorHandle == IntPtr.Zero) return;
            WlanCloseHandle(monitorHandle, IntPtr.Zero);   // also unregisters the callback
            monitorHandle = IntPtr.Zero;
        }

        static void OnNotification(IntPtr data, IntPtr context)
        {
            try
            {
                // WLAN_NOTIFICATION_DATA: NotificationSource(0) NotificationCode(4) InterfaceGuid(8) dwDataSize(24) pData
                uint source = (uint)Marshal.ReadInt32(data, 0);
                int code = Marshal.ReadInt32(data, 4);
                if (source == NotificationSourceAcm && (code == AcmScanComplete || code == AcmScanFail))
                    ScanDone.Set();
                else if ((source == NotificationSourceAcm && (code == AcmConnectionComplete || code == AcmDisconnected)) ||
                         (source == NotificationSourceMsm && (code == MsmConnected || code == MsmRoamingEnd || code == MsmDisconnected)))
                    ConnectionChanged?.Invoke();
            }
            catch (Exception) { /* never let an exception escape into native code */ }
        }

        static string ReadSsid(IntPtr p, int offset)
        {
            int len = Math.Min(Math.Max(Marshal.ReadInt32(p, offset), 0), 32);
            var bytes = new byte[len];
            Marshal.Copy(p + offset + 4, bytes, 0, len);
            return Encoding.UTF8.GetString(bytes);
        }

        static string ReadMac(IntPtr p, int offset)
        {
            var sb = new StringBuilder(17);
            for (int i = 0; i < 6; i++)
            {
                if (i > 0) sb.Append(':');
                sb.Append(Marshal.ReadByte(p, offset + i).ToString("x2"));
            }
            return sb.ToString();
        }

        static byte[] ParseMac(string bssid)
        {
            var parts = bssid.Split(':');
            var mac = new byte[6];
            for (int i = 0; i < 6; i++) mac[i] = Convert.ToByte(parts[i], 16);
            return mac;
        }

        static void FromFrequency(int mhz, out int channel, out string band)
        {
            if (mhz >= 2412 && mhz <= 2484) { band = "2.4 GHz"; channel = mhz == 2484 ? 14 : (mhz - 2407) / 5; }
            else if (mhz == 5935) { band = "6 GHz"; channel = 2; }
            else if (mhz >= 5950 && mhz <= 7125) { band = "6 GHz"; channel = (mhz - 5950) / 5; }
            else if (mhz >= 4900 && mhz < 5935) { band = "5 GHz"; channel = (mhz - 5000) / 5; }
            else { band = "?"; channel = 0; }
        }
    }
}
