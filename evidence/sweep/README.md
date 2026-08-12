# Parameter sweep — what the identity rule implies for a library

This is a **simulation of the analyzer against a model of how Jellyfin strands
rows**. It establishes what DESIGN §7.3 implies for a library of a given shape.
It is not evidence about how real libraries are shaped, and no arrangement of it
could be — the generator's inputs are chosen, so its outputs cannot testify about
the world.

What it is good for: showing which library properties the rules are sensitive to,
how much a run can vary for reasons that have nothing to do with the library's
average quality, and which failure modes are structural rather than fixable.

Produced by [`tools/Jellyfin.Plugin.UserDataRestore.Sweep`](../../tools/Jellyfin.Plugin.UserDataRestore.Sweep).
Every point is **20 deterministic seeds**, ~2000 items each. Rates are pooled over
all 20 populations rather than averaged across per-seed rates, so every
opportunity carries equal weight; the ranges are the per-seed extremes. Raw
output: [`sweep.csv`](sweep.csv), which also records the **realized** mean series
length for every point. Every percentage in this document is transcribed from the
program's own printed table, so rounding is applied once, in one place.

```sh
dotnet run -c Release --project tools/Jellyfin.Plugin.UserDataRestore.Sweep -- evidence/sweep/sweep.csv
```

## The model

Not parameterised, because the live runs settled it: a moved title keeps one
detached row per `(user, provider key)` holding its last snapshot, and every
removed item leaves GUID-keyed rows that nothing can map (DESIGN §17.5). Keys are
the ones Jellyfin emits — a movie reports IMDb, a bare TMDb number, then its own
GUID; an episode reports its **series'** IMDb ID with zero-padded season and
episode, then its own GUID.

Parameterised: provider coverage, series length, episode share, users, how much
of the library was watched, moves per title, duplicate current items, and how
often the current item already holds state.

**Coverage is drawn once per series and inherited by every episode**, and series
lengths vary geometrically. That coupling is load-bearing: one absent series IMDb
ID takes all of that show's episodes with it, so recovery rides on a few hundred
draws rather than a few thousand independent ones. Drawing per episode — as the
first version of this tool did — collapses the variance and quietly makes the
simulation a restatement of its own parameters.

Two measures of coverage are reported:

- **Opportunity-weighted** — of the `(user, item)` pairs that had stranded state,
  the fraction whose current item exposes an IMDb-derived key. This is what
  predicts recovery.
- **Item-weighted** — the same fraction over catalog items. This is what a server
  can measure about itself, and it is only a proxy. Here the two agree closely
  *because this generator watches every item with equal probability*. On a real
  server where one long-running show was watched end to end and another was not,
  they can diverge, and nothing in this simulation says by how much.

## Result 1 — recovery is opportunity-weighted IMDb coverage

| nominal IMDb coverage | 0 | 0.2 | 0.4 | 0.6 | 0.8 | 0.9 | 1.0 |
|---|---|---|---|---|---|---|---|
| recovery (pooled) | 0.0% | 20.6% | 41.5% | 61.5% | 82.2% | 91.4% | 100% |
| spread across seeds | — | 13.4–30.5% | 32.2–49.5% | 51.7–68.8% | 72.4–87.2% | 83.4–95.4% | — |

The pooled rate tracks coverage exactly, which is arithmetic rather than
discovery: with no duplicates and no pre-existing state, an opportunity is
recoverable exactly when its item has an IMDb key. The informative part is the
**spread**. At nominal 50% coverage, individual servers of identical description
land anywhere from 39.8% to 58.8%, because coverage is a property of a series and
series are not the same size.

## Result 2 — TMDb alone recovers nothing, at any coverage

| TMDb coverage, no IMDb | 0 | 0.25 | 0.5 | 0.75 | 1.0 |
|---|---|---|---|---|---|
| recovery | 0.0% | 0.0% | 0.0% | 0.0% | 0.0% |

A fully catalogued, 100%-TMDb library recovers **zero**. Jellyfin stores TMDb IDs
as bare numbers with no provider namespace, so a lone number is not admissible
identity evidence, and §7.3 case 3 needs two provider keys to corroborate — which
a TMDb-only movie does not have. Episodes never have a second provider key under
any circumstances.

This one is not a property of the parameter draw. It follows from the rule and the
key shapes, and it says those libraries are out of scope by design rather than
under-served.

## Result 3 — duplicates and pre-existing state each subtract their own frequency

| titles duplicated | 0 | 0.05 | 0.1 | 0.2 | 0.35 | 0.5 |
|---|---|---|---|---|---|---|
| recovery | 91.4% | 86.9% | 82.1% | 73.1% | 59.7% | 45.8% |

| current items already holding state | 0 | 0.25 | 0.5 | 0.75 | 1.0 |
|---|---|---|---|---|---|
| recovery | 91.4% | 68.6% | 45.7% | 22.8% | 0.0% |

Duplicates make a key ambiguous; existing state is refused rather than overwritten
(DESIGN §4.3). Note that item-weighted coverage stays at 91.6% down every column —
these losses are invisible to any coverage measurement.

## Result 4 — move history is noise, not damage

| moves per title | 1 | 2 | 3 | 5 | 8 |
|---|---|---|---|---|---|
| recovery | 91.4% | 91.4% | 91.4% | 91.4% | 91.4% |
| dead GUID rows | 1,609 | 3,217 | 4,826 | 8,044 | 12,870 |

