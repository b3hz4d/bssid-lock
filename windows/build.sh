#!/usr/bin/env bash
# Builds windows/out/BSSIDLock.exe from macOS or Linux. Needs the .NET SDK 8+ (https://dot.net).
set -euo pipefail
cd "$(dirname "$0")"
dotnet build BSSIDLock/BSSIDLock.csproj -c Release -nologo
mkdir -p out
cp BSSIDLock/bin/Release/net48/BSSIDLock.exe out/
echo "Built windows/out/BSSIDLock.exe"
