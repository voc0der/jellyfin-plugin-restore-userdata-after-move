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

## Linting

Run lint checks locally before opening a PR:

```bash
dotnet format whitespace --verify-no-changes
dotnet format style --verify-no-changes --severity warn
```

## Before changing behaviour

Read [DESIGN.md](DESIGN.md) first. This plugin reads a database that holds the
only surviving copy of someone's watch history, so several things that look like
obvious improvements are refused on purpose — fuzzy title matching, treating a
bare number as a TMDb ID, merging into existing user state, editing the sentinel
rows. §15 lists them with reasons, and PLAN.md §5 lists what has already been
decided.

Those are not closed forever, but reopening one takes evidence, not preference.

Two properties are load-bearing and worth stating plainly:

- **Analysis writes nothing.** The task fingerprints every `UserData` row before
  and after each run and records both in the plan. If a change makes that proof
  stop holding, the change is wrong, not the proof.
- **An honest "cannot tell" beats a plausible guess.** A high no-match or
  ambiguity rate is a real result about a library, not a bug to be tuned away.

## Reporting Issues

- Search existing issues before opening a new one
- Include Jellyfin version, plugin version, and relevant logs
- For classification problems, the `summary` block of your plan file is the most
  useful thing you can attach — counts only, no titles or user IDs

## Pull Requests

- Keep changes focused and minimal
- Test against a running Jellyfin instance before submitting
- Describe what your PR changes and why

## LLM Disclosure

This project uses LLM-assisted development (Claude). Contributions generated with
AI assistance are welcome, but please review and test all code before submitting.
