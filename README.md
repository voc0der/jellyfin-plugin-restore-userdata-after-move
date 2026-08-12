<p align="center">
  <img src="icon.png" alt="jellyfin-plugin-restore-userdata-after-move icon" width="180" />
</p>

# Restore User Data After Move

**Moved your media and Jellyfin forgot everything you'd watched?** This recovers
it.

When a file's path changes, Jellyfin treats it as a different item — it derives
an item's ID by hashing its path — so the old item is deleted and a new one takes
its place. Your played flags, resume positions, favorites and ratings belong to
the old item and are left behind.

They are not deleted. Jellyfin keeps those rows, blanks the link to the item that
owned them, and stamps a retention date. The cleanup task that would eventually
purge them has no default schedule, so on a stock server they sit there
indefinitely — unreachable rather than gone.

This plugin finds those stranded rows and works out which item each one belongs to
now. Putting the state back — through Jellyfin's own APIs, never by editing the
database — is the second half, and it is not built yet.

> **Status: analysis only.** This build finds and reports. It contains no code
> that can write user data — the restore half is not built, and will only be
> built if the analysis says there is enough to recover to justify it. See
> [PLAN.md](PLAN.md).

## What it recovers

Per user, for movies and episodes: played status, play count, resume position,
favorite, last-played date, and rating.

A stranded snapshot is only matched when a current item reports the exact same key
*and* the match carries real identity evidence — the item's own GUID, an IMDb ID,
or two provider keys corroborating each other. A bare number is not enough on its
own: Jellyfin stores TMDb IDs with no provider namespace, so a number that is
unique among your current items could still have belonged to something you deleted
years ago.

## What it does not do

- **It does not prevent future loss.** Move your files again and the same thing
  happens. This is a one-time cleanup, not protection.
- **It does not reconstruct your watch history.** Jellyfin keeps only the most
  recent stranded snapshot per title, not every state it ever had.
- **It does not guess.** A stranded row is only restored when it maps to exactly
  one current item with sufficient identity evidence. Anything ambiguous is
  reported and skipped, by design — a plausible wrong match is worse than an
  honest "couldn't tell."
- **It does not overwrite.** If the current item already has user state, it is
  left alone.
- **It does not touch the stranded rows themselves**, and it never writes to the
  database directly. Everything goes through Jellyfin's own user-data manager.

## How it works

One manual task with no schedule: **Analyze detached user data**. It reports what
could be recovered and gives a reason for everything it cannot, then writes a plan
file you can read. Nothing consumes that plan — the apply task described in
[DESIGN.md](DESIGN.md) does not exist in this build.

The task proves it changed nothing rather than asking you to take its word: it
fingerprints every row of the `UserData` table before and after the run and
records both in the plan.

Install it, run it, remove it. It is not meant to live on your server.

## Installing

**Jellyfin 10.11.11** — add the plugin repository to Dashboard → Plugins →
Repositories:

```
https://raw.githubusercontent.com/voc0der/jellyfin-plugin-restore-userdata-after-move/main/manifest.json
```

Then install **Restore User Data After Move** from the catalogue and restart.

**Jellyfin 12.0 RC5** — download
`RestoreUserDataAfterMove_<version>_jellyfin-12.0.0-rc5.zip` from the
[latest release](https://github.com/voc0der/jellyfin-plugin-restore-userdata-after-move/releases),
unzip it into `<jellyfin-data>/plugins/`, and restart. The RC build is not in the
catalogue on purpose: Jellyfin treats `targetAbi` as a *minimum*, so listing both
builds would let a 12.0 server offer you the 10.11 one.

Building it yourself produces the same archives:

```sh
./build.sh      # runs the tests, writes artifacts/ — one archive per server version
```

## Using it

Open the plugin's settings and click **Run analysis**. There is nothing to
configure: it uses every movie and TV library, scoped to those libraries' own
folders as the server reports them.

Results appear in the task's summary and the server log; the full detail is in a
plan file under `<jellyfin-data>/plugins/Jellyfin.Plugin.UserDataRestore/plans/`.

Under **Advanced** you can restrict it to particular libraries or folders. The
only common reason to bother is a library spanning two roots where you moved
everything into one of them and the other still holds the items you left behind.

Do this after your file moves are finished and Jellyfin has completed a full
scan. Run it mid-migration and the old items still linger alongside the new ones,
which honestly reports as ambiguous — the right answer to an unanswerable
question, but not a useful one.

If you are willing to share the `summary` block of your plan — counts only, no
titles or user IDs — it is exactly the evidence the go/no-go review in
[PLAN.md](PLAN.md) needs.

## Requirements

Jellyfin 10.11.11, or 12.0 RC5. Builds are pinned per server version: the plugin
depends on Jellyfin implementation internals, and Jellyfin's `targetAbi` is only
a *minimum* version check, so a build for the wrong server will load and run
rather than refuse. The plugin therefore checks the running version itself before
touching the database, and stops if it does not match.

That check is `major.minor.build`, which is as exact as Jellyfin allows: nothing
in a 12.0 RC5 install identifies it as a prerelease — its assembly, file, and
informational versions all read `12.0.0`, indistinguishable from RC4 or from
stable 12.0.0. **The archive named `jellyfin-12.0.0-rc5` was built against RC5 and
will also load on any other server reporting 12.0.0.** Before trusting it on
stable 12.0, re-run the validation in [evidence/alpha/](evidence/alpha/) against
that build. As a backstop the plugin verifies that the server's `UserData` table
still has every column it reads, and refuses if not.

## Documentation

- [DESIGN.md](DESIGN.md) — full specification, safety invariants, and the
  empirical results behind them.
- [PLAN.md](PLAN.md) — what is built, what is not, and what must be true before
  writes are implemented.
- [evidence/](evidence/) — the Gate 0 probe, server logs, and row dumps;
  [evidence/alpha/](evidence/alpha/) holds the analyzer's validation runs and
  published plans.
- [CONTRIBUTING.md](CONTRIBUTING.md) — building, linting, and what not to change
  without evidence.

## License

[MIT](LICENSE).
