# Plan

Where this stands, what gets built next, and what has already been decided so it
does not get relitigated.

Full specification: [DESIGN.md](DESIGN.md). Validation artifacts:
[`evidence/`](evidence/).

---

## 1. Status

**Settled.** Form (a separate, install-run-remove Jellyfin plugin rather than an
external SQLite reader or a Coldarr subsystem), identity, safety invariants,
matching rules, and the two-task analyze/apply split.

**Proven on disposable 10.11.11 and 12.0 RC5 servers** (DESIGN.md §17):

- A stock plugin resolves `IDbContextFactory<JellyfinDbContext>` by constructor
  injection and reads `UserData` through it, on both server lines, from a single
  source tree that differs only in target framework and package version.
- Detached rows retain `UserId`, `CustomDataKey`, and every state field; only
  `ItemId` is replaced with the sentinel.
- Key generation is as specified: a movie snapshot writes three rows (bare
  numeric TMDb, item GUID, IMDb), an episode two (item GUID, series IMDb plus
  zero-padded season/episode). Numeric keys carry no provider namespace.
- **Writing recovered state onto the current item does not collide with the
  stranded rows.** The table key includes `ItemId`, so live and sentinel rows
  coexist for the same `(UserId, CustomDataKey)`. This was the last surviving
  objection to the whole approach.
- Provider-keyed rows are not duplicated across moves — one row per
  `(UserId, CustomDataKey)`, holding the most recent snapshot. GUID-keyed rows
  accumulate per removed item and are generally unmappable.

**Not proven, and not blocking the next milestone:** direct in-process
`IUserDataManager` calls, non-SQLite providers, matching behaviour at scale.

**Built and validated:** the analyzer-only alpha (§2), on both server lines.

**Next:** collect classification results and hold the go/no-go review (§2.4).
Nothing in Milestone 2 or 3 starts before that review.

---

## 2. Milestone 1 — analyzer-only alpha — **built**

Ships **no apply task**. Nothing in this milestone can change Jellyfin state.

Implemented and validated on disposable 10.11.11 and 12.0 RC5 servers; artifacts
in [`evidence/alpha/`](evidence/alpha/). What is in the tree:

```
src/Jellyfin.Plugin.UserDataRestore.Core/   matching, classification, planning — no Jellyfin reference
src/Jellyfin.Plugin.UserDataRestore/        plugin, scheduled task, host adapters
tests/…Core.Tests/                          69 tests over the §12.1 cases
build.sh                                    both artifacts, with packaging checks
```

`./build.sh` runs the tests and produces one archive per server line. Each holds
only the plugin's own two assemblies plus a generated `meta.json`; the script
fails if a host assembly leaks in or the manifest ABI does not match.

### Build

