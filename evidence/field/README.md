# First real installation

An analysis run on a production Jellyfin 10.11.11 server: ~31,000 movies and
episodes, ~15 users, ~9,200 detached rows. Plugin 1.0.0.5, 2026-08-12. Read-only
proof reported `unchanged` over ~20,000 rows.

> **Provenance and precision.** The operator adjusted the counts before sharing
> them and asked for further distance from the real values. Rather than publish
> invented numbers that look exact, everything below is **rounded**, and small
> counts are given as bounds. Zeros are exact — they are the load-bearing part.
> Nothing here should be read as a measurement; read it as shape.

## Summary, rounded

| | rows | candidates |
|---|---|---|
| `ready` | ~490 | ~240 |
| `already_applied` | ~4,700 | ~2,700 |
| `current_state_conflict` | ~330 | ~180 |
| `no_current_key_match` | ~3,600 | 0 |
| `source_has_no_effect` | ~15 | <10 |
| `insufficient_identity_evidence` | <10 | <10 |
| `inconsistent_source_state` | <10 | <10 |
| `ambiguous_current_key` | <10 | 0 |
| `unknown_user` | 0 | 0 |
| `unsupported_current_item` | 0 | 0 |
| `path_outside_final_scope` | 0 | 0 |
| `invalid_source_state` | 0 | 0 |

Diagnostics:

| | |
|---|---|
| detached rows inspected | ~9,200 |
| current items inspected | ~31,000 |
| eligible targets | ~31,000 |
| eligible targets with provider keys | all but one |
| distinct current keys | ~92,000 |
| known users | ~15 |
| `seriesGuidEpisodeDerivedRows` | **0** |
| `candidatesBlockedOnlyBySeriesGuidEvidence` | **0** |
| unique match evidence — `series_imdb_episode` | ~2,700 |
| unique match evidence — `current_item_guid` | ~2,100 |
| unique match evidence — `imdb` | ~400 |
| unique match evidence — `other_provider` | ~320 |
| items excluded — every reason | **0** |

## What it establishes

**The identity rule is not what limits recovery.** Single-digit counts of
insufficient evidence and of ambiguity, against roughly three thousand formed
candidates. The strictness of DESIGN §7.3 — no fuzzy matching, no bare
numbers, no title-and-year guessing — was the main open risk to the whole approach
and it costs almost nothing here.

**The fourth evidence case is not needed.** `seriesGuidEpisodeDerivedRows` and
`candidatesBlockedOnlyBySeriesGuidEvidence` are both zero. Those are counts of a
kind, not magnitudes, so the perturbation does not touch them: widening §7.3 to
admit series-GUID-derived episode keys would have unblocked nothing. PLAN §2 can
stop holding that question open.

**Provider hydration works at scale.** All but one of ~31,000 eligible items
reported a key beyond its own GUID. That is the failure that silently broke the
first live run of this plugin, and it does not recur on a real library.

**The new exclusion diagnostics work and report a clean scope.** Every `items.*`
bucket except `eligible` is zero — nothing dropped for a missing media file, an
unconfigured library, or a path outside scope. Auto-detected scope resolved to six
folders across two libraries with no operator input.

**Most stranded state was already recovered by Jellyfin itself.** `already_applied`
dominates the formed candidates, and `current_item_guid` is the second-largest
evidence source — rows whose item ID never changed. This is §17.7 and §17.12
behaviour at production scale: much of what detaches gets reattached, and what
remains is the residue.

**Dead GUID rows are the largest row bucket.** `no_current_key_match` is roughly
40% of the table and can never map to anything, exactly as
[`../sweep/`](../sweep/) predicts. A recovery ratio computed over rows would look
like a failure while nothing was actually lost.

## What it does not establish

Whether the yield justifies the write path. That is a judgement about a few hundred
recoverable snapshots on a ~31,000-item library, and it belongs to whoever runs the
server. Nothing here is a second data point either — one installation, and an
unusually large one.

Nor does it place this server on any curve in [`../sweep/`](../sweep/). Those are
plotted against opportunity-weighted IMDb coverage, which is not something this
summary reports and not something a server can easily measure. What this result
checks is the assumptions underneath them — dead GUID rows dominating the table,
the identity rule costing almost nothing, provider hydration holding at scale —
so the simulation's shape has been compared against reality once and its numbers
have not been compared at all.
