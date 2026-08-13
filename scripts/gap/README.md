# Live proof for the apply path

`gap.sh` is the answer to the one question the unit tests cannot reach: does the
plugin actually put the data back, on a real Jellyfin server, through Jellyfin's
own user-data manager?

Everything below the HTTP boundary is covered by
[`tests/`](../../tests) — matching, classification, plan hashing, preflight
reconciliation, all of it, with no server in sight. What no amount of that can
tell you is whether `IUserDataManager.SaveUserData` lands where the design says
it lands, whether Jellyfin's key fan-out behaves, and whether the stranded rows
survive the round trip. This script finds out.

```sh
scripts/gap/gap.sh                    # both server lines
scripts/gap/gap.sh 10.11.11           # one
scripts/gap/gap.sh --keep 12.0-rc5    # keep the scratch tree afterwards
```

Exit status is the result. Zero means every assertion held.

## What it does

For each server line it stands up a throwaway Jellyfin from the official
tarball, builds a small library, strands its user data the way a path change
strands it, installs this plugin, and runs the two scheduled tasks.

The fixture is three titles across two libraries that each span a hot and a cold
root, with internet providers disabled so provider IDs come only from local NFO:

| Title | Provider IDs | Role |
|---|---|---|
| The Matrix (1999) | `imdb tt0133093`, `tmdb 603` | recovered |
| Breaking Bad S01E01 | series `imdb tt0903747`, `tmdb 1396` | recovered |
| Fight Club (1999) | `imdb tt0137523`, `tmdb 550` | deliberately spoiled |

Two users get different state on each, so a restore that crossed users or
collapsed them into one would be caught.

Stranding is the real mechanism, not a hand-written row: every title moves to a
previously unused path, and the harness rescans until Jellyfin removes the old
items and detaches their rows onto the sentinel. That takes a nudge — emptying a
library root makes Jellyfin treat it as inaccessible and suppress removal
altogether, so the script drops an unrelated file back into the vacated roots and
scans again. If Jellyfin reattaches the rows instead — which it does under other
move patterns — the script says so and stops, because a green run against a
library that was never broken proves nothing.

## What it asserts

- Both scheduled tasks register, and the analysis finds the stranded state
  **with nothing configured**. No libraries selected, no path prefixes typed.
- The analysis leaves `UserData` byte-for-byte unchanged.
- Preflight **refuses** a plan whose preconditions moved, and a refused run is
  all-or-nothing: not one of the other writes lands.
- The apply restores each user's state on each title, field for field, matching
  what the server held before the move — not what the harness posted.
- Every key the target item reports carries a row afterwards, so the next move
  finds the state too.
- The stranded rows are still there, byte for byte, with the same digest and the
  same count. The plugin reads them; it never consumes them.
- The restored state survives a server restart.
- A second run is a no-op: everything classifies `already_applied`, the plan
  proposes zero writes, and applying again changes nothing.
- A library scan afterwards completes, and the server logs nothing at error
  level once the apply begins.

## One thing worth knowing before you extend it

**You cannot read Jellyfin's database while Jellyfin is running.** Not "you
shouldn't" — you get the wrong answer. With the server up there is no `-wal` file
on disk, the main database does not grow, and a second process querying it sees
an empty `UserData` table. The rows appear only after the server shuts down.

That is the worst possible failure mode for a harness, because it does not error:
every assertion about stranded rows would pass against a table that reads as
empty. So `gap.sh` stops the server around each database read, and `db()` refuses
outright if the server is up. Keep that guard.

## Requirements

`curl`, `jq`, `sqlite3`, `ffmpeg`, `unzip`, `tar`, `dotnet`, and network access
to `repo.jellyfin.org` for the first run. Servers are cached under
`~/.cache/jellyfin-gap`, so later runs are offline and much faster.

## What it will not do

It downloads its own server, pins its own port, and keeps every byte it writes
inside one scratch directory. Before starting it probes the port it intends to
use and **refuses to run if anything answers** — if you have a real Jellyfin on
this machine, the harness will not go near it. Use `--port` to move it.

`--keep` leaves the scratch tree in place: server log, database, media, and
every plan the run produced.

## Reading a failure

Every assertion reports what was expected against what the server actually did,
followed by the scratch path and the server log. The plan files under
`<scratch>/<line>/data/plugins/**/plan-*.json` carry the full classification for
every row and candidate, which is usually where the answer is.
