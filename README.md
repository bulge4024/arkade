# Arkade

A Windows arcade-cabinet frontend for an existing RetroBat installation, built with C# and WPF on .NET 10.

## Features

- Fullscreen startup with the Arkade intro video and joystick/keyboard navigation.
- Console browser, alphabet jumps, quick console switching and persistent Last played history.
- Background ROM discovery and automatic metadata/artwork lookup.
- Muted selected-game video previews from local RetroBat media.
- Local-folder music player with media-key controls and selectable now-playing animations.
- Optional Windows sign-in startup and a confirmed Power off / Sleep / Restart menu.
- Landscape companion-display artwork that releases the DMD screen while pinball runs.

RetroBat, emulators and games must already be installed. This repository contains no ROMs or commercial game-media packs. Game covers, metadata caches and local machine settings are not uploaded. Console art is read from the installed RetroBat Carbon theme when available.

## Build and run

Use Windows x64 and the .NET 10 SDK. Visual Studio with .NET desktop development is also supported.

```powershell
dotnet build ArcadeOS.slnx -c Release
dotnet run --project ArcadeOS/ArcadeOS.csproj
```

The internal project/namespace remains `ArcadeOS` to retain compatibility with existing installations. The cabinet application is branded Arkade.

The checked-in `retrobat.settings.json` defaults to `C:\RetroBat`. Set the RetroBat path in the installed application's settings file, or use the installer when preparing a cabinet. Do not commit personal ROM paths or credentials.

## Create a cabinet test package

```powershell
.\scripts\Publish-TestBuild.ps1 -Version 0.1.3
```

This produces a self-contained Windows x64 ZIP under `artifacts/`. It includes the .NET runtime, setup scripts, default RetroBat configuration, Arkade intro and branding. No separate .NET install or Visual Studio is required on the cabinet. Extract the entire ZIP and run `Setup.cmd`.

To update the cabinet, close Arkade and rerun setup from the newer package. The installer preserves custom launch profiles and ROM-root settings. Music settings, history and metadata remain in `%LOCALAPPDATA%\ArcadeOS\Metadata`.

In GitHub, **Actions → Cabinet test package → Run workflow** creates a downloadable package for the version you choose. Packages are built only on manual request or when you push a `v*` tag; ordinary source pushes run a compile check. This does not implement an in-app updater or automatically publish GitHub Releases.

## Controls

| Action | Key |
| --- | --- |
| Navigate | Arrow keys / mapped joystick |
| Select | Enter or Space |
| Start game | 1 |
| Back | Esc |
| Menu / systems | Tab |
| Refresh library | F5 |
| Music play/pause | F6 or Media Play/Pause |
| Next / previous song | F7 / F8 or media keys |
| Toggle fullscreen | F11 |
| Close Arkade | Alt+F4 |

Settings includes music-folder selection, animation choice, Start with Windows and companion-screen selection. Start with Windows is per Windows user. Turn it off before removing/moving an installation. Power controls require confirmation and do not force-close other applications.

For companion displays, use Windows extended-desktop mode. Artwork follows console/game selection, remains visible for non-pinball gameplay, and hides before pinball launches. The DMD handoff and hardware-specific sleep behavior should be checked on the cabinet.

Local `<video>`, `<fanart>` and `<marquee>` gamelist entries are supported, as are matching files such as `videos/romname-video.mp4` and `images/romname-marquee.png`. MP4/H.264 and MP3/WAV are recommended; available codecs depend on Windows.

## Third-party content

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The Arkade branding and intro are included; legacy branding and cached game artwork remain local. Downloaded metadata/media retains its respective source and ownership.

## Automatic cabinet updates

Pushes to main run the cabinet package workflow, test the updater, and publish a complete GitHub prerelease with the ZIP and SHA256 file. Versions are generated as 0.2.<workflow run number>; rerun failures with the same number only while the release is still a draft. Installed updater-enabled builds include these public cabinet test releases.

Settings > Check for updates runs a check now. The automatic check uses a persistent 48-hour interval and retries a failed connection after six hours. Prompts wait until browsing; no game is interrupted. Installation requires explicit confirmation, verifies the download, stages a replacement, retains a backup, preserves retrobat.settings.json and restarts Arkade. User data remains under LocalAppData/ArcadeOS. A cabinet running 0.1.x needs one manual upgrade to get this feature.


