# Sports Guide Art

A Jellyfin plugin that fills missing Live TV guide art for sports programs.

Many IPTV guides only attach a poster to the game itself. The slots around it, such as `Upcoming: Anaheim Ducks vs Utah Mammoth`, `Pregame: …` and `Postgame: …`, have no art, so most Live TV cards are blank for most of the day. This plugin finds those programs and gives them art.

## How it picks art

For every guide program with no poster, it tries these in order:

1. **Same channel:** a program on the same channel with the same title, once prefixes like `Upcoming:` are removed. This is the game's own poster.
2. **Same title anywhere:** the same title on another channel.
3. **Remembered art:** art the plugin saw for that title in an earlier guide refresh. This covers `Postgame:` slots after the game has left the guide.
4. **Generated matchup image:** for titles like `Team A vs Team B` (also `v`, `@` and `at`), it draws a poster and a landscape image with both team logos and colors.

It never replaces art your provider supplied. A generated image is swapped for real art as soon as matching real art appears.

| Poster | Landscape |
| --- | --- |
| Generated 600×900 for the program poster | Generated 1280×720 for thumbnails and backdrops |

## Team logos

- **ESPN** (on by default): looks teams up in ESPN's public team lists for the leagues you choose (NHL, NBA, NFL, MLB, WNBA, MLS and college by default).
- **TheSportsDB** (optional): searches TheSportsDB by team name. The free key `123` is rate-limited; paid keys are not.

You choose which source to try first. The program's categories (e.g. `NHL`) are used to search the right league first.

## Caching

Every image it uses (copied art, generated matchups and team logos) is saved under the server's data folder in `sports-guide-art/`. Art therefore survives guide refreshes and provider URL changes. Unused images are deleted after 30 days (configurable).

A Jellyfin guide refresh removes art the provider didn't send, so the plugin runs again automatically after every **Refresh Guide** task. It also runs at startup and every 6 hours.

## Install

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add:
   ```
   https://raw.githubusercontent.com/RazorzMcLazorz/jellyfin-plugin-sports-guide-art/gh-pages/manifest.json
   ```
2. Install **Sports Guide Art** from the catalog (category **Live TV**) and restart Jellyfin.
3. Open **Dashboard → Plugins → Sports Guide Art** to adjust settings, or click **Save and run now**.

Requires Jellyfin 12.4 or later.

## Settings

| Setting | Default |
| --- | --- |
| Title prefixes to ignore | `Upcoming`, `Pregame`, `Postgame`, `Next`, `Live`, `Replay`, league names, … |
| Ignore trailing `(…)` / `[…]` | On |
| Same channel / same title / remembered art | On |
| Only these channels | All channels |
| Generate matchup images | On |
| ESPN / ESPN leagues | On / NHL, NBA, NFL, MLB, WNBA, MLS, college football and basketball |
| TheSportsDB / API key | Off / `123` |
| Try first | ESPN |
| Run after every guide refresh | On |
| Keep unused cached images | 30 days |

## Build

Requires the .NET 10 SDK.

```sh
dotnet test
dotnet publish src/Jellyfin.Plugin.SportsGuideArt -c Release -o artifacts/publish
```

To install a build by hand, copy `Jellyfin.Plugin.SportsGuideArt.dll` into `<config>/plugins/Sports Guide Art_<version>/` and restart Jellyfin.

## Releasing

`main` is staging and `production` is what's released. Fast-forward `production` from `main`, then push a `vX.Y.Z` tag on `production`. The release workflow tests and builds the plugin, attaches the zip to a GitHub release (notes from `releases/X.Y.Z.md` when present), and adds the version to `manifest.json` on the `gh-pages` branch.

## Notes

Team names, logos and colors belong to their owners. ESPN's team API is public but undocumented and may change. TheSportsDB data is provided under its own terms. This project is not affiliated with Jellyfin, ESPN, TheSportsDB, or any league.

## License

GPL-3.0
