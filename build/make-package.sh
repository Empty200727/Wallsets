#!/usr/bin/env bash
# Builds Wallsets.exe with .NET inside (nothing to install on the other computer) and packs
# dist/Wallsets.zip. Works on Windows (Git Bash) and Linux; needs the .NET 10 SDK and Python 3.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# Build from a copy so the build folders under src stay untouched.
mkdir -p "$work/src"
cp "$here"/src/*.cs "$here"/src/*.csproj "$here"/src/app.manifest "$here"/src/app.ico "$work/src/"
dotnet publish "$work/src/Wallsets.csproj" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none \
  -p:EnableWindowsTargeting=true -o "$work/out"
python3 "$here/build/make-package.py" "$here" "$work/out/Wallsets.exe" "$here/dist/Wallsets.zip"
