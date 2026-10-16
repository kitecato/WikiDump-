// Mock of the parts of assembly_valheim the dumper reads. Field names/types mirror the real game.
using System; using System.Collections.Generic; using UnityEngine;
public class ObjectDB : MonoBehaviour { public static ObjectDB instance; public List<GameObject> m_items = new List<GameObject>(); public List<Recipe> m_recipes = new List<Recipe>(); public List<StatusEffect> m_StatusEffects = new List<StatusEffect>(); }
public class ZNetScene : MonoBehaviour { public static ZNetScene instance; public List<GameObject> m_prefabs = new List<GameObject>(); public Dictionary<int, GameObject> m_namedPrefabs = new Dictionary<int, GameObject>(); }
public class ZNetView : MonoBehaviour {}
public class ItemDrop : MonoBehaviour {
  public ItemData m_itemData = new ItemData();
  public class ItemData { public SharedData m_shared = new SharedData(); public int m_quality = 1;
    public static string GetTooltip(ItemData item, int qualityLevel, bool crafting, float worldLevel, int stackOverride = -1) { return item.m_shared.m_name + " $item_weight q" + qualityLevel + " dmg " + (item.m_shared.m_damages.m_slash + item.m_shared.m_damagesPerLevel.m_slash*(qualityLevel-1)); } }
  public enum ItemType { None, Material, OneHandedWeapon, Ammo, Bow, Tool, Consumable, Helmet, Chest }
  public class SharedData { public string m_name = ""; public string m_description = ""; public ItemType m_itemType; public Sprite[] m_icons = new Sprite[0]; public int m_maxStackSize = 1; public float m_weight = 1; public int m_maxQuality = 1; public float m_armor, m_armorPerLevel; public float m_maxDurability = 100, m_durabilityPerLevel = 50;
    public HitData.DamageTypes m_damages; public HitData.DamageTypes m_damagesPerLevel; public string m_ammoType = ""; public string m_setName = ""; public int m_setSize; public StatusEffect m_setStatusEffect, m_consumeStatusEffect, m_equipStatusEffect; public PieceTable m_buildPieces; public Attack m_attack = new Attack(); public float m_food; }
}
public class Attack { public string m_attackAnimation = "swing"; public float m_attackStamina = 10; public GameObject m_attackProjectile; }
public class HitData { public struct DamageTypes { public float m_damage, m_blunt, m_slash, m_pierce, m_chop, m_pickaxe, m_fire, m_frost, m_lightning, m_poison, m_spirit; } }
public class Recipe : ScriptableObject { public ItemDrop m_item; public int m_amount = 1; public bool m_enabled = true; public CraftingStation m_craftingStation, m_repairStation; public int m_minStationLevel = 1; public Piece.Requirement[] m_resources = new Piece.Requirement[0]; public bool m_requireOnlyOneIngredient; public float m_qualityResultAmountMultiplier = 1; }
public class Piece : MonoBehaviour { public string m_name = "", m_description = ""; public Sprite m_icon; public Requirement[] m_resources = new Requirement[0]; public CraftingStation m_craftingStation; public PieceCategory m_category; public int m_comfort; public string m_comfortGroup = "None";
  public enum PieceCategory { Misc, Crafting, Building, Furniture }
  [Serializable] public class Requirement { public ItemDrop m_resItem; public int m_amount = 1; public int m_amountPerLevel = 1; public bool m_recover = true; } }
public class PieceTable : MonoBehaviour { public List<GameObject> m_pieces = new List<GameObject>(); }
public class CraftingStation : MonoBehaviour { public string m_name = ""; public Sprite m_icon; public float m_rangeBuild = 10; }
public class StationExtension : MonoBehaviour { public CraftingStation m_craftingStation; }
public class Character : MonoBehaviour { public string m_name = ""; public float m_health = 10; public string m_faction = "ForestMonsters"; public bool m_boss; public string m_bossEvent = ""; public string m_defeatSetGlobalKey = ""; public List<string> m_damageModifiers = new List<string>(); }
public class Humanoid : Character { public GameObject[] m_defaultItems = new GameObject[0]; public GameObject[] m_randomWeapon = new GameObject[0]; }
public class CharacterDrop : MonoBehaviour { public List<Drop> m_drops = new List<Drop>(); [Serializable] public class Drop { public GameObject m_prefab; public int m_amountMin = 1, m_amountMax = 1; public float m_chance = 1; public bool m_onePerPlayer, m_levelMultiplier = true, m_dontScale; } }
[Serializable] public class DropTable { public List<DropData> m_drops = new List<DropData>(); public int m_dropMin = 1, m_dropMax = 1; public float m_dropChance = 1; public bool m_oneOfEach;
  [Serializable] public struct DropData { public GameObject m_item; public int m_stackMin, m_stackMax; public float m_weight; public bool m_dontScale; } }