- Scaffold from the official
  [`jellyfin-plugin-template`](https://github.com/jellyfin/jellyfin-plugin-template).
- Assembly `Jellyfin.Plugin.UserDataRestore`, GUID
  `6b416775-6a90-436f-a034-796c52f5a317`.
- Two build artifacts per DESIGN.md §11: 10.11.11 / net9.0 and 12.0 / net10.0.
  Host packages referenced compile-time only (`ExcludeAssets=runtime`).
- **Exact runtime version check before opening a context.** Not optional:
  §17.3 showed `targetAbi` is a minimum-version filter, so a 10.11-targeted build
  loads and runs on 12.0 without complaint.
- Keep matching, classification, and plan generation in a Jellyfin-light core
  library so they can be unit tested without a server (§12.1).

### Behaviour

One scheduled task, **Analyze detached user data**, returning no default
triggers. It implements DESIGN.md §7 end to end:

1. Project sentinel rows with `AsNoTracking()`; materialize and dispose the
   context before touching anything else (§7.1).
2. Enumerate eligible current items and build the reverse index from their real
   `GetUserDataKeys()` values — never manufactured keys (§7.2).
3. Join, requiring exactly one target and sufficient identity evidence (§7.3).
4. Collapse redundant keys; reject inconsistent or invalid source state (§7.4).
5. Inspect current target state (§7.5).
6. Classify every row into exactly one reason code (§7.6).
7. Write the plan artifact (§8) — but no arming and no consumption of it.

Do not discard GUID-shaped keys before the reverse index runs. Most will not
match, but an exact current-item GUID is the strongest identity evidence
available (§7.3 case 1). Build the index first, classify unmatched keys after.

### Acceptance — met

| Criterion | Result |
|---|---|
| Zero `UserData` mutations, by before/after row comparison | The task fingerprints every row of the table before and after each run and records both in the plan; every run reported `unchanged`. Confirmed independently by hashing the database outside the plugin. |
| Runs on both supported server lines | Identical classifications on 10.11.11 and 12.0 RC5, for a moved movie **and** a moved episode. The wrong-ABI build loaded silently on 12.0 and the version check refused to proceed. |
| Reports counts for every reason code | Logged and in the plan summary, including zeroes. |
| Sensible classifications against the evidence scenario | Movie: `imdb` rule from `tt0133093`, with the bare TMDb key contributing. Episode: `series_imdb_episode` from `tt0903747001001`. Removed items' GUID rows correctly unmappable. `path_outside_final_scope`, `already_applied`, and `current_state_conflict` exercised by reconfiguring. |

Episodes were worth their own live run rather than unit coverage alone: their key
is derived from the *series'* IMDb ID, and `Episode.GetUserDataKeys()` resolves
that series dynamically at call time, so the classification depends on host
behaviour no in-process test reproduces. It works on both lines — which is not a
foregone conclusion, given that the same class of hydration dependency is what
produced the one silent failure this milestone hit.

### Implementation decisions worth knowing

Refinements made while building, none of which loosen §7:

- **Uniqueness is judged across every current movie and episode on the server**,
  not only configured libraries. A key exposed by an eligible item *and* by one in
  an unconfigured library, or by one still lingering at a vacated path, is
  ambiguous. Configuration restricts what may be written to; it does not narrow
  what counts as competition.
- **Reason-code precedence is fixed**: inconsistent, then no-effect, then
  insufficient evidence, then current-state. A group whose state is entirely
  default is `source_has_no_effect` even when its evidence is also weak — it
  produces no write either way, and counting it as an evidence failure would
  overstate what the identity rule costs in the very numbers the go/no-go reads.
- **Two state comparisons, deliberately different.** Collapsing redundant source
  rows is exact, as §7.4 requires. Comparing against current target state tolerates
  sub-second date drift and floating-point noise, because that comparison has to
  survive a round trip through the database and the manager.
- **Series-GUID-derived episode keys are recorded, not admitted.** A key of the
  form `<current series GUID><SSSEEE>` is strong evidence, but §7.3 does not list
  it, and widening the sufficient set is a design change. The plan counts how many
  candidates it would have unblocked, so the question can be settled with data
  rather than reopened on instinct.
- **Item queries must hydrate provider IDs.** Without `ItemFields.ProviderIds`
  every item reports only its own GUID and the whole plugin quietly finds nothing.
  The plan carries `eligibleTargetsWithProviderKeys` and the task warns on the
  shape of that failure. See [`evidence/alpha/`](evidence/alpha/).
- **Canonical plan hashing preserves array order.** Sorting arrays before hashing
  would let the "exact ordered list of writes" be reordered while still matching
  the ID an operator reviewed and armed. Determinism comes from the plan builder
  emitting every array in a defined order instead — and, for the arrays that mean
  a set rather than a ledger, deduplicating them, since a hash counts length as
  much as order. This matters for Milestone 3, and is cheaper to get right before
  anything depends on it.
- **The runtime version check is as exact as Jellyfin permits, and no more.**
  Nothing in a 12.0 RC5 build identifies it as a prerelease — assembly, file, and
  informational versions all read `12.0.0`, same as RC4 and as stable will. So the
  check compares `major.minor.build`, the archive is named for the *package* it
  was built against, the plan records `builtAgainstJellyfinPackage`, and the
  plugin additionally verifies the host's `UserData` model carries every column it
  reads. That model check is the compatibility the version number was standing in
  for; it is not an authenticity check and cannot tell two builds apart when they
  share a model.

### Go / no-go

The alpha exists to answer one question: **are uniquely matchable, state-bearing
stranded rows common enough to justify a write path?** Collect classification
results from disposable servers and consenting real backups. A high ambiguity or
no-match rate is a legitimate result that ends the project at this milestone —
it is not a signal to add fallback matching.

**This is the open work**, but it is now a smaller question than it was.
[`evidence/sweep/`](evidence/sweep/) maps how recovery responds to library shape,
and the answer is dominated by one term:

```
recovery ≈ imdbCoverage × (1 − duplication) × (1 − alreadyHasState)
```

validated to within two points on held-out configurations. So the ask of a real
installation is no longer "send classification results" but *"what fraction of
your movies and series carry an IMDb ID"* — one number, measurable without running
anything. Two findings from the sweep bear directly on this review:

- **A TMDb-only library recovers nothing at any coverage**, and no fallback fixes
  that: a bare number is not identity evidence, and an episode never has a second
  provider key to corroborate with. Those installations are out of scope by
  design, not by omission.
- **Row-level ratios are misleading.** Dead GUID rows accumulate linearly with
  move count while contributing nothing, so `no_current_key_match` grows without
  recovery falling at all. Judge on opportunities — `(user, title)` pairs that had
  stranded state — not on rows.

Real summaries are still worth collecting, but as a check on the sweep's
assumptions about duplication and pre-existing state rather than as the primary
evidence.

Ask each reviewer for the `summary` block of their plan — counts, no titles, no
user IDs. Alongside the ratio itself, three diagnostics decide follow-up work:

- `insufficient_identity_evidence` — how much the §7.3 rule costs in practice.
- `candidatesBlockedOnlyBySeriesGuidEvidence` — whether a fourth evidence case is
  worth its own evidence.
- `eligibleTargetsWithProviderKeys` — a zero here means the run was never capable
  of matching anything and its other counts say nothing.

---

## 3. Milestone 2 — manager probe

A prerequisite for apply, not for the analyzer. Small: one item, one user, per
server line.

Inject `IUserDataManager`, restore through the DTO overload, verify all of the
current item's keys received the expected state, verify the sentinel rows are
unchanged, restart the server, and re-read the state.

The HTTP path already proved the underlying save semantics (§17.9). This probe
closes only plugin DI and the direct-call integration.

---

## 4. Milestone 3 — apply

Only if the go/no-go passes. Implements DESIGN.md §6.3 arming, §9 preflight and
sequential write, and §12.3–12.5 testing. No work here begins before Milestone 2
passes.

---

## 5. Decided — do not reopen without new evidence

| Decision | Where |
|---|---|
| A plugin, not an external SQLite reader or a Coldarr subsystem | §1, §15 |
| The EF context is query-only; every write goes through `IUserDataManager` | §4.1 |
| Sentinel rows are never edited, reattached, deleted, or garbage-collected | §3, §15 |
| No fuzzy title/year/path matching; no parsing numeric keys as TMDb | §15 |
| No merging into existing non-empty user state | §4.3, §15 |
| Two tasks, both untriggered; apply requires a one-time expiring arm | §5.2 |
| Per-version builds with an exact runtime check | §11, §17.3 |
| Name states the trigger and an outcome, never a mechanism | §5 |

---

## 6. Open questions

- **Scope boundary.** §17.7 observed that provider-row recovery tracked whether
  source and destination shared a configured location, and cites the
  folder-validation code as the likely explanation. §17.12 narrows this: a
  relocation *across* two configured locations of one library, removed and
  recreated in a single scan, still reattached. Sharing a configured location is
  therefore not necessary, which favours the validating-parent explanation.
  Nested, mixed-content, and multi-location libraries remain untested, and no
  instrumented build has been run.
- **Providers other than SQLite.** Untested. Decide whether to claim support
  before writing provider-agnostic code for a case nobody runs.
- **Catalogue listing for 12.0.** Only the 10.11.11 build is published to the
  plugin catalogue; the 12.0 archive ships on the GitHub release for manual
  install. Listing both under one version would let a 12.0 server install the
  10.11 build, because `targetAbi` is a minimum. Revisit when 12.0 is stable and
  validated — either as a second version stream whose ordering resolves correctly,
  or a second manifest.
- **Upstream.** The findings in §17.6 and §17.7 describe behaviour worth
  reporting to Jellyfin independently of this plugin. A fix upstream would reduce
  the population this tool serves, which is a good outcome.
