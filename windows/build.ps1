# Builds windows\out\BSSIDLock.exe on Windows. Needs the .NET SDK 8+ (https://dot.net).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet build BSSIDLock\BSSIDLock.csproj -c Release -nologo
New-Item -ItemType Directory -Force out | Out-Null
Copy-Item BSSIDLock\bin\Release\net48\BSSIDLock.exe out\ -Force
Write-Host "Built windows\out\BSSIDLock.exe"
