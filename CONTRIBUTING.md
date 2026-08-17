# Contributing to Restore User Data After Move

Issues and pull requests are welcome!

## Getting Started

1. Fork the repository
2. Create a feature branch from `main`
3. Make your changes
4. Submit a pull request

## Building

```bash
./build.sh
```

That runs the tests and writes one archive per supported Jellyfin version to
`artifacts/`. For a plain compile:

```bash
dotnet build --configuration Release
```

The plugin is multi-targeted — `net9.0` for Jellyfin 10.11.11 and `net10.0` for
12.0 — from a single source tree, so both ABIs are compiled on every build.

## Testing

```bash
dotnet test
```

Matching, classification, and plan generation live in
`src/Jellyfin.Plugin.UserDataRestore.Core/`, which references no Jellyfin
assembly, so they can be tested without a server. Keep them there. If a change
needs a Jellyfin type to be tested, that is usually a sign the logic belongs in
the core with the host type mapped at the boundary.

Above that sit two suites the boundary makes necessary: one that builds real
`Movie`, `Episode` and `Series` entities to check what the adapter asks the
server for, and one that runs the database reads against the host's own
`JellyfinDbContext` on both servers' Entity Framework providers, since a query
one translates is not automatically one the other does.

Past all of them is [`scripts/gap/gap.sh`](scripts/gap/gap.sh), which stands up a
throwaway Jellyfin, strands user data by moving files, and proves the restore on
a real server:

```bash
scripts/gap/gap.sh                 # both server lines, around 17 minutes
scripts/gap/gap.sh 10.11.11        # one, and about half that
```

You do not have to run it — CI runs it on every push and on any pull request
that touches `src/`, `build.sh` or the harness — but if you are changing what
the plugin asks of Jellyfin, it is the only thing that will tell you the truth.
Nothing else in the repository starts a server.

## Linting

Run lint checks locally before opening a PR:

```bash
dotnet format whitespace --verify-no-changes
dotnet format style --verify-no-changes --severity warn
python3 .github/workflows/check-release-concurrency.py
python3 .github/workflows/check-live-matrix.py
```

The last two guard invariants that fail silently rather than loudly: that every
commit still gets its own release, and that CI's live proof still covers every
server line the harness supports.

## Before changing behaviour

Read [DESIGN.md](DESIGN.md) first. This plugin reads a database that holds the
only surviving copy of someone's watch history, so several things that look like
obvious improvements are refused on purpose — fuzzy title matching, treating a
bare number as a TMDb ID, merging into existing user state, editing the sentinel
rows. §15 lists them with reasons, and PLAN.md §5 lists what has already been
decided.

Those are not closed forever, but reopening one takes evidence, not preference.

One task analyses and restores in the same pass. There is no separate apply
task, no plan to arm, and no whole-plan preflight — an earlier design had all
three, and the parts of DESIGN.md describing them are marked SUPERSEDED rather
than deleted, because the reasoning is still why the current shape is what it is.
What replaced the preflight is per-write: every condition that admitted a target
is asked again of the live item immediately before writing to it.

Three properties are load-bearing and worth stating plainly:

- **The run accounts for itself.** Every `UserData` row is fingerprinted before
  and after, and every planned write is recorded with what became of it —
  restored, skipped, failed, uncertain, or never attempted. A change that lets a
  run mutate user data without leaving that record is wrong, however convenient.
- **Uncertainty stops the run.** A write whose result cannot be established stops
  the batch rather than being counted and stepped over. The stranded rows are
  never modified, so nothing is lost by retrying tomorrow.
- **An honest "cannot tell" beats a plausible guess.** A high no-match or
  ambiguity rate is a real result about a library, not a bug to be tuned away.

## Reporting Issues

- Search existing issues before opening a new one
- Include Jellyfin version, plugin version, and relevant logs
- For classification problems, the `summary` block of your plan file is the most
  useful thing you can attach — counts only, no titles or user IDs

## Rules

- Keep branches, commits, and PRs focused. Do not mix unrelated local changes into the same PR.
- Use semantic names by default.

## Naming

- Branches: `fix/<scope>-<summary>`, `feat/<scope>-<summary>`, `refactor/<scope>-<summary>`
- Commits: `fix(scope): summary`, `feat(scope): summary`, `refactor(scope): summary`
- PR titles: `fix(scope): summary`, `feat(scope): summary`, `refactor(scope): summary`

## Pull Requests

- Keep changes focused and minimal
- Test against a running Jellyfin instance before submitting
- Describe what your PR changes and why

## LLM Disclosure

This project uses LLM-assisted development (Claude). Contributions generated with
AI assistance are welcome, but please review and test all code before submitting.
