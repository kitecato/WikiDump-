using System; using System.Collections.Generic; using System.IO; using System.Linq; using UnityEngine; using WikiDumpMods;
class T {
  static Dictionary<string,GameObject> P = new Dictionary<string,GameObject>();
  static GameObject Item(string name, string tok, ItemDrop.ItemType type, bool reg = true) {
    var g = new GameObject(name); var d = g.Add<ItemDrop>(); d.m_itemData.m_shared.m_name = "$" + tok; d.m_itemData.m_shared.m_itemType = type;
    var sp = new Sprite(); sp.name = name + "_icon"; d.m_itemData.m_shared.m_icons = new[] { sp };
    if (reg) { ObjectDB.instance.m_items.Add(g); ZNetScene.instance.m_prefabs.Add(g); } P[name] = g; return g; }
  static ItemDrop ID(string n) { return P[n].GetComponent(typeof(ItemDrop)) as ItemDrop; }
  static Piece.Requirement Req(string n, int a, int per = 1) { return new Piece.Requirement { m_resItem = ID(n), m_amount = a, m_amountPerLevel = per }; }
  static void Main(string[] args) {
    string outDir = args[0]; string modFolder = args[1];
    var core = new GameObject("_core"); ObjectDB.instance = core.Add<ObjectDB>(); ZNetScene.instance = core.Add<ZNetScene>(); ZoneSystem.instance = core.Add<ZoneSystem>(); RandEventSystem.instance = core.Add<RandEventSystem>(); MessageHud.instance = core.Add<MessageHud>();
    Localization.All["English"] = new Dictionary<string,string>{{"item_wood","Wood"},{"item_iron","Iron"},{"item_sword_iron","Iron sword"},{"item_coins","Coins"},{"piece_forge","Forge"},{"piece_chest","Chest"},{"enemy_greydwarf","Greydwarf"},{"enemy_boar","Boar"},{"item_swordfrost","Frost sword"},{"item_frostshard","Frost shard"},{"se_frostaura","Frost aura"},{"enemy_draugrfrost","Frost draugr"},{"npc_haldor","Haldor"},{"item_weight","Weight"},{"piece_smelter","Smelter"}};
    Localization.All["Ukrainian"] = new Dictionary<string,string>{{"item_wood","Деревина"},{"item_iron","Залізо"},{"item_sword_iron","Залізний меч"},{"item_coins","Монети"},{"piece_forge","Кузня"},{"piece_chest","Скриня"},{"enemy_greydwarf","Сірий гном"},{"enemy_boar","Вепр"},{"item_swordfrost","Морозний меч"},{"se_frostaura","Аура морозу"},{"npc_haldor","Гальдор"},{"item_weight","Вага"},{"piece_smelter","Плавильня"}};
    Localization.ModWords["English"] = new Dictionary<string,string>{{"item_swordfrost","Frost sword"}};
    Localization.ModWords["Ukrainian"] = new Dictionary<string,string>{{"item_swordfrost","Морозний меч"}};
    Localization.All["English"].Remove("item_frostshard"); Localization.All["English"].Remove("item_swordfrost"); Localization.All["Ukrainian"].Remove("item_swordfrost");
    Localization.instance.SetupLanguage("Ukrainian");
    // ---------- vanilla
    Item("Wood","item_wood",ItemDrop.ItemType.Material); Item("Iron","item_iron",ItemDrop.ItemType.Material); Item("Coins","item_coins",ItemDrop.ItemType.Material);
    var sw = Item("SwordIron","item_sword_iron",ItemDrop.ItemType.OneHandedWeapon); var sws = ID("SwordIron").m_itemData.m_shared; sws.m_maxQuality = 4; sws.m_damages.m_slash = 55; sws.m_damagesPerLevel.m_slash = 6;
    var forge = new GameObject("forge"); var fp = forge.Add<Piece>(); fp.m_name = "$piece_forge"; fp.m_resources = new[]{ Req("Wood",4) }; var fcs = forge.Add<CraftingStation>(); fcs.m_name = "$piece_forge"; ZNetScene.instance.m_prefabs.Add(forge); P["forge"] = forge;
    var chest = new GameObject("piece_chest"); var cp = chest.Add<Piece>(); cp.m_name = "$piece_chest"; cp.m_resources = new[]{ Req("Wood",10) }; chest.Add<Container>(); ZNetScene.instance.m_prefabs.Add(chest);
    var hammer = Item("Hammer","item_hammer",ItemDrop.ItemType.Tool); var tbl = new GameObject("_HammerPieceTable").Add<PieceTable>(); tbl.m_pieces.Add(forge); tbl.m_pieces.Add(chest); ID("Hammer").m_itemData.m_shared.m_buildPieces = tbl;
    var rSword = ScriptableObject.CreateInstanceTest<Recipe>(); rSword.name = "Recipe_SwordIron"; rSword.m_item = ID("SwordIron"); rSword.m_craftingStation = fcs; rSword.m_minStationLevel = 2; rSword.m_resources = new[]{ Req("Wood",2,1), Req("Iron",20,10) }; ObjectDB.instance.m_recipes.Add(rSword);
    var grey = new GameObject("Greydwarf"); var gh = grey.Add<Humanoid>(); gh.m_name = "$enemy_greydwarf"; var gd = grey.Add<CharacterDrop>(); gd.m_drops.Add(new CharacterDrop.Drop{ m_prefab = P["Wood"], m_chance = 1 }); ZNetScene.instance.m_prefabs.Add(grey);
    var boar = new GameObject("Boar"); boar.Add<Character>().m_name = "$enemy_boar"; var bai = boar.Add<MonsterAI>(); boar.Add<Tameable>(); ZNetScene.instance.m_prefabs.Add(boar);
    var haldor = new GameObject("Haldor"); var tr = haldor.Add<Trader>(); tr.m_name = "$npc_haldor"; ZNetScene.instance.m_prefabs.Add(haldor);
    var smelter = new GameObject("smelter"); var sm = smelter.Add<Smelter>(); sm.m_name = "$piece_smelter"; ZNetScene.instance.m_prefabs.Add(smelter);
    var player = new GameObject("Player"); player.Add<Player>(); var sk = player.Add<Skills>(); sk.m_skills.Add(new Skills.SkillDef{ m_skill = Skills.SkillType.Swords }); ZNetScene.instance.m_prefabs.Add(player);
    // vanilla location with a chest inside (child under a 50% RandomSpawn)
    var crypt = new GameObject("Crypt2"); var rsHolder = crypt.Child("loot_group"); rsHolder.Add<RandomSpawn>().m_chance = 50; var lootChest = rsHolder.Child("TreasureChest_crypt"); var lc = lootChest.Add<Container>();
    lc.m_defaultItems.m_dropMin = 2; lc.m_defaultItems.m_dropMax = 3; lc.m_defaultItems.m_oneOfEach = true; lc.m_defaultItems.m_drops.Add(new DropTable.DropData{ m_item = P["Coins"], m_weight = 3, m_stackMin = 5, m_stackMax = 10 });
    ZoneSystem.instance.m_locations.Add(new ZoneSystem.ZoneLocation{ m_prefabName = "Crypt2", m_prefab = new SoftRef{ asset = crypt }, m_biome = Heightmap.Biome.BlackForest });
    var sl = new GameObject("_ZoneCtrl").Add<SpawnSystemList>();
    var self = new ModEntry{ Guid = "ua.valheimwiki.wikidumpmods", Name = "WikiDumpMods", Version = "1.0.0" };
    // ---------- 1) baseline run: only this plugin installed
    if (args.Length < 3) Run(outDir, new List<ModEntry>{ self });
    // ---------- now "install" mods
    sws.m_damages.m_slash = 60; // a mod rebalanced a vanilla item
    var frost = Item("FrostShard","item_frostshard",ItemDrop.ItemType.Material); ItemManager.Item.registeredItems.Add(new ItemManager.Item{ Prefab = frost, vanillaRef = P["Iron"] });
    var swf = Item("SwordFrost","item_swordfrost",ItemDrop.ItemType.OneHandedWeapon); var swfs = ID("SwordFrost").m_itemData.m_shared; swfs.m_maxQuality = 3; swfs.m_damages.m_slash = 70; swfs.m_damages.m_frost = 20; swfs.m_damagesPerLevel.m_slash = 6; swfs.m_setName = "frostset"; swfs.m_setSize = 2;
    var se = ScriptableObject.CreateInstanceTest<SE_Stats>(); se.name = "SE_FrostAura"; se.m_name = "$se_frostaura"; se.m_tooltip = "$se_frostaura"; se.MakeRuntimeClone(); ObjectDB.instance.m_StatusEffects.Add(se); swfs.m_equipStatusEffect = se;
    var rFrost = ScriptableObject.CreateInstanceTest<Recipe>(); rFrost.name = "Recipe_SwordFrost"; rFrost.m_item = ID("SwordFrost"); rFrost.m_craftingStation = fcs; rFrost.m_minStationLevel = 3; rFrost.m_resources = new[]{ Req("Iron",10,5), Req("FrostShard",5,2) }; rFrost.MakeRuntimeClone(); ObjectDB.instance.m_recipes.Add(rFrost);
    var dr = new GameObject("Draugr_Frost"); dr.MakeRuntimeClone(); var dh = dr.Add<Humanoid>(); dh.m_name = "$enemy_draugrfrost"; dh.m_health = 300; dh.m_defaultItems = new[]{ P["SwordFrost"] }; var dd = dr.Add<CharacterDrop>(); dd.m_drops.Add(new CharacterDrop.Drop{ m_prefab = frost, m_chance = 0.5f, m_amountMin = 1, m_amountMax = 2 }); ZNetScene.instance.m_prefabs.Add(dr);
    gd.m_drops.Add(new CharacterDrop.Drop{ m_prefab = frost, m_chance = 0.1f });
    bai.m_consumeItems.Add(ID("FrostShard"));
    tr.m_items.Add(new Trader.TradeItem{ m_prefab = ID("SwordFrost"), m_price = 1500, m_requiredGlobalKey = "defeated_bonemass" });
    sm.m_conversion.Add(new Smelter.ItemConversion{ m_from = ID("FrostShard"), m_to = ID("Iron") });
    lc.m_defaultItems.m_drops.Add(new DropTable.DropData{ m_item = frost, m_weight = 1, m_stackMin = 1, m_stackMax = 2 });
    sl.m_spawners.Add(new SpawnSystem.SpawnData{ m_name = "frost draugr", m_prefab = dr, m_biome = Heightmap.Biome.Mountain, m_spawnAtDay = false });
    RandEventSystem.instance.m_events.Add(new RandomEvent{ m_name = "army_frost", m_biome = Heightmap.Biome.Mountain, m_spawn = new List<SpawnSystem.SpawnData>{ new SpawnSystem.SpawnData{ m_prefab = dr } } });
    sk.m_skills.Add(new Skills.SkillDef{ m_skill = (Skills.SkillType)999 });
    var ji = new Jotunn.Utils.ModInfo{ GUID = "test.modA", Name = "Mod A" }; ji.Items.Add(new Jotunn.Entities.CustomItem{ ItemPrefab = swf, Recipe = new Jotunn.Entities.CustomRecipe{ Recipe = rFrost } }); ji.ItemConversions.Add(new Jotunn.Entities.CustomItemConversion{ Smelter = smelter }); Jotunn.Utils.ModRegistry.Mods.Add(ji);
    ModB.ModBPlugin.vanillaTouched.Add(P["Wood"]);
    var bundle = new AssetBundle(); bundle.name = "modbbundle"; bundle.Assets = new[]{ "assets/modb/frostshard.prefab" }; AssetBundle.Loaded.Add(bundle);
    var fakeHammer = new GameObject("Hammer_inbundle"); fakeHammer.name = "Hammer"; bundle.Assets = new[]{ "assets/modb/frostshard.prefab", "assets/modb/hammer.prefab" }; bundle.Objects["assets/modb/frostshard.prefab"] = frost; bundle.Objects["assets/modb/hammer.prefab"] = fakeHammer;
    var bleed = ScriptableObject.CreateInstanceTest<SE_Warfare_Bleeding>(); bleed.name = "Warfare_Bleeding"; bleed.MakeRuntimeClone(); ObjectDB.instance.m_StatusEffects.Add(bleed);
    var vb = new AssetBundle(); vb.name = "vanilla_soft_assets"; vb.Assets = new[]{ "assets/wood.prefab" }; AssetBundle.Loaded.Add(vb);
    var modA = new ModEntry{ Guid = "test.modA", Name = "Mod A", Version = "1.2.0", Asm = typeof(Jotunn.Utils.ModRegistry).Assembly };
    var modB = new ModEntry{ Guid = "test.modB", Name = "Mod B", Version = "0.9.0", Asm = typeof(ModB.ModBPlugin).Assembly, Folder = modFolder, FolderName = "Author-ModB" };
    Run(outDir, new List<ModEntry>{ self, modA, modB });
    Console.WriteLine("HUD: " + string.Join(" | ", MessageHud.Shown.ToArray()));
    Console.WriteLine("lang after: " + Localization.instance.GetSelectedLanguage() + " soft loads/releases: " + SoftRef.loads + "/" + SoftRef.releases);
  }
  static void Run(string outDir, List<ModEntry> mods) {
    var opt = new DumpOptions{ ConfigDir = Path.GetDirectoryName(outDir.TrimEnd('/')) + "/cfg", OutDir = outDir, BaselinePath = Path.Combine(outDir, "vanilla_baseline.jsonl.gz"), PluginVersion = "1.0.0", SelfGuid = "ua.valheimwiki.wikidumpmods" };
    var d = new Dumper(opt, mods, s => Console.WriteLine("LOG " + s), s => MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, s));
    var it = d.Run(); while (it.MoveNext()) {}
  }
}
