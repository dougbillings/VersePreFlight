<img src="assets/icon-256.png" alt="Verse Preflight icon" width="96" align="right" />

# Verse Preflight

> **This is an unofficial Star Citizen fan project, not affiliated with the Cloud Imperium group of companies. All content in this project not authored by its developers or users is property of their respective owners.**
> Official site: [robertsspaceindustries.com](https://robertsspaceindustries.com)

**Patch day? Run Preflight.** A free, open-source tool for Star Citizen players that gets your
game ready after every patch:

- **Folder links:** keep your control mappings, custom characters and screenshots in a folder
  you choose (for example a cloud-synced folder shared between PCs). Patches can't wipe them,
  and your existing files are never overwritten: conflicting copies are backed up instead.
  Every link can be undone.
- **Cache cleaner:** clears the shader cache and other leftovers the game rebuilds, with a
  checkbox and an explanation for each item.
- **StarStrings (optional):** installs or updates MrKraken's community
  [StarStrings](https://github.com/MrKraken/StarStrings) pack, checking that it matches your
  game build first. Your other `user.cfg` settings are kept.

Verse Preflight never modifies the game's executables or `Data.p4k`, never interacts with the
running game, and collects no data.

## Status

Early development. Planned milestones:

1. **Core library** (done): all features, with automated tests on Windows and Linux.
2. **Desktop app** (Avalonia, in progress) and the first public Windows release.
3. **Linux support** for Wine/Proton installs.

Until the first release, there is nothing to download yet.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet test
dotnet run --project src/VersePreflight.App
```

The app icon is generated from code: `dotnet run tools/IconGen/IconGen.cs`
(add `-- --preview preview.png` to see the small sizes enlarged).

## Credits

- **StarStrings** by [MrKraken](https://github.com/MrKraken/StarStrings): a community-made,
  unofficial string replacement pack. Verse Preflight downloads it from the author's own GitHub
  releases and never redistributes it.

## Legal

Star Citizen®, Squadron 42®, Roberts Space Industries® and Cloud Imperium® are registered
trademarks of Cloud Imperium Rights LLC. This project follows the
[Star Citizen Fankit and Fandom FAQ](https://support.robertsspaceindustries.com/hc/en-us/articles/360006895793-Star-Citizen-Fankit-and-Fandom-FAQ):
it is free, non-commercial, contains no Cloud Imperium assets, and does not accept donations.

Verse Preflight's own code is released under the [MIT License](LICENSE). See [NOTICE.md](NOTICE.md).
