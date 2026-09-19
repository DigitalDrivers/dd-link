#!/usr/bin/env bash
# Local quality gate: unit tests of the core library, then a full build of the plugin against the
# AssettoServer sources of the pinned server version. Run from anywhere: scripts/check.sh
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="$(cat assettoserver.version)"
SRC="${ASSETTOSERVER_SRC:-$HOME/.cache/dd-link/AssettoServer-v$VERSION}"

if [ ! -d "$SRC/AssettoServer" ]; then
  echo "== cloning AssettoServer v$VERSION into $SRC"
  mkdir -p "$(dirname "$SRC")"
  git clone --quiet --depth 1 --branch "v$VERSION" https://github.com/compujuckel/AssettoServer.git "$SRC"
fi

echo "== test"
dotnet test tests/DDLink.Core.Tests --nologo

echo "== build plugin against AssettoServer v$VERSION"
rm -rf out
dotnet publish src/DDLinkPlugin/DDLinkPlugin.csproj -c Release -r linux-x64 --nologo \
  -p:AssettoServerSrc="$SRC" -o out/DDLinkPlugin

test -f out/DDLinkPlugin/DDLinkPlugin.dll
test -f out/DDLinkPlugin/DDLink.Core.dll

echo "== race on a real server with simulated drivers"
DDLINK_SERVER_DLL="$SRC/AssettoServer/bin/Release/net9.0/AssettoServer.dll" DDLINK_PLUGIN_DIR="$PWD/out/DDLinkPlugin" \
  dotnet test tests/DDLink.ServerTests --nologo -p:AssettoServerSrc="$SRC"
echo "== all checks passed; plugin is in out/DDLinkPlugin"
