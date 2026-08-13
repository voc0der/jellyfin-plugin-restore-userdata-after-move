#!/usr/bin/env bash
#
# Live end-to-end proof for the apply path.
#
# Stands up a disposable Jellyfin server from the official tarball, strands user
# data the way a real path change strands it, installs this plugin, runs the two
# scheduled tasks, and asserts what the design claims. Nothing here touches the
# machine's real server: it downloads its own, pins its own port, and keeps every
# byte it writes inside one scratch directory.
#
#   scripts/gap/gap.sh                 both server lines
#   scripts/gap/gap.sh 10.11.11        one line
#   scripts/gap/gap.sh --keep 12.0-rc5 keep the scratch tree for inspection
#
# Exit status is the result: 0 means every assertion held.
set -euo pipefail

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CACHE="${GAP_CACHE:-$HOME/.cache/jellyfin-gap}"
SCRATCH_ROOT="${GAP_SCRATCH:-}"
KEEP=0
PORT_BASE=""
LINES=()

# framework:package-version:tarball-url-path:default-port
readonly LINE_10="net9.0|10.11.11|stable/v10.11.11/amd64/jellyfin_10.11.11-amd64.tar.gz|18096"
readonly LINE_12="net10.0|12.0.0-rc5|preview/v12.0-rc5/amd64/jellyfin_12.0-rc5-amd64.tar.gz|18098"

readonly SENTINEL="00000000-0000-0000-0000-000000000001"
readonly ADMIN_PW="gap-admin-pw"
readonly VIEWER_PW="gap-viewer-pw"
readonly ANALYZE_KEY="UserDataRestoreAnalyze"
readonly APPLY_KEY="UserDataRestoreApply"

while [ $# -gt 0 ]; do
    case "$1" in
        --keep) KEEP=1 ;;
        --scratch) SCRATCH_ROOT="$2"; shift ;;
        --port) PORT_BASE="$2"; shift ;;
        --cache) CACHE="$2"; shift ;;
        10.11.11|12.0-rc5) LINES+=("$1") ;;
        both) LINES=(10.11.11 12.0-rc5) ;;
        -h|--help) sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \?//'; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
    shift
done
[ ${#LINES[@]} -gt 0 ] || LINES=(10.11.11 12.0-rc5)

# ---------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------

if [ -t 1 ]; then
    C_RED=$'\033[31m'; C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'
    C_BOLD=$'\033[1m'; C_DIM=$'\033[2m'; C_OFF=$'\033[0m'
else
    C_RED=''; C_GREEN=''; C_YELLOW=''; C_BOLD=''; C_DIM=''; C_OFF=''
fi

CHECKS=0

step() { printf '\n%s== %s%s\n' "$C_BOLD" "$*" "$C_OFF"; }
info() { printf '   %s%s%s\n' "$C_DIM" "$*" "$C_OFF"; }
warn() { printf '   %s! %s%s\n' "$C_YELLOW" "$*" "$C_OFF"; }
pass() { CHECKS=$((CHECKS + 1)); printf '   %s+%s %s\n' "$C_GREEN" "$C_OFF" "$*"; }

TOP_PID=$$

# Most of this harness runs inside command substitutions, where a bare `exit`
# would only end the subshell and let the run carry on against a half-built
# world. Signalling the top-level shell is what makes a failure actually stop.
die() {
    printf '\n%sFAILED%s %s\n' "$C_RED$C_BOLD" "$C_OFF" "$*" >&2
    [ -n "${SCRATCH:-}" ] && printf '  scratch:    %s\n' "$SCRATCH" >&2
    [ -n "${SERVER_LOG:-}" ] && printf '  server log: %s\n' "$SERVER_LOG" >&2
    kill -TERM "$TOP_PID" 2>/dev/null
    exit 1
}

# Every assertion in this harness funnels through here, so a failure always
# reports what was expected against what the server actually did.
require() {
    local what=$1 expected=$2 actual=$3
    if [ "$expected" != "$actual" ]; then
        die "$what
  expected: $expected
  actual:   $actual"
    fi
    pass "$what"
}

require_contains() {
    local what=$1 needle=$2 haystack=$3
    case "$haystack" in
        *"$needle"*) pass "$what" ;;
        *) die "$what
  expected to contain: $needle
  actual:              $haystack" ;;
    esac
}

# ---------------------------------------------------------------------------
# Preconditions
# ---------------------------------------------------------------------------

for tool in curl jq sqlite3 ffmpeg unzip tar dotnet; do
    command -v "$tool" >/dev/null || die "$tool is required and is not on PATH."
done

# ---------------------------------------------------------------------------
# HTTP
# ---------------------------------------------------------------------------

# Jellyfin 12.0 RC5 rejects X-Emby-Token and ?api_key=, so everything goes
# through the Authorization header, which both lines accept.
auth_header() {
    if [ -n "${TOKEN:-}" ]; then
        printf 'Authorization: MediaBrowser Client="gap", Device="harness", DeviceId="gap-harness", Version="1.0.0", Token="%s"' "$TOKEN"
    else
        printf 'Authorization: MediaBrowser Client="gap", Device="harness", DeviceId="gap-harness", Version="1.0.0"'
    fi
}

API_STATUS=0

