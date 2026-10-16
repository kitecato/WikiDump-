"""Converts a vanilla WikiDump 1.1.x dump (made with no other mods) into a WikiDumpMods baseline.
The old dump lists every item, recipe, status effect, location and event, but only 'content' prefabs
(ItemDrop / Piece / Character / the component types below), so the baseline is marked partial."""
import json, sys, datetime
src, out = sys.argv[1], sys.argv[2]
L = lambda f: json.load(open(f"{src}/{f}", encoding="utf-8"))
items, pf, rc, se, loc, world, man = L("items.json"), L("prefabs.json"), L("recipes.json"), L("status_effects.json"), L("locations.json"), L("world.json"), L("manifest.json")
assert man.get("mods") == ["ua.valheimwiki.wikidump " + man["plugin"]], ("not a vanilla dump", man.get("mods"))
COVERED = ["ItemDrop", "Piece", "Character", "Container", "DropOnDestroyed", "CharacterDrop", "Pickable", "PickableItem", "MineRock", "MineRock5",
           "TreeBase", "TreeLog", "LootSpawner", "Destructible", "SpawnArea", "CreatureSpawner", "CraftingStation", "StationExtension", "Smelter",
           "Fermenter", "CookingStation", "Incinerator", "Beehive", "SapCollector", "Plant", "Fish", "Trader", "Tameable", "Procreation", "Growup",
           "MonsterAI", "AnimalAI", "Projectile", "Aoe", "WearNTear", "Fireplace", "Bed", "Chair", "Ship", "Vagon", "TeleportWorld", "ShieldGenerator",
           "Turret", "ItemStand", "OfferingBowl", "RuneStone", "Vegvisir", "Talker", "Leviathan", "Tail", "SEMan", "Humanoid"]
FP_COMPS = ["Piece", "Character", "CharacterDrop", "Container", "Pickable", "DropOnDestroyed", "MineRock5", "MineRock", "TreeBase", "TreeLog", "Trader",
            "Smelter", "CookingStation", "Fermenter", "Incinerator", "MonsterAI", "Tameable", "CraftingStation", "StationExtension", "WearNTear",
            "Plant", "Fish", "OfferingBowl", "SpawnArea"]

def flatten(n, prefix, into, limit=4000):
    if len(into) >= limit: return
    if isinstance(n, dict):
        for k, v in n.items(): flatten(v, k if not prefix else prefix + "." + k, into, limit)
    elif isinstance(n, list):
        into[prefix + ".#"] = str(len(n))
        for i, v in enumerate(n): flatten(v, f"{prefix}[{i}]", into, limit)
    else:
        into[prefix] = "null" if n is None else json.dumps(n, ensure_ascii=False)

keys, fp = set(), {}
def add(k, node):
    d = {}; flatten(node, "", d); fp[k] = d
for x in items:
    keys.add("prefab:" + x["prefab"]); add("item:" + x["prefab"], x.get("shared") or {})
for x in pf:
    keys.add("prefab:" + x["prefab"])
    for c in FP_COMPS:
        if c in x and isinstance(x[c], dict): add(f"{c}:{x['prefab']}", x[c])
for x in rc:
    keys.add("recipe:" + x["recipe"]); add("recipe:" + x["recipe"], x.get("fields") or {})
for x in se:
    keys.add("se:" + x["id"]); add("se:" + x["id"], x.get("fields") or {})
for x in loc: keys.add("loc:" + x["prefabName"])
for x in world.get("randomEvents", []):
    if x.get("m_name"): keys.add("event:" + x["m_name"])
o = {"format": 2, "gameVersion": man["gameVersion"], "created": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S"),
     "source": f"converted from vanilla WikiDump {man['plugin']} dump of {man.get('dumpedUtc')}",
     "partial": True, "covered": COVERED, "keys": sorted(keys)}
# format 2 = JSON lines: header, then one {"k": key, "fp": {...}} per entity, so the plugin can stream it
import gzip
with gzip.open(out, "wt", encoding="utf-8", compresslevel=9) as f:
    f.write(json.dumps(o, ensure_ascii=False, separators=(",", ":")) + "\n")
    for k in sorted(fp): f.write(json.dumps({"k": k, "fp": fp[k]}, ensure_ascii=False, separators=(",", ":")) + "\n")
print(len(keys), "keys,", len(fp), "fingerprints")
