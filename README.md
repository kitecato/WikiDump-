# WikiDump

BepInEx plugins that export Valheim's data to JSON and PNG, built for
[Valheim Wiki](https://valheimwiki.net/en/main), a Ukrainian/English Valheim wiki.

- **WikiDump**: dumps the vanilla game (items, recipes, drops, creatures, spawns, locations, translations, icons).
- **WikiDumpMods**: dumps everything installed mods add, attributes each object to its mod,
  and lists what mods changed in vanilla (recipes, drops, trader stock, stats).

Read-only: nothing in the world, character or other mods is changed.
Output goes to `BepInEx/config/wikidump/` and `BepInEx/config/wikidump_mods/`.

## WikiDumpMods

Tested on Valheim 1.0.16 with BepInEx 5.4.23.

**Install:** import the Thunderstore package (`WikiDumpMods_<version>_thunderstore.zip` from Releases) in r2modman,
or drop `WikiDumpMods.dll` into `BepInEx/plugins`. Enter a world and wait ~10 s; press **F11** to dump again.

**What it exports** (`BepInEx/config/wikidump_mods/`):

| File | Contents |
|---|---|
| `items.json` | modded items: names/descriptions per language, icons, stats per quality, recipes with per-upgrade cost and station level, how to obtain, uses, sets, status effects, in-game tooltips |
| `pieces.json` | modded build pieces and stations, plus vanilla ones that now need modded materials |
| `creatures.json` | modded creatures and vanilla ones that drop / eat modded items: health, resistances, attacks, drops, spawns, AI, taming |
| `status_effects.json`, `recipes.json`, `world.json` | effects, recipes, spawns, events, locations and dungeon rooms with modded content |
| `vanilla_changes.json` | field-level list of vanilla values the mods changed |
| `vanilla_items_linked.json` | vanilla items that mods gave new sources or uses |
| `mods.json`, `mods/` | per plugin: version, Thunderstore manifest, README, all config entries, Harmony patches, asset bundles, content list |
| `origin.json` | why each object counts as modded and which mod owns it |
| `localization/` | translation tables, per-mod translations with the source of each line, missing translations |
| `icons/` | PNG icons of items (all variants), pieces, stations, status effects, skills, plus each mod's icon |
| `raw/` | raw component dump of every modded prefab |

**How mod ownership is decided:** Jotunn's mod registry, ItemManager/PieceManager-style libraries, asset bundles
shipped with a mod (checked by object identity, not file name), runtime-created objects, and a built-in vanilla
baseline for game 1.0.16. Every verdict lists its evidence in `origin.json`.

**Drop chances** (`chanceAny`) model the game's drop-table code: table chance, roll count, weights and `oneOfEach`
(exact for one-of-each tables).

**Vanilla baseline:** a baseline of unmodded Valheim 1.0.16 is embedded. After a game update, make a new one:
disable every mod except BepInExPack, WikiDumpMods and Jotunn, enter a world, and wait for
"збережено ванільний еталон" (`vanilla_baseline.jsonl.gz`). A local baseline takes priority over the embedded one.

**Config:** `BepInEx/config/ua.valheimwiki.wikidumpmods.cfg` (hotkey, languages, icons, icon colour mode,
vanilla locations scan, include vanilla, tooltips, mod static data, raw depth, force baseline).

### Build

`WikiDumpMods/build.sh` compiles with Mono `mcs` against BepInEx 5.4.23.2 and a small Unity stub
(`stub/UnityEngine.cs`); game types are accessed by reflection, so no game DLLs are needed.
`test/` holds a mock of the game used to run the dumper end-to-end outside Valheim.
`baseline/convert.py` turns a vanilla WikiDump 1.1.x dump into the embedded baseline.

## Repository layout

```
WikiDumpMods/
  src/            plugin source
  stub/           Unity API stub used for compiling
  baseline/       embedded vanilla baseline + converter
  test/           offline mock tests
  thunderstore/   manifest.json, icon.png, README.md for the package
  build.sh
```
