# Radarr Movie Packs

This is a fork of [Radarr](https://github.com/Radarr/Radarr) that can automatically download a pack containing several movies. It is intended for filling gaps in a library while keeping the files you already have.

Movie packs are off by default. To enable them, open a quality profile and turn on **Allow Movie Packs**. Radarr turns upgrades off for that profile and keeps them off while the option is enabled. This covers quality upgrades, custom format upgrades, propers and repacks. Turning pack support off does not turn upgrades back on.

## What Radarr can identify

A pack must identify a known collection or name its movies explicitly. For example:

- `The.Matrix.Trilogy.1999-2003.1080p.BluRay`
- `The Matrix Collection (1999-2003) 1080p`
- `The Matrix (1999) + The Matrix Reloaded (2003) 1080p BluRay`

Refresh the collection in Radarr first. Every movie identified in the pack must already be in your library, be monitored, and use a profile with movie packs enabled. At least one must be missing its file. A stated movie count must agree with the collection metadata; a year range limits the movies considered. Radarr does not guess which three movies an old “trilogy” release means if the collection now contains four.

Actor bundles, vague release names and arbitrary numbered volumes cannot reliably identify their contents. Those need manual review. There is no claim that every pack on every indexer can be recognized automatically.

## Search and import

Normal movie searches continue to work. With packs enabled, Radarr also searches by collection title without restricting the request to one movie's ID or year. Newznab and Torznab use the title-search capabilities advertised by the indexer. FileList, HDBits, Nyaa, PassThePopcorn and TorrentPotato use their existing title-search APIs. RSS feeds can supply packs even when the indexer has no search API. This works through Prowlarr or Jackett when the connected indexer exposes the necessary search capability.

Packs go through the normal release checks for each missing movie, including allowed quality, language, custom formats, availability, release restrictions, tags, delays, blocklists and minimum seeders. The indexer's total size is apportioned by movie runtime for size comparisons; the actual file is checked again before import. One cached collection query can serve several movie searches without sending duplicate requests to the indexer.

Each file is matched independently using Radarr's movie matcher. An obfuscated filename can use its immediate folder when that folder identifies an individual movie. The pack title never supplies the identity of every file. Each file's own quality, language, size and custom formats must satisfy its movie's profile.

Existing movie files are retained. Imports copy or hardlink the source, including for Usenet, until completion is verified. The expected movie list is saved with the grab and survives restarts or later collection changes. Queue entries and history show the individual movies, while the client receives one download. Removing or blocklisting a queue entry applies to the whole pack; the confirmation dialog explains this.

A pack remains incomplete until every expected movie has a file on disk. Unidentified videos and remaining archives also prevent automatic cleanup. Use manual import to identify unresolved videos, or review unwanted extras yourself. Manually importing one movie does not complete the whole pack. A failed pack is blocklisted for all its movies before a replacement search is queued.

## Windows and Linux

The fork supports the normal Windows and Linux builds. Windows path handling includes spaces and 8.3 aliases. The Windows package is a self-contained ZIP with the web interface and ffprobe; extract it before running `Radarr.exe` or `Radarr.Console.exe`. It does not require a separate .NET runtime.

Use a separate application data directory and port when trying the fork. Keep a backup of your existing Radarr database and disable application updates that would replace this build with upstream Radarr. Media upgrades and application updates are separate settings.

## Build and checks

Use the .NET SDK specified in `global.json` and Node.js 20:

```sh
yarn install --frozen-lockfile
yarn tsc --noEmit --skipLibCheck
yarn build --env production
dotnet build src/Radarr.sln
dotnet test src/Radarr.sln --no-build --no-restore --filter 'Category!=ManualTest&Category!=IntegrationTest&Category!=AutomationTest&Category!=WINDOWS'
```

Application type checking skips third-party declaration files because upstream dependencies include conflicting Node type declarations. Build the solution rather than an individual project so the shared analyzer settings are loaded.

On Windows, run `scripts/build-windows.ps1`. The ZIP is written to `_artifacts/radarr-multipack-win-x64.zip`.

The compatibility workflow builds both platforms, runs the unit suite, and exercises disposable application instances with generated video, simulated indexers and a simulated download client. To run a local trial, install FFmpeg, place ffprobe beside the built application, and run:

```sh
python scripts/test-movie-packs.py --app _output/net8.0/Radarr.dll
python scripts/test-movie-packs.py --app _output/net8.0/Radarr.dll --grab-method rss
```

The trial checks discovery, seeders, duplicate prevention, preservation of existing files, partial imports, restart recovery and manual completion. It uses temporary directories and does not connect to your normal Radarr instance or download media. Native Windows trials can add `--short-paths` to exercise 8.3 aliases.

The design addresses the single-movie assumption discussed in [Radarr issue 3866](https://github.com/Radarr/Radarr/issues/3866) and the file-matching problem discussed in [pull request 11554](https://github.com/Radarr/Radarr/pull/11554). This fork is independent of the upstream project. Radarr's GPL license and copyright notices remain in place.
