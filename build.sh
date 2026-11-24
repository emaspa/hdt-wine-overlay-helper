#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Build WineOverlayHide.exe with the .NET Framework csc.exe that already lives
# in the Wine prefix where HDT is installed.
#
#   WINEPREFIX=~/Games/battlenet ./build.sh [--install]
#
# Proton/umu prefixes: also set PROTONPATH (and optionally GAMEID) and the
# build runs through umu-run, joining the running wineserver if there is one.
# --install copies the program to AppData/Local/WineOverlayHide in the prefix.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PREFIX="${WINEPREFIX:?set WINEPREFIX to the Wine prefix that contains HDT}"
PREFIX="${PREFIX%/}"

# Proton keeps drive_c under pfx/ in some layouts.
[ -d "$PREFIX/drive_c" ] || [ ! -d "$PREFIX/pfx/drive_c" ] || PREFIX="$PREFIX/pfx"
[ -d "$PREFIX/drive_c" ] || { echo "no drive_c in $PREFIX" >&2; exit 1; }

# Find the Windows user that has HDT installed.
USERNAME_WIN=""
for d in "$PREFIX"/drive_c/users/*/; do
    if [ -f "$d/AppData/Local/HearthstoneDeckTracker/current/HearthstoneDeckTracker.exe" ]; then
        USERNAME_WIN="$(basename "$d")"
        break
    fi
done
[ -n "$USERNAME_WIN" ] || { echo "HDT not found under $PREFIX/drive_c/users/*/AppData/Local" >&2; exit 1; }

USERDIR="$PREFIX/drive_c/users/$USERNAME_WIN"
BUILD="$USERDIR/wineoverlayhide-build"
DEST="$USERDIR/AppData/Local/WineOverlayHide"
WUSER="C:\\users\\$USERNAME_WIN"
WBUILD="$WUSER\\wineoverlayhide-build"
FW='C:\windows\Microsoft.NET\Framework64\v4.0.30319'

[ -f "$PREFIX/drive_c/windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe" ] \
    || { echo "no .NET Framework 4 csc.exe in the prefix" >&2; exit 1; }

if [ -n "${PROTONPATH:-}" ]; then
    run() { env PROTON_VERB=run UMU_CONTAINER_NSENTER=1 GAMEID="${GAMEID:-umu-default}" \
                WINEPREFIX="$WINEPREFIX" PROTONPATH="$PROTONPATH" umu-run "$@"; }
else
    run() { env WINEPREFIX="$PREFIX" WINEDEBUG=-all "${WINE:-wine}" "$@"; }
fi

mkdir -p "$BUILD"
cp "$HERE/WineOverlayHide.cs" "$BUILD/"
rm -f "$BUILD/WineOverlayHide.exe"

run "$FW\\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ \
    "/out:$WBUILD\\WineOverlayHide.exe" \
    "$WBUILD\\WineOverlayHide.cs" 2>&1 \
    | grep -vE 'ProtonFixes|^(INFO|DEBUG|WARN)' || true

[ -f "$BUILD/WineOverlayHide.exe" ] || { echo "build failed" >&2; exit 1; }
cp "$BUILD/WineOverlayHide.exe" "$HERE/"
rm -rf "$BUILD"
echo "built $HERE/WineOverlayHide.exe"

if [ "${1:-}" = "--install" ]; then
    mkdir -p "$DEST"
    cp "$HERE/WineOverlayHide.exe" "$DEST/"
    echo "installed to $DEST"
fi
