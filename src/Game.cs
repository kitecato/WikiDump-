using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace WikiDumpMods
{
    /// <summary>Everything the game currently has registered, collected once per dump.</summary>
    internal sealed class Registry
    {
        public readonly Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>();
        public readonly List<string> DuplicatePrefabs = new List<string>();
        /// <summary>Prefabs registered in ZNetScene (networked). Build-table-only pieces (hoe paths, repair…) are not.</summary>
        public readonly HashSet<string> Networked = new HashSet<string>();
        public readonly Dictionary<string, GameObject> Items = new Dictionary<string, GameObject>();
        public readonly List<object> Recipes = new List<object>();
        public readonly List<object> StatusEffects = new List<object>();
        public readonly List<object> Locations = new List<object>();
        public readonly List<object> Rooms = new List<object>();
        public readonly List<object> SpawnLists = new List<object>();
        public readonly List<object> Events = new List<object>();
        /// <summary>piece prefab name -> tool item prefab names whose build table contains it</summary>
        public readonly Dictionary<string, List<string>> PieceTools = new Dictionary<string, List<string>>();
        public readonly List<string> Problems = new List<string>();

        public static Registry Collect()
        {
            var r = new Registry();
            var zns = Refl.Singleton("ZNetScene");
            var odb = Refl.Singleton("ObjectDB");
            if (zns == null) r.Problems.Add("ZNetScene not ready");
            if (odb == null) r.Problems.Add("ObjectDB not ready");

            foreach (var o in Refl.Enum(Refl.Get(zns, "m_prefabs"))) r.AddPrefab(o as GameObject);
            var named = Refl.Get(zns, "m_namedPrefabs") as System.Collections.IDictionary;
            if (named != null) foreach (var o in named.Values) r.AddPrefab(o as GameObject);
            foreach (var n in r.Prefabs.Keys) r.Networked.Add(n);

            foreach (var o in Refl.Enum(Refl.Get(odb, "m_items")))
            {
                var go = o as GameObject;
                if (!go) continue;
                if (!r.Items.ContainsKey(go.name)) r.Items[go.name] = go;
                if (!r.Prefabs.ContainsKey(go.name)) r.Prefabs[go.name] = go;
            }
            // items that only live in ZNetScene (rare, but some mods forget ObjectDB)
            foreach (var go in r.Prefabs.Values)
                if (!r.Items.ContainsKey(go.name) && Refl.Comp(go, "ItemDrop")) r.Items[go.name] = go;

            foreach (var o in Refl.Enum(Refl.Get(odb, "m_recipes"))) if (o is UnityEngine.Object && (UnityEngine.Object)o) r.Recipes.Add(o);
            foreach (var o in Refl.Enum(Refl.Get(odb, "m_StatusEffects"))) if (o is UnityEngine.Object && (UnityEngine.Object)o) r.StatusEffects.Add(o);

            // build tables: which tool builds which piece
            foreach (var it in r.Items.Values)
            {
                var table = Refl.Get(Shared(it), "m_buildPieces");
                foreach (var p in Refl.Enum(Refl.Get(table, "m_pieces")))
                {
                    var pg = p as GameObject;
                    if (!pg) continue;
                    List<string> l;
                    if (!r.PieceTools.TryGetValue(pg.name, out l)) r.PieceTools[pg.name] = l = new List<string>();
                    if (!l.Contains(it.name)) l.Add(it.name);
                    if (!r.Prefabs.ContainsKey(pg.name)) r.Prefabs[pg.name] = pg;
                }
            }

            var zs = Refl.Singleton("ZoneSystem");
            foreach (var l in Refl.Enum(Refl.Get(zs, "m_locations"))) if (l != null) r.Locations.Add(l);
            var ddb = Refl.Singleton("DungeonDB");
            var rooms = Refl.Get(ddb, "m_rooms") ?? Refl.Call(ddb, "GetRooms") ?? Refl.CallStatic("DungeonDB", "GetRooms");
            foreach (var l in Refl.Enum(rooms)) if (l != null) r.Rooms.Add(l);

            var seen = new HashSet<object>(RefEq.I);
            var tSL = Refl.T("SpawnSystemList");
            if (tSL != null)
            {
                foreach (var o in FindAll(tSL)) if (seen.Add(o)) r.SpawnLists.Add(o);
                foreach (var go in r.Prefabs.Values)
                    foreach (var ss in Refl.Comps(go, "SpawnSystem", true))
                        foreach (var sl in Refl.Enum(Refl.Get(ss, "m_spawnLists")))
                            if (sl != null && seen.Add(sl)) r.SpawnLists.Add(sl);
            }
            var res = Refl.Singleton("RandEventSystem");
            foreach (var e in Refl.Enum(Refl.Get(res, "m_events"))) if (e != null) r.Events.Add(e);
            return r;
        }

        private void AddPrefab(GameObject go)
        {
            if (!go) return;
            GameObject prev;
            if (Prefabs.TryGetValue(go.name, out prev)) { if (!ReferenceEquals(prev, go) && !DuplicatePrefabs.Contains(go.name)) DuplicatePrefabs.Add(go.name); return; }
            Prefabs[go.name] = go;
        }

        /// <summary>Resources.FindObjectsOfTypeAll - also finds objects that live only on prefabs (not in the scene).</summary>
        public static IEnumerable<object> FindAll(Type t)
        {
            var tRes = Refl.T("UnityEngine.Resources");
            object arr = tRes == null ? null : Refl.Invoke(tRes, null, "FindObjectsOfTypeAll", Refl.Stat, new object[] { t });
            return Refl.Enum(arr).Where(o => o is UnityEngine.Object && (UnityEngine.Object)o).ToList();
        }

        public static object ItemData(GameObject go) { return Refl.Get(Refl.Comp(go, "ItemDrop"), "m_itemData"); }
        public static object Shared(GameObject go) { return Refl.Get(ItemData(go), "m_shared"); }

        /// <summary>A SoftReference / direct GameObject field of a ZoneLocation or RoomData, loaded on demand.</summary>
        public static GameObject LoadSoft(object holder, List<object> toRelease, JObj diag = null)
        {
            var v = Refl.Get(holder, "m_prefab");
            var go = Refl.GO(v);
            if (go) return go;
            if (v == null) { if (diag != null) diag["m_prefab"] = "null"; return null; }
            try
            {
                var t = v.GetType();
                if (diag != null && !diag.Has("type"))
                {
                    diag["type"] = t.FullName;
                    var ms = new JArr();
                    foreach (var m in t.GetMembers(Refl.Inst)) if (!(m is ConstructorInfo) && !m.Name.StartsWith("<")) ms.Add(m.MemberType + " " + m.Name);
                    diag["members"] = ms;
                }
                var already = Refl.GO(Refl.Get(v, "Asset"));
                if (already) return already;
                var load = t.GetMethods(Refl.Inst).FirstOrDefault(m => m.Name == "Load" && m.GetParameters().Length == 0);
                if (load == null) { if (diag != null) diag["error"] = "no Load()"; return null; }
                // the struct is boxed once and Load/Asset/Release all go through that same box
                load.Invoke(v, null);
                toRelease.Add(v);
                var asset = Refl.Get(v, "Asset");
                go = Refl.GO(asset);
                if (!go && diag != null) diag["error"] = "Asset after Load(): " + (asset == null ? "null" : asset.GetType().Name);
                return go;
            }
            catch (Exception e) { if (diag != null) diag["error"] = (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; return null; }
        }
    }

    /// <summary>Translation tables captured per language.</summary>
    internal sealed class Loc
    {
        public readonly Dictionary<string, Dictionary<string, string>> Tables = new Dictionary<string, Dictionary<string, string>>();
        public readonly List<string> Languages = new List<string>();
        public string Original;
        private static readonly Regex Token = new Regex(@"\$[A-Za-z0-9_]+", RegexOptions.Compiled);

        public static string Current() { return Refl.Call(Refl.Singleton("Localization"), "GetSelectedLanguage") as string; }

        public static bool Switch(string lang)
        {
            var r = Refl.Call(Refl.Singleton("Localization"), "SetupLanguage", lang);
            return !(r is bool) || (bool)r;
        }

        /// <summary>lang -> token -> text, resolved by the game's own Localize() (includes mod translations).</summary>
        public readonly Dictionary<string, Dictionary<string, string>> Resolved = new Dictionary<string, Dictionary<string, string>>();
        public ModTexts Mod;

        /// <summary>One token in one language and where the text came from (game | config:… | folder:… | embedded:… | loaded:… | code:… | jotunn:… | vanilla).</summary>
        public string Word(string token, string lang, out string source)
        {
            source = null;
            string modSrc = null, modText = Mod == null ? null : Mod.Get(lang, token, out modSrc);
            if (modText != null && modSrc.StartsWith("config:")) { source = modSrc; return modText; }
            Dictionary<string, string> d; string v;
            if (Resolved.TryGetValue(lang, out d) && d.TryGetValue(token, out v) && v != null) { source = "game"; return v; }
            if (modText != null) { source = modSrc; return modText; }
            if (Tables.TryGetValue(lang, out d) && d.TryGetValue(token, out v) && v != null) { source = "vanilla"; return v; }
            return null;
        }

        /// <summary>The game's Localize() on one token; null when the game itself has no text for it ("[token]" or the token back).</summary>
        public static string GameLocalize(string token)
        {
            var l = Refl.Singleton("Localization");
            if (l == null) return null;
            var r = Refl.Call(l, "Localize", "$" + token) as string;
            if (string.IsNullOrEmpty(r) || r == "$" + token || r == token || r == "[" + token + "]" || r == "[$" + token + "]") return null;
            return r;
        }

        /// <summary>Resolves every token for the active language through the game (call while that language is active).</summary>
        public void ResolveTokens(string lang, IEnumerable<string> tokens)
        {
            Dictionary<string, string> d;
            if (!Resolved.TryGetValue(lang, out d)) Resolved[lang] = d = new Dictionary<string, string>();
            foreach (var t in tokens) if (!d.ContainsKey(t)) d[t] = GameLocalize(t);
        }

        public static string LocalizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var l = Refl.Singleton("Localization");
            return l == null ? text : (Refl.Call(l, "Localize", text) as string ?? text);
        }

        public void Capture(string lang)
        {
            var d = new Dictionary<string, string>();
            var tr = Refl.Get(Refl.Singleton("Localization"), "m_translations") as System.Collections.IDictionary;
            if (tr != null) foreach (System.Collections.DictionaryEntry kv in tr) if (kv.Key != null) d[kv.Key.ToString()] = kv.Value == null ? null : kv.Value.ToString();
            Tables[lang] = d;
            if (!Languages.Contains(lang)) Languages.Add(lang);
        }

        /// <summary>Resolves "$token" (and tokens embedded in longer text) for one language; null when nothing resolves.</summary>
        public string Text(string raw, string lang)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            if (!Languages.Contains(lang)) return null;
            bool any = false;
            var s = Token.Replace(raw, m =>
            {
                string src, v = Word(m.Value.Substring(1), lang, out src);
                if (v != null) { any = true; return v; }
                return m.Value;
            });
            if (!raw.Contains("$")) return raw;
            return any ? s : null;
        }

        /// <summary>{"English": "...", "Ukrainian": "..."} plus the raw token.</summary>
        public JObj Names(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var o = new JObj();
            o["token"] = raw;
            foreach (var l in Languages) o[l] = Text(raw, l);
            return o;
        }

        public IEnumerable<string> Tokens(string raw)
        {
            if (string.IsNullOrEmpty(raw)) yield break;
            foreach (Match m in Token.Matches(raw)) yield return m.Value.Substring(1);
        }
    }
}
