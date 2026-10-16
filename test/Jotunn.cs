using System.Collections.Generic; using UnityEngine;
namespace Jotunn.Entities { public class CustomItem { public GameObject ItemPrefab { get; set; } public ItemDrop ItemDrop { get; set; } public CustomRecipe Recipe; }
  public class CustomRecipe { public Recipe Recipe { get; set; } }
  public class CustomItemConversion { public object ItemConversion; public GameObject Smelter; } }
namespace Jotunn.Utils {
  public class ModInfo { public string GUID { get; set; } public string Name { get; set; } public List<Jotunn.Entities.CustomItem> Items { get; set; } = new List<Jotunn.Entities.CustomItem>(); public List<Jotunn.Entities.CustomItemConversion> ItemConversions { get; set; } = new List<Jotunn.Entities.CustomItemConversion>(); }
  public static class ModRegistry { public static List<ModInfo> Mods = new List<ModInfo>(); public static IEnumerable<ModInfo> GetMods(bool includeJotunn = false) { return Mods; } } }
