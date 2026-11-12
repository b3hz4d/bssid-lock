// Automatic updates from this project's GitHub Releases.
//
// Once a day (if enabled) it asks GitHub for the latest release. A newer version is downloaded next to the
// running exe, checked against the release's SHA-256 checksum and its version info, swapped in (Windows
// allows renaming a running exe) and started; this copy then exits. No data is sent beyond a normal
// HTTPS request to api.github.com / github.com.
//
// Installed with winget (a "portable" package in winget's own folder), it doesn't replace itself: winget checks
// the exe hasn't changed when it upgrades or uninstalls. Instead, once winget's catalog has the new version,
// it runs "winget upgrade" after exiting and starts again.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BSSIDLock
{
    [DataContract]
    sealed class GitHubRelease
    {
        [DataMember(Name = "tag_name")] public string Tag { get; set; }
        [DataMember(Name = "html_url")] public string Url { get; set; }
        [DataMember(Name = "assets")] public GitHubAsset[] Assets { get; set; }
    }

    [DataContract]
    sealed class GitHubAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "browser_download_url")] public string Url { get; set; }
    }

    sealed class Updater
    {
        public const string Repo = "b3hz4d/bssid-lock";
        public const string WingetId = "b3hz4d.BSSIDLock";
        static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

        readonly Engine engine;
        GitHubRelease pending;

        public event EventHandler Changed;
        /// <summary>The new exe is in place; the app should start it (with these arguments) and exit.</summary>
        public event Action<string> RestartRequested;
        /// <summary>winget has the new version; the app should start the upgrade (StartWingetUpgrade) and exit.</summary>
        public event Action WingetUpgradeRequested;

        public string Status { get; private set; } = "";
        public string AvailableVersion { get; private set; }
        public bool Busy { get; private set; }
        /// <summary>The new exe is in place and the app is restarting; blocks a second install.</summary>
        public bool Installing { get; private set; }

        public Updater(Engine engine) { this.engine = engine; }

        /// <summary>Installed by winget: the exe runs from winget's package folder (or its command alias).</summary>
        public static bool InstalledByWinget
        {
            get
            {
                string path = Application.ExecutablePath;
                return path.IndexOf(@"\WinGet\Packages\", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf(@"\WinGet\Links\", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public static Version Current
        {
            get { var v = Assembly.GetExecutingAssembly().GetName().Version; return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }
        }

        public string MenuText =>
            Installing ? "Restarting to update…" : Busy ? "Checking for updates…" : AvailableVersion != null ? "Install version " + AvailableVersion : "Check for updates";

        void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        /// <summary>Called periodically: checks once a day when automatic updates are on.</summary>
        public async Task MaybeCheckAsync()
        {
            if (!engine.Settings.AutoUpdate || Busy || Installing) return;
            var last = new DateTime(Math.Max(0, engine.Settings.LastUpdateCheckTicks), DateTimeKind.Utc);
            if (DateTime.UtcNow - last < CheckEvery) return;
            await CheckAsync(userInitiated: false);
        }

        /// <summary>Check now. Installs right away if automatic updates are on, or if the user asked.</summary>
        public async Task CheckAsync(bool userInitiated)
        {
            if (Busy || Installing) return;
            Busy = true;
            Status = "Checking for updates…";
            Raise();
            try
            {
                var release = await Task.Run(() => FetchLatest());
                engine.Settings.LastUpdateCheckTicks = DateTime.UtcNow.Ticks;
                engine.Settings.Save();

                var latest = ParseVersion(release.Tag);
                if (latest == null || latest <= Current)
                {
                    AvailableVersion = null;
                    pending = null;
                    Status = "BSSIDLock is up to date (version " + Current + ").";
                    return;
                }
                AvailableVersion = latest.ToString();
                pending = release;
                if (!engine.Settings.AutoUpdate && !userInitiated)
                {
                    Status = "Version " + AvailableVersion + " is available.";
                    engine.AddLog(Status);
                }
                else if (InstalledByWinget) await UpgradeWithWingetAsync(latest);
                else await InstallAsync(latest);
            }
            catch (Exception ex)
            {
                Status = "Couldn't check for updates: " + ex.Message;
                engine.AddLog(Status);
            }
            finally
            {
                if (!Installing) Busy = false;
                Raise();
            }
        }

        static GitHubRelease FetchLatest()
        {
            using (var wc = NewClient())
            {
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                byte[] json = wc.DownloadData("https://api.github.com/repos/" + Repo + "/releases/latest");
                using (var ms = new MemoryStream(json))
                    return (GitHubRelease)new DataContractJsonSerializer(typeof(GitHubRelease)).ReadObject(ms);
            }
        }

        static WebClient NewClient()
        {
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "BSSIDLock/" + Current + " (+https://github.com/" + Repo + ")";
            return wc;
        }

        static Version ParseVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        async Task InstallAsync(Version version)
        {
            string exe = Application.ExecutablePath;
            string dir = Path.GetDirectoryName(exe);
            string exeUrl = pending.Assets?.FirstOrDefault(a => a.Name == "BSSIDLock.exe")?.Url;
            string sumsUrl = pending.Assets?.FirstOrDefault(a => a.Name == "SHA256SUMS.txt")?.Url;
            if (exeUrl == null || sumsUrl == null) throw new InvalidOperationException("the release is missing its files");

            if (!CanWriteTo(dir))
            {
                Status = "Version " + version + " is available. Run the install command again to update (no write access to " + dir + ").";
                engine.AddLog(Status);
                return;
            }

            Status = "Downloading version " + version + "…";
            Raise();
            string download = Path.Combine(dir, "BSSIDLock.download");
            string sums = null;
            await Task.Run(() =>
            {
                using (var wc = NewClient())
                {
                    wc.DownloadFile(exeUrl, download);
                    sums = wc.DownloadString(sumsUrl);
                }
            });

            try
            {
                // 1. Checksum published with the release
                var m = Regex.Match(sums, @"^([0-9a-fA-F]{64})\s+\*?BSSIDLock\.exe\s*$", RegexOptions.Multiline);
                string actual;
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(download))
                    actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
                if (!m.Success || !string.Equals(m.Groups[1].Value, actual, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("the download didn't match the release checksum");

                // 2. It really is BSSIDLock, at the version we expect
                var info = FileVersionInfo.GetVersionInfo(download);
                if (info.ProductName != "BSSIDLock" || ParseVersion(info.FileVersion) != version)
                    throw new InvalidOperationException("the downloaded file isn't BSSIDLock " + version);
            }
            catch
            {
                TryDelete(download);
                throw;
            }

            // 3. Swap: a running exe can be renamed, then the new file takes its name.
            string old = Path.Combine(dir, "BSSIDLock.old.exe");
            if (File.Exists(old) && !TryDelete(old)) old = Path.Combine(dir, "BSSIDLock.old-" + DateTime.Now.Ticks + ".exe");
            File.Move(exe, old);
            try { File.Move(download, exe); }
            catch { File.Move(old, exe); throw; }

            Installing = true;
            Status = "Updated to version " + version + ". Restarting…";
            engine.AddLog(Status);
            Raise();
            RestartRequested?.Invoke(exe);
        }

        async Task UpgradeWithWingetAsync(Version version)
        {
            // New versions reach winget's catalog some hours (or days) after the GitHub release.
            bool inCatalog = await Task.Run(() => WingetHas(version));
            if (!inCatalog)
            {
                Status = "Version " + version + " is available. It will install through winget once winget has it (usually within a day).";
                engine.AddLog(Status);
                return;
            }
            Installing = true;
            Status = "Updating to version " + version + " with winget. BSSIDLock restarts by itself in a minute.";
            engine.AddLog(Status);
            Raise();
            WingetUpgradeRequested?.Invoke();
        }

        /// <summary>Whether winget's community catalog (github.com/microsoft/winget-pkgs) has this version.</summary>
        static bool WingetHas(Version version)
        {
            string url = "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/b/b3hz4d/BSSIDLock/" + version;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "BSSIDLock/" + Current + " (+https://github.com/" + Repo + ")";
            request.Accept = "application/vnd.github+json";
            request.Method = "GET";
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse()) return response.StatusCode == HttpStatusCode.OK;
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse r && r.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        /// <summary>
        /// Start a hidden helper that waits for this process to exit, runs "winget upgrade" and starts BSSIDLock again
        /// (with --after-winget, so the new copy can tell whether the upgrade worked). The caller then exits.
        /// </summary>
        public static void StartWingetUpgrade(bool startHidden)
        {
            string exe = Application.ExecutablePath.Replace("'", "''");
            string args = "--after-winget " + Current + (startHidden ? " --minimized" : "");
            string script =
                "$ErrorActionPreference = 'SilentlyContinue'\n" +
                "Wait-Process -Id " + Process.GetCurrentProcess().Id + " -Timeout 30\n" +
                "winget upgrade --id " + WingetId + " --exact --silent --accept-source-agreements --accept-package-agreements | Out-Null\n" +
                // Started directly (not through the shell), like the self-updater's restart.
                "[Diagnostics.Process]::Start((New-Object Diagnostics.ProcessStartInfo -Property @{ FileName = '" + exe +
                "'; Arguments = '" + args + "'; UseShellExecute = $false })) | Out-Null\n";
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi).Dispose();
        }

        static bool CanWriteTo(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".bssidlock-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }

        static bool TryDelete(string path)
        {
            try { File.Delete(path); return true; } catch (Exception) { return false; }
        }

        /// <summary>Remove files left by a previous update (called at startup).</summary>
        public static void CleanUpOldVersions()
        {
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                foreach (var f in Directory.GetFiles(dir, "BSSIDLock.old*.exe")) TryDelete(f);
                TryDelete(Path.Combine(dir, "BSSIDLock.download"));
            }
            catch (Exception) { }
        }
    }
}
