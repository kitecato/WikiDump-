using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace WikiDumpMods
{
    /// <summary>One installed BepInEx plugin.</summary>
    internal sealed class ModEntry
    {
        public string Guid, Name, Version, DllPath, Folder, FolderName;
        public Assembly Asm;
        public object Instance;
        public readonly HashSet<string> BundleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Resources = new List<string>();
    }

    internal sealed class OriginInfo
    {
        public string Key;
        public string Mod;
        public string Status = "vanilla"; // vanilla | mod | vanilla_replaced
        public int Strength = -1;
        public readonly List<string> Signals = new List<string>();
        public readonly List<KeyValuePair<int, string>> Owners = new List<KeyValuePair<int, string>>();
        public bool IsMod { get { return Status != "vanilla"; } }
        public JObj ToJson()
        {
            var o = new JObj();
            o["status"] = Status;
            o["mod"] = Mod;
            var a = new JArr(); a.AddRange(Signals.Cast<object>()); o["signals"] = a;
            return o;
        }
    }

    /// <summary>
    /// Decides which plugin a prefab / recipe / status effect / location came from. Evidence, strongest first:
    ///  3  registered through Jotunn's ModRegistry or a *Manager library (ItemManager, PieceManager, CreatureManager...)
    ///  2  the asset sits in an AssetBundle that ships with the plugin (file in its folder or embedded resource)
    ///  1  created at runtime (negative Unity instance id = cloned/CreateInstance'd, never a disk asset); owner unknown
    ///  1  missing from the vanilla baseline (exact, when a baseline exists)
    ///  0  referenced from a static field of the plugin (owner hint only, never makes something "modded")
    /// With a vanilla baseline the modded/vanilla split is exact; without one it relies on the signals above.
    /// </summary>
    internal sealed class Origin
    {
        private readonly Dictionary<string, OriginInfo> _info = new Dictionary<string, OriginInfo>();
        private sealed class BundleAsset { public string Mod, Bundle, Path; public object Obj; }
        private readonly Dictionary<string, List<BundleAsset>> _bundleAsset = new Dictionary<string, List<BundleAsset>>(StringComparer.OrdinalIgnoreCase);
        public readonly JArr Bundles = new JArr();
        public readonly JArr Notes = new JArr();
        public Baseline Baseline;
        /// <summary>Keys of everything the game has registered (ZNetScene/ObjectDB/ZoneSystem/events). Only these are judged against the baseline.</summary>
        public readonly HashSet<string> Registered = new HashSet<string>();
        /// <summary>For a partial baseline: does it list this kind of object at all? (absence only counts when it does)</summary>
        public Func<string, bool> BaselineCovers;
        private readonly List<ModEntry> _mods;
        private readonly Dictionary<string, ModEntry> _byGuid;

        public Origin(List<ModEntry> mods)
        {
            _mods = mods;
            _byGuid = mods.Where(m => m.Guid != null).GroupBy(m => m.Guid).ToDictionary(g => g.Key, g => g.First());
        }

        public static string KeyOf(object o)
        {
            if (o == null) return null;
            var u = o as UnityEngine.Object;
            if (ReferenceEquals(u, null) || !u) return null;
            if (u is UnityEngine.GameObject) return "prefab:" + u.name;
            var c = u as UnityEngine.Component;
            if (!ReferenceEquals(c, null)) return c.gameObject ? "prefab:" + c.gameObject.name : null;
            var tRecipe = Refl.T("Recipe"); var tSE = Refl.T("StatusEffect");
            if (tRecipe != null && tRecipe.IsInstanceOfType(u)) return "recipe:" + u.name;
            if (tSE != null && tSE.IsInstanceOfType(u)) return "se:" + u.name;
            return "asset:" + u.GetType().Name + ":" + u.name;
        }

        private OriginInfo Info(string key)
        {
            OriginInfo i;
            if (!_info.TryGetValue(key, out i)) { i = new OriginInfo { Key = key }; _info[key] = i; }
            return i;
        }

        public void Signal(string key, string mod, int strength, string what)
        {
            if (key == null) return;
            var i = Info(key);
            var tag = what + (mod != null ? "(" + mod + ")" : "");
            if (!i.Signals.Contains(tag)) i.Signals.Add(tag);
            if (strength > i.Strength) i.Strength = strength;
            if (mod != null) i.Owners.Add(new KeyValuePair<int, string>(strength, mod));
        }

        /// <summary>Final verdict for a key. Call after all signals are in.</summary>
        public OriginInfo Get(string key)
        {
            if (key == null) return new OriginInfo();
            var i = Info(key);
            if (_resolved.Contains(key)) return i;
            _resolved.Add(key);
            bool? inBaseline = Baseline == null || !Registered.Contains(key) ? (bool?)null : Baseline.Contains(key);
            if (inBaseline == false && Baseline.Partial && BaselineCovers != null && !BaselineCovers(key)) inBaseline = null;
            int strong = i.Strength;
            var best = i.Owners.OrderByDescending(o => o.Key).FirstOrDefault();
            i.Mod = best.Value;
            if (inBaseline == false) { i.Status = "mod"; i.Signals.Add("not-in-vanilla-baseline"); }
            else if (inBaseline == true) { if (strong >= 2 && i.Mod != null) i.Status = "vanilla_replaced"; }
            else if (strong >= 1) i.Status = "mod";
            if (i.Status == "vanilla") i.Mod = null;
            return i;
        }
        private readonly HashSet<string> _resolved = new HashSet<string>();

        public bool IsMod(object o) { var k = KeyOf(o); return k != null && Get(k).IsMod; }
        public bool IsModKey(string k) { return k != null && Get(k).IsMod; }
        public string ModOf(object o) { var k = KeyOf(o); return k == null ? null : Get(k).Mod; }

        public IEnumerable<OriginInfo> All() { foreach (var k in _info.Keys.ToList()) yield return Get(k); }

        // ---------------------------------------------------------------- evidence collectors

        /// <summary>Jotunn.Utils.ModRegistry: exact owner for everything a Jotunn mod registered.</summary>
        public JObj FromJotunn()
        {
            var report = new JObj();
            var t = Refl.T("Jotunn.Utils.ModRegistry");
            if (t == null) { report["present"] = false; return report; }
            report["present"] = true;
            object mods = Refl.Invoke(t, null, "GetMods", Refl.Stat, new object[] { true }) ?? Refl.Invoke(t, null, "GetMods", Refl.Stat, new object[0]);
            foreach (var mi in Refl.Enum(mods))
            {
                var guid = Refl.S(Refl.Get(mi, "GUID"));
                if (guid == null) continue;
                var counts = new JObj();
                foreach (var p in mi.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (p.PropertyType == typeof(string) || !typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType)) continue;
                    if (p.GetIndexParameters().Length > 0) continue;
                    object v; try { v = p.GetValue(mi, null); } catch { continue; }
                    int n = 0;
                    bool conversions = p.Name.IndexOf("Conversion", StringComparison.OrdinalIgnoreCase) >= 0;
                    foreach (var entry in Refl.Enum(v))
                    {
                        n++;
                        if (conversions) continue; // conversions point at vanilla items too: never claim those
                        foreach (var u in UnityRefs(entry, 2))
                        {
                            var key = KeyOf(u);
                            if (key == null) continue;
                            if (p.Name.IndexOf("Location", StringComparison.OrdinalIgnoreCase) >= 0 && u is UnityEngine.GameObject)
                                Signal("loc:" + u.name, guid, 3, "jotunn." + p.Name);
                            Signal(key, guid, 3, "jotunn." + p.Name);
                        }
                    }
                    if (n > 0) counts[p.Name] = n;
                }
                report[guid] = counts;
            }
            return report;
        }

        /// <summary>Unity objects held directly by a registry entry (and by nested Jotunn/manager helper objects).</summary>
        private static IEnumerable<UnityEngine.Object> UnityRefs(object entry, int depth)
        {
            if (entry == null) yield break;
            var direct = entry as UnityEngine.Object;
            if (!ReferenceEquals(direct, null)) { if (direct) yield return direct; yield break; }
            var t = entry.GetType();
            foreach (var m in t.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Type mt; object v;
                try
                {
                    if (m is FieldInfo) { var f = (FieldInfo)m; if (f.Name.StartsWith("<")) continue; mt = f.FieldType; v = f.GetValue(entry); }
                    else if (m is PropertyInfo) { var p = (PropertyInfo)m; if (!p.CanRead || p.GetIndexParameters().Length > 0) continue; mt = p.PropertyType; v = p.GetValue(entry, null); }
                    else continue;
                }
                catch { continue; }
                if (v == null) continue;
                var u = v as UnityEngine.Object;
                if (!ReferenceEquals(u, null)) { if (u) yield return u; continue; }
                if (depth > 1 && mt.Namespace != null && (mt.Namespace.StartsWith("Jotunn") || mt.Namespace.EndsWith("Manager")) && !mt.IsEnum
                    && !typeof(System.Collections.IEnumerable).IsAssignableFrom(mt))
                    foreach (var x in UnityRefs(v, depth - 1)) yield return x;
            }
        }

        /// <summary>
        /// Blaxxun-style libraries merged into mods (ItemManager.Item, PieceManager.BuildPiece, CreatureManager.Creature,
        /// LocationManager.Location, ...): static registries of wrapper objects that hold the mod's prefab.
        /// Any other static Unity reference in the plugin assembly only becomes an owner hint.
        /// </summary>
        public JObj FromStatics()
        {
            var report = new JObj();
            foreach (var mod in _mods)
            {
                if (mod.Asm == null) continue;
                Type[] types;
                try { types = mod.Asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); } catch { continue; }
                int strong = 0, hints = 0;
                foreach (var t in types)
                {
                    if (t.IsGenericTypeDefinition) continue;
                    bool managerNs = t.Namespace != null && t.Namespace.EndsWith("Manager");
                    if (!SafeStatics(t, mod, managerNs)) continue;
                    FieldInfo[] fs;
                    try { fs = t.GetFields(Refl.Stat | BindingFlags.DeclaredOnly); } catch { continue; }
                    foreach (var f in fs)
                    {
                        if (f.IsLiteral || typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                        object v; try { v = f.GetValue(null); } catch { continue; }
                        if (v == null || v is string) continue;
                        var where = t.FullName + "." + f.Name;
                        var single = v as UnityEngine.Object;
                        IEnumerable<object> elems = !ReferenceEquals(single, null) ? new object[] { single }
                            : (v is System.Collections.IDictionary ? Refl.Enum(((System.Collections.IDictionary)v).Values) : Refl.Enum(v));
                        int n = 0;
                        foreach (var e in elems)
                        {
                            if (n++ > 20000) break;
                            var eu = e as UnityEngine.Object;
                            if (!ReferenceEquals(eu, null))
                            {
                                if (!eu) continue;
                                Signal(KeyOf(eu), mod.Guid, 0, "static:" + where); hints++;
                                continue;
                            }
                            if (e == null) continue;
                            var et = e.GetType();
                            bool wrapper = managerNs || (et.Namespace != null && et.Namespace.EndsWith("Manager"));
                            foreach (var u in UnityRefs(e, 2))
                            {
                                var k = KeyOf(u);
                                // Only the wrapper's own prefab ("Prefab"/"prefab") counts as proof; other members may point at vanilla things.
                                bool own = wrapper && IsOwnPrefabMember(e, u);
                                Signal(k, mod.Guid, own ? 3 : 0, (own ? "manager:" : "static:") + where);
                                if (own) strong++; else hints++;
                            }
                        }
                    }
                }
                if (strong + hints > 0) report[mod.Guid] = new JObj { { "managerPrefabs", strong }, { "staticRefs", hints } };
            }
            return report;
        }

        /// <summary>
        /// Reading a static field runs the type's static constructor if it hasn't run yet. To never trigger arbitrary
        /// mod code, only types without an explicit static constructor (BeforeFieldInit: just field initialisers),
        /// the plugin class itself and the well-known *Manager libraries are read.
        /// </summary>
        public static bool SafeStatics(Type t, ModEntry mod, bool managerNs)
        {
            if (managerNs) return true;
            if (mod.Instance != null)
            {
                var pt = mod.Instance.GetType();
                for (var x = t; x != null; x = x.DeclaringType) if (x == pt) return true;
            }
            return (t.Attributes & TypeAttributes.BeforeFieldInit) != 0 || t.TypeInitializer == null;
        }

        private static bool IsOwnPrefabMember(object wrapper, UnityEngine.Object u)
        {
            foreach (var n in new[] { "Prefab", "prefab", "m_prefab", "location", "Location", "Effect", "effect" })
            {
                var v = Refl.Get(wrapper, n);
                if (ReferenceEquals(v, u)) return true;
                var c = v as UnityEngine.Component;
                if (!ReferenceEquals(c, null) && c && ReferenceEquals(c.gameObject, u)) return true;
            }
            return false;
        }

        /// <summary>Maps every loaded AssetBundle to the plugin that ships it, then attributes the prefabs inside.</summary>
        public void FromBundles()
        {
            var tAB = Refl.T("UnityEngine.AssetBundle");
            if (tAB == null) { Notes.Add("AssetBundle API not found"); return; }
            var all = Refl.Invoke(tAB, null, "GetAllLoadedAssetBundles", Refl.Stat, new object[0]);
            foreach (var b in Refl.Enum(all))
            {
                var bn = Refl.UName(b) ?? "";
                var owner = OwnerOfBundle(bn);
                bool streamed = Refl.B(Refl.Get(b, "isStreamedSceneAssetBundle"));
                var names = streamed ? new string[0] : (Refl.Call(b, "GetAllAssetNames") as string[] ?? new string[0]);
                var bo = new JObj { { "name", bn }, { "mod", owner == null ? null : owner.Guid }, { "assets", names.Length } };
                Bundles.Add(bo);
                if (owner == null) continue;
                owner.BundleNames.Add(bn);
                foreach (var path in names)
                {
                    var file = Path.GetFileNameWithoutExtension(path);
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext != ".prefab" && ext != ".asset") continue;
                    var k = ext + ":" + file;
                    List<BundleAsset> l;
                    if (!_bundleAsset.TryGetValue(k, out l)) _bundleAsset[k] = l = new List<BundleAsset>();
                    l.Add(new BundleAsset { Mod = owner.Guid, Bundle = bn, Path = path, Obj = b });
                }
            }
        }

        private Dictionary<ModEntry, HashSet<string>> _files;

        /// <summary>Lower-case file names (with and without extension) shipped in each mod's own folder.</summary>
        private HashSet<string> FilesOf(ModEntry m)
        {
            if (_files == null) _files = new Dictionary<ModEntry, HashSet<string>>();
            HashSet<string> hs;
            if (_files.TryGetValue(m, out hs)) return hs;
            hs = new HashSet<string>();
            if (m.Folder != null && Directory.Exists(m.Folder))
            {
                try
                {
                    foreach (var f in Directory.GetFiles(m.Folder, "*", SearchOption.AllDirectories))
                    {
                        var fn = Path.GetFileName(f).ToLowerInvariant();
                        if (fn.EndsWith(".dll") || fn.EndsWith(".md") || fn.EndsWith(".json") || fn.EndsWith(".png") || fn.EndsWith(".cfg") || fn.EndsWith(".txt") || fn.EndsWith(".yml") || fn.EndsWith(".yaml")) continue;
                        hs.Add(fn); hs.Add(Path.GetFileNameWithoutExtension(fn));
                    }
                }
                catch { }
            }
            _files[m] = hs;
            return hs;
        }

        private ModEntry OwnerOfBundle(string bundleName)
        {
            if (string.IsNullOrEmpty(bundleName)) return null;
            var bn = bundleName.ToLowerInvariant();
            var bnNoExt = Path.GetFileNameWithoutExtension(bn);
            foreach (var m in _mods)
            {
                var files = FilesOf(m);
                if (files.Contains(bn) || files.Contains(bnNoExt)) return m;
                foreach (var r in m.Resources)
                {
                    var rl = r.ToLowerInvariant();
                    if (rl == bn || rl.EndsWith("." + bn) || rl.EndsWith("." + bnNoExt)) return m;
                }
            }
            return null;
        }

        /// <summary>Attributes one object using bundle membership and the runtime-clone test.</summary>
        public void Inspect(UnityEngine.Object u)
        {
            if (ReferenceEquals(u, null) || !u) return;
            var key = KeyOf(u);
            if (key == null) return;
            List<BundleAsset> hits;
            var ext = u is UnityEngine.ScriptableObject ? ".asset" : ".prefab";
            if (_bundleAsset.TryGetValue(ext + ":" + u.name, out hits))
                foreach (var hit in hits)
                {
                    // Same file name is not proof (e.g. a mod bundle holding its own "hammer.prefab"): the bundle must return this very object.
                    object same = null;
                    try { same = Refl.Call(hit.Obj, "LoadAsset", hit.Path); } catch { }
                    if (ReferenceEquals(same, u)) { Signal(key, hit.Mod, 2, "bundle:" + hit.Bundle); break; }
                    if (same == null) { Signal(key, hit.Mod, 0, "bundle-name-only:" + hit.Bundle); }
                }
            int id;
            try { id = u.GetInstanceID(); } catch { id = 0; }
            if (id < 0) Signal(key, null, 1, "runtime-clone");
        }

        private Dictionary<Assembly, string> _asmOwner;

        private string OwnerOfType(Type t)
        {
            if (_asmOwner == null)
            {
                _asmOwner = new Dictionary<Assembly, string>();
                foreach (var m in _mods) if (m.Asm != null && !_asmOwner.ContainsKey(m.Asm)) _asmOwner[m.Asm] = m.Guid;
            }
            string g;
            return t != null && _asmOwner.TryGetValue(t.Assembly, out g) ? g : null;
        }

        /// <summary>
        /// Second pass for modded things whose owner is still unknown (and status effects of a mod-defined type):
        ///  - type: the object's own class (status effect) or one of its components is defined in a plugin's assembly
        ///  - referenced-by: every modded object that references it belongs to one single mod
        ///  - name-match: its name (minus "_cooldown" etc.) is contained in names of exactly one mod's objects (inference)
        /// </summary>
        public void Refine(Registry r, Dictionary<string, JArr> refs)
        {
            foreach (var se in r.StatusEffects)
            {
                var g = OwnerOfType(se.GetType());
                if (g == null) continue;
                var k = KeyOf(se);
                _resolved.Remove(k);
                Signal(k, g, 3, "type:" + se.GetType().Name);
            }
            var unknown = All().Where(x => x.IsMod && x.Mod == null).ToList();
            foreach (var i in unknown)
            {
                if (!i.Key.StartsWith("prefab:")) continue;
                GameObject go;
                if (!r.Prefabs.TryGetValue(i.Key.Substring(7), out go)) continue;
                var owners = new HashSet<string>();
                foreach (var c in go.GetComponentsInChildren(typeof(MonoBehaviour), true)) { var g = c ? OwnerOfType(c.GetType()) : null; if (g != null) owners.Add(g); }
                if (owners.Count == 1) { i.Mod = owners.First(); i.Signals.Add("type:component(" + i.Mod + ")"); }
            }
            foreach (var i in All().Where(x => x.IsMod && x.Mod == null).ToList())
            {
                JArr rs;
                if (refs == null || !refs.TryGetValue(i.Key, out rs)) continue;
                var owners = new HashSet<string>();
                bool vanillaRef = false;
                foreach (JObj rec in rs)
                {
                    var from = rec["from"] as JObj;
                    var m = from == null ? null : from["mod"] as string;
                    if (m == null) vanillaRef = true; else if (m != "unknown") owners.Add(m);
                }
                if (owners.Count == 1 && !vanillaRef) { i.Mod = owners.First(); i.Signals.Add("referenced-by(" + i.Mod + ")"); }
            }
            var names = new Dictionary<string, List<string>>();
            foreach (var i in All().Where(x => x.IsMod && x.Mod != null))
            {
                List<string> l; if (!names.TryGetValue(i.Mod, out l)) names[i.Mod] = l = new List<string>();
                l.Add(Norm(i.Key.Substring(i.Key.IndexOf(':') + 1)));
            }
            foreach (var i in All().Where(x => x.IsMod && x.Mod == null).ToList())
            {
                var n = Norm(i.Key.Substring(i.Key.IndexOf(':') + 1));
                foreach (var suffix in new[] { "cooldown", "stacking", "recipe" }) if (n.EndsWith(suffix) && n.Length > suffix.Length + 5) n = n.Substring(0, n.Length - suffix.Length);
                if (n.StartsWith("recipe")) n = n.Substring(6);
                if (n.Length < 6) continue;
                var hit = names.Where(kv => kv.Value.Any(x => x.Contains(n) || (x.Length >= 6 && n.Contains(x)))).Select(kv => kv.Key).ToList();
                if (hit.Count == 1) { i.Mod = hit[0]; i.Signals.Add("name-match(" + i.Mod + ")"); }
            }
        }

        private static string Norm(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Recipes, conversions etc. follow their result item when they carry no evidence of their own.</summary>
        /// <summary>
        /// Copies the owner of fromKey onto key when key has none. markModded: key becomes modded too (a recipe that
        /// produces a modded item is modded); otherwise only an already-modded key gets the owner (status effects).
        /// </summary>
        public void Inherit(string key, string fromKey, string why, bool markModded)
        {
            if (key == null || fromKey == null) return;
            var src = Get(fromKey);
            if (!src.IsMod || src.Mod == null) { if (markModded && src.IsMod) { var j = Get(key); if (j.Status == "vanilla" && (Baseline == null || !Baseline.Contains(key))) j.Status = "mod"; } return; }
            var i = Get(key);
            if (!markModded && !i.IsMod) return;
            if (i.Mod == null) { i.Mod = src.Mod; i.Signals.Add("via:" + why + "(" + src.Mod + ")"); }
            if (i.Status == "vanilla" && (Baseline == null || !Baseline.Contains(key))) i.Status = "mod";
        }

        public string ModName(string guid)
        {
            ModEntry m;
            return guid != null && _byGuid.TryGetValue(guid, out m) ? m.Name : null;
        }
    }
}