# api <method> <path> [json-body] -> response body on stdout, status in API_STATUS.
api() {
    local method=$1 path=$2 body=${3-} raw status
    local -a args=(-sS -m 180 -X "$method" "$BASE$path" -H "$(auth_header)")
    if [ -n "$body" ]; then
        args+=(-H 'Content-Type: application/json' --data-binary "$body")
    fi

    raw=$(curl "${args[@]}" -w $'\n%{http_code}') || die "curl failed: $method $path"
    status=${raw##*$'\n'}
    API_STATUS=$status
    printf '%s' "${raw%$'\n'*}"

    case "$status" in
        2*) return 0 ;;
        *) return 1 ;;
    esac
}

# Same, but a non-2xx is fatal. Used for everything that is setup rather than
# an assertion about behaviour.
api_ok() {
    local out
    if ! out=$(api "$@"); then
        # A dead server produces the same "HTTP 000" as a network blip, and the
        # two want very different responses from whoever is reading this.
        if [ -n "$SERVER_PID" ] && ! kill -0 "$SERVER_PID" 2>/dev/null; then
            die "the server died during $1 $2.
  Last words:
$(grep -E '\[(ERR|FTL)\]' "$SERVER_LOG" | tail -3 | sed 's/^/    /')"
        fi

        die "$1 $2 returned HTTP $API_STATUS
  body: $(printf '%s' "$out" | head -c 400)"
    fi
    printf '%s' "$out"
}

json_escape() { printf '%s' "$1" | jq -Rs .; }

# ---------------------------------------------------------------------------
# Server lifecycle
# ---------------------------------------------------------------------------

SERVER_PID=""

fetch_server() {
    local url_path=$1 archive
    archive="$CACHE/$(basename "$url_path")"
    mkdir -p "$CACHE"

    if [ ! -s "$archive" ]; then
        info "downloading $(basename "$url_path")"
        curl -sSL --fail -o "$archive.part" "https://repo.jellyfin.org/files/server/linux/$url_path" \
            || die "could not download https://repo.jellyfin.org/files/server/linux/$url_path"
        mv "$archive.part" "$archive"
    fi

    SERVER_DIR="$CACHE/srv/$LINE"
    if [ ! -x "$SERVER_DIR/jellyfin/jellyfin" ]; then
        mkdir -p "$SERVER_DIR"
        tar xzf "$archive" -C "$SERVER_DIR"
    fi
    [ -x "$SERVER_DIR/jellyfin/jellyfin" ] || die "extracted tarball has no jellyfin executable"
}

# Jellyfin serves a startup splash page on the real port while the application
# is still coming up, and that page answers 200 on every route. Readiness is
# therefore "/System/Info/Public returns the JSON it is supposed to return",
# not "something answered".
server_ready() {
    curl -s -m 5 "$BASE/System/Info/Public" 2>/dev/null | jq -e '.Version' >/dev/null 2>&1
}

