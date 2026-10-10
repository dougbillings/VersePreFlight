# Notices

## Fan project

Verse Preflight is an unofficial Star Citizen fan project, not affiliated with the Cloud
Imperium group of companies. All content not authored by its developers or users is property
of their respective owners. Official site: https://robertsspaceindustries.com

Star Citizen®, Squadron 42®, Roberts Space Industries® and Cloud Imperium® are registered
trademarks of Cloud Imperium Rights LLC.

## Rules this project follows

These come from the
[Star Citizen Fankit and Fandom FAQ](https://support.robertsspaceindustries.com/hc/en-us/articles/360006895793-Star-Citizen-Fankit-and-Fandom-FAQ)
and apply to every contribution and release:

- **Free and non-commercial.** No sales, paid features, ads, paywalls or donation links
  (the FAQ lists donations as a prohibited commercial use).
- **No Cloud Imperium assets.** No logos, artwork, screenshots, fonts, music or game files in
  the app, its icon, the repository or release downloads.
- **No redistribution of game content.** Files such as StarStrings' `global.ini` are downloaded
  from their authors at run time and never included in this repository or its releases.
- **The game itself is never modified.** No changes to game executables or `Data.p4k`, and no
  interaction with the running game.
- **Game files are only read, locally.** The experimental character viewer reads the player's own
  installation (`Data.p4k`, read-only; encrypted entries are refused and no keys are included) to
  draw their saved characters on their own screen. It has no export: nothing is extracted for
  sharing, and what it caches (head shapes, item names) stays on that PC.
- **Visible notices.** The fan-project notice above appears in the README, the app's main window,
  the About screen and the first-run screen, in normal-sized text.
- **Names.** No "Star Citizen", "Roberts Space Industries", "Cloud Imperium", "Turbulent",
  "Squadron 42" or in-game entity names (manufacturers, ships, locations) in the product name or
  any domain.
- **No data collection.** No telemetry or analytics.

## Third-party components

| Component | Author | License |
|---|---|---|
| [StarStrings](https://github.com/MrKraken/StarStrings) (downloaded at run time, optional) | MrKraken | See the project; unofficial fan localization |
| [.NET](https://github.com/dotnet/runtime) | .NET Foundation | MIT |
| [StarBreaker](https://github.com/diogotr7/StarBreaker): game file format knowledge (archive, meshes, character files, binary XML, game database), ported to C# where noted in the source | diogotr7 | MIT |
| [ZstdSharp](https://github.com/oleg-st/ZstdSharp) | Oleg Stepanischev | MIT |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | AvaloniaUI OÜ | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) (via Avalonia; also used to generate the icon) | Microsoft | MIT |
| [Velopack](https://github.com/velopack/velopack) (installer and updates) | Velopack Ltd | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | .NET Foundation | MIT |
| [Inter](https://github.com/rsms/inter) font (via Avalonia.Fonts.Inter) | Rasmus Andersson | SIL Open Font License 1.1 |

A complete list of bundled libraries will be generated automatically for each release.
