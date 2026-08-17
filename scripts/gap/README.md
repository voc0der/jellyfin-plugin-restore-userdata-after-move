# Live proof for the write path

`gap.sh` is the answer to the one question the unit tests cannot reach: does the
plugin actually put the data back, on a real Jellyfin server, through Jellyfin's
own user-data manager?

Everything below the HTTP boundary is covered by
[`tests/`](../../tests) — matching, classification, plan hashing, per-write
revalidation, where a run stops, all of it, with no server in sight. What no
amount of that can tell you is whether `IUserDataManager.SaveUserData` lands
where the design says it lands, whether Jellyfin's key fan-out behaves, whether
the stranded rows survive the round trip, and whether the outcome the plan
claims for a write is true of the database. This script finds out.

```sh
scripts/gap/gap.sh                    # both server lines
scripts/gap/gap.sh 10.11.11           # one
scripts/gap/gap.sh --keep 12.0-rc5    # keep the scratch tree afterwards
```

Exit status is the result. Zero means every assertion held.

## What it does

For each server line it stands up a throwaway Jellyfin from the official
tarball, builds a small library, strands its user data the way a path change
strands it, installs this plugin, and runs its scheduled task — one task, which
analyses and restores in the same pass.

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

- The scheduled task registers, ships with no trigger of its own, and finds the
  stranded state across **both selected libraries**. The harness ticks the two
  libraries it created, exactly as the settings page would; no path prefixes are
  typed, because there is no control for them and the scope comes from the
  libraries' own locations.
- **Unticking every library refuses the run** rather than widening it, and the
  task's error message names the page to go and fix it on. Asserted on its own,
  at the end, and never used as the setup for anything else: from 1.0.0.16 an
  empty selection is a refusal, and a harness that plants one reaches no plan,
  no write, and no assertion after it.
- A run with nothing to restore leaves `UserData` byte-for-byte unchanged.
- A target whose current state moved since the analysis is **left alone**, and
  the run says which check declined it. The revalidation is per write, taken
  from the live item moments before the save; there is no whole-plan preflight
  to test, because there is no gap between planning and writing for one to
  cover.
- The run restores each user's state on each title, field for field, matching
  what the server held before the move — not what the harness posted.
- The plan says what became of each of those writes, and says `restored` for the
  ones that are genuinely back. The unit tests can prove an outcome survives the
  document; only a real server can prove the outcome is true.
- A scope setting saved by 1.0.0.7, which no version since has a control for, is
  cleared rather than obeyed. The planted prefix is one no title sits beneath, so
  a run that still honoured it would restore nothing and fail the assertion
  above — the setting cannot come back and hide behind a green line.
- Every key the target item reports carries a row afterwards, so the next move
  finds the state too.
- The stranded rows are still there, byte for byte, with the same digest and the
  same count. The plugin reads them; it never consumes them.
- The restored state survives a server restart.
- A second run is a no-op: everything classifies `already_applied`, the plan
  proposes zero writes, and running again changes nothing.
- A library scan afterwards completes, and the server logs nothing at error
  level once the run begins.

## Where it runs

Here, and in CI on every push to `main`, every pull request that touches
behaviour, and once a week — `.github/workflows/live-gap.yml`.

That last one is not the usual drift check. Both servers are pinned to exact
tarballs and the code under test is whatever the commit says, so a weekly run
over an unchanged repository re-tests identical inputs and can only agree with
itself. What it watches is this script's own footing: `repo.jellyfin.org` still
serving those two files, the runner image still carrying the tools below, a new
image still able to run a Jellyfin built against an older glibc. Those move
without anybody committing anything, and they take the live proof down with them.

CI runs one job per server line rather than one run covering both, because this
script stops at the first failed assertion and a break in 10.11.11 would
otherwise leave 12.0 untested. A failing job uploads the server log, every plan
file, the plugin's saved configuration and the database as an artifact, which is
the same evidence `--keep` leaves behind locally.

If you add a server line here, add it to that workflow's matrix too.
`.github/workflows/check-live-matrix.py` fails the lint run if you don't — a line
this script supports and CI never starts is invisible otherwise, since every
check still passes.

## One thing worth knowing before you extend it

**Do not open Jellyfin's database from another process while Jellyfin is
running.** Not "prefer not to" — it takes the server down, and it does not look
like your fault when it does.

Jellyfin runs SQLite with its `NoLock` behaviour. There is no `-shm` file, so the
WAL index lives in the server's heap rather than in shared memory where other
connections could invalidate it. A second connection that opens the database
checkpoints and truncates the `-wal` when it closes. The server's in-memory index
still points into the frames that were just deleted, and its next read runs off
the end of a now-empty file:

```
pread64(jellyfin.db-wal, 4096 bytes @ 679856) -> 0, filesize=0
```

SQLite turns that short read into `SQLITE_IOERR_SHORT_READ` and reports it as
**"disk I/O error"**. Every subsequent query fails identically and the server
exits with an unhandled exception, typically seconds later during a library scan
— far enough from the cause to look like failing storage.

This cost about an hour of chasing a phantom. The harness had a guard on `db()`
from early on, but `locate_db` opened the database directly to check for a
`UserData` table, which slipped straight past it. Both are fixed: reads happen
only with the server stopped, `db()` refuses otherwise, and the database is now
located by filename with its contents verified later, in a stopped window.

A secondary consequence, same root: with the server up a second reader sees an
*empty* `UserData` table. That failure is silent, so every assertion about
stranded rows would pass vacuously. Keep the guard.

## Requirements

`curl`, `jq`, `sqlite3`, `ffmpeg`, `unzip`, `tar`, `dotnet` and `python3`, all
checked before anything starts, plus `zip` for the build it runs. Network access
to `repo.jellyfin.org` is needed for the first run; servers are cached under
`~/.cache/jellyfin-gap`, so later runs are offline and much faster.

GitHub's `ubuntu-latest` image carries every one of those except `ffmpeg`, which
is why the workflow installs exactly that and nothing else.

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

From a CI failure, the same things are in the `live-gap-<line>` artifact on the
run, and the job summary names the assertion that failed. The scratch tree
itself is gone with the runner, so if you need to reproduce, the artifact is
what you have — which is why the database is in it.