Identical to four decimal places, while unmappable rows grow linearly. **Any
ratio computed over rows rather than opportunities is dominated by this
artifact** — worth knowing before reading any summary block, including the ones
in `evidence/alpha`.

## Result 5 — long series widen the spread

| configured mean episodes/series | 1 | 6 | 18 | 60 | 150 |
|---|---|---|---|---|---|
| **realized** mean | 1.00 | 5.96 | 17.85 | 59.28 | 159.97 |
| recovery (pooled) | 89.8% | 90.5% | 91.4% | 91.1% | 93.6% |
| spread across seeds | 88.2–91.9% | 87.6–93.9% | 83.4–95.4% | 78.8–97.9% | 80.5–98.0% |

The pooled rate barely moves; the range roughly quadruples. A library of a few
long-running shows is a far less predictable candidate than a library of many
short ones at the same coverage, because one missing series IMDb ID carries
hundreds of episodes with it.

The realized column is reported because it is the axis the curve is actually
plotted against, and it does not always match the request: at a configured mean of
150 only about nine series exist in a 1,400-episode library, so sampling noise
moves the realized mean several percent either way. An earlier version was worse
than noisy — it rounded a continuous exponential up and capped lengths at 400,
which turned a configured 1 into 1.57 and a configured 150 into 133. The sampler
is now an exact geometric whose mean is the configured value, series are never
truncated mid-way to hit an item target, and the cap sits far outside the
distribution.

## About the multiplicative relationship

```
recovery = imdbCoverage × (1 − duplication) × (1 − alreadyHasState)
```

**This is the generator's expected result, not a finding.** Those three properties
are drawn as independent events, so their effects necessarily compose this way.
Presenting a run of the same generator as "held-out validation" of it — as an
earlier version of this document did — is circular: it confirms only that the
analyzer follows the assumptions the population was built from.

What the comparison is worth keeping for is narrower: it checks that the analyzer,
the generator, and the arithmetic all agree, so an unexpected bend in a curve
means a bug worth finding rather than noise.

| imdb | duplication | current state | expected | simulated | delta |
|---|---|---|---|---|---|
| 0.70 | 0.20 | 0.30 | 39.2% | 40.5% | +1.3 |
| 0.40 | 0.35 | 0.10 | 23.4% | 24.6% | +1.2 |
| 0.95 | 0.05 | 0.50 | 45.1% | 45.5% | +0.4 |
| 0.55 | 0.10 | 0.75 | 12.4% | 12.4% | 0.0 |

Real validation would require analyzer results from real installations. There are
none yet.

## What this does and does not establish

**Does:** that TMDb-only libraries recover nothing under the current identity rule;
that dead GUID rows scale with move count and make row-level ratios misleading;
that duplicate current keys and pre-existing target state each reduce ready
candidates roughly in proportion to their frequency; that identically-described
libraries vary by tens of percentage points, and that the variation grows with
series length.

**Does not:** say where real installations sit on any of these curves, or that a
single measured coverage number is enough to place one. Opportunity-weighted
coverage is the predictive quantity; the item-weighted figure a server can easily
measure only approximates it, and the approximation degrades exactly when viewing
is concentrated in a few titles.

The simulation also inherits its stranding model from `evidence/alpha`. If Jellyfin
strands rows differently — on another provider, or after an upstream change —
these curves move with it.

## Corrections made while building this

**Published in the first version of this document, and withdrawn:**

- **Coverage was drawn per episode.** Jellyfin derives an episode's key from its
  series' IMDb ID, so coverage is a property of a series and one missing ID takes
  every episode of that show with it. Thousands of independent per-episode draws
  averaged that risk away, understating the variance and making the result close
  to a restatement of its own parameters. Every figure in the first version is
  superseded for this reason.
- **The multiplicative relationship was called validated** against held-out
  configurations. Those configurations came from the same generator, so the check
  was circular. See the section above.
- **A single unweighted coverage fraction was said to locate a real server on the
  curve.** It cannot: the predictive quantity is opportunity-weighted.

**Introduced during the rewrite and fixed before publication:**

- **Removed items' GUIDs were drawn from the same identity space as live items**,
  so some stranded "dead" rows matched a live item exactly and counted as
  recovered under §7.3 case 1. It put a 2% floor under zero-coverage libraries,
  pushed recovery to 101%, and made recovery appear to improve with move count.
  This never reached a published number — the first version drew those GUIDs from
  `Random`, where a collision is possible only in the sense that any 128-bit
  collision is, and none occurred. An earlier draft of this section claimed the
  published figures contained it; that was wrong, and the git history says so.
  There is now a test asserting no stranded GUID key matches a live item.
- **Series lengths did not have the configured mean.** The sampler rounded a
  continuous exponential up and capped at 400, so a configured mean of 1 produced
  1.57 and 150 produced 133, and truncating the final series to hit an item target
  biased it further. Corrected to an exact geometric, whole series only, with the
  realized mean now reported alongside every point.
- **Ambiguity and no-match counts were read off candidate totals**, where they are
  always zero — those codes are row-level, since a row that matched nothing never
  becomes a candidate.
- **Episodes were given a TMDb composite key.** The published plan shows Jellyfin
  emits only the IMDb composite even when the series carries both IDs.
