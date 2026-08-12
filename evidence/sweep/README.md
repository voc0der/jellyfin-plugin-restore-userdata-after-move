# Parameter sweep — what recovery depends on

The go/no-go in [PLAN.md §2](../../PLAN.md) asks whether stranded rows are
recoverable often enough to justify building the write path. That is a question
about real libraries, and no synthetic library answers it: whoever generates the
population picks the inputs, and the inputs *are* the answer.

So this does not report a number. It reports the **response surface** — how
recovery moves as library shape moves — which turns the open question from "what
happens on real servers" into "which of these shapes is a real server", something
an operator can check about themselves without publishing anything.

Produced by [`tools/Jellyfin.Plugin.UserDataRestore.Sweep`](../../tools/Jellyfin.Plugin.UserDataRestore.Sweep),
seed `20260812`, 2000 titles per configuration. Raw output: [`sweep.csv`](sweep.csv).

```sh
dotnet run --project tools/Jellyfin.Plugin.UserDataRestore.Sweep -- evidence/sweep/sweep.csv
```

## What is varied, and what is not

Parameterised, because real libraries differ in these: IMDb coverage, TMDb
coverage, share of episodes vs movies, number of users, how much of the library
each user watched, how many times each title moved, how often a title exists
twice in the current catalog, and how often the current item already carries user
state.

**Not** parameterised, because the live runs settled it: how Jellyfin strands rows
at all. A moved title keeps one detached row per `(user, provider key)` holding its
last snapshot, and every removed item leaves GUID-keyed rows that nothing can map
(DESIGN §17.5). The keys are the ones Jellyfin actually emits, in the order it
emits them — a movie reports IMDb, a bare TMDb number, then its own GUID; an
episode reports the series' IMDb ID with zero-padded season and episode, then its
own GUID.

The denominator throughout is **opportunities**: `(user, title)` pairs that had
stranded state. Rows that never became a candidate — because every key they had
was a dead GUID — count as losses, which is the honest accounting for a question
about how much comes back.

## Result 1 — recovery is IMDb coverage, near enough exactly

| IMDb coverage | 0 | 0.2 | 0.4 | 0.6 | 0.8 | 0.9 | 1.0 |
|---|---|---|---|---|---|---|---|
| recovered | 0.0% | 19.7% | 39.1% | 58.6% | 78.9% | 89.8% | 100% |

Nothing else in the sweep has this leverage. The line is straight because an IMDb
key is the only evidence most items can offer: DESIGN §7.3 case 1 (the item's own
GUID) can never fire for a *moved* item — the GUID it was stranded under belongs
to the deleted item and matches nothing.

## Result 2 — TMDb alone recovers nothing, at any coverage

| TMDb coverage, no IMDb | 0 | 0.25 | 0.5 | 0.75 | 1.0 |
|---|---|---|---|---|---|
| recovered | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |

A perfectly catalogued, 100%-TMDb library recovers **zero**. Jellyfin stores TMDb
IDs as bare numbers with no provider namespace, so a lone number is not admissible
identity evidence, and §7.3 case 3 needs *two* provider keys to corroborate each
other — which a TMDb-only movie does not have.

This is the rule behaving exactly as designed, and it is also the sharpest
argument the sweep produces: for these libraries the answer is not "add fallback
matching", it is that the data genuinely does not identify the item.

Episodes are worse off still. In `evidence/alpha` the episode's series carried
**both** an IMDb and a TMDb ID, and Jellyfin emitted only the IMDb composite key —
so an episode has no second key to corroborate with under any circumstances. A
series without an IMDb ID contributes nothing recoverable, which is why the
`episode_share = 1` sweep shows zero `insufficient_identity_evidence`: its
episodes either match on IMDb or produce no candidate at all.

## Result 3 — duplicates cost about what they weigh

| titles duplicated | 0 | 0.05 | 0.1 | 0.2 | 0.35 | 0.5 |
|---|---|---|---|---|---|---|
| recovered | 89.8% | 86.2% | 80.1% | 71.6% | 58.0% | 45.8% |

A second current item reporting the same provider key — another copy in a
different library, or the old one still sitting at a vacated path mid-migration —
makes the key ambiguous and the row is skipped. This is the one loss an operator
can actually reduce: finish the move, let a full scan complete, then run.

## Result 4 — move history is noise, not damage

| moves per title | 1 | 2 | 3 | 5 | 8 |
|---|---|---|---|---|---|
| recovered | 89.8% | 90.9% | 89.4% | 90.6% | 90.3% |
| dead GUID rows | 1,566 | 3,150 | 4,746 | 7,985 | 12,584 |

A library that has been reorganised eight times recovers as well as one moved
once. The GUID rows pile up linearly and every one of them is unmappable, so the
raw `no_current_key_match` count looks alarming while meaning nothing. **Any
headline ratio computed over rows rather than opportunities is dominated by this
artifact** — worth knowing before reading anyone's summary block, including the
ones in `evidence/alpha`.

## Result 5 — existing state subtracts directly

| current items already holding state | 0 | 0.25 | 0.5 | 0.75 | 1.0 |
|---|---|---|---|---|---|
| recovered | 89.8% | 67.7% | 45.7% | 23.3% | 0.0% |

Refusing to overwrite (DESIGN §4.3) costs exactly its own frequency. On a server
where the media moved and nobody watched anything since, this term is near zero;
on one where people kept watching for months before anyone noticed, it dominates.

## The model, tested rather than asserted

The three losses look independent, so:

```
recovery ≈ imdbCoverage × (1 − duplication) × (1 − alreadyHasState)
```

Checked against four configurations the sweeps never visited:

| imdb | duplication | current state | predicted | actual | error |
|---|---|---|---|---|---|
| 0.70 | 0.20 | 0.30 | 39.2% | 40.9% | +1.7 |
| 0.40 | 0.35 | 0.10 | 23.4% | 23.2% | −0.2 |
| 0.95 | 0.05 | 0.50 | 45.1% | 46.4% | +1.3 |
| 0.55 | 0.10 | 0.75 | 12.4% | 12.3% | −0.1 |

Within two points across the range. Good enough to estimate a server's outcome
from three numbers it can measure about itself.

## What this does and does not establish

**Does:** that recovery is governed by IMDb coverage and nothing else of
comparable weight; that TMDb-only libraries are unrecoverable by design, not by
oversight; that move history does not degrade recovery; that row-level ratios are
a misleading way to read any of this.

**Does not:** say where real installations sit on these curves. That is still an
empirical question — but it is now a much smaller one, answerable by *"what
fraction of your movies and series have an IMDb ID"* rather than by collecting
full classification results. One or two real answers are enough to place a whole
population.

The sweep also inherits every assumption in the stranding model. If Jellyfin
strands rows differently than `evidence/alpha` observed — on another provider, or
after an upstream change — these curves move with it.

## A correction found while building this

The first version of the generator gave episodes a TMDb-derived composite key.
Checking against the published plan showed Jellyfin emits only the IMDb composite
even when the series has both IDs. The bug flattered TMDb-only TV libraries, which
in reality recover nothing. Fixed before any figure here was recorded.
