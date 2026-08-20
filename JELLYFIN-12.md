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
a 12.0 RC5 install identifies it as a prerelease: its assembly, file and
informational versions all read `12.0.0`, indistinguishable from RC4 or from
stable 12.0.0.

The archive named `jellyfin-12.0.0-rc5` was built against RC5 and will also load
on any other server reporting `12.0.0`. Before trusting it on stable 12.0, re-run
the validation in [evidence/alpha/](evidence/alpha/) against that build. As a
backstop the plugin verifies that the server's `UserData` table still has every
column it reads, and refuses if not.
