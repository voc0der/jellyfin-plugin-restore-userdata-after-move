#!/usr/bin/env bash
#
# Audits every entry retained in the plugin catalogues against the archive it
# actually serves.
#
# The release workflow verifies a *new* asset before advertising it, which stops
# the next entry going wrong. It says nothing about the ones already published:
# 1.0.0.4 sat in manifest.json for months advertising an MD5 no download had ever
# hashed to, and the archive behind it held a build from before the tag it was
# attached to. Nothing in CI could have noticed, because nothing looked back.
#
# So this looks back. For every version in every catalogue it fetches the URL the
# catalogue publishes and asks whether the catalogue told the truth about it:
#
#   * the advertised MD5 is the MD5 of the bytes served;
#   * the archive is the expected one folder of three files, nothing more;
#   * the packaged meta.json agrees with the entry's version and targetAbi;
#   * the assembly's own informational version names the entry's version, and
#     the commit it was built from is the commit the release tag points at.
#
# The last one is the check that would have caught 1.0.0.4 for what it was rather
# than as a stale checksum: correcting the hash would have published materially
# older code under a newer tag and changelog. It is also the one that goes quiet
# on its own -- several of these builds were made from commits no branch or tag
# reaches any more, so a fresh clone has never heard of them -- which is why it
# fetches what it needs by SHA and treats anything it still cannot establish as a
# failure. An audit that reports a number larger than what it checked is worse
# than no audit.
#
#   scripts/audit-catalogue.sh                    both catalogues
#   scripts/audit-catalogue.sh manifest.json      one of them
#   scripts/audit-catalogue.sh --allow-unverified-provenance
#                                                 downgrade unreachable history
#                                                 to a warning, and say so
#
# Exit status is the result: 0 means every retained entry matched its download.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

CACHE="${AUDIT_CACHE:-${TMPDIR:-/tmp}/jellyfin-catalogue-audit}"

# Provenance that cannot be established is a failure, not a note. A green run
# that quietly skipped the one check able to tell a stale checksum from
# materially different code is the state this whole script exists to leave
# behind, and it is a state that arrives on its own: the commits behind
# 1.0.0.8 through 1.0.0.11 are reachable from no branch or tag, so a fresh clone
# has never heard of them and skipped four entries while reporting 24 verified.
# They are fetched by SHA now. --allow-unverified-provenance is for a clone that
# genuinely cannot reach the remote, and says so in the summary either way.
STRICT=1
MANIFESTS=()
for arg in "$@"; do
    case "$arg" in
        --allow-unverified-provenance) STRICT=0 ;;
        -*) echo "unknown option: $arg" >&2; exit 2 ;;
        *) MANIFESTS+=("$arg") ;;
    esac
