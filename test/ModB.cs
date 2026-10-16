// A mod using Blaxxun-style ItemManager merged in, plus a static config table.
using System.Collections.Generic; using UnityEngine;
namespace LocalizationManager { public class Localizer { public static Dictionary<string, Dictionary<string,string>> loadedTexts = new Dictionary<string, Dictionary<string,string>>{{"English", new Dictionary<string,string>{{"item_frostshard","Frost shard"},{"item_frostshard_desc","Cold."}}}}; } }
namespace PieceManager { public class LocalizeKey { public static List<LocalizeKey> keys = new List<LocalizeKey>{ new LocalizeKey{ Key = "enemy_draugrfrost", Localizations = new Dictionary<string,string>{{"English","Frost draugr"}} } }; public string Key; public Dictionary<string,string> Localizations; } }
namespace ItemManager { public class Item { public static List<Item> registeredItems = new List<Item>(); public GameObject Prefab; public GameObject vanillaRef; } }
namespace ModB { public class ModBPlugin { public static Dictionary<string,int> UpgradeCosts = new Dictionary<string,int>{{"BronzeAxe",5}}; public static List<GameObject> vanillaTouched = new List<GameObject>(); } }

public class SE_Warfare_Bleeding : SE_Stats {}
