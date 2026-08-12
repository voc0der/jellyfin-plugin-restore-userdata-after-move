#!/usr/bin/env bash
# Builds one installable archive per supported Jellyfin server line
# (DESIGN §11). Each archive holds only this plugin's own assemblies plus
# meta.json; host assemblies are compile-time references and must not ship.
#
# VERSION may be overridden by CI: VERSION=1.0.0.7 ./build.sh
set -euo pipefail

cd "$(dirname "$0")"

VERSION="${VERSION:-1.0.0.0}"
PLUGIN_NAME="RestoreUserDataAfterMove"
PROJECT="src/Jellyfin.Plugin.UserDataRestore/Jellyfin.Plugin.UserDataRestore.csproj"
OUT="artifacts"

# framework:package-version:manifest-abi
#
# The archive is named for the package it was built against, not the version the
# server reports. A build made against 12.0.0-rc5 must not be handed out as
# "12.0.0": RC5 and stable both report 12.0.0 and nothing in the assemblies tells
# them apart, so the file name is the only place the distinction survives.
TARGETS=(
    "net9.0:10.11.11:10.11.11.0"
    "net10.0:12.0.0-rc5:12.0.0.0"
)

rm -rf "$OUT"
mkdir -p "$OUT"

dotnet test tests/Jellyfin.Plugin.UserDataRestore.Core.Tests/Jellyfin.Plugin.UserDataRestore.Core.Tests.csproj \
    --nologo -v q

for target in "${TARGETS[@]}"; do
    IFS=: read -r framework server abi <<< "$target"

    # The archive contains a single top-level folder, which is what Jellyfin's
    # installer extracts into the plugins directory.
    folder="${PLUGIN_NAME}_${VERSION}_jellyfin-${server}"
    staging="$OUT/staging-$server/$folder"

    echo
    echo "==> Jellyfin $server ($framework)"

    dotnet publish "$PROJECT" -c Release -f "$framework" -o "$staging" \
        -p:Version="$VERSION" --nologo -v q

    # Anything from the host is a packaging bug, not a dependency: shipping a
    # second copy of Jellyfin.Database.Implementations would give the plugin its
    # own JellyfinDbContext type, distinct from the server's.
    strays="$(find "$staging" -maxdepth 1 -name '*.dll' \
        ! -name 'Jellyfin.Plugin.UserDataRestore.dll' \
        ! -name 'Jellyfin.Plugin.UserDataRestore.Core.dll')"
    if [ -n "$strays" ]; then
        echo "ERROR: host assemblies leaked into the plugin archive:" >&2
        echo "$strays" >&2
        exit 1
    fi

    if [ ! -f "$staging/meta.json" ]; then
        echo "ERROR: meta.json was not generated for $framework." >&2
        exit 1
    fi

    grep -q "\"targetAbi\": \"$abi\"" "$staging/meta.json" || {
        echo "ERROR: meta.json targetAbi is not $abi." >&2
        cat "$staging/meta.json" >&2
        exit 1
    }

    grep -q "\"version\": \"$VERSION\"" "$staging/meta.json" || {
        echo "ERROR: meta.json version is not $VERSION." >&2
        cat "$staging/meta.json" >&2
        exit 1
    }

    find "$staging" -maxdepth 1 \
        ! -name '*.dll' ! -name 'meta.json' ! -path "$staging" -delete

    archive="${folder}.zip"
    (cd "$OUT/staging-$server" && zip -qr "../$archive" "$folder")
    rm -rf "$OUT/staging-$server"

    echo "    $OUT/$archive"
    unzip -l "$OUT/$archive" | sed 's/^/    /'
done

echo
echo "Install: unzip into <jellyfin data>/plugins/ and restart."
