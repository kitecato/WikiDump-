using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace WikiDumpMods
{
    internal sealed class DumpOptions
    {
        public string OutDir;
        public string BaselinePath;
        public string[] Languages = { "English", "Ukrainian" };
        public bool Icons = true;
        public string IconColor = "LinearToSRGB";
        public bool ScanVanillaLocations = true;
        public bool IncludeVanilla;
        public bool Tooltips = true;
        public bool ModStatics = true;
        public bool ForceBaseline;
        public string ConfigDir;
        public int RawDepth = 4;
        public string PluginVersion;
        public string SelfGuid;
    }

    /// <summary>The whole export. Run() is a coroutine: it yields between sections so the game keeps breathing.</summary>
    internal sealed class Dumper
    {
        private readonly DumpOptions _opt;
        private readonly List<ModEntry> _mods;
        private readonly Action<string> _log;
        private readonly Action<string> _hud;
        private Registry _r;
        private Origin _o;
        private Loc _loc;
        private DataIndex _ix;
        private Icons _icons;
        private readonly JObj _manifest = new JObj();
        private readonly JArr _errors = new JArr();
        private readonly Dictionary<string, JObj> _tooltips = new Dictionary<string, JObj>();
        private readonly Dictionary<string, JArr> _iconFiles = new Dictionary<string, JArr>();
        private readonly Dictionary<string, HashSet<string>> _modTokens = new Dictionary<string, HashSet<string>>();
        private readonly Dictionary<string, JObj> _modContent = new Dictionary<string, JObj>();
        private readonly Stopwatch _sw = new Stopwatch();
        public bool BaselineMode;
        public string Summary;

        public Dumper(DumpOptions opt, List<ModEntry> mods, Action<string> log, Action<string> hud)
        {
            _opt = opt; _mods = mods; _log = log ?? (s => { }); _hud = hud ?? (s => { });
        }

        private string P(params string[] parts) { return Path.Combine(_opt.OutDir, Path.Combine(parts)); }

        private void Step(string name, Action a)
        {
            var t = _sw.ElapsedMilliseconds;
            try { a(); }
            catch (Exception e)
            {
                var ie = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                _errors.Add(new JObj { { "section", name }, { "error", ie.GetType().Name + ": " + ie.Message }, { "stack", ie.StackTrace } });
                _log("section " + name + " failed: " + ie);
            }
            var tm = _manifest.Obj("timingsMs");
            tm[name] = (tm[name] is long ? (long)tm[name] : 0L) + (_sw.ElapsedMilliseconds - t);
        }

        private bool InScope(string key) { return _opt.IncludeVanilla || _o.IsModKey(key); }
        private string ModOfKey(string key) { var i = _o.Get(key); return i.IsMod ? (i.Mod ?? "unknown") : null; }

        private JObj Content(string mod)
        {
            JObj c;
            if (!_modContent.TryGetValue(mod, out c)) _modContent[mod] = c = new JObj();
            return c;
        }

        private void Track(string key, string kind, string name, string token)
        {
            var mod = ModOfKey(key);
            if (mod == null) return;
            Content(mod).Arr(kind).Add(name);
            foreach (var t in _loc.Tokens(token))
            {
                HashSet<string> hs;
                if (!_modTokens.TryGetValue(mod, out hs)) _modTokens[mod] = hs = new HashSet<string>();
                hs.Add(t);
            }
        }

        public IEnumerator Run()
        {
            _sw.Start();
            Directory.CreateDirectory(_opt.OutDir);
            CleanOutput();
            _hud("WikiDumpMods: збираю дані…");

            _manifest["plugin"] = _opt.PluginVersion;
            _manifest["gameVersion"] = GameVersion();
            _manifest["dumpedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            _manifest["includeVanilla"] = _opt.IncludeVanilla;

            // vanilla run = nothing installed except this plugin (Jotunn alone adds no content, so it may stay)
            BaselineMode = _opt.ForceBaseline || _mods.All(m => m.Guid == _opt.SelfGuid || m.Guid == "com.jotunn.jotunn");
            var present = new JArr(); foreach (var m in _mods) present.Add(m.Guid + " " + m.Version);
            _manifest["plugins"] = present;
            _manifest["mode"] = BaselineMode ? "vanilla-baseline" : "mods";

            Step("registry", () => _r = Registry.Collect());
            yield return null;
            if (_r == null) { Finish(); yield break; }
            foreach (var p in _r.Problems) _errors.Add(new JObj { { "section", "registry" }, { "error", p } });

            // ---- origin
            _o = new Origin(_mods);
            if (!BaselineMode) Step("baseline.load", () =>
            {
                // a baseline made on this PC wins; otherwise the one shipped inside the plugin (vanilla 1.0.16, partial)
                _o.Baseline = Baseline.Load(_opt.BaselinePath) ?? Baseline.LoadEmbedded(typeof(Dumper).Assembly, "WikiDumpMods.vanilla_baseline.jsonl.gz");
                if (_o.Baseline != null && _o.Baseline.GameVersion != GameVersion())
                    _errors.Add(new JObj { { "section", "baseline" }, { "warning", "baseline is for game " + _o.Baseline.GameVersion + ", running " + GameVersion() + ": things the update added may show as modded" } });
            });
            var jotunn = new JObj(); var statics = new JObj();
            Step("origin", () =>
            {
                jotunn = _o.FromJotunn();
                statics = _o.FromStatics();
                _o.FromBundles();
                foreach (var go in _r.Prefabs.Values) _o.Inspect(go);
                foreach (var rc in _r.Recipes) _o.Inspect((UnityEngine.Object)rc);
                foreach (var se in _r.StatusEffects) _o.Inspect((UnityEngine.Object)se);
                foreach (var go in _r.Prefabs.Values) _o.Registered.Add("prefab:" + go.name);
                _o.BaselineCovers = key =>
                {
                    if (!key.StartsWith("prefab:")) return true;
                    var name = key.Substring(7);
                    if (_r.Items.ContainsKey(name)) return true;
                    // the converted WikiDump baseline only walked ZNetScene: build-table-only pieces were never listed
                    if (!_r.Networked.Contains(name)) return false;
                    GameObject g;
                    if (!_r.Prefabs.TryGetValue(name, out g)) return false;
                    foreach (var tn in _o.Baseline.Covered) if (Refl.Comp(g, tn)) return true;
                    return false;
                };
                foreach (var rc in _r.Recipes) _o.Registered.Add(Origin.KeyOf(rc));
                foreach (var se in _r.StatusEffects) _o.Registered.Add(Origin.KeyOf(se));
                foreach (var zl in _r.Locations) { var ln = Refl.S(Refl.Get(zl, "m_prefabName")); if (ln != null) _o.Registered.Add("loc:" + ln); }
                foreach (var ev in _r.Events) { var en = Refl.S(Refl.Get(ev, "m_name")); if (en != null) _o.Registered.Add("event:" + en); }
                foreach (var rc in _r.Recipes)
                    _o.Inherit(Origin.KeyOf(rc), Origin.KeyOf(Refl.Get(rc, "m_item")), "result item", true);
                foreach (var it in _r.Items.Values)
                    foreach (var f in new[] { "m_consumeStatusEffect", "m_equipStatusEffect", "m_setStatusEffect", "m_attackStatusEffect" })
                        _o.Inherit(Origin.KeyOf(Refl.Get(Registry.Shared(it), f)), "prefab:" + it.name, "used by item", false);
            });
            yield return null;

            if (BaselineMode)
            {
                Step("baseline.write", WriteBaseline);
                Finish();
                yield break;
            }

            // ---- localisation: tooltips are rendered per language while that language is active
            _loc = new Loc { Original = Loc.Current() };
            Step("localization", () => Languages());
            yield return null;

            // ---- indexes (built twice: the second pass sees owners settled by Refine, which itself needs the references)
            for (var it = BuildIndex(); it.MoveNext();) yield return it.Current;
            Step("origin.refine", () => _o.Refine(_r, _ix.Refs));
            yield return null;
            for (var it = BuildIndex(); it.MoveNext();) yield return it.Current;

            // ---- locations / dungeon rooms (loads prefabs on demand)
            JArr locations = new JArr(), rooms = new JArr();
            int n = 0;
            foreach (var zl in _r.Locations)
            {
                Step("locations", () => { var j = Location(zl, false); if (j != null) locations.Add(j); });
                if (++n % 10 == 0) yield return null;
            }
            foreach (var rd in _r.Rooms)
            {
                Step("rooms", () => { var j = Location(rd, true); if (j != null) rooms.Add(j); });
                if (++n % 10 == 0) yield return null;
            }

            // ---- icons
            if (_opt.Icons)
            {
                _icons = new Icons(_opt.IconColor);
                Step("icons", ExportIcons);
                yield return null;
            }

            // ---- outputs
            _hud("WikiDumpMods: записую файли…");
            Step("items", WriteItems); yield return null;
            Step("pieces", WritePieces); yield return null;
            Step("creatures", WriteCreatures); yield return null;
            Step("status_effects", WriteStatusEffects); yield return null;
            Step("recipes", WriteRecipes); yield return null;
            Step("world", () => WriteWorld(locations, rooms)); yield return null;
            Step("vanilla_links", WriteVanillaLinks); yield return null;
            Step("vanilla_changes", WriteVanillaChanges); yield return null;
            Step("references", WriteReferences); yield return null;
            Step("raw_prefabs", WriteRaw); yield return null;
            Step("localization.files", WriteLocalization); yield return null;
            Step("origin.file", () => WriteOrigin(jotunn, statics)); yield return null;
            Step("mods", WriteMods); yield return null;
            if (_icons != null) _icons.Dispose();
            Finish();
        }

        private IEnumerator BuildIndex()
        {
            _ix = new DataIndex(_r, _o, _loc);
            Step("index.recipes", _ix.BuildRecipes);
            yield return null;
            Step("index.prefabs", _ix.BuildPrefabs);
            yield return null;
            Step("index.spawns", _ix.BuildSpawns);
            yield return null;
            int n = 0;
            foreach (var go in _r.Prefabs.Values.ToList())
            {
                Step("index.references", () => _ix.ScanReferences(go));
                if (++n % 60 == 0) yield return null;
            }
            foreach (var so in _r.Recipes.Concat(_r.StatusEffects)) Step("index.references", () => _ix.ScanReferencesOf((UnityEngine.Object)so));
            yield return null;
        }

        private void CleanOutput()
        {
            // only our own previous output; the baseline file is kept
            foreach (var d in new[] { "icons", "raw", "mods", "localization" })
            {
                var p = P(d);
                try { if (Directory.Exists(p)) Directory.Delete(p, true); } catch { }
            }
            foreach (var f in Directory.GetFiles(_opt.OutDir, "*.json"))
            {
                if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(_opt.BaselinePath ?? ""), StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch { }
            }
        }

        private void Finish()
        {
            if (_locLoaded + _locFailed > 0) _manifest["locationLoading"] = new JObj { { "loaded", _locLoaded }, { "failed", _locFailed }, { "diagnostics", _locDiag } };
            _manifest["errors"] = _errors;
            _manifest["totalMs"] = _sw.ElapsedMilliseconds;
            if (_icons != null) _manifest["icons"] = new JObj { { "saved", _icons.Saved }, { "reused", _icons.Reused }, { "failed", _icons.Failed }, { "lastError", _icons.LastError }, { "colorMode", _opt.IconColor } };
            if (_o != null && _o.Baseline != null) _manifest["baseline"] = new JObj { { "path", _o.Baseline.Path }, { "gameVersion", _o.Baseline.GameVersion }, { "created", _o.Baseline.Created } };
            else if (!BaselineMode) _manifest["baseline"] = "none (modded/vanilla split uses heuristics; see README)";
            Json.WriteFile(P("manifest.json"), _manifest, true);
            Summary = BaselineMode
                ? "WikiDumpMods: збережено ванільний еталон"
                : "WikiDumpMods: готово" + (_errors.Count > 0 ? " (помилок: " + _errors.Count + ")" : "")
                  + (_restoreWarning ? ". Перезапусти гру: частина перекладів модів зникла" : "");
            _hud(Summary);
            _log(Summary + " in " + _sw.ElapsedMilliseconds + " ms -> " + _opt.OutDir);
        }

        private static string GameVersion()
        {
            foreach (var args in new[] { new object[0], new object[] { false } })
            {
                var v = Refl.CallStatic("Version", "GetVersionString", args) as string;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            var cur = Refl.GetStatic("Version", "CurrentVersion");
            return cur != null ? cur.ToString() : "?";
        }

        // ================================================================ localisation & tooltips

        private void Languages()
        {
            var orig = _loc.Original ?? "English";
            var langs = new List<string> { orig };
            foreach (var l in _opt.Languages) if (!string.IsNullOrEmpty(l) && !langs.Contains(l.Trim())) langs.Add(l.Trim());
            var tokens = AllTokens();
            _loc.Mod = new ModTexts(langs);
            try
            {
                var cfgDir = _opt.ConfigDir;
                _loc.Mod.Collect(_mods, cfgDir, _opt.OutDir);
                _manifest["translationSources"] = _loc.Mod.SourcesRead;
            }
            catch (Exception e) { _errors.Add(new JObj { { "section", "localization.files" }, { "error", e.Message } }); }
            bool switched = false;
            try
            {
                foreach (var l in langs)
                {
                    if (l != orig) { if (!Loc.Switch(l)) { _errors.Add(new JObj { { "section", "localization" }, { "error", "language not available: " + l } }); continue; } switched = true; }
                    _loc.Capture(l);
                    _loc.ResolveTokens(l, tokens);
                    if (_opt.Tooltips) Tooltips(l);
                }
            }
            finally
            {
                if (switched) Loc.Switch(orig);
            }
            // Switching languages must not cost the running game any mod text: re-check every token that resolved before.
            Dictionary<string, string> before;
            if (switched && _loc.Resolved.TryGetValue(orig, out before))
            {
                int checkedN = 0; var lost = new JArr();
                foreach (var kv in before)
                {
                    if (kv.Value == null) continue;
                    checkedN++;
                    if (Loc.GameLocalize(kv.Key) == null && lost.Count < 50) lost.Add(kv.Key);
                }
                _manifest["languageRestore"] = new JObj { { "language", orig }, { "tokensChecked", checkedN }, { "lostAfterRestore", lost } };
                if (lost.Count > 0) _restoreWarning = true;
            }
            _manifest["languages"] = new JArr(); foreach (var l in _loc.Languages) ((JArr)_manifest["languages"]).Add(l);
        }

        private bool _restoreWarning;

        /// <summary>Every display token the export can need: names, descriptions, tooltips of prefabs, items, stations, status effects.</summary>
        private List<string> AllTokens()
        {
            var hs = new HashSet<string>();
            Action<object> add = o => { foreach (var t in _loc.Tokens(o as string)) hs.Add(t); };
            foreach (var go in _r.Prefabs.Values)
            {
                add(DataIndex.DisplayToken(go));
                var sh = Registry.Shared(go);
                add(Refl.Get(sh, "m_name")); add(Refl.Get(sh, "m_description")); add(Refl.Get(sh, "m_setName"));
                var pc = Refl.Comp(go, "Piece");
                add(Refl.Get(pc, "m_name")); add(Refl.Get(pc, "m_description"));
                foreach (var tn in new[] { "Character", "CraftingStation", "Smelter", "CookingStation", "Trader", "OfferingBowl", "Container", "Pickable", "Fermenter", "Incinerator" })
                {
                    var c = Refl.Comp(go, tn);
                    add(Refl.Get(c, "m_name")); add(Refl.Get(c, "m_overrideName"));
                }
            }
            foreach (var se in _r.StatusEffects) { add(Refl.Get(se, "m_name")); add(Refl.Get(se, "m_tooltip")); }
            foreach (var ev in _r.Events) { add(Refl.Get(ev, "m_startMessage")); add(Refl.Get(ev, "m_endMessage")); }
            return hs.ToList();
        }

        /// <summary>The game's own tooltip text for every item quality and every status effect, in the active language.</summary>
        private void Tooltips(string lang)
        {
            var tID = Refl.T("ItemDrop+ItemData");
            MethodInfo tip = tID == null ? null : tID.GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(m => m.Name == "GetTooltip" && m.GetParameters().Length >= 2 && m.GetParameters()[0].ParameterType == tID)
                .OrderBy(m => m.GetParameters().Length).FirstOrDefault();
            foreach (var go in _r.Items.Values)
            {
                if (!InScope("prefab:" + go.name)) continue;
                var data = Registry.ItemData(go);
                if (data == null || tip == null) continue;
                int maxQ = Math.Max(1, Refl.I(Refl.Get(Registry.Shared(go), "m_maxQuality"), 1));
                var per = new JObj();
                for (int q = 1; q <= maxQ; q++)
                {
                    try
                    {
                        var ps = tip.GetParameters();
                        var args = new object[ps.Length];
                        for (int i = 0; i < ps.Length; i++)
                        {
                            var pt = ps[i].ParameterType;
                            if (i == 0) args[i] = data;
                            else if (pt == typeof(int) && ps[i].Name.IndexOf("quality", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = q;
                            else if (pt == typeof(bool)) args[i] = ps[i].Name.IndexOf("craft", StringComparison.OrdinalIgnoreCase) >= 0;
                            else if (pt == typeof(float)) args[i] = 0f;
                            else if (pt == typeof(int)) args[i] = ps[i].IsOptional ? ps[i].DefaultValue : (object)(-1);
                            else args[i] = ps[i].IsOptional ? ps[i].DefaultValue : null;
                        }
                        // GetTooltip returns $tokens; they are localized at write time (game text + mod translation sources)
                        per[q.ToString()] = tip.Invoke(null, args) as string;
                    }
                    catch (Exception e) { per[q.ToString()] = null; if (!_tooltipErr) { _tooltipErr = true; _errors.Add(new JObj { { "section", "tooltips" }, { "error", (e.InnerException ?? e).Message } }); } }
                }
                TipFor("item:" + go.name)[lang] = per;
            }
            foreach (var se in _r.StatusEffects)
            {
                if (!InScope(Origin.KeyOf(se))) continue;
                string s = null;
                try { s = Refl.Call(se, "GetTooltipString") as string; } catch { }
                if (s != null) TipFor("se:" + ((UnityEngine.Object)se).name)[lang] = s;
            }
        }
        private bool _tooltipErr;

        private static string Localize(string s)
        {
            var l = Refl.Singleton("Localization");
            return l == null ? s : (Refl.Call(l, "Localize", s) as string ?? s);
        }

        /// <summary>{lang: text} or {lang: {quality: text}} with every $token replaced for that language.</summary>
        private JObj LocalizedTips(JObj tips)
        {
            var res = new JObj();
            foreach (var kv in tips)
            {
                if (kv.Value is string) { res[kv.Key] = _loc.Text((string)kv.Value, kv.Key) ?? kv.Value; continue; }
                var per = kv.Value as JObj;
                if (per == null) continue;
                var o = new JObj();
                foreach (var q in per) o[q.Key] = q.Value is string ? (_loc.Text((string)q.Value, kv.Key) ?? q.Value) : null;
                res[kv.Key] = o;
            }
            return res;
        }

        private JObj TipFor(string key)
        {
            JObj o;
            if (!_tooltips.TryGetValue(key, out o)) _tooltips[key] = o = new JObj();
            return o;
        }

        // ================================================================ baseline & vanilla diffs

        private Dictionary<string, Dictionary<string, string>> Fingerprints(bool onlyVanilla)
        {
            var fp = new Dictionary<string, Dictionary<string, string>>();
            Action<string, object> add = (k, node) =>
            {
                if (node == null) return;
                var d = new Dictionary<string, string>();
                Ser.Flatten(node, "", d);
                fp[k] = d;
            };
            foreach (var go in _r.Items.Values)
            {
                if (onlyVanilla && _o.IsModKey("prefab:" + go.name)) continue;
                add("item:" + go.name, Ser.Node(Registry.Shared(go), 3));
            }
            foreach (var rc in _r.Recipes)
            {
                var k = Origin.KeyOf(rc);
                if (onlyVanilla && _o.IsModKey(k)) continue;
                add(k, Ser.Node(rc, 2));
            }
            foreach (var se in _r.StatusEffects)
            {
                var k = Origin.KeyOf(se);
                if (onlyVanilla && _o.IsModKey(k)) continue;
                add(k, Ser.Node(se, 2));
            }
            foreach (var go in _r.Prefabs.Values)
            {
                if (onlyVanilla && _o.IsModKey("prefab:" + go.name)) continue;
                foreach (var tn in new[] { "Piece", "Character", "CharacterDrop", "Container", "Pickable", "DropOnDestroyed", "MineRock5", "MineRock",
                                           "TreeBase", "TreeLog", "Trader", "Smelter", "CookingStation", "Fermenter", "Incinerator", "MonsterAI", "Tameable",
                                           "CraftingStation", "StationExtension", "WearNTear", "Plant", "Fish", "OfferingBowl", "SpawnArea" })
                {
                    var c = Refl.Comp(go, tn);
                    if (!c) continue;
                    add(tn + ":" + go.name, Ser.Node(c, tn == "Character" || tn == "MonsterAI" ? 2 : 3));
                }
            }
            return fp;
        }

        private void WriteBaseline()
        {
            var b = new Baseline { GameVersion = GameVersion(), Created = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"), Source = "WikiDumpMods " + _opt.PluginVersion + " vanilla run" };
            foreach (var go in _r.Prefabs.Values) b.Keys.Add("prefab:" + go.name);
            foreach (var rc in _r.Recipes) b.Keys.Add(Origin.KeyOf(rc));
            foreach (var se in _r.StatusEffects) b.Keys.Add(Origin.KeyOf(se));
            foreach (var zl in _r.Locations) { var n = Refl.S(Refl.Get(zl, "m_prefabName")); if (n != null) b.Keys.Add("loc:" + n); }
            foreach (var ev in _r.Events) { var n = Refl.S(Refl.Get(ev, "m_name")); if (n != null) b.Keys.Add("event:" + n); }
            foreach (var kv in Fingerprints(false)) b.Fp[kv.Key] = kv.Value;
            b.Save(_opt.BaselinePath);
            _manifest["baselineWritten"] = new JObj { { "path", _opt.BaselinePath }, { "keys", b.Keys.Count }, { "fingerprints", b.Fp.Count } };
        }

        private void WriteVanillaChanges()
        {
            var res = new JObj();
            if (_o.Baseline == null) { res["note"] = "No vanilla baseline: run once with only WikiDumpMods installed to enable this file (see README)."; Json.WriteFile(P("vanilla_changes.json"), res, true); return; }
            res["baseline"] = new JObj { { "path", _o.Baseline.Path }, { "source", _o.Baseline.Source }, { "gameVersion", _o.Baseline.GameVersion }, { "partial", _o.Baseline.Partial } };
            if (_o.Baseline.GameVersion != GameVersion()) res["warning"] = "baseline game version " + _o.Baseline.GameVersion + " differs from " + GameVersion() + ": some changes may come from the game update, not from mods";
            var now = Fingerprints(true);
            var changes = new JArr();
            foreach (var kv in _o.Baseline.EachFingerprint())
            {
                Dictionary<string, string> cur;
                if (kv.Key == null || !now.TryGetValue(kv.Key, out cur)) continue;
                var diff = Diff(kv.Value, cur);
                if (diff.Count == 0) continue;
                var name = kv.Key.Substring(kv.Key.IndexOf(':') + 1);
                GameObject go; _r.Prefabs.TryGetValue(name, out go);
                var o = new JObj { { "key", kv.Key } };
                if (go) o.Opt("names", _loc.Names(DataIndex.DisplayToken(go)));
                o["changes"] = diff;
                changes.Add(o);
            }
            res["note"] = "Values that differ from the unmodded game. Which mod made a change cannot be read from the data; see mods.json -> harmonyPatches for candidates. Fields one side does not record are not compared.";
            res["changed"] = changes;
            Json.WriteFile(P("vanilla_changes.json"), res, false);
            _manifest.Obj("counts")["vanillaChanged"] = changes.Count;
        }

        /// <summary>
        /// Field-level diff tolerant of format differences between dumps: references compare by object name
        /// ({"$go":"X"} == "X"), numbers numerically, and a path missing on one side is only reported when it is
        /// a list element beyond the other side's list length (a drop / requirement added or removed).
        /// </summary>
        private static JArr Diff(Dictionary<string, string> old, Dictionary<string, string> cur)
        {
            var d = new JArr();
            foreach (var p in cur)
            {
                string ov;
                if (old.TryGetValue(p.Key, out ov))
                {
                    if (p.Key.EndsWith(".#")) { if (ov != p.Value) d.Add(new JObj { { "path", p.Key }, { "vanilla", ov }, { "now", p.Value } }); continue; }
                    var a = NormVal(ov); var b = NormVal(p.Value);
                    if (a == null || b == null || Same(a, b)) continue;
                    d.Add(new JObj { { "path", p.Key }, { "vanilla", Show(a) }, { "now", Show(b) } });
                }
                else if (BeyondList(p.Key, old)) { var b = NormVal(p.Value); if (b != null) d.Add(new JObj { { "path", p.Key }, { "vanilla", null }, { "now", Show(b) } }); }
            }
            foreach (var p in old)
                if (!cur.ContainsKey(p.Key) && BeyondList(p.Key, cur)) { var a = NormVal(p.Value); if (a != null) d.Add(new JObj { { "path", p.Key }, { "vanilla", Show(a) }, { "now", null } }); }
            return d;
        }

        /// <summary>True when the path addresses list[i] and the other side's list has i or fewer elements.</summary>
        private static bool BeyondList(string path, Dictionary<string, string> other)
        {
            int open = path.LastIndexOf('[');
            while (open > 0)
            {
                int close = path.IndexOf(']', open);
                int i;
                if (close > open && int.TryParse(path.Substring(open + 1, close - open - 1), out i))
                {
                    string cnt;
                    if (other.TryGetValue(path.Substring(0, open) + ".#", out cnt)) { int c; return int.TryParse(cnt, out c) && i >= c; }
                }
                open = path.LastIndexOf('[', open - 1);
            }
            return false;
        }

        /// <summary>"n:1.5" | "s:text" | "b:true" | "null"; null = not comparable (depth/cycle markers).</summary>
        private static string NormVal(string v)
        {
            if (v == null || v == "null") return "null";
            object x;
            try { x = JsonParse.Parse(v); } catch { return "s:" + v; }
            var o = x as JObj;
            if (o != null)
            {
                foreach (var k in new[] { "$go", "$sprite" }) if (o[k] is string) return "s:" + o[k];
                if (o.Has("$comp")) return "s:" + o["go"];
                if (o.Has("$so") || o.Has("$asset")) return "s:" + o["name"];
                return null;
            }
            if (x is double) return "n:" + ((double)x).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (x is bool) return "b:" + ((bool)x ? "true" : "false");
            var s = x as string;
            if (s == null) return "null";
            if (s.StartsWith("$depth") || s.StartsWith("$cycle") || s == "<cycle>" || s.StartsWith("$error")) return null;
            return "s:" + s;
        }

        private static bool Same(string a, string b)
        {
            if (a == b) return true;
            if (a.StartsWith("n:") && b.StartsWith("n:"))
            {
                double x = double.Parse(a.Substring(2), System.Globalization.CultureInfo.InvariantCulture), y = double.Parse(b.Substring(2), System.Globalization.CultureInfo.InvariantCulture);
                return Math.Abs(x - y) <= 1e-4 * Math.Max(1, Math.Abs(x));
            }
            return false;
        }

        private static object Show(string n)
        {
            if (n == "null") return null;
            if (n.StartsWith("n:")) return double.Parse(n.Substring(2), System.Globalization.CultureInfo.InvariantCulture);
            if (n.StartsWith("b:")) return n == "b:true";
            return n.Substring(2);
        }

        // ================================================================ locations

        private readonly JObj _locDiag = new JObj();
        private int _locLoaded, _locFailed;

        private JObj Location(object holder, bool room)
        {
            string name = Refl.S(Refl.Get(holder, "m_prefabName"));
            var release = new List<object>();
            try
            {
                GameObject go = null;
                if (name == null || room) { go = Registry.LoadSoft(holder, release, _locDiag); name = name ?? (go ? go.name : null); }
                if (name == null) return null;
                var key = (room ? "room:" : "loc:") + name;
                bool modded = _o.IsModKey(key);
                if (!modded && !_opt.ScanVanillaLocations && !_opt.IncludeVanilla) return null;
                if (!go) go = Registry.LoadSoft(holder, release, _locDiag);
                if (go) _locLoaded++; else _locFailed++;
                if (go) _o.Inspect(go);
                if (go && _o.IsMod(go)) modded = true;
                var where = new JObj { { "location", name } };
                var found = go ? _ix.ScanContainer(go, where) : new JArr();
                var involving = found.Cast<JObj>().Where(f => (f["item"] is string && _o.IsModKey("prefab:" + f["item"])) || (f["creature"] is string && _o.IsModKey("prefab:" + f["creature"]))).ToList();
                if (!modded && !_opt.IncludeVanilla && involving.Count == 0) return null;
                var o = new JObj();
                o["name"] = name;
                if (modded) o["mod"] = ModOfKey(key) ?? _o.ModOf(go) ?? "unknown";
                var meta = Ser.Node(holder, 2) as JObj;
                if (meta != null) { meta.Remove("m_prefab"); o["meta"] = meta; }
                if (go)
                {
                    var locComp = Refl.Comp(go, "Location");
                    if (locComp) o["location"] = Ser.Node(locComp, 2);
                }
                var contents = new JArr();
                foreach (var f in (modded || _opt.IncludeVanilla) ? found.Cast<JObj>() : involving) contents.Add(f);
                o["contents"] = contents;
                if (modded) Content(ModOfKey(key) ?? _o.ModOf(go) ?? "unknown").Arr(room ? "dungeonRooms" : "locations").Add(name);
                return o;
            }
            finally
            {
                foreach (var r in release) { try { Refl.Call(r, "Release"); } catch { } }
            }
        }

        // ================================================================ icons

        private void Icon(string key, string mod, string kind, string name, object sprite, int variant)
        {
            if (sprite == null) return;
            var rel = "icons/" + Icons.SafeName(mod ?? "vanilla") + "/" + kind + "/" + Icons.SafeName(name) + (variant > 0 ? "_" + variant : "") + ".png";
            var saved = _icons.Save(sprite, _opt.OutDir, rel);
            if (saved == null) return;
            JArr a;
            if (!_iconFiles.TryGetValue(key, out a)) _iconFiles[key] = a = new JArr();
            a.Add(saved);
        }

        private void ExportIcons()
        {
            if (!_icons.Ready) { _errors.Add(new JObj { { "section", "icons" }, { "error", "Unity texture API not found" } }); return; }
            foreach (var go in _r.Items.Values)
            {
                var k = "prefab:" + go.name;
                if (!InScope(k)) continue;
                int i = 0;
                foreach (var sp in Refl.Enum(Refl.Get(Registry.Shared(go), "m_icons"))) Icon(k, ModOfKey(k), "items", go.name, sp, i++);
            }
            foreach (var go in _r.Prefabs.Values)
            {
                var k = "prefab:" + go.name;
                if (!InScope(k)) continue;
                var pc = Refl.Comp(go, "Piece");
                if (pc && !_r.Items.ContainsKey(go.name)) Icon(k, ModOfKey(k), "pieces", go.name, Refl.Get(pc, "m_icon"), 0);
                var cs = Refl.Comp(go, "CraftingStation");
                if (cs && !pc) Icon(k, ModOfKey(k), "stations", go.name, Refl.Get(cs, "m_icon"), 0);
            }
            foreach (var se in _r.StatusEffects)
            {
                var k = Origin.KeyOf(se);
                if (!InScope(k)) continue;
                Icon(k, ModOfKey(k), "status", ((UnityEngine.Object)se).name, Refl.Get(se, "m_icon"), 0);
            }
            // skills (vanilla Skills list on the player prefab + whatever mods injected there)
            GameObject player;
            if (_r.Prefabs.TryGetValue("Player", out player))
                foreach (var sd in Refl.Enum(Refl.Get(Refl.Comp(player, "Skills"), "m_skills")))
                {
                    var st = Refl.S(Refl.Get(sd, "m_skill"));
                    if (st == null || (!_opt.IncludeVanilla && IsVanillaSkill(st))) continue;
                    Icon("skill:" + st, null, "skills", st, Refl.Get(sd, "m_icon"), 0);
                }
            // Thunderstore icon of every mod
            foreach (var m in _mods)
            {
                if (m.Folder == null) continue;
                var png = Path.Combine(m.Folder, "icon.png");
                if (!File.Exists(png)) continue;
                var rel = "icons/" + Icons.SafeName(m.Guid) + "/mod_icon.png";
                try { Directory.CreateDirectory(Path.GetDirectoryName(P(rel))); File.Copy(png, P(rel), true); _iconFiles["mod:" + m.Guid] = new JArr { rel }; } catch { }
            }
        }

        private static bool IsVanillaSkill(string s)
        {
            var t = Refl.T("Skills+SkillType");
            return t != null && t.IsEnum && Enum.GetNames(t).Contains(s);
        }

        private JArr IconsOf(string key) { JArr a; return _iconFiles.TryGetValue(key, out a) ? a : null; }

        // ================================================================ items

        private static readonly string[] PerLevel =
        {
            "m_armor|m_armorPerLevel", "m_maxDurability|m_durabilityPerLevel", "m_blockPower|m_blockPowerPerLevel",
            "m_deflectionForce|m_deflectionForcePerLevel"
        };

        private JArr QualityStats(object shared)
        {
            var a = new JArr();
            int maxQ = Math.Max(1, Refl.I(Refl.Get(shared, "m_maxQuality"), 1));
            var dmg = Refl.Get(shared, "m_damages"); var dmgL = Refl.Get(shared, "m_damagesPerLevel");
            for (int q = 1; q <= maxQ; q++)
            {
                var o = new JObj { { "quality", q } };
                if (dmg != null)
                {
                    var d = new JObj();
                    foreach (var f in dmg.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (f.FieldType != typeof(float)) continue;
                        var v = (float)f.GetValue(dmg) + (dmgL == null ? 0f : (float)f.GetValue(dmgL)) * (q - 1);
                        if (Math.Abs(v) > 1e-6) d[f.Name.Substring(2)] = v;
                    }
                    o.Opt("damage", d);
                }
                foreach (var pl in PerLevel)
                {
                    var parts = pl.Split('|');
                    var b = Refl.Get(shared, parts[0]);
                    if (b == null) continue;
                    var v = Refl.F(b) + Refl.F(Refl.Get(shared, parts[1])) * (q - 1);
                    if (Math.Abs(v) > 1e-6) o[parts[0].Substring(2)] = v;
                }
                a.Add(o);
            }
            return a;
        }

        /// <summary>Recipe sources expanded per quality: what each upgrade step costs and which station level it needs.</summary>
        private JArr RecipesFor(string item, int maxQ)
        {
            var res = new JArr();
            JArr src;
            if (!_ix.Sources.TryGetValue(item, out src)) return res;
            foreach (JObj s in src)
            {
                if ((string)s["type"] != "craft") continue;
                var r = new JObj(); foreach (var kv in s) r[kv.Key] = kv.Value;
                int minLvl = Math.Max(1, Refl.I(s["minStationLevel"], 1));
                var levels = new JArr();
                for (int q = 1; q <= maxQ; q++)
                {
                    var lv = new JObj { { "quality", q }, { "stationLevel", minLvl + q - 1 } };
                    var cost = new JArr();
                    foreach (JObj req in (JArr)s["requirements"])
                    {
                        int amt = q <= 1 ? Refl.I(req["amount"]) : (q - 1) * Refl.I(req["amountPerLevel"]);
                        if (amt <= 0) continue;
                        cost.Add(new JObj { { "prefab", req["prefab"] }, { "name", req["name"] }, { "amount", amt } });
                    }
                    lv["cost"] = cost;
                    levels.Add(lv);
                }
                r["perQuality"] = levels;
                res.Add(r);
            }
            return res;
        }

        private JArr SourcesExceptCraft(string item)
        {
            var res = new JArr();
            JArr src;
            if (_ix.Sources.TryGetValue(item, out src)) foreach (JObj s in src) if ((string)s["type"] != "craft") res.Add(s);
            return res;
        }

        private JArr Of(Dictionary<string, JArr> d, string k) { JArr a; return d.TryGetValue(k, out a) ? a : new JArr(); }

        private void WriteItems()
        {
            var all = new JArr();
            var sets = new Dictionary<string, List<GameObject>>();
            foreach (var go in _r.Items.Values)
            {
                var sn = Refl.S(Refl.Get(Registry.Shared(go), "m_setName"));
                if (string.IsNullOrEmpty(sn)) continue;
                List<GameObject> l; if (!sets.TryGetValue(sn, out l)) sets[sn] = l = new List<GameObject>(); l.Add(go);
            }
            foreach (var go in _r.Items.Values)
            {
                var k = "prefab:" + go.name;
                if (!InScope(k)) continue;
                var sh = Registry.Shared(go);
                var token = Refl.S(Refl.Get(sh, "m_name"));
                var o = new JObj();
                o["prefab"] = go.name;
                o["mod"] = ModOfKey(k);
                o["modName"] = _o.ModName(ModOfKey(k));
                o["names"] = _loc.Names(token);
                o.Opt("description", _loc.Names(Refl.S(Refl.Get(sh, "m_description"))));
                o["itemType"] = Refl.S(Refl.Get(sh, "m_itemType"));
                o.Opt("icons", IconsOf(k));
                int maxQ = Math.Max(1, Refl.I(Refl.Get(sh, "m_maxQuality"), 1));
                o["maxQuality"] = maxQ;
                o["perQuality"] = QualityStats(sh);
                o.Opt("recipes", RecipesFor(go.name, maxQ));
                o.Opt("obtain", SourcesExceptCraft(go.name));
                o.Opt("usedIn", Of(_ix.Uses, go.name));
                var sn = Refl.S(Refl.Get(sh, "m_setName"));
                if (!string.IsNullOrEmpty(sn))
                {
                    var set = new JObj { { "name", sn }, { "size", Refl.I(Refl.Get(sh, "m_setSize")) } };
                    set.Opt("effect", Refl.UName(Refl.Get(sh, "m_setStatusEffect")));
                    var mem = new JArr(); foreach (var m in sets[sn]) mem.Add(_ix.Who(m)); set["members"] = mem;
                    o["set"] = set;
                }
                var ses = new JObj();
                foreach (var f in new[] { "m_consumeStatusEffect", "m_equipStatusEffect", "m_setStatusEffect", "m_attackStatusEffect" })
                    ses.Opt(f.Substring(2), Refl.UName(Refl.Get(sh, f)));
                o.Opt("statusEffects", ses);
                JObj tips; if (_tooltips.TryGetValue("item:" + go.name, out tips)) o["tooltips"] = LocalizedTips(tips);
                o.Opt("referencedBy", Of(_ix.Refs, k));
                o["origin"] = _o.Get(k).ToJson();
                o["shared"] = Ser.Node(sh, 5);
                var other = new JObj();
                foreach (var c in go.GetComponents(typeof(MonoBehaviour)))
                {
                    var tn = c.GetType().Name;
                    if (tn == "ItemDrop" || tn == "ZNetView" || tn == "ZSyncTransform" || c.GetType().Assembly.GetName().Name.StartsWith("Unity")) continue;
                    other[tn] = Ser.Node(c, 3);
                }
                o.Opt("components", other);
                Track(k, "items", go.name, token);
                all.Add(o);
            }
            Json.WriteFile(P("items.json"), all, false);
            _manifest.Obj("counts")["items"] = all.Count;
        }

        // ================================================================ pieces & stations

        private void WritePieces()
        {
            var all = new JArr();
            var recipesAt = new Dictionary<string, JArr>();
            foreach (var kv in _ix.Sources)
                foreach (JObj s in kv.Value)
                {
                    if ((string)s["type"] != "craft") continue;
                    var st = s["station"] as JObj;
                    var sp = st == null ? null : st["prefab"] as string;
                    if (sp == null) continue;
                    JArr l; if (!recipesAt.TryGetValue(sp, out l)) recipesAt[sp] = l = new JArr();
                    l.Add(new JObj { { "item", kv.Key }, { "recipe", s["recipe"] }, { "minStationLevel", s["minStationLevel"] } });
                }
            var extensions = new Dictionary<string, JArr>();
            foreach (var go in _r.Prefabs.Values)
            {
                var ext = Refl.Comp(go, "StationExtension");
                var st = Refl.UName(Refl.Get(ext, "m_craftingStation"));
                if (st == null) continue;
                JArr l; if (!extensions.TryGetValue(st, out l)) extensions[st] = l = new JArr();
                l.Add(_ix.Who(go));
            }
            foreach (var go in _r.Prefabs.Values)
            {
                var piece = Refl.Comp(go, "Piece");
                if (!piece || _r.Items.ContainsKey(go.name)) continue;
                var k = "prefab:" + go.name;
                bool usesModded = Refl.Enum(Refl.Get(piece, "m_resources")).Any(q => _o.IsMod(Refl.Get(q, "m_resItem")));
                bool station = recipesAt.ContainsKey(go.name) && recipesAt[go.name].Cast<JObj>().Any(x => _o.IsModKey("prefab:" + x["item"]));
                if (!InScope(k) && !usesModded && !station) continue;
                var token = Refl.S(Refl.Get(piece, "m_name"));
                var o = new JObj();
                o["prefab"] = go.name;
                o["mod"] = ModOfKey(k);
                o["modName"] = _o.ModName(ModOfKey(k));
                if (!InScope(k)) o["vanillaWithModdedLinks"] = true;
                o["names"] = _loc.Names(token);
                o.Opt("description", _loc.Names(Refl.S(Refl.Get(piece, "m_description"))));
                o.Opt("icons", IconsOf(k));
                List<string> tools; if (_r.PieceTools.TryGetValue(go.name, out tools)) { var t = new JArr(); foreach (var x in tools) t.Add(_ix.Who(_r.Prefabs[x])); o["builtWith"] = t; }
                o["category"] = Refl.S(Refl.Get(piece, "m_category"));
                o.Opt("station", _ix.Station(Refl.Get(piece, "m_craftingStation")));
                o["requirements"] = DataIndex.Requirements(Refl.Get(piece, "m_resources"), _ix.Who);
                o["comfort"] = Refl.I(Refl.Get(piece, "m_comfort"));
                o["comfortGroup"] = Refl.S(Refl.Get(piece, "m_comfortGroup"));
                JArr ra; if (recipesAt.TryGetValue(go.name, out ra)) o["craftsHere"] = ra;
                JArr ea; if (extensions.TryGetValue(go.name, out ea)) o["extensions"] = ea;
                o.Opt("obtain", SourcesExceptCraft(go.name));
                o.Opt("referencedBy", Of(_ix.Refs, k));
                o["origin"] = _o.Get(k).ToJson();
                var comps = new JObj();
                foreach (var c in go.GetComponents(typeof(MonoBehaviour)))
                {
                    var tn = c.GetType().Name;
                    if (tn == "ZNetView" || c.GetType().Assembly.GetName().Name.StartsWith("Unity")) continue;
                    comps[tn] = Ser.Node(c, 3);
                }
                o["components"] = comps;
                if (InScope(k)) Track(k, "pieces", go.name, token);
                all.Add(o);
            }
            Json.WriteFile(P("pieces.json"), all, false);
            _manifest.Obj("counts")["pieces"] = all.Count;
        }

        // ================================================================ creatures

        private void WriteCreatures()
        {
            var all = new JArr();
            foreach (var go in _r.Prefabs.Values)
            {
                var ch = Refl.Comp(go, "Character");
                if (!ch) continue;
                var k = "prefab:" + go.name;
                bool dropsModded = Refl.Enum(Refl.Get(Refl.Comp(go, "CharacterDrop"), "m_drops")).Any(d => _o.IsMod(Refl.Get(d, "m_prefab")));
                bool eatsModded = Refl.Enum(Refl.Get(Refl.Comp(go, "MonsterAI"), "m_consumeItems")).Any(i => _o.IsMod(i));
                if (!InScope(k) && !dropsModded && !eatsModded) continue;
                if (go.name == "Player") continue;
                var token = Refl.S(Refl.Get(ch, "m_name"));
                var o = new JObj();
                o["prefab"] = go.name;
                o["mod"] = ModOfKey(k);
                o["modName"] = _o.ModName(ModOfKey(k));
                if (!InScope(k)) o["vanillaWithModdedLinks"] = true;
                o["names"] = _loc.Names(token);
                o["health"] = Refl.F(Refl.Get(ch, "m_health"));
                o["faction"] = Refl.S(Refl.Get(ch, "m_faction"));
                o["boss"] = Refl.B(Refl.Get(ch, "m_boss"));
                o.Opt("bossEvent", Refl.S(Refl.Get(ch, "m_bossEvent")));
                o.Opt("defeatSetGlobalKey", Refl.S(Refl.Get(ch, "m_defeatSetGlobalKey")));
                o["damageModifiers"] = Ser.Node(Refl.Get(ch, "m_damageModifiers"), 2);
                var hum = Refl.Comp(go, "Humanoid");
                if (hum)
                {
                    var atk = new JArr();
                    foreach (var f in new[] { "m_defaultItems", "m_randomWeapon", "m_randomArmor", "m_randomShield" })
                        foreach (var it in Refl.Enum(Refl.Get(hum, f)))
                        {
                            var ig = it as GameObject;
                            if (!ig) continue;
                            var sh = Registry.Shared(ig);
                            var a = new JObj { { "slot", f.Substring(2) }, { "prefab", ig.name } };
                            a.Opt("names", _loc.Names(Refl.S(Refl.Get(sh, "m_name"))));
                            a["itemType"] = Refl.S(Refl.Get(sh, "m_itemType"));
                            a.Opt("damage", ((JArr)QualityStats(sh))[0]);
                            a["attack"] = Ser.Node(Refl.Get(sh, "m_attack"), 2);
                            atk.Add(a);
                        }
                    foreach (var set in Refl.Enum(Refl.Get(hum, "m_randomSets")))
                    {
                        var s = new JArr(); foreach (var it in Refl.Enum(Refl.Get(set, "m_items"))) s.Add(Refl.UName(it));
                        atk.Add(new JObj { { "slot", "randomSet" }, { "items", s } });
                    }
                    o.Opt("equipmentAndAttacks", atk);
                }
                var drops = new JArr();
                foreach (var kv in _ix.Sources)
                    foreach (JObj s in kv.Value)
                        if ((string)s["type"] == "creatureDrop" && s["from"] is JObj && (string)((JObj)s["from"])["prefab"] == go.name)
                        {
                            var d = new JObj { { "item", kv.Key } };
                            GameObject ig; if (_r.Items.TryGetValue(kv.Key, out ig)) d["names"] = _loc.Names(Refl.S(Refl.Get(Registry.Shared(ig), "m_name")));
                            foreach (var p in s) if (p.Key != "type" && p.Key != "from" && p.Key != "component") d[p.Key] = p.Value;
                            drops.Add(d);
                        }
                o.Opt("drops", drops);
                o.Opt("spawns", Of(_ix.Spawns, go.name));
                foreach (var tn in new[] { "MonsterAI", "AnimalAI", "Tameable", "Procreation", "Growup", "Saddle" })
                {
                    var c = Refl.Comp(go, tn);
                    if (c) o[tn] = Ser.Node(c, 2);
                }
                var food = Refl.Enum(Refl.Get(Refl.Comp(go, "MonsterAI"), "m_consumeItems")).Select(x => (object)_ix.Who(x)).ToList();
                if (food.Count > 0) { var fa = new JArr(); fa.AddRange(food); o["eats"] = fa; }
                o.Opt("referencedBy", Of(_ix.Refs, k));
                o["origin"] = _o.Get(k).ToJson();
                o["character"] = Ser.Node(ch, 3);
                if (InScope(k)) Track(k, "creatures", go.name, token);
                all.Add(o);
            }
            Json.WriteFile(P("creatures.json"), all, false);
            _manifest.Obj("counts")["creatures"] = all.Count;
        }

        // ================================================================ status effects

        private void WriteStatusEffects()
        {
            var users = new Dictionary<string, JArr>();
            foreach (var go in _r.Items.Values)
            {
                var sh = Registry.Shared(go);
                foreach (var f in new[] { "m_consumeStatusEffect", "m_equipStatusEffect", "m_setStatusEffect", "m_attackStatusEffect" })
                {
                    var n = Refl.UName(Refl.Get(sh, f));
                    if (n == null) continue;
                    JArr l; if (!users.TryGetValue(n, out l)) users[n] = l = new JArr();
                    var w = _ix.Who(go); w["as"] = f.Substring(2); l.Add(w);
                }
            }
            var all = new JArr();
            foreach (var se in _r.StatusEffects)
            {
                var k = Origin.KeyOf(se);
                var name = ((UnityEngine.Object)se).name;
                if (!InScope(k) && !(users.ContainsKey(name) && users[name].Cast<JObj>().Any(u => u["mod"] != null))) continue;
                var token = Refl.S(Refl.Get(se, "m_name"));
                var o = new JObj();
                o["name"] = name;
                o["type"] = se.GetType().Name;
                o["mod"] = ModOfKey(k);
                o["modName"] = _o.ModName(ModOfKey(k));
                o.Opt("names", _loc.Names(token));
                o.Opt("tooltipText", _loc.Names(Refl.S(Refl.Get(se, "m_tooltip"))));
                JObj tips; if (_tooltips.TryGetValue(k, out tips)) o["tooltips"] = LocalizedTips(tips);
                o.Opt("icons", IconsOf(k));
                JArr u2; if (users.TryGetValue(name, out u2)) o["usedBy"] = u2;
                o.Opt("referencedBy", Of(_ix.Refs, k));
                o["origin"] = _o.Get(k).ToJson();
                o["fields"] = Ser.Node(se, 3);
                if (InScope(k)) Track(k, "statusEffects", name, token);
                all.Add(o);
            }
            Json.WriteFile(P("status_effects.json"), all, false);
            _manifest.Obj("counts")["statusEffects"] = all.Count;
        }

        // ================================================================ recipes

        private void WriteRecipes()
        {
            var all = new JArr();
            foreach (var rc in _r.Recipes)
            {
                var k = Origin.KeyOf(rc);
                var item = Refl.Get(rc, "m_item");
                bool touches = InScope(k) || _o.IsMod(item) || _o.IsMod(Refl.Get(rc, "m_craftingStation"))
                               || Refl.Enum(Refl.Get(rc, "m_resources")).Any(q => _o.IsMod(Refl.Get(q, "m_resItem")));
                if (!touches) continue;
                var o = new JObj();
                o["name"] = ((UnityEngine.Object)rc).name;
                o["mod"] = ModOfKey(k);
                o["modName"] = _o.ModName(ModOfKey(k));
                o["result"] = item == null ? null : _ix.Who(item);
                o["amount"] = Refl.I(Refl.Get(rc, "m_amount"), 1);
                o["enabled"] = Refl.B(Refl.Get(rc, "m_enabled"));
                o.Opt("station", _ix.Station(Refl.Get(rc, "m_craftingStation")));
                o.Opt("repairStation", _ix.Station(Refl.Get(rc, "m_repairStation")));
                o["minStationLevel"] = Refl.I(Refl.Get(rc, "m_minStationLevel"), 1);
                o["requireOnlyOneIngredient"] = Refl.B(Refl.Get(rc, "m_requireOnlyOneIngredient"));
                o["requirements"] = DataIndex.Requirements(Refl.Get(rc, "m_resources"), _ix.Who);
                o["origin"] = _o.Get(k).ToJson();
                o["fields"] = Ser.Node(rc, 2);
                if (InScope(k)) Track(k, "recipes", o["name"] as string, null);
                all.Add(o);
            }
            Json.WriteFile(P("recipes.json"), all, false);
            _manifest.Obj("counts")["recipes"] = all.Count;
        }

        // ================================================================ world: spawns, events, locations, rooms

        private void WriteWorld(JArr locations, JArr rooms)
        {
            var w = new JObj();
            var spawns = new JObj();
            foreach (var kv in _ix.Spawns)
            {
                var k = "prefab:" + kv.Key;
                if (!InScope(k)) continue;
                spawns[kv.Key] = kv.Value;
            }
            w["spawnsByCreature"] = spawns;
            var events = new JArr();
            foreach (var ev in _r.Events)
            {
                var name = Refl.S(Refl.Get(ev, "m_name"));
                var k = "event:" + name;
                bool modded = _o.IsModKey(k);
                bool involves = Refl.Enum(Refl.Get(ev, "m_spawn")).Any(sd => _o.IsMod(Refl.Get(sd, "m_prefab")));
                if (!modded && !involves && !_opt.IncludeVanilla) continue;
                var o = new JObj { { "name", name }, { "modded", modded }, { "involvesModdedCreatures", involves } };
                o.Opt("startMessage", _loc.Names(Refl.S(Refl.Get(ev, "m_startMessage"))));
                o.Opt("endMessage", _loc.Names(Refl.S(Refl.Get(ev, "m_endMessage"))));
                o["fields"] = Ser.Node(ev, 3);
                events.Add(o);
            }
            w["events"] = events;
            w["locations"] = locations;
            w["dungeonRooms"] = rooms;
            Json.WriteFile(P("world.json"), w, false);
            _manifest.Obj("counts")["locations"] = locations.Count;
            _manifest.Obj("counts")["dungeonRooms"] = rooms.Count;
            _manifest.Obj("counts")["events"] = events.Count;
        }

        // ================================================================ vanilla items touched by mods

        private void WriteVanillaLinks()
        {
            var all = new JArr();
            foreach (var go in _r.Items.Values)
            {
                var k = "prefab:" + go.name;
                if (_o.IsModKey(k)) continue;
                var src = Of(_ix.Sources, go.name).Cast<JObj>().Where(ModRecord).ToList();
                var use = Of(_ix.Uses, go.name).Cast<JObj>().Where(ModRecord).ToList();
                if (src.Count == 0 && use.Count == 0) continue;
                var o = new JObj { { "prefab", go.name }, { "names", _loc.Names(Refl.S(Refl.Get(Registry.Shared(go), "m_name"))) } };
                var a = new JArr(); a.AddRange(src.Cast<object>()); o.Opt("newWaysToObtain", a);
                var b = new JArr(); b.AddRange(use.Cast<object>()); o.Opt("newUses", b);
                all.Add(o);
            }
            Json.WriteFile(P("vanilla_items_linked.json"), all, false);
        }

        /// <summary>True when a source/use record involves modded content anywhere inside it.</summary>
        private static bool ModRecord(JObj r)
        {
            if (r["mod"] != null) return true;
            foreach (var kv in r)
            {
                var o = kv.Value as JObj;
                if (o != null && (o["mod"] != null || ModRecord(o))) return true;
                var a = kv.Value as JArr;
                if (a != null) foreach (var x in a) if (x is JObj && ModRecord((JObj)x)) return true;
            }
            return false;
        }

        private void WriteReferences()
        {
            var o = new JObj();
            foreach (var kv in _ix.Refs) o[kv.Key] = kv.Value;
            Json.WriteFile(P("references.json"), o, false);
        }

        // ================================================================ raw component dump, per mod

        private void WriteRaw()
        {
            var byMod = new Dictionary<string, JObj>();
            foreach (var go in _r.Prefabs.Values)
            {
                var k = "prefab:" + go.name;
                if (!_o.IsModKey(k)) continue;
                var mod = ModOfKey(k);
                JObj m; if (!byMod.TryGetValue(mod, out m)) byMod[mod] = m = new JObj();
                var comps = new JObj();
                foreach (var c in go.GetComponentsInChildren(typeof(MonoBehaviour), true))
                {
                    if (!c || c.GetType().Assembly.GetName().Name.StartsWith("Unity")) continue;
                    var key = c.GetType().Name + (c.gameObject == go ? "" : "@" + c.gameObject.name);
                    int i = 2; var baseKey = key; while (comps.Has(key)) key = baseKey + "#" + i++;
                    comps[key] = Ser.Node(c, _opt.RawDepth);
                }
                m[go.name] = comps;
            }
            foreach (var kv in byMod) Json.WriteFile(P("raw", Icons.SafeName(kv.Key) + ".json"), kv.Value, false);
        }

        // ================================================================ localisation files

        private void WriteLocalization()
        {
            foreach (var kv in _loc.Tables)
            {
                var o = new JObj();
                foreach (var t in kv.Value.OrderBy(x => x.Key, StringComparer.Ordinal)) o[t.Key] = t.Value;
                Json.WriteFile(P("localization", kv.Key + ".json"), o, false);
            }
            var missing = new JObj();
            var first = _loc.Languages.Contains("English") ? "English" : _loc.Languages.FirstOrDefault();
            foreach (var kv in _modTokens)
            {
                var m = new JObj();
                foreach (var lang in _loc.Languages)
                {
                    var none = new JArr(); var same = new JArr();
                    foreach (var t in kv.Value.OrderBy(x => x))
                    {
                        var v = _loc.Text("$" + t, lang);
                        if (v == null) none.Add(t);
                        else if (lang != first && v == _loc.Text("$" + t, first)) same.Add(t);
                    }
                    var lo = new JObj(); lo.Opt("noText", none); lo.Opt("sameAsEnglish", same);
                    m.Opt(lang, lo);
                }
                missing.Opt(kv.Key, m);
                var own = new JObj();
                foreach (var t in kv.Value.OrderBy(x => x))
                {
                    var e = new JObj(); var src = new JObj();
                    foreach (var lang in _loc.Languages) { string s; e[lang] = _loc.Word(t, lang, out s); src[lang] = s; }
                    e["source"] = src;
                    own[t] = e;
                }
                Json.WriteFile(P("localization", "mod_" + Icons.SafeName(kv.Key) + ".json"), own, false);
            }
            Json.WriteFile(P("localization", "missing_translations.json"), missing, true);
        }

        // ================================================================ origin & mods

        private void WriteOrigin(JObj jotunn, JObj statics)
        {
            var o = new JObj();
            o["note"] = "status: mod | vanilla_replaced. Signals: jotunn/manager = registered by that mod (exact); bundle = asset ships in that mod's AssetBundle; runtime-clone = created at runtime (owner may be unknown); not-in-vanilla-baseline = absent from the unmodded game; static = owner hint only.";
            o["jotunn"] = jotunn;
            o["managerLibraries"] = statics;
            o["bundles"] = _o.Bundles;
            var ent = new JObj();
            foreach (var i in _o.All().Where(x => x.IsMod).OrderBy(x => x.Key, StringComparer.Ordinal)) ent[i.Key] = i.ToJson();
            o["modded"] = ent;
            var dup = new JArr(); dup.AddRange(_r.DuplicatePrefabs.Cast<object>()); o.Opt("duplicatePrefabNames", dup);
            Json.WriteFile(P("origin.json"), o, false);
        }

        private void WriteMods()
        {
            var patches = HarmonyOwners();
            var all = new JArr();
            foreach (var m in _mods.OrderBy(x => x.Name))
            {
                var o = new JObj();
                o["guid"] = m.Guid; o["name"] = m.Name; o["version"] = m.Version;
                o["folder"] = m.FolderName;
                o["dll"] = m.DllPath == null ? null : Path.GetFileName(m.DllPath);
                o.Opt("icon", IconsOf("mod:" + m.Guid));
                if (m.Folder != null)
                {
                    var man = Path.Combine(m.Folder, "manifest.json");
                    if (File.Exists(man)) { try { o["thunderstore"] = JsonParse.Parse(File.ReadAllText(man)); } catch { o["thunderstore"] = File.ReadAllText(man); } }
                    var readme = Path.Combine(m.Folder, "README.md");
                    if (File.Exists(readme)) { try { o["readme"] = File.ReadAllText(readme); } catch { } }
                    var changelog = Path.Combine(m.Folder, "CHANGELOG.md");
                    if (File.Exists(changelog)) { try { o["changelog"] = File.ReadAllText(changelog); } catch { } }
                }
                o.Opt("config", ConfigOf(m));
                var bn = new JArr(); bn.AddRange(m.BundleNames.Cast<object>()); o.Opt("assetBundles", bn);
                JArr hp; if (patches.TryGetValue(m.Guid, out hp)) o["harmonyPatches"] = hp;
                JObj c; if (_modContent.TryGetValue(m.Guid, out c)) o["content"] = c;
                if (_opt.ModStatics && m.Asm != null && m.Guid != _opt.SelfGuid) o.Opt("staticData", Statics(m));
                all.Add(o);
            }
            JObj unk; if (_modContent.TryGetValue("unknown", out unk)) all.Add(new JObj { { "guid", "unknown" }, { "name", "(modded, owner not determinable)" }, { "content", unk } });
            var other = new JArr();
            foreach (var kv in patches) if (!_mods.Any(m => m.Guid == kv.Key)) other.Add(new JObj { { "owner", kv.Key }, { "patches", kv.Value } });
            Json.WriteFile(P("mods.json"), new JObj { { "mods", all }, { "otherHarmonyOwners", other } }, true);
            foreach (var kv in _modContent) Json.WriteFile(P("mods", Icons.SafeName(kv.Key) + ".json"), kv.Value, true);
        }

        private JArr ConfigOf(ModEntry m)
        {
            var res = new JArr();
            var cfg = Refl.Get(m.Instance, "Config");
            foreach (var kv in Refl.Enum(cfg))
            {
                var def = Refl.Get(kv, "Key"); var entry = Refl.Get(kv, "Value");
                if (entry == null) continue;
                var desc = Refl.Get(entry, "Description");
                var o = new JObj
                {
                    { "section", Refl.S(Refl.Get(def, "Section")) }, { "key", Refl.S(Refl.Get(def, "Key")) },
                    { "value", Ser.Node(Refl.Get(entry, "BoxedValue"), 1) }, { "default", Ser.Node(Refl.Get(entry, "DefaultValue"), 1) },
                    { "type", (Refl.Get(entry, "SettingType") as Type) == null ? null : ((Type)Refl.Get(entry, "SettingType")).Name },
                    { "description", Refl.S(Refl.Get(desc, "Description")) }
                };
                var acc = Refl.Get(desc, "AcceptableValues");
                if (acc != null) o["acceptable"] = Refl.S(Refl.Call(acc, "ToDescriptionString"));
                res.Add(o);
            }
            return res;
        }

        /// <summary>Harmony patches grouped by owner id (usually the plugin GUID).</summary>
        private Dictionary<string, JArr> HarmonyOwners()
        {
            var res = new Dictionary<string, JArr>();
            var tH = Refl.T("HarmonyLib.Harmony");
            if (tH == null) return res;
            var methods = Refl.Invoke(tH, null, "GetAllPatchedMethods", Refl.Stat, new object[0]);
            foreach (var mb in Refl.Enum(methods))
            {
                var info = Refl.Invoke(tH, null, "GetPatchInfo", Refl.Stat, new[] { mb });
                if (info == null) continue;
                var target = mb as MethodBase;
                var tname = target == null ? Refl.S(mb) : (target.DeclaringType == null ? "" : target.DeclaringType.FullName) + "." + target.Name;
                foreach (var kind in new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
                    foreach (var p in Refl.Enum(Refl.Get(info, kind)))
                    {
                        var owner = Refl.S(Refl.Get(p, "owner")) ?? "?";
                        JArr l; if (!res.TryGetValue(owner, out l)) res[owner] = l = new JArr();
                        l.Add(new JObj { { "target", tname }, { "kind", kind.TrimEnd('s').Replace("Prefixe", "Prefix") } });
                    }
            }
            return res;
        }

        /// <summary>Static, non-Unity data held by the plugin (config-driven tables, lookups). Depth- and size-capped.</summary>
        private JObj Statics(ModEntry m)
        {
            var res = new JObj();
            Type[] types;
            try { types = m.Asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); } catch { return res; }
            int budget = 400;
            var oldMax = Ser.MaxList;
            Ser.MaxList = 300;
            try
            {
                foreach (var t in types)
                {
                    if (t.IsGenericTypeDefinition || t.Name.StartsWith("<")) continue;
                    var ns = t.Namespace ?? "";
                    if (!Origin.SafeStatics(t, m, ns.EndsWith("Manager"))) continue;
                    if (ns.StartsWith("ServerSync") || ns.StartsWith("Microsoft") || ns.StartsWith("System") || ns.StartsWith("YamlDotNet") || ns.StartsWith("Newtonsoft")) continue;
                    FieldInfo[] fs;
                    try { fs = t.GetFields(Refl.Stat | BindingFlags.DeclaredOnly); } catch { continue; }
                    foreach (var f in fs)
                    {
                        if (budget <= 0) return res;
                        if (f.IsLiteral || f.Name.StartsWith("<") || typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                        if (typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType)) continue;
                        var fns = f.FieldType.Namespace ?? "";
                        if (fns.StartsWith("System") && !f.FieldType.IsPrimitive && f.FieldType != typeof(string) && !f.FieldType.IsEnum
                            && !typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType)) continue;
                        if (f.FieldType.FullName != null && (f.FieldType.FullName.StartsWith("BepInEx") || f.FieldType.FullName.StartsWith("HarmonyLib"))) continue;
                        object v; try { v = f.GetValue(null); } catch { continue; }
                        if (v == null) continue;
                        object n; try { n = Ser.Node(v, 0, 3, new HashSet<object>(RefEq.I), true); } catch { continue; }
                        if (n == null || (n is JArr && ((JArr)n).Count == 0) || (n is JObj && ((JObj)n).Count == 0)) continue;
                        res[t.FullName + "." + f.Name] = n;
                        budget--;
                    }
                }
            }
            finally { Ser.MaxList = oldMax; }
            return res;
        }
    }
}
