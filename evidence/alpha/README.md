# Analyzer alpha — validation runs

Artifacts from running the Milestone 1 build (`Jellyfin.Plugin.UserDataRestore`
1.0.0.0) on disposable Jellyfin 10.11.11 and 12.0-rc5 servers, 2026-08-12.

These are the acceptance evidence for [PLAN.md §2](../../PLAN.md). The
[`../src`](../src) and [`../results`](../results) artifacts are from the Gate 0
probe and are a separate, historical thing.

## Contents

```
plan-10.11.11.json                     movie + episode: 2 ready, 2 already applied, 2 conflicting, 4 unmappable
plan-12.0-rc5.json                     movie + episode: 6 ready, 4 unmappable
plan-10.11.11-path-outside-scope.json  the same rows with the prefixes pointed at the wrong roots
analyze-10.11.11.log                   task output, grouped by server session
analyze-12.0-rc5.log                   task output, grouped by server session
redact-plan.py                         the script that produced the published plans
```

Servers were the official generic linux-amd64 tarballs run with dedicated
`--datadir/--configdir/--cachedir/--logdir`, as in [`../README.md`](../README.md).

### These plans are redacted and do not verify

A plan ID is a SHA-256 over the plan's own contents. Collapsing the disposable
server's absolute paths to `<SCRATCH>` changes those contents, so a published copy
that still carried `planId` would assert an integrity check that fails on the file
you are holding.

`redact-plan.py` therefore drops `planId` and records provenance in its place:

As it appears in `plan-10.11.11.json`:

```json
"redaction": {
  "note": "Derived artifact…",
  "originalPlanId": "459be0ecca767856f636880627c291a062dd63d930650af4e5f3088a31949a5e",
  "originalFileSha256": "c61c88c051e3fe2ee0fa77dbd9e719164f16b9784eaa9ade93785d745650a08a",
  "replaced": ["/tmp/…/scratchpad/jf"]
}
```

The published copies are meant to fail `PlanCanonicalizer.VerifyPlanId`, and do.
The unmodified plans the servers wrote verify against the shipped canonicalizer;
all three were checked before redaction:

```
VERIFIES    plan-20260812T190800Z-459be0ecca76.json   claimedId=459be0ecca76
VERIFIES    plan-20260812T190746Z-1a0c1b8fe830.json   claimedId=1a0c1b8fe830
VERIFIES    plan-20260812T190917Z-9978a9fb2643.json   claimedId=9978a9fb2643
```

Every published plan came from the build in `artifacts/`, not from an earlier
iteration — session 6 in each log. The 12.0 plan records
`"builtAgainstJellyfinPackage": "12.0.0-rc5"`, which is the only durable record
that it was produced by a prerelease-targeted build.

One change landed in `PlanBuilder` after these runs: the plan's set-valued arrays
— a target's key set, library membership, the configured prefixes — are now
deduplicated as well as sorted, so a host that reports the same key twice cannot
change a plan's identity. On this evidence it is an identity transform. No array
in any published plan holds a repeated entry, so the shipped builder emits these
documents unchanged and the `originalPlanId` values above still hold. The servers
were not re-run for a change that provably cannot alter their output.

The originals were not published: they carry the test host's absolute paths, and
the repository's convention is to collapse those. `originalFileSha256` lets anyone
holding an original confirm it is the file a published copy came from.

## Scenario

One `Movies` library and one `Shows` library, each spanning a hot and a cold root.
Internet providers disabled; provider IDs from local NFO — movie `tmdb 603` /
`imdb tt0133093`, series `tmdb 1396` / `imdb tt0903747`. Two users, distinct state
on both the movie and the episode:

```
movie    admin   played=true  count=3 ticks=12345     favorite=true  rating=10  lastPlayed=2026-01-01T12:00:00Z
         viewer  played=false count=0 ticks=600000000 favorite=true  rating=1   lastPlayed=none
episode  admin   played=true  count=2 ticks=98765     favorite=true  rating=8   lastPlayed=2026-02-02T20:00:00Z
         viewer  played=false count=0 ticks=4200000000 favorite=false rating=3  lastPlayed=none
```

To strand the rows, each title was moved to a previously unused path with the
vacated root left **empty**, so §17.6's accessibility guard suppressed removal on
the first scan: the new item was created in pass one and the old item removed in
pass two. That reproduces the §17.5 state — the movie's three keys and the
episode's two, per user, detached with state intact and the new items carrying
nothing.

## Results

### Movies and episodes both recover, on both server lines

12.0 RC5, with both titles moved and nothing else in the way:

```
Inspected 10 detached rows against 2 eligible items for 2 users.
4 recoverable snapshots found. This build cannot apply them.
Candidates: ready=4
Rows: ready=6, no_current_key_match=4
```

The four unmatched rows are the removed items' own GUID keys — the unmappable case
of §2.1. The rest collapsed per user and per title:

