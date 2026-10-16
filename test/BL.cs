using System; using System.Collections.Generic; using System.Diagnostics; using System.Linq; using WikiDumpMods;
class B { static void Main() {
  var sw = Stopwatch.StartNew();
  var b = Baseline.LoadEmbedded(typeof(Baseline).Assembly, "WikiDumpMods.vanilla_baseline.jsonl.gz");
  Console.WriteLine("header " + sw.ElapsedMilliseconds + "ms keys=" + b.Keys.Count + " partial=" + b.Partial + " covered=" + b.Covered.Count + " v=" + b.GameVersion);
  int n = 0, leaves = 0; Dictionary<string,string> sword = null, grey = null;
  foreach (var kv in b.EachFingerprint()) { n++; leaves += kv.Value.Count; if (kv.Key == "item:SwordIron") sword = kv.Value; if (kv.Key == "CharacterDrop:Greydwarf") grey = kv.Value; }
  Console.WriteLine("stream " + sw.ElapsedMilliseconds + "ms entities=" + n + " leaves=" + leaves + " mem=" + GC.GetTotalMemory(true)/1048576 + "MB");
  Console.WriteLine(string.Join(" | ", sword.Where(p => p.Key.StartsWith("m_damages.") || p.Key.StartsWith("m_icons")).Select(p => p.Key + "=" + p.Value).ToArray()));
  Console.WriteLine(string.Join(" | ", grey.Take(12).Select(p => p.Key + "=" + p.Value).ToArray()));
} }
