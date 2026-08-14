# Design: Jellyfin detached user-data recovery plugin

**Status:** Shipped, and narrower than what this document originally described.

What exists is **one scheduled task**, `Restore user data after move` (key
`UserDataRestore`), carrying **no trigger by default**.  Install it, tick the
libraries it may touch, and add a trigger that lands after your own mover and the
library scan that follows it.  Jellyfin has no task chaining — only clock and
interval triggers — so it cannot express the dependency this task actually has,
and a shipped default would be a guess at an operator's maintenance window that
runs mid-move when wrong.  It analyses and restores in the same
run, writes a plan artifact for the record, skips itself while a library scan is
in progress, and re-checks each target immediately before writing so it can never
overwrite state a user set since the analysis.  Detached rows are never modified
or deleted.

The following sections describe machinery that was **designed and then removed**,
and are kept only for the reasoning behind them:

- **§1 "Why two tasks instead of one"** — superseded.  There is one task.
- **§6.3 "Arming"** — removed entirely.  See the note in that section.
- **§9 preflight** — removed.  The per-write guard replaced it.

`readOnlyProof` in the plan artifact is now `userDataTable`; the document records
what the table did rather than asserting the run changed nothing.

See [§17](#17-empirical-results) for method and results, [`evidence/`](evidence/)
for probe source, logs, and row dumps, and [`scripts/gap/`](scripts/gap/) for the
end-to-end harness that stands up throwaway 10.11.11 and 12.0-RC5 servers and
asserts the behaviour above against real ones.
**Scope:** One-shot recovery of user state that Jellyfin detached after path-based
item identity changed.  This is recovery only.  Preventing *future* loss is a
separate problem, addressed by the stable-path design in the Coldarr repository
(`jellyfin-path-stability.md`); nothing in this plugin depends on it, and the
plugin is useful to any Jellyfin administrator who has reorganised a library.
**Form:** A separate Jellyfin plugin with two manual scheduled tasks: analyze and
apply.

---

## 1. Decision

Build a small, version-pinned Jellyfin plugin rather than a Coldarr subsystem or
an external SQLite reader.

The plugin will:

1. Read detached `UserData` through Jellyfin's own EF Core context.
2. Ask current Jellyfin items for their actual `GetUserDataKeys()` values.
3. Produce a read-only recovery plan containing only unique, conservative
   matches.
4. Require an explicit, expiring, one-time arm before applying that exact plan.
5. Restore state through `IUserDataManager`, never through direct database
   updates.
6. Leave detached rows untouched.

```text
read-only JellyfinDbContext ── detached rows ─┐
ILibraryManager ── current items + real keys ├─> analyzer ─> immutable plan
IUserManager/current rows ───────────────────┘                    │
                                                                  v
                                                       one-time operator arm
                                                                  │
                                                                  v
                                                    IUserDataManager writes
                                                                  │
                                                                  v
                                                        verify + run ledger
```

Jellyfin's own `CleanupUserDataTask` demonstrates the basic shape: it is an
`IScheduledTask`, injects `IDbContextFactory<JellyfinDbContext>`, queries
detached rows, and returns no default triggers
([10.11.11 source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Server.Implementations/ScheduledTasks/Tasks/CleanupUserDataTask.cs),
[12.0 RC5 source](https://github.com/jellyfin/jellyfin/blob/v12.0-rc5/Emby.Server.Implementations/ScheduledTasks/Tasks/CleanupUserDataTask.cs)).
The unproven part is whether a third-party plugin can resolve that database
factory cleanly on both supported ABIs.  That is Gate 0, not an assumption.

### Why two tasks instead of one — SUPERSEDED

> This section is kept for its reasoning.  The plugin ships **one** task.  What
> follows is what was originally argued, and why it turned out to be solving a
> non-problem.
>
> Note that the shipped task does return an empty trigger list, as this section
> wanted — but not for this section's reason.  The argument below is that a
> schedule is *dangerous* because a repeating apply could repeat writes.  That is
> false, for the reason given at the end.  The real reason there is no default
> schedule is that Jellyfin cannot chain a task to the library scan that has to
> precede it, so any shipped time would be a guess at an operator's maintenance
> window.  Adding a trigger is expected, not a hazard.

`GetDefaultTriggers()` returning no triggers makes a task manual by default, but
an administrator can still add a schedule later.  A single task whose saved
configuration remains in "apply" mode could therefore repeat writes.

Use two tasks:

- **Analyze detached user data** — incapable of changing Jellyfin user state.
- **Apply detached user-data recovery** — refuses to run without a valid arm for
  a specific plan, consumes the arm before its first write, and then becomes
  inert again.

Both tasks return an empty trigger list.  The second task remains safe even if a
trigger is added accidentally.

**Why this was wrong.** Repeating a write is not a hazard here, because a repeat
is not a write at all.  Jellyfin *zeroes* user-data rows on clear rather than
deleting them, so a target a user has since cleared reads as a populated row in
default state, classifies as `current_state_conflict`, and is skipped — measured,
not assumed.  A target that was already recovered classifies as `already_applied`.
Either way a second run issues no writes and needs no ledger to know that: the
memory is the target's own rows.  The design is stateless.

Repeat runs are not merely harmless, they are the point.  Jellyfin reattaches
user data to the item at a new path by provider id, but only if that item is
already identified when the old one is removed; when identification lags the
move, the rows strand and Jellyfin never gets a second chance.  A task on a
repeating trigger does.  The harness asserts exactly this sequence.

This is why the task carries no trigger of its own and asks the operator for one,
rather than shipping a schedule: it needs to run *after* a scan, repeatedly, and
Jellyfin can express the "repeatedly" but not the "after".

---

## 2. What Jellyfin actually stores

Deletion replaces a removed item's `ItemId` in `UserData` with the sentinel
`00000000-0000-0000-0000-000000000001` and sets `RetentionDate`.  The remaining
row contains:

- `UserId`
- `CustomDataKey`
- played flag and play count
- playback position
- favorite flag
- last-played date
- rating/like data
- selected audio and subtitle stream indexes

The table key is `(ItemId, UserId, CustomDataKey)`
([entity](https://github.com/jellyfin/jellyfin/blob/v12.0-rc5/src/Jellyfin.Database/Jellyfin.Database.Implementations/Entities/UserData.cs),
[configuration](https://github.com/jellyfin/jellyfin/blob/v12.0-rc5/src/Jellyfin.Database/Jellyfin.Database.Implementations/ModelConfiguration/UserDataConfiguration.cs)).

### 2.1 `CustomDataKey` is not a provider-ID column

Jellyfin writes the same user-state snapshot once for every key returned by the
item:

- `BaseItem` contributes the item GUID.
- `Video` prepends IMDb and TMDb IDs when available.
- `Episode` additionally derives keys from the series keys plus zero-padded
  season and episode numbers.

See Jellyfin's
[`SaveUserData`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Server.Implementations/Library/UserDataManager.cs#L50-L84),
[`BaseItem.GetUserDataKeys`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/Entities/BaseItem.cs#L1468-L1481),
[`Video.GetUserDataKeys`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/Entities/Video.cs#L274-L306),
and
[`Episode.GetUserDataKeys`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/Entities/TV/Episode.cs#L158-L184).

Consequences:

- Several detached rows can represent one state snapshot.
- A numeric value does not carry a provider namespace or media type.
- Old path-derived GUID keys generally cannot be mapped to a current item.
- Duplicate and merged items can legitimately produce the same provider-derived
  key.
- The detached table is a set of snapshots and lookup keys, not a playback event
  history.

The plugin must never parse a key and assume that it is a TMDb ID.  It instead
asks every eligible current item for its keys and performs an exact reverse
lookup.

### 2.2 Usually only the latest reusable snapshot survives

Before 10.11.11 detaches new rows, it deletes existing sentinel rows with the
same `(UserId, CustomDataKey)`
([source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Jellyfin.Server.Implementations/Item/BaseItemRepository.cs#L118-L141)).
Provider-derived keys therefore tend to retain the most recently detached
snapshot, not every historical state.  Path-GUID rows may accumulate, but are
normally unmappable.

The product promise is consequently:

> Recover a uniquely attributable stranded snapshot when the current item has no
> user state.

It is not "reconstruct all watch-history events."

### 2.3 Issue #16975 is not the recovery write path

[#16975](https://github.com/jellyfin/jellyfin/issues/16975) is a collision while
library cleanup moves several duplicate-item rows onto the same sentinel
`ItemId`.  It is not caused by saving state on an existing current item.  It is
closed as a duplicate of #15343; the merged 12.x work explicitly deduplicates
rows before batch tombstoning
([PR #16062](https://github.com/jellyfin/jellyfin/pull/16062),
[12.0 RC5 implementation](https://github.com/jellyfin/jellyfin/blob/v12.0-rc5/Jellyfin.Server.Implementations/Item/ItemPersistenceService.cs#L50-L127)).

The plugin still fails closed on duplicate current keys, but #16975 is not a
reason to update or delete tombstones directly.

---

## 3. Goals and non-goals

### 3.1 Goals

- Recover state independently for every surviving Jellyfin user.
- Support movies and episodes first.
- Use Jellyfin's own item-key generation rather than duplicating it.
- Make analysis demonstrably read-only.
- Make apply reviewable, bounded, sequential, cancelable, and idempotent.
- Preserve Jellyfin caches and user-data events by using its manager for writes.
- Produce machine-readable evidence for every match, skip, and write.
- Work against the configured EF provider without SQLite-specific SQL.

### 3.2 Non-goals

- Preventing future detachment.  The stable-path subsystem owns that.
- Running after scans, polling, syncing, or maintaining a permanent mapping.
- Recovering to a different Jellyfin server whose user IDs changed.
- Guessing from titles, filenames, years, or fuzzy provider searches.
- Restoring series, seasons, folders, collections, playlists, music, people,
  studios, trailers, or extras in v1.
- Resolving alternate-version or duplicate-library ambiguity.
- Merging into user state that already exists on the current item.
- Reattaching, editing, deleting, or garbage-collecting sentinel rows.
- Restoring selected audio/subtitle indexes in v1.
- Repairing arbitrary Jellyfin database corruption.

---

## 4. Safety invariants

These are implementation requirements, not preferences.

1. **No direct user-data writes.** The EF context is query-only.  Every recovery
   write goes through `IUserDataManager.SaveUserData`.
2. **No fuzzy or single-numeric guesses.** A detached key must map to exactly one
   eligible current item, and the match must meet the identity-evidence rule in
   §7.3.  Current-catalog uniqueness alone does not recover the missing provider
   namespace of a numeric key.
3. **No overwrite.** Apply requires no current `UserData` rows for that
   `(UserId, ItemId)`, unless the current semantic state already equals the plan
   and the operation is classified as an idempotent no-op.
4. **No implicit apply.** Apply requires a plan ID, expected write count, recent
   backup acknowledgement, and an unexpired one-time arm.
5. **No stale plan.** All source rows, target IDs, target keys, target paths, user
   IDs, and current-state preconditions are revalidated before the first write.
6. **No wrong namespace.** Every target must belong to a configured final
   library and be beneath a configured final path prefix.
7. **No parallel writes.** Recovery is sequential to limit database contention
   and make failure boundaries obvious.
8. **No silent partial success.** The first save or verification failure stops
   the apply task.  Completed writes remain recorded and are safe to discover on
   a rerun.
9. **No retained apply mode.** The task consumes and persists removal of its arm
   before writing the first item.
10. **No dependency on reports for correctness.** If a crash occurs after a
    state write but before the ledger write, the next analysis recognizes the
    target as already recovered.

---

## 5. Plugin shape and dependencies

Identity, as settled:

| | |
|---|---|
| Repository | `jellyfin-plugin-restore-userdata-after-move` |
| Assembly | `Jellyfin.Plugin.UserDataRestore` |
| Display name | Restore User Data After Move |
| Plugin GUID | `6b416775-6a90-436f-a034-796c52f5a317` |

The name deliberately states the trigger ("after move") and an outcome verb
("restore").  It must not imply a mechanism: earlier candidates built on
"reattach" and "detombstone" were rejected because they name the operation §3
and §15 forbid — this plugin leaves sentinel rows untouched.

Keep it separate from the Coldarr process and release lifecycle.  It is useful
to any Jellyfin administrator recovering from a path migration, and it should be
installable, run once, and removable without affecting anything else.

### 5.1 Host services

The scheduled tasks need:

| Service | Use |
|---|---|
| `IDbContextFactory<JellyfinDbContext>` | Short, provider-aware, read-only projections from `UserData` |
| `ILibraryManager` | Enumerate and resolve current movie/episode objects |
| `IUserManager` | Resolve surviving users by the stored GUID |
| `IUserDataManager` | Read semantic current state and perform supported writes |
| `ITaskManager` | Detect conflicting running maintenance/library tasks if the API permits it |
| `IApplicationPaths` or plugin data path | Plans, ledgers, and plugin configuration |
| `ILogger` | Summary and diagnostic logging |

Do not make loopback HTTP calls to Jellyfin.  The manager is the same logical
write path without authentication, network, serialization, or base-URL failure
modes.

### 5.2 Implementation-level dependency

`JellyfinDbContext` is not a stable high-level plugin contract.  Reference the
exact `Jellyfin.Database.Implementations` package used by the target server and
exclude the host assembly from the plugin bundle.  The running server must
supply it.

Avoid a dependency on `Jellyfin.Server.Implementations` solely for the placeholder
constant.  Define the documented sentinel GUID locally and cover it with
version-specific integration tests.

### 5.3 Gate 0: prove the boundary first

Before implementing matching or writes, build the smallest possible plugin:

1. Implement one `IScheduledTask` with no default triggers.
2. Constructor-inject `IDbContextFactory<JellyfinDbContext>`.
3. Query only the count of rows whose `ItemId` is the sentinel using
   `AsNoTracking()`.
4. Log the count and exit.
5. Install and run it on disposable 10.11.11 and 12.0 RC5 servers.
6. Restart each server and run a normal scan afterward to catch assembly-load,
   context-lifetime, and database-lock regressions.

If either dependency cannot be resolved from a stock plugin, stop and revisit
the external-tool form.  Do not fall back to opening the live SQLite file from
inside the plugin.

**Result: executed and passed on both server lines.**  See
[§17.2](#172-gate-0-results) for the resolved types and
[§17.3](#173-plugin-abi-enforcement) for an unrelated finding about manifest ABI
enforcement observed during the same probe.

---

## 6. Configuration and operator workflow

### 6.1 Persistent configuration

- Eligible Jellyfin library IDs.  **Empty means every movie and TV library**, and
  no other kind is ever offered or defaulted to: nothing in a music, photo, or
  book library can hold a recovery target.
- Final path prefixes.  **Always the in-scope libraries' own configured
  locations**, read from the server.
- Whether items whose media file is missing are skipped.  Always on.

The library list is the only editable scope setting.  The two path settings were
editable through 1.0.0.7; requiring them guaranteed that the first run of a fresh
install failed, and the prefix field asked the operator to retype something the
server already knows — in a form that is easy to get wrong and whose failure is
silent.  A host path entered where the server sees a container path excludes
every item and reports "nothing recoverable", which is indistinguishable from a
correct empty result.  1.0.0.8 removed both controls.

Removing a control does not remove what it wrote.  An upgrade preserved whatever
those two fields last held, and the task went on honouring them from a page that
no longer showed them — a setting that changes write scope, simultaneously active
and uneditable.  A run therefore clears both before resolving scope, and says in
the log what it found and what will change as a result.  The values are the
operator's own past choice and the reason their results are about to differ, so
they are named rather than silently reset.

Defaulting is not the same as tolerating.  "Empty means every library" is a
reading of an *absent* selection, and it must never be reached by way of a
present one: a stored library list whose entries do not parse as IDs fails the
run outright, rather than being reduced to nothing and then read as unconfigured.
Dropping bad values and asking what is left cannot tell "nobody has chosen yet"
from "the stored form of somebody's choice is corrupt", and collapsing the second
into the first *widens* the scope of a mutating run on the strength of a value
the plugin has just admitted it cannot read.  One bad entry condemns the whole
selection: a partial read is still a guess at what was meant, and the only writer
of this field is a page that posts IDs the server itself supplied.

Whatever the scope resolves to is what the plan records, so an audit reads the
same whether it was typed or derived.  Paths are compared using Jellyfin's
host-platform path semantics.  Prefix tests must be path-component-aware;
`/data/library/tv2` is not beneath `/data/library/tv`.

Plan retention is not configurable.  It is housekeeping over small files, and a
plugin page should not ask a question its reader has no basis to answer.  The
apply-side settings (arming, backup acknowledgement, write caps) arrive with the
apply task in Milestone 3.

### 6.2 Operator sequence

1. Finish the stable-tree cutover and complete the final Jellyfin scan.
2. Confirm playback works from the final paths.
3. Install the plugin build matching the exact server version.
4. Narrow the scope on the plugin page, if and only if the defaults are wrong.
5. Run **Analyze detached user data**, from the plugin page or from Scheduled
   Tasks.
6. Review the summary and JSON plan, especially ambiguity and conflict counts.
7. Take a current Jellyfin full-system backup.
8. Enter the displayed confirmation phrase, containing the plan ID and write
   count, and acknowledge the backup.
9. Run **Apply detached user-data recovery** during a quiet window with no scan
   or user activity.
10. Review verification results, spot-check users in the Jellyfin UI, and rerun
    analysis.  Successfully restored entries should now be `already_applied`.
11. Uninstall the plugin when satisfied, or leave it inert with no arm and no
    triggers.

### 6.3 Arming — REMOVED, NOT IMPLEMENTED

> No arming ceremony exists.  The configuration page has library checkboxes, a
> Save button, and a Run now button; there is no phrase to type, no expiry, and
> no backup acknowledgement.
>
> It was defending against an administrator adding a schedule to the apply task
> and turning a one-time recovery into something that repeats writes forever.
> That is now the intended mode of operation, and the two guards that actually
> matter — `current_state_conflict` on the plan side and a re-read of the target
> immediately before each write — make it safe without a ceremony.  A ceremony
> that must be performed nightly is not a safety control, it is an outage.
>
> The rest of this section records what was designed.

The configuration page displays a phrase such as:

```text
APPLY 3f17c4a9b28e 428
```

Arming stores:

- the full plan hash,
- the exact expected number of writes,
- the current server identity and version,
- the arm timestamp and expiry,
- backup acknowledgement.

Default expiry: 15 minutes.  The apply task clears and saves this configuration
before its first recovery write.  A crash requires an administrator to review
and arm again.

---

## 7. Analysis pipeline

Analysis is a pure planning operation.

### 7.1 Snapshot detached rows

Open a host-provided context, project only the required columns, use
`AsNoTracking()`, materialize the result, and dispose the context immediately.
Do not hold a transaction or context while enumerating libraries or writing the
report.

Required projection:

```text
UserId
CustomDataKey
RetentionDate
Played
PlayCount
PlaybackPositionTicks
IsFavorite
LastPlayedDate
Rating
AudioStreamIndex       # report only in v1
SubtitleStreamIndex    # report only in v1
```

Only rows with the sentinel `ItemId` are eligible.

### 7.2 Snapshot current items and exact keys

Enumerate current items from the configured libraries and retain only:

- concrete movies,
- concrete episodes,
- items with an existing media path beneath an allowed final prefix,
- non-extra, non-trailer, non-virtual items.

For each item, call its actual `GetUserDataKeys()`.  Build an ordinal,
case-sensitive reverse index:

```text
CustomDataKey -> set of current Jellyfin item IDs
```

Do not normalize numeric strings, strip prefixes, lowercase keys, or manufacture
provider keys independently.

If two current items expose the same key, that key is ambiguous even if their
titles or paths appear related.  Merged and alternate versions receive no
special exemption in v1.

Also annotate current keys with identity evidence without changing their value:

- `current_item_guid` when a parsed GUID equals the current item ID,
- `imdb` when it is the current item's IMDb ID,
- `series_imdb_episode` when it is an episode key derived from the current
  series IMDb ID,
- `other_provider` for TMDb and other provider-derived values,
- `unknown` otherwise.

The value used for joining always comes from `GetUserDataKeys()`.  Provider
metadata is used only to classify confidence; it must not manufacture additional
join keys.

### 7.3 Join users and items

For each detached row:

1. Require a surviving Jellyfin user with exactly that `UserId`.
2. Look up `CustomDataKey` in the reverse index.
3. Require exactly one current target.
4. Group matches by `(UserId, TargetItemId)`.

One user's failure does not invalidate another user's independently unique
match.

An exact numeric match is not sufficient on its own.  TMDb movie, series, and
episode IDs occupy separate namespaces, but Jellyfin stores the bare number.
The original row no longer carries an item type, so a key that is unique among
*current* items can still have belonged to a now-absent item of another type.

A v1 candidate has sufficient identity evidence only when at least one of these
holds:

1. A contributing key is the exact current item GUID.
2. A contributing key is the current item or series-derived IMDb key; IMDb IDs
   identify one title/entity across media types.
3. At least two distinct provider-derived current keys have detached rows with
   identical `RecoveryState` and identical `RetentionDate`, **both of which must
   be present**.  Two rows with no retention stamp do not corroborate: that is
   two absences, not an agreement, and reading `null == null` as a match is the
   inference this case exists to refuse.  Identical state alone is nearly
   worthless as evidence — "watched once, never resumed, no rating" describes
   most rows in most libraries — so the stamp is what carries the claim that both
   rows came from the same detach event.

Case 3 is corroboration rather than mathematical proof, but removes the unsafe
single bare-number inference while retaining TMDb-only recovery where Jellyfin
wrote more than one usable key.  A unique target supported only by one numeric
key is reported as `insufficient_identity_evidence` and is not applied.

A row without a retention stamp is not invalid; §7.4 does not reject it, and it
can still be recovered under case 1 or case 2, neither of which needs one.  It
simply cannot corroborate, or be corroborated by, anything.

### 7.4 Collapse redundant keys

Convert each source row into a `RecoveryState`:

```text
Played
PlayCount
PlaybackPositionTicks
IsFavorite
LastPlayedDate
Rating
```

Rows in the same `(UserId, TargetItemId)` group are redundant only when these
fields are identical.  If they disagree, classify the group as
`inconsistent_source_state` and skip it.  V1 does not choose a winner using
`RetentionDate`, maximum play count, or maximum position; those policies can
silently combine different moments in time.

Ignore source groups whose recoverable state is entirely default:

```text
Played == false
PlayCount == 0
PlaybackPositionTicks == 0
IsFavorite == false
LastPlayedDate == null
Rating == null
```

Negative counts, negative positions, ratings outside 0–10, malformed dates, or
missing keys are `invalid_source_state`.

### 7.5 Inspect the current target

Query current `UserData` rows for candidate `(UserId, ItemId)` pairs in batches.
The row-existence check matters because a row containing default-looking values
may represent an explicit unwatch, unfavorite, or rating removal.

Classify:

| Current condition | Result |
|---|---|
| No current rows | `ready` |
| Current semantic state equals recovered state | `already_applied` |
| Any current rows with different state | `current_state_conflict` |

Do not merge a partially populated current record in v1.  The table has no
per-field modification timestamps, so there is no principled way to know whether
the detached favorite, rating, or resume position is newer.

### 7.6 Classification reason codes

Every detached row or collapsed candidate ends in one explicit category:

- `ready`
- `already_applied`
- `source_has_no_effect`
- `unknown_user`
- `no_current_key_match`
- `ambiguous_current_key`
- `unsupported_current_item`
- `path_outside_final_scope`
- `insufficient_identity_evidence`
- `inconsistent_source_state`
- `invalid_source_state`
- `current_state_conflict`

The report includes counts and records for all categories.  A high no-match or
ambiguity rate is a product result, not an error to hide with fallback matching.

---

## 8. Recovery plan

Write plans under the plugin data directory using a temporary file plus atomic
rename.  The plan is immutable after publication.

Minimum contents:

- plan schema version,
- plugin version, the Jellyfin package it was built against, and target ABI,
- Jellyfin server identity and exact version,
- creation timestamp,
- configured library IDs and final path prefixes,
- summary counts for every classification,
- each source row's user ID, key, retention date, state, and source fingerprint,
- target item ID, type, name, path, library ID, and complete key set,
- target-row existence and semantic-state result,
- the exact ordered list of `ready` writes,
- canonical SHA-256 plan ID.

The plan hash covers every field except the ID itself.  Canonical serialization
sorts object properties by name and **preserves array order**, so the ordered
write list is part of the identity: a plan cannot be reordered and still claim the
hash an operator reviewed and armed.  The same inputs still produce the same hash
regardless of the order rows were read in, because the plan builder emits every
array in a defined order before hashing.  Arrays that state a *set* rather than a
sequence—the target's key set, its library membership, the configured
prefixes—are also deduplicated there: a hash counts an array's length as much as
its order, so a host that reports the same key twice must not be able to give the
same analysis two identities.

Plans and run ledgers are audit artifacts, not a standing identity database.
Keep a bounded number—for example, the latest five plans and twenty run ledgers—
and never delete the currently armed plan.

---

## 9. Apply pipeline

### 9.1 Whole-plan preflight — SUPERSEDED

> There is no whole-plan preflight, because there is no plan to fly ahead of.
> One task analyses and writes in the same pass (§1), so checks 1 and 10 no
> longer exist, and checks 4–9 have nothing to re-read: the analysis that
> produced the write happened seconds ago in the same process.
>
> What survives is per-write, listed in §9.2: every condition that admitted a
> target is asked again of the live item immediately before writing to it.  That
> is strictly later than a preflight pass would have asked — a preflight
> validates the whole batch and then writes it over the following minutes, so its
> last checks are the stalest.  Check 3 survives too, both at entry and before
> each write, and a scan starting mid-run abandons the rest of the batch.
>
> Check 7's *uniqueness* clause is the one thing that cannot be answered from the
> live item, because it is a property of the whole catalogue: no amount of looking
> at a target reveals that a second item has started reporting its key.  So it is
> re-established once at the start of the apply pass, from a `KeyOwnership` index
> built over every current movie and episode, and each write is checked against
> that (§9.2 step 4).  Once per run rather than once per write is a deliberate
> cost decision — establishing it is a full catalogue pass — and it leaves drift
> *inside* the write loop uncovered.  What moves keys in bulk is a scan or a
> refresh, and check 3 covers those: the run refuses to start during a scan and
> abandons the remaining writes if one begins.
>
> The rest of this section records what was designed.

Before any recovery write:

1. Validate the arm, expiry, backup acknowledgement, server identity, exact
   server version, plan hash, and expected write count.
2. Refuse an unsupported plugin/database schema combination.
3. Refuse if a library scan or relevant maintenance task is already running,
   where Jellyfin exposes that state.
4. Re-read every planned detached source row and compare its full fingerprint.
5. Re-resolve every user and target item.
6. Recompute every target's full `GetUserDataKeys()` set.
7. Revalidate library membership, type, path prefix, and unique reverse mapping.
8. Re-query current target-row existence and semantic state.
9. Permit targets now equal to the plan as no-ops; abort on any other changed
   precondition.
10. Consume and persist removal of the arm.

This is an all-plan validation pass.  A stale candidate is not discovered after
hundreds of writes have already occurred.

### 9.2 Write

For each remaining `ready` `(UserId, ItemId)` pair, sequentially:

1. Resolve the user and current item again.
2. Check cancellation, and abandon the remaining writes if a library scan has
   started since the run began.
3. Revalidate the target against the conditions that admitted it, from a snapshot
   of the item taken now: kind, virtual/extra status, path, path existence,
   library membership, final-path prefix, and — the identity itself — that it
   still reports every detached key the stranded rows matched on.  A metadata
   refresh landing between analysis and write can replace an item's provider IDs,
   and the item that answers to different keys is not the item the evidence was
   about.  Every one of these is read from the live item, including library
   membership, which is asked of the item's own ancestors rather than looked up in
   a map built earlier in the run: checking a target against the same photograph
   that admitted it checks nothing.
4. Revalidate *uniqueness* — that the target is still the only current item
   reporting each of those keys — against the `KeyOwnership` index built at the
   start of the apply pass.  Unlike step 3 this is catalogue-wide, so it is
   established once per run and not once per write; see §9.1.
5. Check current state through `IUserDataManager`, and skip anything not at
   defaults.
6. Re-query `UserData` row *existence* for the exact pair, against the database
   rather than through `IUserDataManager`.  The manager reports "no row" and "a
   row holding defaults" identically, and the difference between them is the
   difference between an item nobody has touched and an item somebody has just
   deliberately cleared.  Any row at all: skip.  This is the last check before the
   save, and deliberately so — it is the authoritative one, so nothing belongs
   between it and the write it guards.
7. Create an `UpdateUserItemDataDto` containing only the six recoverable fields.
8. Call `IUserDataManager.SaveUserData(user, item, dto,
   UserDataSaveReason.UpdateUserData)`.
9. Re-read through `IUserDataManager` and verify the six semantic fields.
10. Query the current item's rows and verify that Jellyfin wrote the expected
    current keys with the recovered state.
11. Append and flush a completed ledger record.
12. Report progress.

Using the partial-update DTO preserves current audio/subtitle selections.  V1
does not restore the detached stream indexes because they are positional and may
refer to different streams if the media was replaced.

Do not use played/favorite toggle operations or play-count increments.  Absolute
values make retries idempotent.

### 9.3 Failure behavior

- Cancellation stops before the next item and reports a canceled partial run.
- A save exception, failed verification, database exception, or ledger failure
  stops the run immediately.
- A ledger failure after a successful save does not roll the user state back.
  The next analysis detects semantic equality and reports `already_applied`.
- There is deliberately no transaction spanning multiple titles or users.
- Detached rows remain exactly as they were before the run.

### 9.4 Concurrency limitation

Jellyfin provides no public compare-and-swap operation for user data.  A user can
change an item between the final current-state check and the save.  Keeping the
write sequence short narrows this window but cannot remove it.

The window is now the gap between §9.2's row-existence query and the save on the
next line, rather than the gap between analysis and apply.  That matters for the
one case where the two differ in kind: clearing a played flag writes a row full
of default values, which `IUserDataManager` cannot distinguish from no row at
all, so a check made through the manager reads a deliberate clear as an untouched
item and overwrites it.  Querying row existence directly is what closes that, and
it has to happen immediately before the save to mean anything.

The window is narrowed, not removed, and it is worth being exact about what is
left.  A clear landing inside one database round trip would still be overwritten.
Reaching that window at all requires the clear to land on a pair that currently
has *no* row — which from the user's side is clearing something that already
reads as clear — and the stranded row survives either way, so the state is
recoverable by the next run.  Closing it properly needs a conditional write, and
the only conditional write available is a direct database update, rejected below.

Apply therefore requires a quiet maintenance window:

- no library scans or path changes,
- no active playback,
- no clients changing favorites, ratings, or played state.

This residual race must be documented in the confirmation UI.  Direct database
conditional updates would avoid the race but violate the cache/event and
no-direct-write invariants, so they are rejected.

---

## 10. Observability

The scheduled-task result and logs should lead with:

- detached rows inspected,
- unique user/item snapshots formed,
- ready writes,
- already-applied pairs,
- current-state conflicts,
- ambiguous keys,
- unmatched keys,
- invalid rows,
- successful writes and verified writes,
- first failure, if any,
- plan or run-ledger path and ID.

Log individual records at debug level.  Normal information logs should contain
counts and item IDs, not every title, username, path, or state value.  The local
JSON report may contain those details because an administrator explicitly needs
them for review.

Analysis with zero recoverable entries is a successful task with a clear "no
writes available" result.  Apply with zero planned writes is refused because
there is nothing to authorize.

---

## 11. Versioning and packaging

Produce separate build artifacts for each supported Jellyfin ABI:

| Initial target | Framework | Dependency policy |
|---|---|---|
| 10.11.11 | .NET 9 | Pin Jellyfin packages to 10.11.11; runtime task initially permits 10.11.11 only |
| 12.0 RC5 | .NET 10 | Pin packages to RC5; replace with the stable 12.0 build after validation |

Use the official
[`jellyfin-plugin-template`](https://github.com/jellyfin/jellyfin-plugin-template)
packaging pattern.  Host assemblies are compile-time references and must not be
shipped inside the plugin archive.

The scheduled task performs an exact runtime compatibility check in addition to
the manifest ABI.  Broaden the accepted version range only after the integration
suite passes against that version.  Database-entity and manager dependencies
make optimistic compatibility inappropriate.

**How exact that check can be.**  Jellyfin exposes no prerelease or build
identity: 12.0 RC5 reports `12.0.0` as its assembly version, file version, and
informational version, indistinguishable from RC4 or from stable 12.0.0
([§17.12](#1712-analyzer-alpha-on-both-server-lines)).  The runtime check is
therefore `major.minor.build` and cannot be more.  A build made against a
prerelease will also load and run on any other server reporting that version, so:

- the artifact is named for the package it was built against, not for the version
  the server reports, since the file name is the only place the distinction
  survives; and
- the plugin additionally verifies the host's own EF model — that the `UserData`
  entity still carries every column the projection reads — which is the
  compatibility the version number was standing in for.  This is the analyzer-side
  form of the schema refusal in §9.1 item 2.

The model check is a compatibility check, not an authenticity one.  It cannot
distinguish two builds that share a model, and does not claim to.

No schema-name or raw-SQL branches are allowed.  If the EF model changes enough
to break compilation or the tested projection, publish a new plugin build.

---

## 12. Tests

### 12.1 Unit tests

Keep matching, classification, plan generation, and arming in a Jellyfin-light
core library so they can be tested exhaustively.

Required cases:

- Exact ordinal key matching.
- No match, one match, and multiple current matches.
- One snapshot duplicated under GUID, IMDb, TMDb, and episode-derived keys.
- Single bare numeric key is insufficient even when its current target is
  unique.
- Exact current GUID, IMDb, series-derived IMDb, and two-key corroboration
  satisfy the identity-evidence rule.
- Numeric IDs that collide across movie, series, and episode namespaces.
- Conflicting state under two keys for the same user/target.
- Multiple users with different state for the same item.
- Missing and deleted users.
- Default source state produces no write.
- Negative position/count, out-of-range rating, null/empty key, and malformed
  date rejection.
- Existing target rows that are equal, default-looking, partially populated,
  and conflicting.
- Movie/episode inclusion and every excluded item type.
- Component-aware path-prefix checks, including sibling-prefix attacks.
- Deterministic canonical plan hashing regardless of input order.
- Plan tampering changes the hash.
- Wrong server/version/library/path scope rejects apply.
- Arm expiry, plan mismatch, count mismatch, backup acknowledgement, and
  one-time consumption.
- Cancellation before and during the sequential loop.
- Report and ledger retention never removes the armed plan.

### 12.2 Database/component tests

Run against the actual version-pinned `JellyfinDbContext`, preferably with both
the SQLite provider and any other provider Coldarr intends to claim support for.

- Sentinel projection returns all required fields.
- `AsNoTracking()` analysis leaves every `UserData` row unchanged.
- Current-row existence is distinguishable from a synthesized default
  `UserItemData` object.
- Batched queries handle thousands of item/user pairs without exceeding SQLite
  parameter limits.
- Contexts are disposed before any manager write begins.
- Analyze and task cancellation do not leave transactions or database locks.
- Plan and ledger files publish atomically.

### 12.3 Disposable-server integration suite

Run the same black-box scenario on Jellyfin 10.11.11 and 12.0 RC5/stable:

1. Start a clean server with tiny generated movie and episode media plus local
   NFO provider IDs.
2. Create at least two users.
3. Set distinct combinations of played, play count, position, favorite,
   last-played date, and rating through Jellyfin.
4. Move the title between direct library roots and scan until the original item
   IDs are gone and sentinel rows exist.
5. Complete the stable-tree cutover and scan so final item IDs exist.
6. Install the matching plugin.
7. Run analysis and compare its plan with direct read-only database inspection.
8. Arm and run apply.
9. Verify exact state through Jellyfin's API and UI-facing DTOs.
10. Verify the final item IDs did not change and playback opens the stable-tree
    target.
11. Verify sentinel rows are byte-for-byte unchanged.
12. Analyze again and require `already_applied`, with zero proposed writes.

Add focused integration cases for:

- Movie with both IMDb and TMDb keys.
- TMDb-only movie with one usable numeric key is reported but not applied.
- TMDb-only item with two corroborating detached keys.
- Episode keys derived from a series provider key.
- Movie or episode with no provider IDs.
- Provider metadata changed between deletion and recovery.
- Duplicate movies and duplicate episodes sharing a key.
- Alternate/merged versions.
- Two libraries containing the same title.
- A current row with explicit default-looking state.
- A current row with newer resume/favorite/rating data.
- Unknown `UserId` after a user was deleted.
- Detached rows removed by Jellyfin's cleanup task.
- Target moved or rescanned between analyze and apply.
- Source tombstone changed or disappeared after planning.
- Library scan already running when apply starts.
- An accidentally configured apply schedule while the plugin is unarmed.

### 12.4 Failure injection

- Throw before the first save: zero state changes.
- Throw after N successful saves: exactly N verified changes, then stop.
- Fail verification after a save: stop and identify the uncertain item.
- Fail ledger write after a save: rerun classifies the item as already applied.
- Terminate Jellyfin between arm consumption and the first save: restart is
  unarmed and makes no automatic changes.
- Terminate Jellyfin after a save but before its ledger record: restart is
  unarmed; a new analysis is idempotent.
- Corrupt or edit a plan file: hash validation rejects it.

### 12.5 Scale and locking

Generate at least:

- 10,000 episodes,
- 2,000 movies,
- 5 users,
- multiple keys per item.

Measure analysis memory, query duration, plan size, sequential apply throughput,
and database lock time.  The test is successful only if normal Jellyfin reads
remain responsive during analysis.  Apply performance is secondary to safety;
do not parallelize merely to improve the number.

---

## 13. Acceptance criteria

The first releasable version must satisfy all of the following:

- Gate 0 passes on both supported server lines.
- Neither task has a default trigger.
- Analysis produces zero `UserData` mutations.
- Apply cannot write without a matching, unexpired, one-time arm.
- Every write was present in the reviewed plan and passed whole-plan preflight.
- No ambiguous key, unsupported item, unknown user, out-of-scope path, or current
  state conflict is written.
- Writes use `IUserDataManager`; no SQL/EF update or delete targets `UserData`.
- Sentinel rows are unchanged after recovery.
- Each successful write is verified through the manager and current rows.
- A second analysis proposes zero writes for already recovered pairs.
- Cancellation and injected failures leave a truthful partial-run ledger.
- The complete disposable-server suite passes on 10.11.11 and the supported
  12.0 build.
- Documentation clearly states that this recovers uniquely attributable
  snapshots, not a complete event history.

---

## 14. Rollout

1. **Gate 0 probe:** prove plugin discovery and database-factory injection.
2. **Analyzer-only alpha:** ship no apply task; collect classification results
   from disposable and consenting real backups/servers.
3. **Go/no-go review:** continue only if uniquely matchable, state-bearing rows
   are common enough to justify writes.
4. **Apply beta:** enable one-time-armed writes on disposable servers and then a
   small number of backed-up installations.
5. **Version expansion:** add Jellyfin patch versions only through the CI and
   integration matrix.
6. **Maintenance posture:** once the stable-path migration population has paid
   down its stranded state, keep the plugin minimal.  Do not turn low ongoing
   usage into a reason to add synchronization or post-scan hooks.

---

## 15. Deliberately rejected directions

| Direction | Reason |
|---|---|
| Raw SQLite access from the plugin | Competes with the live server, mishandles non-SQLite providers and WAL concerns, and bypasses Jellyfin's context configuration |
| Directly change sentinel `ItemId` to the current item | Can collide with existing rows, bypasses cache/event behavior, and recreates the unsafe database-cutover class of operation |
| Delete tombstones after recovery | Destructive, unnecessary for recovery, and makes mistakes harder to investigate |
| Fuzzy title/year/path matching | A plausible wrong match is worse than an explicit no-match |
| Parse every numeric key as TMDb | Provider namespace and item type are not encoded in the raw value |
| Merge with current non-empty state | No per-field chronology exists for favorite, rating, or resume state |
| Restore stream indexes | Positional indexes may no longer identify the same streams after replacement |
| Parallel apply | Raises lock pressure and expands the failure blast radius |
| One persistent "apply mode" task | Can become a repeating mutator if a trigger is later added |
| Post-scan hook or Coldarr scheduler integration | Converts bounded recovery into a standing reconciliation system |

---

## 16. Possible later extensions

These require separate evidence and are not implied by the v1 design:

- Operator-selected resolution of ambiguous candidates.
- Explicit field-by-field merge policies.
- Recovery of safe stream selections when media identity can be proven.
- Series/season aggregate state.
- Cross-server user mapping.
- A Coldarr manifest-assisted mapping of otherwise unmappable historical
  path-GUID keys.
- Upstreaming a supported Jellyfin service for enumerating detached user data,
  removing the plugin's EF implementation dependency.

The correct default for each is "not implemented," not an automatic fallback.

---

## 17. Empirical results

Observations from a disposable-server run on 2026-08-12.  This section records
what was measured and what was not covered.  It does not revise the design
sections above.

### 17.1 Method

**Servers.** Official generic linux-amd64 tarballs (self-contained) run directly
with dedicated `--datadir/--configdir/--cachedir/--logdir`: Jellyfin 10.11.11 and
12.0-rc5.  Docker was unavailable in the test environment.

**Probe plugin.** One `IScheduledTask` returning an empty trigger list,
constructor-injecting `IDbContextFactory<JellyfinDbContext>`, projecting
`UserData` with `AsNoTracking()` and logging each row.  The plugin archive
contains only its own assembly plus `meta.json`; host packages are referenced
with `ExcludeAssets=runtime`.

**Packages.** `Jellyfin.Controller` and `Jellyfin.Database.Implementations` are
both published on nuget.org at `10.11.11` and `12.0.0-rc5`.  Builds target
`net9.0` for 10.11.11 and `net10.0` for 12.0 RC5.  The plugin source was
byte-identical across the two builds; only `TargetFramework` and package version
differed.

**Library topology.** One `Movies` library and one `Shows` library, each with two
locations — a "hot" root and a "cold" root — mirroring a pre-tree Coldarr
install.  Internet metadata providers disabled; provider IDs supplied by local
NFO.

**Content.** One movie (NFO `tmdb 603`, `imdb tt0133093`), one series (NFO
`tmdb 1396`, `imdb tt0903747`) with one episode, as 1-second generated MP4s.  Two
users with distinct state.

**Move procedure.** Title folders were relocated between roots on disk, followed
by `Scan Media Library` runs, matching what an *arr-driven move produces.

### 17.2 Gate 0 results

Passed on both server lines.  Resolved types:

```text
10.11.11  Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory`1[[
          Jellyfin.Database.Implementations.JellyfinDbContext,
          Jellyfin.Database.Implementations, Version=10.11.11.0]]
12.0 RC5  ... same, Version=12.0.0.0
```

In both cases the injected factory belonged to the host assembly, the context was
`Jellyfin.Database.Implementations.JellyfinDbContext`, and the `AsNoTracking()`
projection over `UserData` executed.  The task registered with `Triggers: []`,
survived a server restart, and a subsequent `Scan Media Library` completed.  No
errors or warnings appeared in either server log.

### 17.3 Plugin ABI enforcement

The `net9.0` build, with `meta.json` declaring `targetAbi 10.11.11.0`, was
installed on the 12.0 RC5 server.  It loaded, registered its task, executed, and
bound to the host's `12.0.0.0` assembly.  No rejection and no warning was
emitted at load or at run time.

This matches `targetAbi` being a minimum-version check — `serverVersion >=
targetAbi` — rather than a declaration of exact compatibility
([`PluginManager.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Server.Implementations/Plugins/PluginManager.cs#L688-L704)).

### 17.4 Key generation

Observed `CustomDataKey` values, matching §2.1:

```text
Movie   (per user, 3 rows)   603
                             74f9957e-b453-7dbb-b614-d528834acab2   # item GUID
                             tt0133093
Episode (per user, 2 rows)   e20d6d96-c126-ffa4-af28-fd74a4da81b2   # item GUID
                             tt0903747001001                        # series IMDb + S001E001
```

The numeric key is stored bare, with no provider namespace.  No TMDb-derived
episode key was produced, although the series carried `tmdb 1396`.

### 17.5 Detach behaviour

After the first move, the movie's six rows (three keys x two users) had `ItemId`
set to `00000000-0000-0000-0000-000000000001` and `RetentionDate` stamped.
`UserId`, `CustomDataKey`, and all state fields were preserved exactly:

```text
DETACHED key=603          item=00000000 user=a7fb7734 played=True  fav=True rating=10
DETACHED key=74f9957e-... item=00000000 user=a7fb7734 played=True  fav=True rating=10
DETACHED key=tt0133093    item=00000000 user=a7fb7734 played=True  fav=True rating=10
DETACHED key=603          item=00000000 user=18fe613b played=False fav=True rating=1
DETACHED key=74f9957e-... item=00000000 user=18fe613b played=False fav=True rating=1
DETACHED key=tt0133093    item=00000000 user=18fe613b played=False fav=True rating=1
```

The item created at the new path carried zero `UserData` rows.  The episode,
which was not moved, retained its live rows throughout.

### 17.6 Removal is suppressed when a root would be left empty

After the first move, repeated `Scan Media Library` runs did not remove the item
at the vacated path; the library reported two movies with the same name at
different paths.  Adding an unrelated file to the vacated root caused removal on
the next scan, logged as `Removing item, Type: "Movie" ...`.  Between creation of
the new item and removal of the old one, both exist simultaneously.

This matches the accessibility guard that skips a top-level library folder when
`GetFileSystemEntryPaths(path)` yields nothing
([`Folder.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/Entities/Folder.cs#L354-L368),
[`DirectoryService.IsAccessible`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/Providers/DirectoryService.cs#L111-L114)).

### 17.7 Reattachment scope

Four relocations were observed.  "Same parent" means the removal and the creation
occurred under the same configured library location.

| # | Title | From → to | Same parent | Detached rows for those keys beforehand | Provider-keyed rows after |
|---|---|---|---|---|---|
| 1 | Test Movie | `hot/movies/Test Movie (2020)` → `cold/movies/Test Movie (2020)` | no | none | detached; new item had zero rows |
| 2 | Test Movie | `cold/movies/Test Movie (2020)` → `cold/movies/Test Movie (2020) [v2]` | yes | yes | live on the new item |
| 3 | Test Movie | `cold/movies/…[v2]` → `hot/movies/…[v3]` | no | none | detached; new item had zero rows |
| 4 | Fresh Title | `cold/movies/Fresh Title (2021)` → `cold/movies/Fresh Title (2021) [renamed]` | yes | none | live on the new item |

Relocations 1–3 vary two things at once — whether the move crossed a configured
location, and whether detached rows for those keys already existed — so they do
not separate the two.  Relocation 4 was run to separate them: a previously unused
title with `tmdb 550` / `imdb tt0137523`, no detached rows for either key, both
roots kept populated so the §17.6 guard could not fire, a rename inside one
location, and exactly one scan, which removed the old item and created the new
one in the same pass.

Before (all rows for those keys):

```text
live key=550                    item=ddf4bdf2 user=a7fb7734 played=True  fav=False rating=10
live key=ddf4bdf2-...           item=ddf4bdf2 user=a7fb7734 played=True  fav=False rating=10
live key=tt0137523              item=ddf4bdf2 user=a7fb7734 played=True  fav=False rating=10
live key=550                    item=ddf4bdf2 user=18fe613b played=False fav=True  rating=None
live key=ddf4bdf2-...           item=ddf4bdf2 user=18fe613b played=False fav=True  rating=None
live key=tt0137523              item=ddf4bdf2 user=18fe613b played=False fav=True  rating=None
```

After (old item `ddf4bdf2` removed, new item `57f62a1b` created in the same scan):

```text
live     key=550                item=57f62a1b user=a7fb7734 played=True  fav=False rating=10
live     key=tt0137523          item=57f62a1b user=a7fb7734 played=True  fav=False rating=10
DETACHED key=ddf4bdf2-...       item=00000000 user=a7fb7734 played=True  fav=False rating=10
live     key=550                item=57f62a1b user=18fe613b played=False fav=True  rating=None
live     key=tt0137523          item=57f62a1b user=18fe613b played=False fav=True  rating=None
DETACHED key=ddf4bdf2-...       item=00000000 user=18fe613b played=False fav=True  rating=None
```

The provider-keyed rows moved to the new item with state intact; only the
GUID-keyed row of the removed item detached.  Across all four relocations,
provider-row recovery tracked whether source and destination shared a configured
location, not whether detached rows already existed.  The folder-validation scope
described below is the source-backed explanation.

**Inference, not established here:** Jellyfin's folder validation deletes removed
items, creates new ones, and then reattaches user data for the valid children of
that pass which share keys with what it just removed
([10.11.11 `Folder.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/Entities/Folder.cs#L455-L493),
[12.0 RC5 `Folder.cs`](https://github.com/jellyfin/jellyfin/blob/v12.0-rc5/MediaBrowser.Controller/Entities/Folder.cs#L687-L695)).
A relocation whose source and destination fall in different validation passes
would place the new item outside the set that pass considers.  This is the only
reattachment call site in either inspected source tree.  That is consistent with
all four observations, but the source path was not stepped through and no
instrumented build was run.

The bounding scope is the validating parent folder, which is not always a
configured library location: nested folders, mixed-content libraries, and
multi-location libraries may draw the boundary elsewhere.  The four relocations
here used one folder level directly beneath a configured location, so they do not
distinguish "root" from "validating parent."

### 17.8 Accumulation by key type

Final state after three moves and two users: 8 detached rows.

```text
4 GUID-keyed      74f9957e-... x2 users,  03cb098f-... x2 users
4 provider-keyed  603 x2 users,           tt0133093 x2 users
```

Provider-keyed rows were not duplicated across moves — one row per
`(UserId, CustomDataKey)`, carrying the most recent snapshot.  GUID-keyed rows
persisted per dead item.

One dead item (`5fc90611...`, created during move 2) contributed no GUID-keyed
row.  Its rows were acquired solely by reattachment, and reattachment did not
add a row keyed by that item's own GUID.

### 17.9 Recovery write against live detached rows

With the six sentinel rows from §17.5 present, played, favorite, and rating were
set on the current item for both users, through the HTTP API.  Every request
returned 200, no exception or constraint error appeared in the log, and the
server remained responsive.  A subsequent dump showed live rows and sentinel rows
coexisting for the same `(UserId, CustomDataKey)` pairs, differing only by
`ItemId`, with the sentinel rows unchanged.

Writes were issued through the HTTP API, which routes to `IUserDataManager`.
Direct in-process `IUserDataManager` calls were not exercised.

### 17.10 Authentication on 12.0 RC5

```text
X-Emby-Token: <token>                    401
?api_key=<token>                         401
Authorization: MediaBrowser Token="..."  200
```

### 17.11 Not covered

- Matching and classification at scale; ambiguity and no-match rates.
- Database providers other than SQLite.
- Direct `IUserDataManager` calls from in-process plugin code.
- The concurrency race described in §9.4.
- Series and season aggregate rows; only movie and episode rows were observed.
- Detached-row counts on a real, long-lived installation.
- Any apply-path safety machinery (arming, plan hashing, preflight, ledger).

### 17.12 Analyzer alpha on both server lines

The Milestone 1 build was run against fresh disposable 10.11.11 and 12.0 RC5
servers reproducing the §17.5 state — six detached rows, three keys x two users,
new item carrying none.  Both lines classified identically: two `ready`
candidates, one per user, admitted by the IMDb evidence rule with the bare TMDb
key contributing as `other_provider`, and the removed item's two GUID-keyed rows
reported `no_current_key_match`.  Recovered state matched what was set through the
API field for field.  `path_outside_final_scope`, `already_applied`, and
`current_state_conflict` were exercised by reconfiguring and re-running.

Every run reported the `UserData` table unchanged, confirmed independently by
hashing every row outside the plugin.  Installing the 10.11.11-targeted build on
the 12.0 server reproduced §17.3 — it loaded silently — and the runtime version
check refused to proceed.  Method, artifacts, and full results:
[`evidence/alpha/`](evidence/alpha/).

Two findings from that run bear on sections above.

**Item queries must hydrate provider IDs.**  `Movie.GetUserDataKeys()` prepends
IMDb and TMDb IDs only from the provider IDs loaded onto the instance.  Querying
`ILibraryManager` without `ItemFields.ProviderIds` makes every item report exactly
one key, its own GUID, so every provider-keyed detached row misses — with no
exception and no warning, reported as "nothing is recoverable".  This is a
property of the reverse index in §7.2 that any implementation has to satisfy, and
its failure mode is indistinguishable from a legitimate result.

**Crossing a configured location does not prevent reattachment.**  §17.7 observed
that provider-row recovery tracked whether source and destination shared a
configured location, while noting its relocations 1–3 confounded that with whether
removal and creation fell in the same validation pass.  A relocation across two
configured locations of one library, with both roots kept populated so a single
scan removed the old item and created the new one, reattached the provider-keyed
rows and detached only the removed item's GUID-keyed row.  Shared configured
location is therefore not necessary for reattachment, which is consistent with
§17.7's inference that the bounding scope is the validating parent — both roots
of one library are children of the same collection folder.  One observation per
server line; no instrumented build.