### Remote ROM library

Settings → Download games / Remote library browses `http://51.255.74.178/R4ms/` and compares each console with the ROM paths from this cabinet's RetroBat configuration (including a custom ROM root). Known system names map directly; unknown or ambiguous server folders require choosing a RetroBat system and pressing Select again to confirm the exact destination. Change Destination allows correcting any mapping, which is saved per cabinet.

Use Up/Down to choose a console or game, Select to open/tick it, Right to reach actions, and Back to close or cancel a download. Select All and Uncheck All affect the current console. Installed titles, including equivalent region-tagged filenames, are disabled until Replace Existing is turned on. Downloads stream on a worker with progress and cancellation; temporary files are removed on failure. A supported archive stays archived; ZIPs for systems that require extracted games are staged and validated before their complete game folder is moved into place. Executables and unsafe archive paths are rejected. Other unsupported archive formats must be supplied as playable files or ZIPs. Folder-based replacements and changes of file format keep the previous content in hidden `.arkade-backup-*` folders for recovery. Backups are excluded from library scans.

A persistent check runs every 36 hours while Arkade is open, with a one-hour retry delay after failure. The first successful scan establishes a baseline; later new file names and new console folders queue a review prompt when the cabinet is idle. Installed games are filtered from notifications when their console mapping is known. No automatic ROM downloads occur. Checks use directory listings and do not download game data. Catalog/mapping state is stored in `%LOCALAPPDATA%\ArcadeOS\Downloads\catalog.json`. Files are downloaded from the configured HTTP server; this directory-index protocol does not provide publisher checksums. Nested remote game directories are not traversed; publish a ZIP or playable file directly inside each console directory.

Run `dotnet run --project tests/RomChecks/RomChecks.csproj -c Release` to exercise the downloader using a fake HTTP server and temporary dummy files. Tests never download actual ROMs or alter the cabinet's library.


### Metadata status and selective retry

Settings → Game Info and Artwork shows the active game, batch progress, matched/unmatched/failure counts, last server error, library cover coverage, unsupported-system count and deferred retries. The browsing progress strip also names the active game and completed cover count. Artwork is applied in batches while browsing; Settings and modal screens defer library changes until you return. Refresh Library rescans local game files and respects provider retry intervals. Retry Missing Info / Artwork queues a selective retry for unmatched, failed and coverless entries; good covers and metadata are preserved. Network failures retry after five minutes; unmatched or coverless results normally retry after 24 hours.

New snapshots arriving during a running metadata pass replace the queued snapshot rather than being dropped. Provider failures are reported per game and do not abort the whole batch. Console metadata now also supports Dreamcast, Saturn, Mega CD, GameCube, DS, PS2, PSP, Atari 2600/7800, Jaguar, Lynx, 3DO, PC Engine, Neo Geo Pocket/Color, WonderSwan/Color and Virtual Boy. The supported disc systems with only title databases use their Redump catalogs for exact box-art matching; full editorial details may be unavailable. Missing database fields are cached to avoid repeatedly requesting nonexistent files.

`dotnet run --project tests/MetadataChecks/MetadataChecks.csproj -c Release` checks the real queue, diagnostics, failure isolation, cancellation, retry behavior, cover preservation and provider catalog fallback using dummy data and a fake HTTP handler.


### Background ROM downloads

Download in Background immediately returns to the library. The queue owns an immutable destination for each selected file and keeps running while browsing, switching consoles, or playing an emulator. You can reopen Download Games to add another console's selections or cancel all remaining downloads. Duplicate queued files are ignored; a failed transfer is reported and the next file continues. Closing the selection screen does not cancel downloads. Closing Arkade cancels the queue and removes incomplete files; automatic resumption after restart is not provided.

A compact widget temporarily replaces the music display in the top right. It shows the current filename, queue position, current-file percentage, transfer speed and estimated time remaining for that file (rather than inventing an estimate for files whose sizes are not yet known). Verification/extraction and waiting-for-game-exit states are explicit. The music itself continues under its normal playback rules and its display returns after the completion notice. During gameplay, a click-through, nonactivating tool window shows this widget only while the launched emulator process is foreground; it never covers the DMD or takes keyboard/controller focus. Exclusive-fullscreen renderers may obscure desktop overlays; borderless/windowed-fullscreen modes permit them.

