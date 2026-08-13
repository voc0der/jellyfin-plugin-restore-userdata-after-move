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

This plugin finds those stranded rows, works out which item each one belongs to
now, and puts the state back — through Jellyfin's own user-data manager, never by
editing the database.

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

One scheduled task, **Restore user data after move**, which runs nightly. It
finds the stranded rows, works out which item each belongs to now, and restores
them in the same pass. It leaves a plan file behind recording exactly what it did
and giving a reason for everything it skipped.

Leave it installed. Media that moves once moves again, and the whole point is
that you stop thinking about it.

Running it repeatedly is safe by construction, not by promise:

- Once a target holds the recovered state, that pair reads as `already_applied`
  and is never written again.
- If you then change it yourself — mark something unwatched, un-favourite it —
  the pair reads as `current_state_conflict` and is never written again either.
  **A nightly run cannot undo what you just did.**
- The stranded rows are never modified or deleted, so a failed run leaves the
  only remaining copy of your history intact and the next run retries it.

Every write is an absolute value through Jellyfin's own `IUserDataManager`, read
back and verified. Nothing writes to the database directly.

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

Install it. That is the whole setup — it uses every movie and TV library, scoped
to those libraries' own folders as the server reports them, and it runs itself
at 3am.

The settings page exists for one thing: ticking specific libraries, if you want
fewer than all of them. There is nothing else on it. If you would rather not
wait for tonight, **Run now** is there.

Results appear in the task's summary and the server log; the full detail is in a
plan file under `<jellyfin-data>/plugins/Jellyfin.Plugin.UserDataRestore/plans/`.

It is at its best running behind whatever moves your files, the morning after.
Running it *during* a migration is harmless but unproductive: the old items still
linger alongside the new ones, which honestly reports as ambiguous — the right
answer to an unanswerable question, but not a useful one. Tonight's run will
catch it.

If you are willing to share the `summary` block of your plan — counts only, no
titles or user IDs — it is useful evidence for how this behaves on libraries
other than the handful it has been measured on.

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
- [PLAN.md](PLAN.md) — what is built and what is not.
- [evidence/](evidence/) — the Gate 0 probe, server logs, and row dumps;
  [evidence/alpha/](evidence/alpha/) holds the analyzer's validation runs and
  published plans.
- [scripts/gap/](scripts/gap/) — the end-to-end harness that stands up a
  disposable server, strands user data for real, and proves the apply path puts
  it back.
- [CONTRIBUTING.md](CONTRIBUTING.md) — building, linting, and what not to change
  without evidence.

## License

[MIT](LICENSE).
