# Jellyfin 12.0 RC5

The plugin depends on Jellyfin implementation internals, so builds are pinned per
server version and 12.0 has its own repository URL.

Add it under **Dashboard > Plugins > Repositories**, install **Restore User Data
After Move** from the catalog, and restart.

```
https://raw.githubusercontent.com/voc0der/jellyfin-plugin-restore-userdata-after-move/main/manifest-jellyfin-12.json
```

Add this repository or the 10.11 one, not both. Jellyfin reads `targetAbi` as a
minimum rather than a match, so a 12.0 server considers the 10.11 build
installable and will offer it to you. It then loads and refuses to run, because
the plugin checks the running server version itself. Adding both repositories has
the same effect: Jellyfin merges them by plugin ID and offers whichever version
number is highest.

## What the version check can and cannot tell apart

The check is `major.minor.build`, which is as exact as Jellyfin allows. Nothing in
a 12.0 install identifies which build it is: assembly, file and informational
versions all read `12.0.0`, and RC4, RC5 and stable are indistinguishable by that
check.

The archive is now named `jellyfin-12.0.0` and is built against the stable
`12.0.0` packages, so the package it was compiled against and the version the
server reports finally agree. It will still load on any server reporting
`12.0.0`, including the release candidates.

One caveat worth stating plainly: the validation on record in
[evidence/alpha/](evidence/alpha/) was performed on 2026-08-12 against **RC5**,
and has not been re-run against stable 12.0.0. The plugin compiles and its tests
pass against the stable packages, but that is a narrower claim than the alpha
run. As a backstop the plugin verifies at runtime that the server's `UserData`
table still has every column it reads, and refuses if not.
