# Restore User Data After Move

<p align="center">
  <img src="icon.png" alt="jellyfin-plugin-restore-userdata-after-move icon" width="180" />
</p>

<p align="center">
  <a href="https://github.com/voc0der/jellyfin-plugin-restore-userdata-after-move/releases/latest">
    <img src="https://img.shields.io/github/v/release/voc0der/jellyfin-plugin-restore-userdata-after-move?label=stable%20release" alt="Stable release version" />
  </a>
  <a href="https://github.com/voc0der/jellyfin-plugin-restore-userdata-after-move/tree/main/tests">
    <img src="https://img.shields.io/badge/coverage-77%25-yellowgreen" alt="Code coverage percentage" />
  </a>
  <a href="https://github.com/voc0der/jellyfin-plugin-restore-userdata-after-move/issues">
    <img src="https://img.shields.io/github/issues/voc0der/jellyfin-plugin-restore-userdata-after-move?color=DAA520" alt="Open issues" />
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/voc0der/jellyfin-plugin-restore-userdata-after-move?color=97CA00" alt="License" />
  </a>
</p>

A Jellyfin plugin that restores watch state lost when media files move. Jellyfin derives an item's ID from its path, so a moved file becomes a new item and the old one's user data is left behind, detached but not deleted. This plugin finds those rows, works out which item each belongs to now, and puts back the played status, play count, resume position, favorite, last played date and rating, per user, for movies and episodes.

## Installation

Requires Jellyfin 10.11.11. Add this repository under **Dashboard > Plugins > Repositories**, install **Restore User Data After Move** from the catalog, and restart.

```
https://raw.githubusercontent.com/voc0der/jellyfin-plugin-restore-userdata-after-move/main/manifest.json
```

On Jellyfin 12.0 RC5, use [JELLYFIN-12.md](JELLYFIN-12.md) instead.

### Manual

1. Download the ZIP for your server from the [releases page](https://github.com/voc0der/jellyfin-plugin-restore-userdata-after-move/releases)
2. Extract it into `<jellyfin-data>/plugins/`
3. Restart Jellyfin

#### Building from source

```bash
./build.sh
```

Runs the tests and writes one archive per server version to `artifacts/`.

## Configuration

**Dashboard > Plugins > Restore User Data After Move**: tick the movie and TV libraries you want recovered, and save. Nothing is ticked on a fresh install, and nothing ticked means nothing runs.

**Dashboard > Scheduled Tasks > Restore user data after move**: the task ships with no schedule, since it is only useful once your files have finished moving and the library has been rescanned. Add a trigger timed to land after both; daily at 3 AM is a reasonable default. That page also runs it by hand and reports the results.

Leave it on a recurring schedule rather than running it once. When a moved item is not yet identified, its rows strand and Jellyfin never retries; each run picks them up as soon as the provider IDs arrive.

Each run writes a plan and a per-write ledger to `<jellyfin-data>/plugins/Jellyfin.Plugin.UserDataRestore/plans/`.

## Documentation

[DESIGN.md](DESIGN.md) covers the specification, safety invariants and measurements, plus [upgrade notes and known limits](DESIGN.md#18-operator-notes). [CONTRIBUTING.md](CONTRIBUTING.md) covers building and linting.

## License

[MIT](LICENSE)
