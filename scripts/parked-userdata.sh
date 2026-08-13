#!/usr/bin/env bash
# Reports user data parked on items whose media file no longer exists.
#
# This is a blind spot the plugin does not cover. When a library folder drains
# completely, Jellyfin's unmount guard suppresses removal of the items that used
# to live there: the item stays in BaseItems, still holding its UserData rows,
# even though the file is gone. Those rows are not on the sentinel, so the
# analyzer never sees them. They only strand -- and only become recoverable --
# once something lands in that folder again and the next scan removes the item.
#
# Nothing here writes. It does not even open your live database: Jellyfin runs
# SQLite in its NoLock mode with the WAL index in the server's heap, and a second
# connection that opens and closes the database will checkpoint and truncate the
# -wal underneath it, taking the server down a few seconds later. So this copies
# the files first and reads only the copy. Safe to run with Jellyfin up.
#
# Usage: scripts/parked-userdata.sh [/path/to/jellyfin.db]
set -Eeuo pipefail

DB=${1:-}
if [ -z "$DB" ]; then
    for c in /var/lib/jellyfin/data/jellyfin.db \
             /config/data/jellyfin.db \
             "$HOME/.local/share/jellyfin/data/jellyfin.db"; do
        [ -f "$c" ] && { DB=$c; break; }
    done
fi
[ -n "$DB" ] && [ -f "$DB" ] || {
    echo "Could not find jellyfin.db. Pass its path as the first argument." >&2
    exit 1
}

command -v sqlite3 >/dev/null || { echo "sqlite3 is required." >&2; exit 1; }

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

# Plain byte copies. cp never opens a SQLite connection, so it cannot checkpoint
# or truncate anything the running server still depends on.
cp "$DB" "$WORK/db"
[ -f "$DB-wal" ] && cp "$DB-wal" "$WORK/db-wal"
[ -f "$DB-shm" ] && cp "$DB-shm" "$WORK/db-shm"

SENTINEL='00000000-0000-0000-0000-000000000001'
q() { sqlite3 -cmd '.timeout 10000' "$WORK/db" "$1"; }

echo "database: $DB"
echo

TOTAL=$(q "SELECT COUNT(*) FROM UserData;")
STRANDED=$(q "SELECT COUNT(*) FROM UserData WHERE lower(ItemId)='$SENTINEL';")
echo "UserData rows total:            $TOTAL"
echo "already stranded on sentinel:   $STRANDED   <- what the plugin recovers"
echo

# Every distinct path that currently carries user data, so existence is tested
# once per file rather than once per row.
echo "checking whether the media behind each item with user data still exists..."
PARKED_ROWS=0
PARKED_ITEMS=0
while IFS=$'\t' read -r path rows; do
    [ -n "$path" ] || continue
    if [ ! -e "$path" ]; then
        PARKED_ITEMS=$((PARKED_ITEMS + 1))
        PARKED_ROWS=$((PARKED_ROWS + rows))
        [ "$PARKED_ITEMS" -le 20 ] && printf '   missing: %s  (%s rows)\n' "$path" "$rows"
    fi
done < <(q "
    SELECT b.Path, COUNT(*)
    FROM UserData u JOIN BaseItems b ON b.Id = u.ItemId
    WHERE lower(u.ItemId) != '$SENTINEL' AND b.Path IS NOT NULL AND b.Path != ''
    GROUP BY b.Path;")

[ "$PARKED_ITEMS" -gt 20 ] && echo "   ... and $((PARKED_ITEMS - 20)) more"

echo
echo "items whose file is gone but still hold user data: $PARKED_ITEMS"
echo "user-data rows parked on them:                     $PARKED_ROWS"
echo
if [ "$PARKED_ROWS" -gt 0 ]; then
    cat <<'NOTE'
These rows are NOT recoverable by the plugin yet. They are not detached, so the
analyzer does not consider them. They will become recoverable on their own once
Jellyfin removes those items, which it defers while their folder looks empty.

To convert them now, put any file into the emptied folders and run a library
scan; the items get removed, the rows land on the sentinel, and the next nightly
run picks them up. Do that only for folders you know are genuinely drained, not
for ones on a mount that happens to be offline -- the guard exists for a reason.
NOTE
else
    echo "Nothing parked. Every item that holds user data still has its media."
fi
