#!/usr/bin/env bash
# Builds the self-contained app and the PeriphHub-Setup-<version>.exe installer.
# Needs: .NET 10 SDK (Microsoft build, with Windows Desktop targeting) and NSIS (makensis).
set -euo pipefail
cd "$(dirname "$0")"
VERSION="${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/PeriphHub/PeriphHub.csproj)}"
DOTNET="${DOTNET:-dotnet}"
rm -rf out
"$DOTNET" publish src/PeriphHub/PeriphHub.csproj -c Release -r win-x64 --self-contained true \
  -p:Version="$VERSION" -o out/publish
mkdir -p out/installer
makensis -V2 -DVERSION="$VERSION" -DSRCDIR="$(pwd)/out/publish" -DOUTDIR="$(pwd)/out/installer" installer/PeriphHub.nsi
ls -lh out/installer
