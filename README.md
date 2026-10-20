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
