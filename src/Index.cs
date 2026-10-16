using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace WikiDumpMods
{
    /// <summary>Reverse indexes: how each item is obtained ("sources") and what it is used for ("uses").</summary>
    internal sealed class DataIndex
    {
        public readonly Dictionary<string, JArr> Sources = new Dictionary<string, JArr>();
        public readonly Dictionary<string, JArr> Uses = new Dictionary<string, JArr>();
        /// <summary>creature prefab -> where it spawns</summary>
        public readonly Dictionary<string, JArr> Spawns = new Dictionary<string, JArr>();
        public readonly Dictionary<string, JArr> Refs = new Dictionary<string, JArr>();
        private readonly Registry _r;
        private readonly Origin _o;
        private readonly Loc _loc;

        public DataIndex(Registry r, Origin o, Loc loc) { _r = r; _o = o; _loc = loc; }

        private static void Add(Dictionary<string, JArr> d, string key, JObj rec)
        {
            if (key == null) return;
            JArr a;
            if (!d.TryGetValue(key, out a)) d[key] = a = new JArr();
            a.Add(rec);
        }

        private string Mod(object o) { return _o.ModOf(o); }

        /// <summary>Short description of the object something comes from / goes into.</summary>
        public JObj Who(object o)
        {
            var go = Refl.GO(o);
            var res = new JObj();
            if (go) res["prefab"] = go.name;
            else if (o is UnityEngine.Object) res["object"] = ((UnityEngine.Object)o).name;
            var raw = DisplayToken(go);
            if (raw != null) res["name"] = _loc.Names(raw);
            var k = Origin.KeyOf(go ? (object)go : o);
            if (k != null) { var oi = _o.Get(k); if (oi.IsMod) res["mod"] = oi.Mod ?? "unknown"; }
            return res;
        }

        public static string DisplayToken(GameObject go)
        {
            if (!go) return null;
            foreach (var pair in new[] { new[] { "Character", "m_name" }, new[] { "Piece", "m_name" }, new[] { "CraftingStation", "m_name" },
                                         new[] { "Pickable", "m_overrideName" }, new[] { "Smelter", "m_name" }, new[] { "CookingStation", "m_name" },
                                         new[] { "Trader", "m_name" }, new[] { "OfferingBowl", "m_name" }, new[] { "Container", "m_name" },
                                         new[] { "MineRock", "m_name" }, new[] { "Location", "m_discoverLabel" } })
            {
                var s = Refl.S(Refl.Get(Refl.Comp(go, pair[0]), pair[1]));
                if (!string.IsNullOrEmpty(s)) return s;
            }
            var sh = Registry.Shared(go);
            var n = Refl.S(Refl.Get(sh, "m_name"));
            return string.IsNullOrEmpty(n) ? null : n;
        }

        private static string Name(object o) { return Refl.UName(o); }

        // ------------------------------------------------------------------ recipes & pieces

        public static JArr Requirements(object reqs, Func<object, JObj> who)
        {
            var a = new JArr();
            foreach (var q in Refl.Enum(reqs))
            {
                var it = Refl.Get(q, "m_resItem");
                if (Name(it) == null) continue;
                var o = who(it);
                o["amount"] = Refl.I(Refl.Get(q, "m_amount"));
                o["amountPerLevel"] = Refl.I(Refl.Get(q, "m_amountPerLevel"));
                o["recover"] = Refl.B(Refl.Get(q, "m_recover"));
                var extra = Refl.Get(q, "m_extraAmountOnlyOneIngredient");
                if (extra != null) o["extraAmountOnlyOneIngredient"] = Refl.I(extra);
                a.Add(o);
            }
            return a;
        }

        public JObj Station(object station)
        {
            if (Name(station) == null) return null;
            var o = Who(station);
            var raw = Refl.S(Refl.Get(station, "m_name"));
            if (raw != null) o["name"] = _loc.Names(raw);
            return o;
        }

        public void BuildRecipes()
        {
            foreach (var rc in _r.Recipes)
            {
                var item = Refl.Get(rc, "m_item");
                var result = Name(item);
                var rec = new JObj();
                rec["type"] = "craft";
                rec["recipe"] = ((UnityEngine.Object)rc).name;
                rec["enabled"] = Refl.B(Refl.Get(rc, "m_enabled"));
                rec["amount"] = Refl.I(Refl.Get(rc, "m_amount"), 1);
                rec.Opt("station", Station(Refl.Get(rc, "m_craftingStation")));
                rec.Opt("repairStation", Station(Refl.Get(rc, "m_repairStation")));
                rec["minStationLevel"] = Refl.I(Refl.Get(rc, "m_minStationLevel"), 1);
                rec["requireOnlyOneIngredient"] = Refl.B(Refl.Get(rc, "m_requireOnlyOneIngredient"));
                var qm = Refl.Get(rc, "m_qualityResultAmountMultiplier");
                if (qm != null) rec["qualityResultAmountMultiplier"] = Refl.F(qm);
                rec["requirements"] = Requirements(Refl.Get(rc, "m_resources"), Who);
                var key = Origin.KeyOf(rc);
                var oi = _o.Get(key);
                if (oi.IsMod) rec["mod"] = oi.Mod ?? "unknown";
                if (result != null) Add(Sources, result, rec);
                foreach (var q in Refl.Enum(Refl.Get(rc, "m_resources")))
                {
                    var res = Name(Refl.Get(q, "m_resItem"));
                    if (res == null) continue;
                    var u = new JObj { { "type", "ingredient" }, { "recipe", rec["recipe"] } };
                    u["result"] = item == null ? null : Who(item);
                    u["amount"] = Refl.I(Refl.Get(q, "m_amount"));
                    u["amountPerLevel"] = Refl.I(Refl.Get(q, "m_amountPerLevel"));
                    u.Opt("station", rec["station"]);
                    u.Opt("mod", rec["mod"]);
                    Add(Uses, res, u);
                }
            }

            foreach (var go in _r.Prefabs.Values)
            {
                var piece = Refl.Comp(go, "Piece");
                if (!piece) continue;
                foreach (var q in Refl.Enum(Refl.Get(piece, "m_resources")))
                {
                    var res = Name(Refl.Get(q, "m_resItem"));
                    if (res == null) continue;
                    var u = new JObj { { "type", "buildMaterial" }, { "piece", Who(go) } };
                    u["amount"] = Refl.I(Refl.Get(q, "m_amount"));
                    u["recover"] = Refl.B(Refl.Get(q, "m_recover"));
                    u.Opt("station", Station(Refl.Get(piece, "m_craftingStation")));
                    Add(Uses, res, u);
                }
            }
        }

        // ------------------------------------------------------------------ drops, stations, traders ...

        /// <summary>Root components of every networked prefab.</summary>
        public void BuildPrefabs()
        {
            foreach (var go in _r.Prefabs.Values) Scan(go, null, false, 1f);
        }

        /// <summary>Location / dungeon-room contents: children included, with RandomSpawn odds.</summary>
        public JArr ScanContainer(GameObject root, JObj where)
        {
            var found = new JArr();
            Scan(root, where, true, 1f, found);
            return found;
        }

        private static readonly string[] Interesting =
        {
            "CharacterDrop", "Container", "Pickable", "PickableItem", "DropOnDestroyed", "MineRock", "MineRock5", "TreeBase", "TreeLog",
            "LootSpawner", "Destructible", "Beehive", "SapCollector", "Smelter", "CookingStation", "Fermenter", "Incinerator", "Trader",
            "Fireplace", "ShieldGenerator", "Turret", "MonsterAI", "OfferingBowl", "ItemStand", "Plant", "Fish", "SpawnArea",
            "CreatureSpawner", "Humanoid", "Procreation", "Growup"
        };

        private void Scan(GameObject go, JObj where, bool children, float chance, JArr found = null)
        {
            if (!go) return;
            foreach (var tn in Interesting)
            {
                foreach (var c in Refl.Comps(go, tn, children))
                {
                    if (!c) continue;
                    var owner = c.gameObject;
                    float odds = children ? RandomSpawnOdds(c, go) : 1f;
                    var ctx = new JObj();
                    ctx["from"] = Who(where == null ? (object)go : owner);
                    if (where != null) { ctx["in"] = where; ctx["object"] = owner.name; if (odds < 1f) ctx["presenceChance"] = odds; }
                    ctx["component"] = tn;
                    try { Handle(tn, c, ctx, found); }
                    catch (Exception e) { ctx["error"] = e.GetType().Name; }
                }
            }
        }

        private static float RandomSpawnOdds(Component c, GameObject root)
        {
            float p = 1f;
            var tRS = Refl.T("RandomSpawn");
            if (tRS == null) return 1f;
            for (var t = c.transform; t != null; t = t.parent)
            {
                var rs = t.gameObject.GetComponent(tRS);
                if (rs) p *= Mathf01(Refl.F(Refl.Get(rs, "m_chance"), 100f) / 100f);
                if (ReferenceEquals(t.gameObject, root)) break;
            }
            return p;
        }

        private static float Mathf01(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        private JObj Clone(JObj ctx, string type)
        {
            var o = new JObj { { "type", type } };
            foreach (var kv in ctx) o[kv.Key] = kv.Value;
            return o;
        }

        private void Src(string item, JObj rec, JArr found) { if (item == null) return; Add(Sources, item, rec); if (found != null) { var f = new JObj { { "item", item } }; foreach (var kv in rec) f[kv.Key] = kv.Value; found.Add(f); } }
        private void Use(string item, JObj rec) { Add(Uses, item, rec); }

        private void Handle(string tn, Component c, JObj ctx, JArr found)
        {
            // generic: any DropTable field on the component
            foreach (var f in c.GetType().GetFields(Refl.Inst))
            {
                if (f.FieldType.Name != "DropTable") continue;
                var table = f.GetValue(c);
                if (table == null) continue;
                foreach (var e in DropOdds(table))
                {
                    var rec = Clone(ctx, "dropTable");
                    rec["field"] = f.Name;
                    foreach (var kv in e.Value) rec[kv.Key] = kv.Value;
                    Src(e.Key, rec, found);
                }
            }

            switch (tn)
            {
                case "CharacterDrop":
                    foreach (var d in Refl.Enum(Refl.Get(c, "m_drops")))
                    {
                        var it = Name(Refl.Get(d, "m_prefab"));
                        if (it == null) continue;
                        var rec = Clone(ctx, "creatureDrop");
                        rec["chance"] = Refl.F(Refl.Get(d, "m_chance"));
                        rec["min"] = Refl.I(Refl.Get(d, "m_amountMin"));
                        rec["max"] = Refl.I(Refl.Get(d, "m_amountMax"));
                        rec["onePerPlayer"] = Refl.B(Refl.Get(d, "m_onePerPlayer"));
                        rec["levelMultiplier"] = Refl.B(Refl.Get(d, "m_levelMultiplier"));
                        rec["dontScale"] = Refl.B(Refl.Get(d, "m_dontScale"));
                        Src(it, rec, found);
                    }
                    break;
                case "Pickable":
                {
                    var it = Name(Refl.Get(c, "m_itemPrefab"));
                    var rec = Clone(ctx, "pickable");
                    rec["amount"] = Refl.I(Refl.Get(c, "m_amount"), 1);
                    var rt = Refl.Get(c, "m_respawnTimeMinutes");
                    if (rt != null) rec["respawnMinutes"] = Refl.F(rt);
                    Src(it, rec, found);
                    break;
                }
                case "PickableItem":
                {
                    var single = Name(Refl.Get(c, "m_itemPrefab"));
                    if (single != null) { var rec = Clone(ctx, "pickableItem"); rec["stack"] = Refl.I(Refl.Get(c, "m_stack"), 1); Src(single, rec, found); }
                    var rnd = Refl.Enum(Refl.Get(c, "m_randomItemPrefabs")).ToList();
                    foreach (var ri in rnd)
                    {
                        var it = Name(Refl.Get(ri, "m_itemPrefab"));
                        if (it == null) continue;
                        var rec = Clone(ctx, "pickableItemRandom");
                        rec["stackMin"] = Refl.I(Refl.Get(ri, "m_stackMin"), 1);
                        rec["stackMax"] = Refl.I(Refl.Get(ri, "m_stackMax"), 1);
                        rec["chance"] = 1.0 / rnd.Count;
                        Src(it, rec, found);
                    }
                    break;
                }
                case "Beehive":
                {
                    var rec = Clone(ctx, "beehive");
                    rec["maxHoney"] = Refl.I(Refl.Get(c, "m_maxHoney"));
                    rec["secPerUnit"] = Refl.F(Refl.Get(c, "m_secPerUnit"));
                    Src(Name(Refl.Get(c, "m_honeyItem")), rec, found);
                    break;
                }
                case "SapCollector":
                {
                    var rec = Clone(ctx, "sapCollector");
                    rec["secPerUnit"] = Refl.F(Refl.Get(c, "m_secPerUnit"));
                    rec["maxLevel"] = Refl.I(Refl.Get(c, "m_maxLevel"));
                    Src(Name(Refl.Get(c, "m_spawnItem")), rec, found);
                    break;
                }
                case "Smelter":
                case "CookingStation":
                case "Fermenter":
                {
                    foreach (var cv in Refl.Enum(Refl.Get(c, "m_conversion")))
                    {
                        var from = Refl.Get(cv, "m_from"); var to = Refl.Get(cv, "m_to");
                        if (Name(from) == null && Name(to) == null) continue;
                        var rec = Clone(ctx, "conversion");
                        rec["station"] = rec["from"]; rec.Opt("stationName", _loc.Names(Refl.S(Refl.Get(c, "m_name"))));
                        rec["input"] = Who(from);
                        rec["output"] = Who(to);
                        foreach (var extra in new[] { "m_cookTime", "m_producedItems" })
                        {
                            var v = Refl.Get(cv, extra);
                            if (v != null) rec[extra.Substring(2)] = Refl.Num(v);
                        }
                        foreach (var extra in new[] { "m_secPerProduct", "m_fuelPerProduct", "m_fermentationDuration" })
                        {
                            var v = Refl.Get(c, extra);
                            if (v != null) rec[extra.Substring(2)] = Refl.Num(v);
                        }
                        rec.Remove("from");
                        Src(Name(to), rec, found);
                        var u = Clone(rec, "conversionInput"); Use(Name(from), u);
                    }
                    var fuel = Refl.Get(c, "m_fuelItem");
                    if (Name(fuel) != null) Use(Name(fuel), Clone(ctx, "fuel"));
                    var over = Refl.Get(c, "m_overCookedItem");
                    if (Name(over) != null) { var rec = Clone(ctx, "overcooked"); Src(Name(over), rec, found); }
                    break;
                }
                case "Incinerator":
                {
                    foreach (var cv in Refl.Enum(Refl.Get(c, "m_conversions")))
                    {
                        var res = Refl.Get(cv, "m_result");
                        var rec = Clone(ctx, "incinerator");
                        rec["requirements"] = new JArr();
                        foreach (var q in Refl.Enum(Refl.Get(cv, "m_requirements")))
                        {
                            var w = Who(Refl.Get(q, "m_resItem")); w["amount"] = Refl.I(Refl.Get(q, "m_amount"));
                            ((JArr)rec["requirements"]).Add(w);
                            Use(Name(Refl.Get(q, "m_resItem")), Clone(ctx, "incineratorInput"));
                        }
                        rec["resultAmount"] = Refl.I(Refl.Get(cv, "m_resultAmount"), 1);
                        rec["requireOnlyOneIngredient"] = Refl.B(Refl.Get(cv, "m_requireOnlyOneIngredient"));
                        rec["priority"] = Refl.I(Refl.Get(cv, "m_priority"));
                        Src(Name(res), rec, found);
                    }
                    break;
                }
                case "Trader":
                    foreach (var ti in Refl.Enum(Refl.Get(c, "m_items")))
                    {
                        var rec = Clone(ctx, "trader");
                        rec["trader"] = rec["from"]; rec.Remove("from");
                        rec.Opt("traderName", _loc.Names(Refl.S(Refl.Get(c, "m_name"))));
                        rec["price"] = Refl.I(Refl.Get(ti, "m_price"));
                        rec["stack"] = Refl.I(Refl.Get(ti, "m_stack"), 1);
                        var key = Refl.S(Refl.Get(ti, "m_requiredGlobalKey"));
                        if (!string.IsNullOrEmpty(key)) rec["requiredGlobalKey"] = key;
                        Src(Name(Refl.Get(ti, "m_prefab")), rec, found);
                    }
                    break;
                case "Fireplace":
                    if (Name(Refl.Get(c, "m_fuelItem")) != null) Use(Name(Refl.Get(c, "m_fuelItem")), Clone(ctx, "fuel"));
                    break;
                case "ShieldGenerator":
                    foreach (var f in Refl.Enum(Refl.Get(c, "m_fuelItems"))) Use(Name(f), Clone(ctx, "fuel"));
                    break;
                case "Turret":
                    foreach (var a in Refl.Enum(Refl.Get(c, "m_allowedAmmo"))) Use(Name(Refl.Get(a, "m_ammo")), Clone(ctx, "turretAmmo"));
                    break;
                case "MonsterAI":
                    foreach (var f in Refl.Enum(Refl.Get(c, "m_consumeItems")))
                    {
                        var rec = Clone(ctx, "creatureFood");
                        rec["creature"] = rec["from"]; rec.Remove("from");
                        rec["tameable"] = Refl.Comp(c.gameObject, "Tameable") != null;
                        Use(Name(f), rec);
                    }
                    break;
                case "OfferingBowl":
                {
                    var it = Name(Refl.Get(c, "m_bossItem"));
                    if (it != null)
                    {
                        var rec = Clone(ctx, "offering");
                        rec["altar"] = rec["from"]; rec.Remove("from");
                        rec["amount"] = Refl.I(Refl.Get(c, "m_bossItems"), 1);
                        rec.Opt("summons", Name(Refl.Get(c, "m_bossPrefab")) == null ? null : Who(Refl.Get(c, "m_bossPrefab")));
                        Use(it, rec);
                    }
                    break;
                }
                case "ItemStand":
                    foreach (var f in Refl.Enum(Refl.Get(c, "m_supportedItems"))) Use(Name(f), Clone(ctx, "itemStand"));
                    break;
                case "Fish":
                    foreach (var b in Refl.Enum(Refl.Get(c, "m_baits")))
                    {
                        var bait = Refl.Get(b, "m_bait");
                        var rec = Clone(ctx, "fishing");
                        rec["bait"] = Who(bait);
                        rec["chance"] = Refl.F(Refl.Get(b, "m_chance"));
                        Src(c.gameObject.name, rec, found);
                        var u = Clone(ctx, "bait"); u["fish"] = u["from"]; u.Remove("from"); u["chance"] = rec["chance"];
                        Use(Name(bait), u);
                    }
                    break;
                case "Plant":
                    foreach (var g in Refl.Enum(Refl.Get(c, "m_grownPrefabs")))
                    {
                        var rec = Clone(ctx, "growsInto");
                        rec["grows"] = Who(g);
                        rec["growTime"] = Refl.F(Refl.Get(c, "m_growTime"));
                        rec["growTimeMax"] = Refl.F(Refl.Get(c, "m_growTimeMax"));
                        rec["biome"] = Refl.S(Refl.Get(c, "m_biome"));
                        Add(Spawns, Name(g), rec);
                    }
                    break;
                case "SpawnArea":
                    foreach (var sd in Refl.Enum(Refl.Get(c, "m_prefabs")))
                    {
                        var rec = Clone(ctx, "spawnArea");
                        rec["weight"] = Refl.F(Refl.Get(sd, "m_weight"));
                        rec["minLevel"] = Refl.I(Refl.Get(sd, "m_minLevel"), 1);
                        rec["maxLevel"] = Refl.I(Refl.Get(sd, "m_maxLevel"), 1);
                        rec["maxNear"] = Refl.I(Refl.Get(c, "m_maxNear"));
                        rec["intervalSec"] = Refl.F(Refl.Get(c, "m_spawnIntervalSec"));
                        Add(Spawns, Name(Refl.Get(sd, "m_prefab")), rec);
                    }
                    break;
                case "CreatureSpawner":
                {
                    var rec = Clone(ctx, "creatureSpawner");
                    rec["minLevel"] = Refl.I(Refl.Get(c, "m_minLevel"), 1);
                    rec["maxLevel"] = Refl.I(Refl.Get(c, "m_maxLevel"), 1);
                    rec["levelupChance"] = Refl.Num(Refl.Get(c, "m_levelupChance"));
                    rec["respawnMinutes"] = Refl.Num(Refl.Get(c, "m_respawnTimeMinuts"));
                    rec["spawnAtDay"] = Refl.B(Refl.Get(c, "m_spawnAtDay"));
                    rec["spawnAtNight"] = Refl.B(Refl.Get(c, "m_spawnAtNight"));
                    Add(Spawns, Name(Refl.Get(c, "m_creaturePrefab")), rec);
                    if (found != null) { var f = Clone(rec, "creatureSpawner"); f["creature"] = Name(Refl.Get(c, "m_creaturePrefab")); found.Add(f); }
                    break;
                }
                case "Procreation":
                {
                    var off = Refl.Get(c, "m_offspring");
                    if (Name(off) != null) { var rec = Clone(ctx, "offspring"); Add(Spawns, Name(off), rec); }
                    break;
                }
                case "Growup":
                {
                    var g = Refl.Get(c, "m_grownPrefab");
                    if (Name(g) != null) { var rec = Clone(ctx, "growsUpFrom"); rec["growTime"] = Refl.Num(Refl.Get(c, "m_growTime")); Add(Spawns, Name(g), rec); }
                    break;
                }
            }
        }

        /// <summary>
        /// Odds per item of one DropTable.GetDropList() call, modelled on the game's code: the whole table fires with
        /// m_dropChance, rolls = Random.Range(m_dropMin, m_dropMax + 1), each roll picks by weight, and with m_oneOfEach
        /// a picked entry is removed for the remaining rolls. "chanceAny" = P(item appears at least once).
        /// Exact for oneOfEach tables up to 14 entries, otherwise rolls are treated as independent.
        /// </summary>
        public static List<KeyValuePair<string, JObj>> DropOdds(object table)
        {
            var res = new List<KeyValuePair<string, JObj>>();
            var drops = Refl.Enum(Refl.Get(table, "m_drops")).ToList();
            var names = new List<string>(); var w = new List<double>(); var raw = new List<object>();
            foreach (var d in drops)
            {
                var n = Refl.UName(Refl.Get(d, "m_item"));
                if (n == null) continue;
                names.Add(n); w.Add(Math.Max(0, Refl.F(Refl.Get(d, "m_weight"), 1f))); raw.Add(d);
            }
            if (names.Count == 0) return res;
            double chance = Math.Min(1, Math.Max(0, Refl.F(Refl.Get(table, "m_dropChance"), 1f)));
            int min = Refl.I(Refl.Get(table, "m_dropMin"), 1), max = Refl.I(Refl.Get(table, "m_dropMax"), 1);
            if (max < min) max = min;
            bool oneOfEach = Refl.B(Refl.Get(table, "m_oneOfEach"));
            double total = w.Sum();
            var pAny = new double[names.Count];
            for (int n = min; n <= max; n++)
            {
                var p = oneOfEach && names.Count <= 14 ? ExactOneOfEach(w, n) : Independent(w, n);
                for (int i = 0; i < names.Count; i++) pAny[i] += p[i] / (max - min + 1);
            }
            for (int i = 0; i < names.Count; i++)
            {
                var o = new JObj();
                o["weight"] = w[i];
                o["totalWeight"] = total;
                o["rollsMin"] = min; o["rollsMax"] = max;
                o["tableChance"] = chance;
                o["oneOfEach"] = oneOfEach;
                o["stackMin"] = Refl.I(Refl.Get(raw[i], "m_stackMin"), 1);
                o["stackMax"] = Refl.I(Refl.Get(raw[i], "m_stackMax"), 1);
                if (Refl.Has(raw[i], "m_dontScale")) o["dontScale"] = Refl.B(Refl.Get(raw[i], "m_dontScale"));
                o["chanceAny"] = Math.Round(chance * Math.Min(1, pAny[i]), 6);
                res.Add(new KeyValuePair<string, JObj>(names[i], o));
            }
            return res;
        }

        private static double[] Independent(List<double> w, int n)
        {
            var r = new double[w.Count];
            double tot = w.Sum();
            for (int i = 0; i < w.Count; i++)
            {
                double p = tot <= 0 ? (i == 0 ? 1 : 0) : w[i] / tot;
                r[i] = n <= 0 ? 0 : 1 - Math.Pow(1 - p, n);
            }
            return r;
        }

        /// <summary>Exact P(picked within n rolls) without replacement, via a DP over the set of already-picked entries.</summary>
        public static double[] ExactOneOfEach(List<double> w, int n)
        {
            int k = w.Count;
            var cur = new Dictionary<int, double> { { 0, 1.0 } };
            for (int roll = 0; roll < n; roll++)
            {
                var next = new Dictionary<int, double>();
                foreach (var kv in cur)
                {
                    int mask = kv.Key; double pm = kv.Value;
                    double rem = 0; int first = -1;
                    for (int i = 0; i < k; i++) if ((mask & (1 << i)) == 0) { rem += w[i]; if (first < 0) first = i; }
                    if (first < 0) { Acc(next, mask, pm); continue; }
                    if (rem <= 0) { Acc(next, mask | (1 << first), pm); continue; }
                    for (int i = 0; i < k; i++)
                        if ((mask & (1 << i)) == 0 && w[i] > 0) Acc(next, mask | (1 << i), pm * w[i] / rem);
                }
                cur = next;
            }
            var r = new double[k];
            foreach (var kv in cur) for (int i = 0; i < k; i++) if ((kv.Key & (1 << i)) != 0) r[i] += kv.Value;
            return r;
        }

        private static void Acc(Dictionary<int, double> d, int k, double v) { double x; d.TryGetValue(k, out x); d[k] = x + v; }

        // ------------------------------------------------------------------ world spawns

        public void BuildSpawns()
        {
            foreach (var sl in _r.SpawnLists)
            {
                var listName = Refl.UName(sl);
                foreach (var sd in Refl.Enum(Refl.Get(sl, "m_spawners")))
                {
                    var p = Refl.Get(sd, "m_prefab");
                    if (Name(p) == null) continue;
                    var rec = (JObj)Ser.Node(sd, 2);
                    rec.Remove("m_prefab");
                    var o = new JObj { { "type", "worldSpawn" }, { "list", listName } };
                    foreach (var kv in rec) o[kv.Key] = kv.Value;
                    Add(Spawns, Name(p), o);
                }
            }
            foreach (var ev in _r.Events)
            {
                foreach (var sd in Refl.Enum(Refl.Get(ev, "m_spawn")))
                {
                    var p = Refl.Get(sd, "m_prefab");
                    if (Name(p) == null) continue;
                    var o = new JObj { { "type", "event" }, { "event", Refl.S(Refl.Get(ev, "m_name")) } };
                    o["biome"] = Refl.S(Refl.Get(ev, "m_biome"));
                    var rec = (JObj)Ser.Node(sd, 2);
                    rec.Remove("m_prefab");
                    o["spawn"] = rec;
                    Add(Spawns, Name(p), o);
                }
            }
            // ammo: weapons whose m_ammoType matches an ammo item's
            var byAmmo = new Dictionary<string, List<GameObject>>();
            foreach (var it in _r.Items.Values)
            {
                var sh = Registry.Shared(it);
                var at = Refl.S(Refl.Get(sh, "m_ammoType"));
                if (string.IsNullOrEmpty(at)) continue;
                var type = Refl.S(Refl.Get(sh, "m_itemType"));
                if (type == "Ammo" || type == "AmmoNonEquipable") { List<GameObject> l; if (!byAmmo.TryGetValue(at, out l)) byAmmo[at] = l = new List<GameObject>(); l.Add(it); }
            }
            foreach (var it in _r.Items.Values)
            {
                var sh = Registry.Shared(it);
                var at = Refl.S(Refl.Get(sh, "m_ammoType"));
                var type = Refl.S(Refl.Get(sh, "m_itemType"));
                List<GameObject> ammo;
                if (string.IsNullOrEmpty(at) || type == "Ammo" || type == "AmmoNonEquipable" || !byAmmo.TryGetValue(at, out ammo)) continue;
                foreach (var a in ammo) Use(a.name, new JObj { { "type", "ammoFor" }, { "weapon", Who(it) }, { "ammoType", at } });
            }
        }

        // ------------------------------------------------------------------ generic references to modded things

        private static bool UnityType(Type t)
        {
            var n = t.Assembly.GetName().Name;
            return n.StartsWith("UnityEngine") || n.StartsWith("Unity.") || n == "UnityEngine";
        }

        /// <summary>
        /// Walks every serialised field (depth 4) of every non-Unity component on every prefab and records each
        /// reference to modded content. Catches mod systems this plugin has no specific knowledge of.
        /// </summary>
        public void ScanReferences(GameObject go)
        {
            if (!go) return;
            Component[] comps;
            try { comps = go.GetComponentsInChildren(typeof(MonoBehaviour), true); } catch { return; }
            foreach (var c in comps)
            {
                if (!c || UnityType(c.GetType())) continue;
                Walk(c, c.GetType().Name, 0, go, c, new HashSet<object>(RefEq.I));
            }
        }

        public void ScanReferencesOf(UnityEngine.Object so)
        {
            if (!so) return;
            Walk(so, so.GetType().Name, 0, null, so, new HashSet<object>(RefEq.I));
        }

        private void Walk(object v, string path, int depth, GameObject root, UnityEngine.Object holder, HashSet<object> seen)
        {
            if (v == null || depth > 4) return;
            var t = v.GetType();
            if (t.IsPrimitive || t.IsEnum || v is string || v is Delegate) return;
            var u = v as UnityEngine.Object;
            if (!ReferenceEquals(u, null) && depth > 0)
            {
                if (!u) return;
                var k = Origin.KeyOf(u);
                if (k == null || k.StartsWith("asset:")) return;
                if (root && k == "prefab:" + root.name) return;
                if (!_o.IsModKey(k)) return;
                var rec = new JObj();
                rec["from"] = root ? Who(root) : new JObj { { "object", holder.name }, { "kind", holder.GetType().Name } };
                if (root && holder is Component && ((Component)holder).gameObject != root) rec["child"] = ((Component)holder).gameObject.name;
                rec["path"] = path;
                Add(Refs, k, rec);
                return;
            }
            if (!t.IsValueType) { if (seen.Contains(v)) return; seen.Add(v); }
            if (v is System.Collections.IEnumerable)
            {
                int i = 0;
                foreach (var x in Refl.Enum(v)) { if (i > 3000) break; Walk(x, path + "[" + i + "]", depth + 1, root, holder, seen); i++; }
                return;
            }
            if (depth > 0 && UnityType(t)) return;
            for (var bt = t; bt != null && bt != typeof(object) && !UnityType(bt); bt = bt.BaseType)
            {
                foreach (var f in bt.GetFields(Refl.Inst | BindingFlags.DeclaredOnly))
                {
                    if (f.Name.StartsWith("<") || f.IsStatic) continue;
                    if (!f.IsPublic && f.GetCustomAttributes(typeof(SerializeField), true).Length == 0) continue;
                    var ft = f.FieldType;
                    if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) continue;
                    object fv; try { fv = f.GetValue(v); } catch { continue; }
                    Walk(fv, path + "." + f.Name, depth + 1, root, holder, seen);
                }
            }
        }
    }
}
