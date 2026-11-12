# BSSIDLock installer for Windows. No code-signing certificate, no SmartScreen warning.
#
#   irm https://raw.githubusercontent.com/b3hz4d/bssid-lock/main/install.ps1 | iex
#
# A specific version:
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/b3hz4d/bssid-lock/main/install.ps1))) -Version 1.2.0
#
# Uninstall:
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/b3hz4d/bssid-lock/main/install.ps1))) -Uninstall
#
# Installs for the current user only (no administrator rights) into %LOCALAPPDATA%\Programs\BSSIDLock
# and adds a Start menu shortcut.
#
# Why there's no warning: SmartScreen only checks files that a browser marked as "downloaded from the
# internet". Files fetched by PowerShell don't carry that mark. Microsoft Defender still scans the file
# as usual. This script is short; read it before running it.
param([switch]$Uninstall, [switch]$NoStart, [string]$Version = '')

& {
    param([bool]$Uninstall, [bool]$NoStart, [string]$Version)
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # much faster downloads on Windows PowerShell 5.1
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $repo = 'b3hz4d/bssid-lock'
    $Version = $Version.TrimStart('v', 'V')
    if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version should look like 1.2.0 (got '$Version')." }
    $baseUrl = if ($env:BSSIDLOCK_BASE_URL) { $env:BSSIDLOCK_BASE_URL }
               elseif ($Version) { "https://github.com/$repo/releases/download/v$Version" }
               else { "https://github.com/$repo/releases/latest/download" }
    $installDir = if ($env:BSSIDLOCK_INSTALL_DIR) { $env:BSSIDLOCK_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\BSSIDLock' }
    $exe = Join-Path $installDir 'BSSIDLock.exe'
    $programs = [Environment]::GetFolderPath('Programs')
    $shortcut = if ($programs) { Join-Path $programs 'BSSIDLock.lnk' } else { $null }

    function Say($text) { Write-Host "==> $text" -ForegroundColor Cyan }

    function Stop-Installed {
        # Only stop the copy we're about to replace.
        Get-Process -Name BSSIDLock -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $exe } |
            ForEach-Object { $_.Kill(); $_.WaitForExit(3000) | Out-Null }
    }

    if ($Uninstall) {
        Stop-Installed
        if ($shortcut) { Remove-Item $shortcut -ErrorAction SilentlyContinue }
        Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'BSSIDLock' -ErrorAction SilentlyContinue
        Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
        if ($env:APPDATA) { Remove-Item (Join-Path $env:APPDATA 'BSSIDLock') -Recurse -Force -ErrorAction SilentlyContinue }
        Say 'BSSIDLock has been removed.'
        return
    }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('bssidlock-' + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        Say ('Downloading BSSIDLock ' + $(if ($Version) { $Version } else { '(latest release)' }) + '...')
        try {
            Invoke-WebRequest "$baseUrl/BSSIDLock.exe" -OutFile (Join-Path $tmp 'BSSIDLock.exe') -UseBasicParsing
            Invoke-WebRequest "$baseUrl/SHA256SUMS.txt" -OutFile (Join-Path $tmp 'SHA256SUMS.txt') -UseBasicParsing
        } catch {
            throw "Download failed ($($_.Exception.Message)). Check https://github.com/$repo/releases for a published release."
        }

        $expected = Get-Content (Join-Path $tmp 'SHA256SUMS.txt') |
            Where-Object { $_ -match '^([0-9a-fA-F]{64})\s+\*?BSSIDLock\.exe$' } |
            ForEach-Object { $Matches[1] } | Select-Object -First 1
        $actual = (Get-FileHash (Join-Path $tmp 'BSSIDLock.exe') -Algorithm SHA256).Hash
        if (-not $expected -or $expected -ne $actual) {
            throw 'Checksum mismatch: the download may be corrupted. Nothing was installed.'
        }
        Say "Checksum verified ($($actual.ToLower()))"

        Stop-Installed
        New-Item -ItemType Directory -Force -Path $installDir | Out-Null
        Copy-Item (Join-Path $tmp 'BSSIDLock.exe') $exe -Force
        Say "Installed $exe"

        if ($shortcut) {
            try {
                $shell = New-Object -ComObject WScript.Shell
                $link = $shell.CreateShortcut($shortcut)
                $link.TargetPath = $exe
                $link.WorkingDirectory = $installDir
                $link.Description = 'Keep your PC on the Wi-Fi access point you choose'
                $link.Save()
                Say 'Added BSSIDLock to the Start menu'
            } catch {
                Write-Warning "Couldn't create the Start menu shortcut: $($_.Exception.Message)"
            }
        }

        if (-not $NoStart) {
            Start-Process -FilePath $exe
            Say 'BSSIDLock is starting. It lives in the notification area (tray) when its window is closed.'
        }
    } finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
} $Uninstall.IsPresent $NoStart.IsPresent $Version