Completed new games are refreshed into the menu in batches while browsing; refreshes wait until gameplay/dialogs finish. Extracted staging folders are hidden from scans. Replacing an existing file waits for the running game to exit before committing the verified download. App-update prompts wait until ROM downloads finish or are cancelled.

`dotnet run --project tests/DownloadQueueChecks/DownloadQueueChecks.csproj -c Release` checks queue progress, speed/ETA, cross-console additions, duplicate suppression, failure isolation, cancellation, restart, deferred replacements and hidden extraction staging using dummy files.


### Metadata refresh recovery and console coverage

Metadata retry/status handlers now catch recoverable setup and source exceptions, restore controls, and save diagnostics to `%LOCALAPPDATA%\ArcadeOS\Metadata\metadata-errors.log` (or the configured metadata cache). Individual malformed records no longer stop the rest of the batch. Duplicate DAT fields are tolerated, malformed cache artwork paths are ignored, database/image sizes are bounded, and cached artwork is decoded on load at a bounded resolution. Corrupt automatically downloaded artwork is isolated as `.invalid` for recovery; custom RetroBat artwork is left untouched. The app records unexpected fatal errors to the same log without globally suppressing unrelated UI failures.

All configured system identifiers can enter metadata lookup. The registry includes all 92 console/portable IDs in the installed RetroBat catalog, multiplayer aliases, manufacturer/name and configured platform-alias resolution for custom systems. Libretro remains the first choice where available; a streamed LaunchBox Games Database fallback supplies records and artwork across 190 current platforms, including Switch, Wii/Wii U, 3DS, Xbox, PlayStation and obscure consoles. Arcade-style Neo Geo/LCD/TV game identifiers also use MAME's exact short-name lookup. No fuzzy match crosses console boundaries.

The fallback's first setup downloads approximately 104 MB from `https://gamesdb.launchbox-app.com/Metadata.zip` and builds a disk index in the background. It does not load the complete 500 MB XML into memory, download ROM data or require an API key. Setup/download/index progress is displayed in the metadata status. The cache is reused and checked for renewal during long-running sessions; a new catalog is staged before replacing the old index. Library scanning and existing RetroBat metadata continue independently. Missing public records cannot be manufactured: platforms absent from the public catalogs (for example Java ME, original Cassette Vision or Gamate in the audited dump) receive an explicit no-catalog result and retain any local metadata. Full metadata/artwork availability is therefore title- and source-dependent.

Source documentation: https://feedback.launchbox-app.com/en/help/articles/8890692-getting-started-with-the-launchbox-games-database and https://forums.launchbox-app.com/topic/30123-launchbox-games-db-external-scraper/ . Game artwork retains its original owners' rights; the database and ROMs are not included in Arkade releases.

Regression checks: `tests/BroadMetadataChecks` exercises the actual provider router, streamed index, aliases, corruption recovery, provider failure isolation and console routing with dummy data. `tests/MetadataUiChecks` exercises the actual asynchronous retry/status handlers and safe artwork decoding in a small standalone WPF host. Live verification indexed 190 platforms and retrieved a Switch title's metadata and cover without reading or downloading a ROM.


### Gameplay screensaver

After five minutes without keyboard, joystick/controller or mouse activity in a browsing menu, Arkade shuffles the local gameplay videos across the whole library. It fills the primary display, preserves each clip’s aspect ratio, mutes all video audio and temporarily pauses music. Clips advance at their end or after 30 seconds. Any key, controller button/stick, mouse button, wheel or movement returns to the same menu and selection; the wake button is consumed until released. A windowed menu is restored to its previous size after waking.

The screensaver waits during gameplay, startup, dialogs, Settings and update installation. Missing videos leave the menu visible; broken/stalled clips are skipped and retried only after their file changes. A temporary display-awake lease prevents Windows monitor blanking while Arkade is active; it is released when Arkade loses focus, launches a game or closes. Windows power settings are not changed.

`dotnet run --project tests/ScreensaverChecks/ScreensaverChecks.csproj -c Release` checks actual screensaver integration with simulated idle time and controller readings in a standalone WPF host. An optional local MP4 path also exercises real muted playback, EOF advancement and fullscreen restoration. No ROMs, emulator or physical DMD are used by these checks.