public class Container : MonoBehaviour { public string m_name = "$piece_chest"; public int m_width = 4, m_height = 2; public DropTable m_defaultItems = new DropTable(); }
public class Pickable : MonoBehaviour { public GameObject m_itemPrefab; public int m_amount = 1; public float m_respawnTimeMinutes = 240; public DropTable m_extraDrops = new DropTable(); public string m_overrideName = ""; }
public class Smelter : MonoBehaviour { public string m_name = ""; public ItemDrop m_fuelItem; public float m_secPerProduct = 30; public List<ItemConversion> m_conversion = new List<ItemConversion>(); [Serializable] public class ItemConversion { public ItemDrop m_from, m_to; } }
public class Trader : MonoBehaviour { public string m_name = ""; public List<TradeItem> m_items = new List<TradeItem>(); [Serializable] public class TradeItem { public ItemDrop m_prefab; public int m_stack = 1, m_price = 10; public string m_requiredGlobalKey = ""; } }
public class MonsterAI : MonoBehaviour { public List<ItemDrop> m_consumeItems = new List<ItemDrop>(); public float m_viewRange = 30; }
public class Tameable : MonoBehaviour { public float m_tamingTime = 1800; }
public class RandomSpawn : MonoBehaviour { public float m_chance = 50; }
public class StatusEffect : ScriptableObject { public string m_name = "", m_tooltip = ""; public Sprite m_icon; public float m_ttl;
  public virtual string GetTooltipString() { return m_tooltip; } }
public class SE_Stats : StatusEffect { public float m_healthRegenMultiplier = 1; public float m_addMaxCarryWeight; }
public class Skills : MonoBehaviour { public List<SkillDef> m_skills = new List<SkillDef>(); public enum SkillType { None, Swords, Knives } public class SkillDef { public SkillType m_skill; public Sprite m_icon; } }
public class ZoneSystem : MonoBehaviour { public static ZoneSystem instance; public List<ZoneLocation> m_locations = new List<ZoneLocation>();
  [Serializable] public class ZoneLocation { public bool m_enable = true; public string m_prefabName; public SoftRef m_prefab; public Heightmap.Biome m_biome; public int m_quantity = 10; } }
public struct SoftRef { public GameObject asset; public static int loads, releases; public bool IsValid { get { return asset != null; } } public void Load() { loads++; } public void Release() { releases++; } public GameObject Asset { get { return asset; } } }
public class Heightmap { [Flags] public enum Biome { None = 0, Meadows = 1, Swamp = 2, Mountain = 4, BlackForest = 8 } }
public class SpawnSystemList : MonoBehaviour { public List<SpawnSystem.SpawnData> m_spawners = new List<SpawnSystem.SpawnData>(); }
public class SpawnSystem : MonoBehaviour { public List<SpawnSystemList> m_spawnLists = new List<SpawnSystemList>(); [Serializable] public class SpawnData { public string m_name = ""; public bool m_enabled = true; public GameObject m_prefab; public Heightmap.Biome m_biome; public int m_maxSpawned = 1; public float m_spawnChance = 100; public bool m_spawnAtNight = true, m_spawnAtDay = true; public int m_minLevel = 1, m_maxLevel = 3; } }
public class RandEventSystem : MonoBehaviour { public static RandEventSystem instance; public List<RandomEvent> m_events = new List<RandomEvent>(); }
[Serializable] public class RandomEvent { public string m_name = ""; public Heightmap.Biome m_biome; public string m_startMessage = ""; public List<SpawnSystem.SpawnData> m_spawn = new List<SpawnSystem.SpawnData>(); }
public class Localization { public static Localization instance = new Localization(); string lang = "English";
  public Dictionary<string,string> m_translations = new Dictionary<string,string>();
  public static Dictionary<string, Dictionary<string,string>> All = new Dictionary<string, Dictionary<string,string>>();
  public string GetSelectedLanguage() { return lang; }
  public bool SetupLanguage(string l) { if (!All.ContainsKey(l)) return false; lang = l; m_translations = new Dictionary<string,string>(All[l]); return true; }
  public static Dictionary<string, Dictionary<string,string>> ModWords = new Dictionary<string, Dictionary<string,string>>();
  public string Localize(string s) { if (s == null) return s; return System.Text.RegularExpressions.Regex.Replace(s, @"\$[A-Za-z0-9_]+", m => { var k = m.Value.Substring(1); if (m_translations.ContainsKey(k)) return m_translations[k]; if (ModWords.ContainsKey(lang) && ModWords[lang].ContainsKey(k)) return ModWords[lang][k]; return "[" + k + "]"; }); } }
public class MessageHud : MonoBehaviour { public static MessageHud instance; public static List<string> Shown = new List<string>(); public enum MessageType { TopLeft = 1, Center = 2 } public void ShowMessage(MessageType type, string text, int amount = 0, Sprite icon = null) { Shown.Add(text); } }
public class Player : Humanoid { public static Player m_localPlayer; }
public class Version { public static string GetVersionString(bool includeMercurialHash = false) { return "1.0.16-mock"; } }
public class Fish : MonoBehaviour { public List<BaitSetting> m_baits = new List<BaitSetting>(); [Serializable] public class BaitSetting { public ItemDrop m_bait; public float m_chance; } }
