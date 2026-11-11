#!/usr/bin/env bash
# Local build: tests, publish (service + agent share one .NET runtime), NSIS installer.
#   ./build.sh            -> dist/HaPcBridge-Setup-<version>.exe
# Needs: .NET 8 SDK, makensis (apt install nsis). Python tests run if pytest-homeassistant-custom-component is installed.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ -f "$ROOT/HaPcBridge.sln" ] || { echo "run from the repository"; exit 1; }
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"
APP="$ROOT/dist/app"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

echo "== HA PC Bridge $VERSION"

echo "== unit tests"
dotnet test "$ROOT/tests/csharp" --nologo -v q

echo "== publish"
rm -rf "$APP" "$ROOT/dist/.svc" "$ROOT/dist/.agent"
dotnet publish "$ROOT/src/HaPcBridge.Service" -c Release -r win-x64 --self-contained -p:DebugType=none -o "$ROOT/dist/.svc" -v q
dotnet publish "$ROOT/src/HaPcBridge.Agent"   -c Release -r win-x64 --self-contained -p:DebugType=none -o "$ROOT/dist/.agent" -v q

# Both go into one folder; where they ship different builds of the same assembly, the agent's copy
# is used (it resolves against the larger desktop runtime). That is only safe if the agent's copy is
# not older than what the service was built against - otherwise the service breaks at run time.
asmver() { strings -el "$1" | grep -A1 -x "Assembly Version" | tail -1; }
conflicts=0
for f in $(comm -12 <(ls "$ROOT/dist/.svc") <(ls "$ROOT/dist/.agent")); do
  cmp -s "$ROOT/dist/.svc/$f" "$ROOT/dist/.agent/$f" && continue
  sv="$(asmver "$ROOT/dist/.svc/$f")"; av="$(asmver "$ROOT/dist/.agent/$f")"
  if [ -z "$sv" ] || [ -z "$av" ] || [ "$(printf '%s\n%s\n' "$sv" "$av" | sort -V | tail -1)" != "$av" ]; then
    echo "version conflict: $f (service $sv, agent $av)"; conflicts=1
  fi
done
[ "$conflicts" = 0 ] || { echo "align the package references in src/ServicePackages.props"; exit 1; }
mkdir -p "$APP"
cp -r "$ROOT/dist/.svc/." "$APP/"
cp -r "$ROOT/dist/.agent/." "$APP/"
rm -rf "$ROOT/dist/.svc" "$ROOT/dist/.agent"
cp "$ROOT/README.md" "$ROOT/config.example.json" "$APP/"

echo "== installer"
LIST="$ROOT/dist/uninstall-files.nsh"
( cd "$APP"
  find . -type f | sed 's|^\./||; s|/|\\|g' | sort | sed 's|.*|Delete "$INSTDIR\\&"|'
  find . -mindepth 1 -type d | sed 's|^\./||; s|/|\\|g' | sort -r | sed 's|.*|RMDir "$INSTDIR\\&"|'
) > "$LIST"
OUT="$ROOT/dist/HaPcBridge-Setup-$VERSION.exe"
makensis -V2 -DVERSION="$VERSION" -DPAYLOAD="$APP" -DFILELIST="$LIST" -DOUTFILE="$OUT" "$ROOT/installer/hapcbridge.nsi"
ls -la "$OUT"