# Refuses to start on a port anything else is already answering. The machine
# running this may well have a real Jellyfin on 8096, and a harness that
# reconfigured somebody's actual library would be unforgivable.
assert_port_free() {
    local code i
    # A previous run's server can take a few seconds to let go of the socket, so
    # give it that. Anything still answering after is somebody else's.
    for i in $(seq 1 15); do
        code=$(curl -s -m 3 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/System/Info/Public" || true)
        if [ "$code" = "000" ]; then
            return 0
        fi
        sleep 1
    done
    die "something is already serving on port $PORT (HTTP $code). Refusing to touch it.
  Re-run with --port <free port> if that is not a server you want left alone."
}

write_network_config() {
    mkdir -p "$CONFIG"
    cat > "$CONFIG/network.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <InternalHttpPort>$PORT</InternalHttpPort>
  <PublicHttpPort>$PORT</PublicHttpPort>
  <EnableHttps>false</EnableHttps>
  <AutoDiscovery>false</AutoDiscovery>
  <EnableUPnP>false</EnableUPnP>
  <EnableRemoteAccess>false</EnableRemoteAccess>
</NetworkConfiguration>
XML
}

start_server() {
    "$SERVER_DIR/jellyfin/jellyfin" \
        --datadir "$DATA" --configdir "$CONFIG" --cachedir "$CACHEDIR" --logdir "$LOGDIR" \
        --nonetchange --service >> "$SERVER_LOG" 2>&1 &
    SERVER_PID=$!

    local i
    for i in $(seq 1 180); do
        if ! kill -0 "$SERVER_PID" 2>/dev/null; then
            die "server exited during startup; see $SERVER_LOG"
        fi
        server_ready && { info "server up on $PORT after ${i}s (pid $SERVER_PID)"; return 0; }
        sleep 1
    done
    die "server did not become ready on $PORT within 180s"
}

stop_server() {
    [ -n "$SERVER_PID" ] || return 0
    kill "$SERVER_PID" 2>/dev/null || true

    local i
    for i in $(seq 1 60); do
        kill -0 "$SERVER_PID" 2>/dev/null || break
        sleep 1
    done
    kill -9 "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
    SERVER_PID=""
}

restart_server() {
    stop_server
    start_server
}

cleanup() {
    stop_server
    if [ "$KEEP" = "0" ] && [ -n "${SCRATCH:-}" ] && [ "${RUN_OK:-0}" = "1" ]; then
        rm -rf "$SCRATCH"
    fi
}
trap cleanup EXIT INT TERM

# ---------------------------------------------------------------------------
# Database
# ---------------------------------------------------------------------------

# Reads the database, and only ever with the server stopped.
#
# Opening this database from another process while Jellyfin is running does not
# just give a wrong answer -- it takes the server down. Measured, not feared:
#
# Jellyfin runs SQLite with its NoLock behaviour, so there is no -shm file and
# the WAL index lives in the server's heap. A second connection that opens the
# database checkpoints and truncates the -wal when it closes. The server's
# in-memory index still points into the frames that were just deleted, so its
# next read runs off the end of a now-empty file:
#
#   pread64(jellyfin.db-wal, 4096 bytes @ 679856) -> 0, filesize=0
#
# SQLite turns that short read into SQLITE_IOERR_SHORT_READ and reports it as
# "disk I/O error". Every later query fails the same way and the server dies
# with an unhandled exception during its next library scan. It reads as a
# storage fault and is nothing of the kind.
#
# So: every read happens with the server stopped, and this guard is what keeps
# it that way.
db() {
    [ -z "$SERVER_PID" ] || die "internal error: tried to read the database while the server was running.
  That truncates the WAL underneath Jellyfin and kills the server a few seconds later."

    sqlite3 -cmd '.timeout 10000' "$DBFILE" "$1" \
        || die "query against $DBFILE failed: $1"
}

# Finds the database by name alone. Deliberately does not open it: this runs
# with the server up, and opening it here is exactly the mistake described
# above. Whether it really is the right database is settled by
# assert_readable_database, in the first stopped window.
locate_db() {
    local candidate
    while IFS= read -r candidate; do
        if [ "$(basename "$candidate")" = "jellyfin.db" ] || [ "$(basename "$candidate")" = "library.db" ]; then
            DBFILE="$candidate"
            info "database: ${DBFILE#$SCRATCH/}"
            return 0
        fi
    done < <(find "$DATA" -maxdepth 3 -name '*.db' 2>/dev/null)
    die "no jellyfin.db or library.db under $DATA"
}

# Jellyfin's EF provider stores Guid as TEXT on SQLite. Verified rather than
# assumed, so a future provider change surfaces here instead of silently
# matching nothing and reporting a clean run.
assert_readable_database() {
    local kind rows table
    table=$(db "SELECT name FROM sqlite_master WHERE type='table' AND name='UserData';")
    [ "$table" = "UserData" ] \
        || die "$DBFILE has no UserData table, so it is not the database the server keeps user state in."

    rows=$(db "SELECT COUNT(*) FROM UserData;")
    [ "${rows:-0}" -gt 0 ] \
        || die "UserData is empty after seeding state for two users on three titles.
  Either the seeding did not reach the database, or this is not the database the server writes."

    kind=$(db "SELECT typeof(ItemId) FROM UserData LIMIT 1;")
    [ "$kind" = "text" ] || die "UserData.ItemId is stored as '$kind', not text. This harness's SQL cannot read it; teach db() the new encoding."
    pass "the database is readable and holds $rows rows"
}

sentinel_rows() {
    db "SELECT lower(UserId) || '|' || CustomDataKey || '|' || COALESCE(Played,'') || '|' || COALESCE(PlayCount,'')
        || '|' || COALESCE(PlaybackPositionTicks,'') || '|' || COALESCE(IsFavorite,'') || '|' || COALESCE(Rating,'')
        || '|' || COALESCE(LastPlayedDate,'') || '|' || COALESCE(Likes,'') || '|' || COALESCE(AudioStreamIndex,'')
        || '|' || COALESCE(SubtitleStreamIndex,'') || '|' || COALESCE(RetentionDate,'')
        FROM UserData WHERE lower(ItemId) = '$SENTINEL' ORDER BY lower(UserId), CustomDataKey;"
}

sentinel_digest() { sentinel_rows | sha256sum | cut -d' ' -f1; }
sentinel_count() { db "SELECT COUNT(*) FROM UserData WHERE lower(ItemId) = '$SENTINEL';"; }

keys_for() {
    local user=$1 item=$2
    db "SELECT CustomDataKey FROM UserData
        WHERE lower(ItemId) = lower('$item') AND lower(UserId) = lower('$user')
        ORDER BY CustomDataKey;"
}

# ---------------------------------------------------------------------------
# Setup wizard and users
# ---------------------------------------------------------------------------

run_wizard() {
    api_ok POST /Startup/Configuration \
        '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
    api_ok GET /Startup/User >/dev/null
    api_ok POST /Startup/User "{\"Name\":\"admin\",\"Password\":$(json_escape "$ADMIN_PW")}" >/dev/null
    api_ok POST /Startup/Complete >/dev/null

    authenticate admin "$ADMIN_PW"
    ADMIN_ID=$(api_ok GET /Users/Me | jq -r .Id)
    [ -n "$ADMIN_ID" ] && [ "$ADMIN_ID" != "null" ] || die "could not resolve the admin user id"

    local created
    created=$(api_ok POST /Users/New "{\"Name\":\"viewer\",\"Password\":$(json_escape "$VIEWER_PW")}")
    VIEWER_ID=$(printf '%s' "$created" | jq -r .Id)
    [ -n "$VIEWER_ID" ] && [ "$VIEWER_ID" != "null" ] || die "could not create the viewer user"

    info "users: admin=$ADMIN_ID viewer=$VIEWER_ID"
}

authenticate() {
    local name=$1 password=$2 body
    TOKEN=""
    body=$(api_ok POST /Users/AuthenticateByName \
        "{\"Username\":$(json_escape "$name"),\"Pw\":$(json_escape "$password")}")
    TOKEN=$(printf '%s' "$body" | jq -r .AccessToken)
    [ -n "$TOKEN" ] && [ "$TOKEN" != "null" ] || die "authentication as $name produced no token"
}

# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------

make_video() {
    local path=$1
    mkdir -p "$(dirname "$path")"
    ffmpeg -nostdin -loglevel error -y -f lavfi -i color=c=black:s=32x32:d=1 \
        -c:v libx264 -pix_fmt yuv420p "$path" || die "ffmpeg could not write $path"
}

# Internet providers are off, so these IDs come only from the NFO. That is the
# point: the plugin's whole matching story rests on Jellyfin hydrating provider
# IDs onto items, and a fixture that fetched them from TMDb would not prove the
# NFO path works.
write_movie_nfo() {
    local dir=$1 title=$2 year=$3 tmdb=$4 imdb=$5
    cat > "$dir/movie.nfo" <<XML
<?xml version="1.0" encoding="utf-8"?>
<movie>
  <title>$title</title>
  <year>$year</year>
  <uniqueid type="tmdb">$tmdb</uniqueid>
  <uniqueid type="imdb" default="true">$imdb</uniqueid>
</movie>
XML
}

write_series_nfo() {
    local dir=$1 title=$2 tmdb=$3 imdb=$4
    cat > "$dir/tvshow.nfo" <<XML
<?xml version="1.0" encoding="utf-8"?>
<tvshow>
  <title>$title</title>
  <uniqueid type="tmdb">$tmdb</uniqueid>
  <uniqueid type="imdb" default="true">$imdb</uniqueid>
</tvshow>
XML
}

make_fixtures() {
    mkdir -p "$MEDIA"/hot/movies "$MEDIA"/cold/movies "$MEDIA"/hot/shows "$MEDIA"/cold/shows

    local d
    d="$MEDIA/hot/movies/The Matrix (1999)"
    make_video "$d/The Matrix (1999).mp4"
    write_movie_nfo "$d" "The Matrix" 1999 603 tt0133093

    d="$MEDIA/hot/movies/Fight Club (1999)"
    make_video "$d/Fight Club (1999).mp4"
    write_movie_nfo "$d" "Fight Club" 1999 550 tt0137523

    d="$MEDIA/hot/shows/Breaking Bad (2008)"
    make_video "$d/Season 01/Breaking Bad (2008) S01E01.mp4"
    write_series_nfo "$d" "Breaking Bad" 1396 tt0903747
}

create_libraries() {
    local movies_options shows_options
    movies_options=$(jq -nc --arg hot "$MEDIA/hot/movies" --arg cold "$MEDIA/cold/movies" '{
        LibraryOptions: {
            PathInfos: [{Path: $hot}, {Path: $cold}],
            EnableRealtimeMonitor: false,
            SaveLocalMetadata: false,
            EnableInternetProviders: false,
            TypeOptions: [{Type: "Movie", MetadataFetchers: [], MetadataFetcherOrder: [],
                           ImageFetchers: [], ImageFetcherOrder: []}]
        }}')
    shows_options=$(jq -nc --arg hot "$MEDIA/hot/shows" --arg cold "$MEDIA/cold/shows" '{
        LibraryOptions: {
            PathInfos: [{Path: $hot}, {Path: $cold}],
            EnableRealtimeMonitor: false,
            SaveLocalMetadata: false,
            EnableInternetProviders: false,
            TypeOptions: [{Type: "Series", MetadataFetchers: [], MetadataFetcherOrder: [],
                           ImageFetchers: [], ImageFetcherOrder: []},
                          {Type: "Season", MetadataFetchers: [], MetadataFetcherOrder: [],
                           ImageFetchers: [], ImageFetcherOrder: []},
                          {Type: "Episode", MetadataFetchers: [], MetadataFetcherOrder: [],
                           ImageFetchers: [], ImageFetcherOrder: []}]
        }}')

    api_ok POST "/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=false" "$movies_options" >/dev/null
    api_ok POST "/Library/VirtualFolders?name=Shows&collectionType=tvshows&refreshLibrary=false" "$shows_options" >/dev/null
}

# ---------------------------------------------------------------------------
# Scheduled tasks
# ---------------------------------------------------------------------------

task_id_by_key() {
    api_ok GET /ScheduledTasks | jq -r --arg key "$1" '.[] | select(.Key == $key) | .Id' | head -1
}

task_field() {
    api_ok GET "/ScheduledTasks/$1" | jq -r "$2"
}

# Runs a task and waits for it. Waits on the execution-result timestamp rather
# than on state alone: a task that has not started yet is also Idle.
run_task() {
    local id=$1 label=$2 before after state i
    before=$(task_field "$id" '.LastExecutionResult.EndTimeUtc // "none"')

    api_ok POST "/ScheduledTasks/Running/$id" >/dev/null

    for i in $(seq 1 600); do
        state=$(task_field "$id" '.State')
        after=$(task_field "$id" '.LastExecutionResult.EndTimeUtc // "none"')
        if [ "$state" = "Idle" ] && [ "$after" != "$before" ]; then
            TASK_STATUS=$(task_field "$id" '.LastExecutionResult.Status // "none"')
            TASK_ERROR=$(task_field "$id" '.LastExecutionResult.ErrorMessage // ""')
            info "$label -> $TASK_STATUS"
            return 0
        fi
        sleep 1
    done
    die "$label did not finish within 600s"
}

run_task_by_key() {
    local key=$1 label=$2 id
    id=$(task_id_by_key "$key")
    [ -n "$id" ] || die "no scheduled task with key $key is registered"
    run_task "$id" "$label"
}

scan_library() {
    local id
    id=$(task_id_by_key RefreshLibrary)
    [ -n "$id" ] || id=$(api_ok GET /ScheduledTasks | jq -r '.[] | select(.Name == "Scan Media Library") | .Id' | head -1)
    [ -n "$id" ] || die "could not find the library scan task"
    run_task "$id" "library scan"
}

# ---------------------------------------------------------------------------
# Items and user data
# ---------------------------------------------------------------------------

find_item() {
    local kind=$1 name=$2
    api_ok GET "/Items?userId=$ADMIN_ID&recursive=true&includeItemTypes=$kind&fields=ProviderIds,Path" \
        | jq -r --arg n "$name" '.Items[] | select(.Name == $n) | .Id' | head -1
}

# Episodes are named by whatever Jellyfin parsed out of the filename, which is
# not worth pinning down: there is exactly one in this library.
find_only_episode() {
    api_ok GET "/Items?userId=$ADMIN_ID&recursive=true&includeItemTypes=Episode" | jq -r '.Items[0].Id // ""'
}

item_provider_id() {
    api_ok GET "/Items?userId=$ADMIN_ID&ids=$1&fields=ProviderIds" \
        | jq -r --arg p "$2" '.Items[0].ProviderIds[$p] // "none"'
}

# POST /UserItems/{id}/UserData is the current route; the /Users/{uid}/... form
# is the pre-10.9 spelling and is still accepted on some builds.
set_user_data() {
    local user=$1 item=$2 body=$3 out
    if out=$(api POST "/UserItems/$item/UserData?userId=$user" "$body"); then
        printf '%s' "$out"
        return 0
    fi
    if out=$(api POST "/Users/$user/Items/$item/UserData" "$body"); then
        printf '%s' "$out"
        return 0
    fi
    die "neither user-data update route was accepted (HTTP $API_STATUS): $out"
}

get_user_data() {
    local user=$1 item=$2
    api_ok GET "/Items?userId=$user&ids=$item&fields=ProviderIds" | jq -c '(.Items[0].UserData // {})'
}

# The six recoverable fields, normalized so a plan and an API response compare
# as strings. Dates go through epoch seconds because the two sides disagree
# about trailing precision, not about the instant.
normalize_state() {
    jq -r '[(.Played // false),
            (.PlayCount // 0),
            (.PlaybackPositionTicks // 0),
            (.IsFavorite // false),
            (if .Rating == null then "none" else (.Rating | tostring) end),
            (.LastPlayedDate // "none")] | @tsv' \
        | awk -F'\t' '{
            d = $6
            if (d != "none") { sub(/\.[0-9]+/, "", d); sub(/\+00:00$/, "Z", d) }
            printf "played=%s count=%s ticks=%s fav=%s rating=%s last=%s", $1, $2, $3, $4, $5, d
        }'
}

plan_state() {
    jq -c '{Played: .played, PlayCount: .playCount, PlaybackPositionTicks: .playbackPositionTicks,
            IsFavorite: .isFavorite, Rating: .rating, LastPlayedDate: .lastPlayedDate}'
}

# ---------------------------------------------------------------------------
# Plugin
# ---------------------------------------------------------------------------

build_plugin() {
    [ -n "${GAP_SKIP_BUILD:-}" ] && { info "reusing the archives already in artifacts/"; return 0; }
    local log="${TMPDIR:-/tmp}/jellyfin-gap-build.log"
    info "running build.sh (tests included)"
    (cd "$REPO" && ./build.sh) > "$log" 2>&1 \
        || { tail -30 "$log" >&2; die "build.sh failed; see $log"; }
}

install_plugin() {
    local archive
    archive=$(find "$REPO/artifacts" -maxdepth 1 -name "*jellyfin-$PACKAGE.zip" | head -1)
    [ -n "$archive" ] || die "no plugin archive for jellyfin-$PACKAGE in $REPO/artifacts"

    mkdir -p "$DATA/plugins"
    unzip -qo "$archive" -d "$DATA/plugins" || die "could not unpack $archive"
    info "installed $(basename "$archive")"
}

newest_plan() {
    find "$DATA" -name 'plan-*.json' -printf '%f\t%p\n' 2>/dev/null | sort -r | head -1 | cut -f2
}

# ---------------------------------------------------------------------------
# Phases
# ---------------------------------------------------------------------------

seed_state() {
    step "Seeding user data on three titles for two users"

    set_user_data "$ADMIN_ID" "$MOVIE_ID" \
        '{"Played":true,"PlayCount":3,"PlaybackPositionTicks":12345,"IsFavorite":true,"Rating":10,"LastPlayedDate":"2026-01-01T12:00:00.0000000Z"}' >/dev/null
    set_user_data "$VIEWER_ID" "$MOVIE_ID" \
        '{"Played":false,"PlayCount":0,"PlaybackPositionTicks":600000000,"IsFavorite":true,"Rating":1}' >/dev/null

    set_user_data "$ADMIN_ID" "$EPISODE_ID" \
        '{"Played":true,"PlayCount":2,"PlaybackPositionTicks":98765,"IsFavorite":true,"Rating":8,"LastPlayedDate":"2026-02-02T20:00:00.0000000Z"}' >/dev/null
    set_user_data "$VIEWER_ID" "$EPISODE_ID" \
        '{"Played":false,"PlayCount":0,"PlaybackPositionTicks":4200000000,"IsFavorite":false,"Rating":3}' >/dev/null

    set_user_data "$ADMIN_ID" "$SPOILER_ID" \
        '{"Played":true,"PlayCount":5,"PlaybackPositionTicks":777,"IsFavorite":false,"Rating":6}' >/dev/null
    set_user_data "$VIEWER_ID" "$SPOILER_ID" \
        '{"Played":true,"PlayCount":1,"PlaybackPositionTicks":888,"IsFavorite":true,"Rating":2}' >/dev/null

    # The state as the server actually stored it, which is what recovery has to
    # reproduce. Reading it back rather than assuming the posted values survived
    # is the difference between testing the plugin and testing our own fixture.
    EXPECT_MOVIE_ADMIN=$(get_user_data "$ADMIN_ID" "$MOVIE_ID" | normalize_state)
    EXPECT_MOVIE_VIEWER=$(get_user_data "$VIEWER_ID" "$MOVIE_ID" | normalize_state)
    EXPECT_EPISODE_ADMIN=$(get_user_data "$ADMIN_ID" "$EPISODE_ID" | normalize_state)
    EXPECT_EPISODE_VIEWER=$(get_user_data "$VIEWER_ID" "$EPISODE_ID" | normalize_state)

    info "movie/admin:   $EXPECT_MOVIE_ADMIN"
    info "movie/viewer:  $EXPECT_MOVIE_VIEWER"
    info "episode/admin: $EXPECT_EPISODE_ADMIN"
    info "episode/viewer:$EXPECT_EPISODE_VIEWER"

    [ "$EXPECT_MOVIE_ADMIN" != "$EXPECT_MOVIE_VIEWER" ] \
        || die "the two users ended up with identical movie state; the fixture proves nothing about per-user recovery"
}

# The relocation that strands the rows. Every title moves to a previously unused
# path under the other configured root.
#
# When exactly Jellyfin removes the vacated item is a server behaviour, not a
# contract, so this rescans until the rows actually detach. Observed on 10.11.11:
# moving everything out empties the source root, Jellyfin then treats that root
# as inaccessible and suppresses removal entirely, and no amount of rescanning
# helps. Putting an unrelated file back in the vacated root makes it plainly
# present again, and the next pass removes the old items and detaches their rows.
strand_data() {
    step "Stranding user data by moving every title to a new path"

    mv "$MEDIA/hot/movies/The Matrix (1999)" "$MEDIA/cold/movies/The Matrix (1999)"
    mv "$MEDIA/hot/movies/Fight Club (1999)" "$MEDIA/cold/movies/Fight Club (1999)"
    mv "$MEDIA/hot/shows/Breaking Bad (2008)" "$MEDIA/cold/shows/Breaking Bad (2008)"

    local attempt count=0
    for attempt in 1 2 3 4; do
        scan_library
        scan_library

        stop_server
        count=$(sentinel_count)
        start_server
        authenticate admin "$ADMIN_PW"

        info "after scan pass $attempt: $count detached rows"
        if [ "$count" -ge 6 ]; then
            break
        fi

        # Jellyfin suppresses removal while a library location looks
        # inaccessible, and an emptied root can look exactly like that. A decoy
        # file makes the root plainly present again.
        if [ "$attempt" = "1" ]; then
            warn "rows have not detached; making the vacated roots non-empty"
            make_video "$MEDIA/hot/movies/Decoy (2001)/Decoy (2001).mp4"
            make_video "$MEDIA/hot/shows/Decoy Show (2001)/Season 01/Decoy Show (2001) S01E01.mp4"
        fi
    done

    [ "$count" -ge 6 ] || die "only $count rows detached after four scan passes; the harness never reproduced the condition under test"
    pass "$count rows detached onto the sentinel item"

    NEW_MOVIE_ID=$(find_item Movie "The Matrix")
    NEW_EPISODE_ID=$(find_only_episode)
    NEW_SPOILER_ID=$(find_item Movie "Fight Club")

    [ -n "$NEW_MOVIE_ID" ] || die "the moved movie is not in the library any more"
    [ -n "$NEW_EPISODE_ID" ] || die "the moved episode is not in the library any more"
    [ -n "$NEW_SPOILER_ID" ] || die "the second moved movie is not in the library any more"

    # If Jellyfin reattached the rows instead of stranding them, there is no
    # condition left to test and a green run would mean nothing.
    [ "$NEW_MOVIE_ID" != "$MOVIE_ID" ] \
        || die "the movie kept its item id, so nothing was stranded. Jellyfin reattached the rows and there is no gap to test."
    [ "$NEW_EPISODE_ID" != "$EPISODE_ID" ] \
        || die "the episode kept its item id, so its rows were reattached rather than stranded."
    pass "the movie and episode came back as new items"

    local empty="played=false count=0 ticks=0 fav=false rating=none last=none"
    require "the new movie item carries no user state" \
        "$empty" "$(get_user_data "$ADMIN_ID" "$NEW_MOVIE_ID" | normalize_state)"
    require "the new episode item carries no user state" \
        "$empty" "$(get_user_data "$VIEWER_ID" "$NEW_EPISODE_ID" | normalize_state)"
}

analyze() {
    run_task_by_key "$ANALYZE_KEY" "analysis"
    require "analysis completed" Completed "$TASK_STATUS"

    PLAN=$(newest_plan)
    [ -n "$PLAN" ] || die "the analysis wrote no plan file"
    info "plan: $(basename "$PLAN")"

    require "analysis left UserData byte-for-byte unchanged" true "$(jq -r '.readOnlyProof.unchanged' "$PLAN")"
}

verify_restored() {
    local label=$1 user=$2 item=$3 expected=$4 actual
    actual=$(get_user_data "$user" "$item" | normalize_state)
    require "$label" "$expected" "$actual"
}

# The claim under test: Jellyfin fans a save out across every key the item
# reports, so a later move that strands a different key still finds the state.
# If it only wrote one key, recovery would work once and never again.
verify_key_fanout() {
    step "Every key the target reports carries the restored row"

    local user item keys key present
    while IFS=$'\t' read -r user item keys; do
        present=$(keys_for "$user" "$item")
        for key in $(printf '%s' "$keys" | tr ',' ' '); do
            case "$present" in
                *"$key"*) ;;
                *) die "item $item has no UserData row for key '$key' after the restore
  keys the item reports: $keys
  keys present in the database: $(printf '%s' "$present" | tr '\n' ' ')" ;;
            esac
        done
        pass "$(printf '%s' "$keys" | tr ',' ' ' | wc -w) keys present for item ${item:0:8} user ${user:0:8}"
    done < <(jq -r '.candidates[] | select(.reason == "already_applied" or .reason == "ready")
                    | [.userId, .targetItemId, (.targetKeys | join(","))] | @tsv' "$PLAN")
}

# ---------------------------------------------------------------------------
# One server line, start to finish
# ---------------------------------------------------------------------------

run_line() {
    LINE=$1
    local spec
    case "$LINE" in
        10.11.11) spec=$LINE_10 ;;
        12.0-rc5) spec=$LINE_12 ;;
    esac
    IFS='|' read -r FRAMEWORK PACKAGE URL_PATH PORT <<< "$spec"
    [ -n "$PORT_BASE" ] && { PORT=$PORT_BASE; PORT_BASE=$((PORT_BASE + 2)); }

    printf '\n%s########## Jellyfin %s (%s) ##########%s\n' "$C_BOLD" "$LINE" "$FRAMEWORK" "$C_OFF"

    SCRATCH="${SCRATCH_ROOT:-$(mktemp -d -t jellyfin-gap-XXXXXX)}/$LINE"
    mkdir -p "$SCRATCH"
    DATA="$SCRATCH/data"; CONFIG="$SCRATCH/config"; CACHEDIR="$SCRATCH/cache"
    LOGDIR="$SCRATCH/log"; MEDIA="$SCRATCH/media"
    SERVER_LOG="$SCRATCH/server.log"
    BASE="http://127.0.0.1:$PORT"
    mkdir -p "$DATA" "$CONFIG" "$CACHEDIR" "$LOGDIR" "$MEDIA"
    info "scratch: $SCRATCH"

    step "Starting a disposable Jellyfin $LINE"
    fetch_server "$URL_PATH"
    assert_port_free
    write_network_config
    start_server
    run_wizard
    locate_db

    step "Building the library"
    make_fixtures
    create_libraries
    scan_library

    MOVIE_ID=$(find_item Movie "The Matrix")
    SPOILER_ID=$(find_item Movie "Fight Club")
    EPISODE_ID=$(find_only_episode)

    [ -n "$MOVIE_ID" ] || die "the movie fixture did not import"
    [ -n "$EPISODE_ID" ] || die "the episode fixture did not import"
    [ -n "$SPOILER_ID" ] || die "the second movie fixture did not import"

    # Without provider IDs on the item there is nothing but a GUID to match on,
    # and the whole run would report a legitimate-looking "nothing recoverable".
    require "the movie carries its NFO IMDb id" tt0133093 "$(item_provider_id "$MOVIE_ID" Imdb)"
    require "the movie carries its NFO TMDb id" 603 "$(item_provider_id "$MOVIE_ID" Tmdb)"

    seed_state

    step "Installing the plugin"
    install_plugin
    stop_server
    assert_readable_database
    start_server
    authenticate admin "$ADMIN_PW"

    [ -n "$(task_id_by_key "$ANALYZE_KEY")" ] || die "the analysis task did not register; the plugin did not load"
    [ -n "$(task_id_by_key "$APPLY_KEY")" ] || die "the apply task did not register"
    pass "both scheduled tasks registered"

    strand_data

    local sentinel_before sentinel_count_before
    stop_server
    sentinel_before=$(sentinel_digest)
    sentinel_count_before=$(sentinel_count)
    start_server
    authenticate admin "$ADMIN_PW"

    step "Analysis, with nothing configured"
    analyze
    local ready
    ready=$(jq -r '.summary.candidateCounts.ready' "$PLAN")
    [ "$ready" -ge 4 ] || die "the analysis found $ready ready candidates; expected at least 4 (two titles x two users).
Read $PLAN for why."
    pass "$ready candidates ready, from a plugin that was never configured"
    require "the plan proposes one write per ready candidate" "$ready" "$(jq -r '.writes | length' "$PLAN")"

    # --- preflight refuses a plan the world has moved out from under -------
    step "Preflight refuses a plan whose preconditions changed"
    set_user_data "$ADMIN_ID" "$NEW_SPOILER_ID" '{"Played":true,"PlayCount":99,"PlaybackPositionTicks":1,"IsFavorite":false}' >/dev/null
    set_user_data "$VIEWER_ID" "$NEW_SPOILER_ID" '{"Played":true,"PlayCount":98,"PlaybackPositionTicks":2,"IsFavorite":false}' >/dev/null

    run_task_by_key "$APPLY_KEY" "apply (expected to refuse)"
    require "the apply failed" Failed "$TASK_STATUS"
    require_contains "it said why" "Preflight refused this run" "$TASK_ERROR"

    verify_restored "the movie was left untouched by the refused run" \
        "$ADMIN_ID" "$NEW_MOVIE_ID" "played=false count=0 ticks=0 fav=false rating=none last=none"
    require "a refused run is all-or-nothing: no writes landed" \
        "played=false count=0 ticks=0 fav=false rating=none last=none" \
        "$(get_user_data "$VIEWER_ID" "$NEW_EPISODE_ID" | normalize_state)"

    stop_server
    require "the stranded rows are still exactly as they were" "$sentinel_before" "$(sentinel_digest)"
    start_server
    authenticate admin "$ADMIN_PW"

    # Everything from here on is the successful path, so the log from this point
    # forward is expected to be clean. The refused run above logs errors on
    # purpose, and counting those would make the health check meaningless.
    local log_mark
    log_mark=$(wc -l < "$SERVER_LOG")

    # --- the real thing ----------------------------------------------------
    step "Applying"
    analyze
    require "the conflicting title is excluded, the rest still ready" 4 "$(jq -r '.writes | length' "$PLAN")"

    run_task_by_key "$APPLY_KEY" "apply"
    require "the apply completed" Completed "$TASK_STATUS"

    step "The data is back"
    verify_restored "movie / admin"    "$ADMIN_ID"  "$NEW_MOVIE_ID"   "$EXPECT_MOVIE_ADMIN"
    verify_restored "movie / viewer"   "$VIEWER_ID" "$NEW_MOVIE_ID"   "$EXPECT_MOVIE_VIEWER"
    verify_restored "episode / admin"  "$ADMIN_ID"  "$NEW_EPISODE_ID" "$EXPECT_EPISODE_ADMIN"
    verify_restored "episode / viewer" "$VIEWER_ID" "$NEW_EPISODE_ID" "$EXPECT_EPISODE_VIEWER"

    step "The stranded rows were not consumed"
    stop_server
    require "sentinel row count is unchanged" "$sentinel_count_before" "$(sentinel_count)"
    require "sentinel rows are byte-for-byte unchanged" "$sentinel_before" "$(sentinel_digest)"

    verify_key_fanout

    step "It survives a restart"
    start_server
    authenticate admin "$ADMIN_PW"
    verify_restored "movie / admin, after restart"    "$ADMIN_ID"  "$NEW_MOVIE_ID"   "$EXPECT_MOVIE_ADMIN"
    verify_restored "episode / viewer, after restart" "$VIEWER_ID" "$NEW_EPISODE_ID" "$EXPECT_EPISODE_VIEWER"

    step "Running it twice changes nothing"
    analyze
    require "nothing is ready any more" 0 "$(jq -r '.summary.candidateCounts.ready' "$PLAN")"
    require "the recovered pairs are recognised as already applied" 4 "$(jq -r '.summary.candidateCounts.already_applied' "$PLAN")"
    require "the plan proposes no writes" 0 "$(jq -r '.writes | length' "$PLAN")"

    run_task_by_key "$APPLY_KEY" "apply (second time)"
    require "the second apply completed" Completed "$TASK_STATUS"
    verify_restored "movie / admin is unchanged by the second apply" \
        "$ADMIN_ID" "$NEW_MOVIE_ID" "$EXPECT_MOVIE_ADMIN"

    step "The server is healthy afterwards"
    scan_library
    require "a library scan after the apply still completes" Completed "$TASK_STATUS"

    stop_server
    require "and the stranded rows are still there at the end of it all" "$sentinel_before" "$(sentinel_digest)"

    local complaints
    complaints=$(tail -n "+$((log_mark + 1))" "$SERVER_LOG" | grep -cE '\[(ERR|FTL)\]' || true)
    require "the server logged nothing at error level once the apply began" 0 "$complaints"

    stop_server
    printf '\n%s%s: all assertions held%s\n' "$C_GREEN$C_BOLD" "$LINE" "$C_OFF"
}

# ---------------------------------------------------------------------------

step "Building the plugin"
build_plugin

for line in "${LINES[@]}"; do
    run_line "$line"
done

RUN_OK=1
printf '\n%s%d assertions passed across %d server line(s).%s\n' \
    "$C_GREEN$C_BOLD" "$CHECKS" "${#LINES[@]}" "$C_OFF"