| Target | Contributing keys | Rule |
|---|---|---|
| Movie | `tt0133093` (`imdb`), `603` (`other_provider`) | `imdb` |
| Episode | `tt0903747001001` (`series_imdb_episode`) | `imdb` |

Recovered state matched what was set through the API, field for field, for both
users on both titles.

The episode case matters on its own: its key is not a provider ID but one Jellyfin
derives from the *series'* IMDb ID plus zero-padded season and episode numbers,
and `Episode.GetUserDataKeys()` resolves that series dynamically at call time. The
plugin never reconstructs the key — it joins on what the item reported — but it
does have to recognise the shape to classify the evidence, which requires the
series' provider IDs to be loaded too. Both halves work on both lines
(`uniqueMatchEvidence.series_imdb_episode: 2`).

### The other classifications

Exercised on 10.11.11 by reconfiguring and re-running against the same rows:

| Setup | Result |
|---|---|
| No libraries or path prefixes configured | Task fails: "Configure at least one eligible library and one final path prefix…" |
| Prefixes pointed at the roots the items are *not* in | `path_outside_final_scope=6`, no writes |
| Current items given exactly the stranded state for one user, different state for the other | `already_applied=2`, `current_state_conflict=2`, no writes |

`already_applied` is the idempotency property of DESIGN §4.10: a second analysis
after a hypothetical apply proposes zero writes for that pair.

### Read-only proof

Every run reported `unchanged: true` with identical before/after digests over the
whole `UserData` table. Independently confirmed by hashing every row of the SQLite
database outside the plugin, before and after a run:

```
before: 9d43db6d77df67a7a4fcd94f2ae853bba1c9e568045c05a597161a48555bbe6b  (6 rows)
after:  9d43db6d77df67a7a4fcd94f2ae853bba1c9e568045c05a597161a48555bbe6b  (6 rows)
```

While the conflict test's live rows were present, sentinel and live rows coexisted
for the same `(UserId, CustomDataKey)` pairs, matching §17.9 — this time observed
through the plugin's own read path rather than the HTTP API.

A `Scan Media Library` run after each analysis completed normally, and neither
server logged an error or warning attributable to the plugin.

### Version gate

The 10.11.11-targeted archive was installed on the 12.0 RC5 server. Jellyfin
loaded it, registered its task, and reported nothing wrong — §17.3 again. Running
the task then failed closed:

> This plugin build is for Jellyfin 10.11.11 but the server is 12.0.0. Jellyfin's
> targetAbi is only a minimum-version check, so the wrong build loads without
> complaint. Install the build matching this server and try again.

That message came from the task's `LastExecutionResult`; the successful run that
followed overwrote it, so it is quoted here rather than present in the log file.

**What this check cannot do.** It compares `major.minor.build`, and that is all
Jellyfin exposes: RC5's assemblies report `12.0.0` as their assembly version, file
version, *and* informational version, identically to RC4 and to stable 12.0.0.
There is no build identity to gate on, so the RC5-built archive also runs on any
other server reporting 12.0.0. The archive is named `jellyfin-12.0.0-rc5` because
the file name is the only place that distinction survives, and the gap is covered
by a model check that verifies the `UserData` entity still carries every column
the plugin reads.

## Two findings

### Items must be queried with their provider IDs, or the plugin silently does nothing

The first run on 10.11.11 classified all six rows as `no_current_key_match` —
session 1 of `analyze-10.11.11.log`. The current item plainly had `Imdb` and
`Tmdb` IDs in the API.

The cause was in the plugin: it asked `ILibraryManager` for items with
`new DtoOptions(false)`. `Movie.GetUserDataKeys()` prepends the item's IMDb and
TMDb IDs to the keys it inherits, but only from the provider IDs actually
hydrated onto the instance, so every item reported exactly one key — its own
GUID — and every stranded provider-keyed row missed. No exception, no warning:
just "nothing is recoverable", which is a legitimate-looking answer.

Fixed by requesting `ItemFields.ProviderIds`. The plan now also carries
`eligibleTargetsWithProviderKeys`, and the task warns when eligible targets exist
but none reports a key other than its own GUID — the shape this bug takes on any
future server line.

### Crossing a configured library location does not prevent reattachment

Before the split-pass move above, the same title was moved `hot` → `cold` — across
configured locations — with the vacated root kept populated, so a single scan
removed the old item and created the new one in one pass. The provider-keyed rows
moved to the new item with state intact; only the removed item's GUID-keyed rows
detached.

§17.7 observed that provider-row recovery tracked whether source and destination
shared a configured location, while noting that its relocations 1–3 confounded
that with whether removal and creation fell in the same validation pass. This run
separates them in the other direction: source and destination were in **different**
configured locations and the rows were still reattached, in a single pass.

Sharing a configured location is therefore not necessary for reattachment. That
is consistent with §17.7's own source-backed inference — the bounding scope is the
validating parent, and both roots of one library are children of the same
collection folder — and it narrows PLAN §6's scope-boundary question. It is one
observation per server line, not an instrumented build.