done
[ ${#MANIFESTS[@]} -gt 0 ] || MANIFESTS=(manifest.json manifest-jellyfin-12.json)

if [ -t 1 ]; then
    C_RED=$'\033[31m'; C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'
    C_BOLD=$'\033[1m'; C_DIM=$'\033[2m'; C_OFF=$'\033[0m'
else
    C_RED=''; C_GREEN=''; C_YELLOW=''; C_BOLD=''; C_DIM=''; C_OFF=''
fi

FAILURES=0
CHECKED=0
SKIPPED=0

fail() { FAILURES=$((FAILURES + 1)); printf '   %s- %s%s\n' "$C_RED" "$*" "$C_OFF"; }
ok()   { printf '   %s+%s %s\n' "$C_GREEN" "$C_OFF" "$*"; }
info() { printf '   %s%s%s\n' "$C_DIM" "$*" "$C_OFF"; }

# Unresolvable provenance. Counted either way, so the summary can never claim
# more than was actually checked; fatal unless the caller opted out.
unverified() {
    SKIPPED=$((SKIPPED + 1))
    if [ "$STRICT" -eq 1 ]; then
        fail "$*
     Pass --allow-unverified-provenance if this clone genuinely cannot reach the
     remote; the summary will then say how much went unchecked."
    else
        printf '   %s? %s%s\n' "$C_YELLOW" "$*" "$C_OFF"
    fi
}

for tool in curl jq unzip md5sum git; do
    command -v "$tool" >/dev/null || { echo "missing required tool: $tool" >&2; exit 2; }
done

mkdir -p "$CACHE"

# The commit a release tag points at, fetching the tag if this clone lacks it.
tag_commit() {
    git rev-parse -q --verify "refs/tags/$1^{commit}" 2>/dev/null && return 0
    git fetch --quiet --no-tags origin "refs/tags/$1:refs/tags/$1" 2>/dev/null || true
    git rev-parse -q --verify "refs/tags/$1^{commit}" 2>/dev/null || true
}

# Whether a commit object is available, fetching it by SHA if it is not.
#
# The builds behind several published versions were made from commits that no
# branch or tag reaches any more, so `git clone` does not bring them and no
# amount of fetch-depth or fetch-tags helps. GitHub still serves them when asked
# for by name, which is the only reason those entries can be checked at all.
have_commit() {
    git cat-file -e "$1^{commit}" 2>/dev/null && return 0
    git fetch --quiet --no-tags origin "$1" 2>/dev/null || true
    git cat-file -e "$1^{commit}" 2>/dev/null
}

audit_entry() {
    local manifest=$1 version=$2 url=$3 checksum=$4 abi=$5
    local archive="$CACHE/$(basename "$url")"

    CHECKED=$((CHECKED + 1))
    printf '\n%s%s %s%s\n' "$C_BOLD" "$manifest" "$version" "$C_OFF"

    if [ ! -s "$archive" ] && ! curl -fsSL -o "$archive" "$url"; then
        rm -f "$archive"
        fail "the published sourceUrl could not be fetched: $url"
        return
    fi

    local actual
    actual=$(md5sum "$archive" | awk '{print $1}')
    if [ "$actual" != "$checksum" ]; then
        fail "checksum mismatch — the catalogue advertises $checksum, the download hashes to $actual.
     Jellyfin refuses this install; bypassing the check installs unreviewed bytes."
        return
    fi
    ok "the advertised checksum is the checksum of the bytes served"

    # One top-level folder, which is what Jellyfin's installer extracts into the
    # plugins directory, holding this plugin's two assemblies and its meta.json.
    # A host assembly in here would give the plugin its own JellyfinDbContext
    # type, distinct from the server's; build.sh refuses to package one, and this
    # asserts that no published archive ever slipped past that.
    local folder contents expected
    folder="$(basename "$url" .zip)"
    contents=$(unzip -Z1 "$archive" | grep -v '/$' | LC_ALL=C sort)
    expected=$(printf '%s\n' \
        "$folder/Jellyfin.Plugin.UserDataRestore.Core.dll" \
        "$folder/Jellyfin.Plugin.UserDataRestore.dll" \
        "$folder/meta.json" | LC_ALL=C sort)
    if [ "$contents" != "$expected" ]; then
        fail "unexpected archive contents:
$(printf '%s\n' "$contents" | sed 's/^/       /')"
    else
        ok "the archive is one folder of the two plugin assemblies and meta.json"
    fi

    local meta_version meta_abi
    meta_version=$(unzip -p "$archive" "$folder/meta.json" | jq -r '.version // ""')
    meta_abi=$(unzip -p "$archive" "$folder/meta.json" | jq -r '.targetAbi // ""')

    if [ "$meta_version" != "$version" ]; then
        fail "the packaged meta.json says version $meta_version, the catalogue says $version"
    else
        ok "the packaged meta.json version matches the entry"
    fi

    if [ "$meta_abi" != "$abi" ]; then
        fail "the packaged meta.json says targetAbi $meta_abi, the catalogue says $abi.
     Jellyfin treats targetAbi as a minimum, so a wrong one here offers this
     build to servers it was not compiled against."
    else
        ok "the packaged meta.json targetAbi matches the entry"
    fi

    # The assembly's own account of what it is. This is the check that separates
    # "the checksum went stale" from "the archive holds different code than the
    # tag and changelog describe".
    local built_commit tagged
    local dll="$CACHE/$folder.dll"
    unzip -p "$archive" "$folder/Jellyfin.Plugin.UserDataRestore.dll" > "$dll"

    # The commit hash first, on its own, because it is the one half of the stamp
    # that can be read out of raw binary unambiguously. The version half cannot:
    # `AssemblyInformationalVersion` sits in a length-prefixed metadata blob, and
    # for a stamp of this shape the length byte is 0x31 — the ASCII digit '1'. Any
    # pattern that reads leftward therefore swallows it and calls 1.0.0.18
    # "11.0.0.18". No lookbehind fixes that, because the byte is genuinely a
    # digit.
    built_commit=$(grep -aoP '(?<=\+)[0-9a-f]{40}' "$dll" | head -1 || true)

    if [ -z "$built_commit" ]; then
        rm -f "$dll"
        unverified "the assembly carries no source-revision stamp, so it cannot be traced to a commit"
        return
    fi

    # So the version is checked by asking rather than parsing: does the assembly
    # contain this entry's version joined to that commit? A build stamped with any
    # other version does not, in either direction — "1.0.0.1+sha" is not a
    # substring of "1.0.0.11+sha", nor the reverse, because the '+' has to line up.
    if grep -aqF "$version+$built_commit" "$dll"; then
        ok "the assembly identifies itself as $version"
    else
        fail "the assembly does not identify itself as $version.
     It was built from ${built_commit:0:12} under some other version number."
    fi

    rm -f "$dll"

    tagged=$(tag_commit "$version")
    if [ -z "$tagged" ]; then
        unverified "release tag $version could not be resolved, so the built commit cannot be checked against it"
        return
    fi

    if [ "$built_commit" = "$tagged" ]; then
        ok "the archive was built from the commit tag $version points at"
        return
    fi

    if ! have_commit "$built_commit"; then
        unverified "the archive was built from ${built_commit:0:12}, which could not be obtained, so the difference from tag $version cannot be read"
        return
    fi

    # A tag one commit ahead of the build is the norm for the first few releases:
    # until the workflow started passing --target, `gh release create` tagged
    # main's tip, and main's tip was the catalogue commit the *previous* release
    # had just pushed. That difference is the catalogue describing itself and
    # changes nothing about the code an operator installs.
    #
    # Anything else is what happened to 1.0.0.4 — an archive holding materially
    # older code than the tag and changelog advertise, where correcting the
    # checksum publishes the mismatch instead of repairing it.
    local drift
    drift=$(git diff --name-only "$built_commit" "$tagged" -- \
        . ':(exclude)manifest.json' ':(exclude)manifest-jellyfin-12.json')

    if [ -n "$drift" ]; then
        fail "the archive was built from ${built_commit:0:12}, but tag $version points at ${tagged:0:12},
     and the difference is more than the catalogues:
$(printf '%s\n' "$drift" | sed 's/^/       /')
     The release notes and changelog describe the tag; the download is other code."
    else
        ok "the archive predates tag $version only by that release's own catalogue commit"
    fi
}

for manifest in "${MANIFESTS[@]}"; do
    [ -f "$manifest" ] || { echo "no such catalogue: $manifest" >&2; exit 2; }
    jq -e '.[0].versions | length > 0' "$manifest" >/dev/null \
        || { echo "$manifest advertises no versions" >&2; exit 2; }

    printf '\n%s########## %s ##########%s\n' "$C_BOLD" "$manifest" "$C_OFF"
    info "cache: $CACHE"

    while IFS=$'\t' read -r version url checksum abi; do
        audit_entry "$manifest" "$version" "$url" "$checksum" "$abi"
    done < <(jq -r '.[0].versions[] | [.version, .sourceUrl, .checksum, .targetAbi] | @tsv' "$manifest")
done

printf '\n'
if [ "$FAILURES" -gt 0 ]; then
    printf '%s%d problem(s) across %d retained entries.%s\n' \
        "$C_RED$C_BOLD" "$FAILURES" "$CHECKED" "$C_OFF"
    printf 'Remove the broken entry, or republish a byte-for-byte build of the tagged\n'
    printf 'commit and say in the entry that the asset was replaced.\n'
    exit 1
fi

# Two numbers, never one. Entries whose provenance could not be established were
# still checked for checksum, shape and metadata -- but saying "24 verified" when
# four of them were never traced to a commit is the shape of claim that let a
# mismatched entry sit in the catalogue for months in the first place.
if [ "$SKIPPED" -gt 0 ]; then
    printf '%s%d retained entries match the archives they serve, and %d of them were not traced to a commit.%s\n' \
        "$C_YELLOW$C_BOLD" "$CHECKED" "$SKIPPED" "$C_OFF"
    printf 'Checksum, archive shape and metadata: %d/%d. Source provenance: %d/%d.\n' \
        "$CHECKED" "$CHECKED" "$((CHECKED - SKIPPED))" "$CHECKED"
    exit 0
fi

printf '%s%d retained entries match the archives they serve, provenance included.%s\n' \
    "$C_GREEN$C_BOLD" "$CHECKED" "$C_OFF"
