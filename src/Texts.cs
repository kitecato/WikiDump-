using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace WikiDumpMods
{
    /// <summary>
    /// Mod translations read straight from their sources, for tokens the game's Localize() doesn't resolve at dump time
    /// (seen with Blaxxun LocalizationManager mods on Valheim 1.0.16, and for languages that are not active).
    /// Sources, highest priority first:
    ///   config  - *.yml / *.json under BepInEx/config (user / translation packs, e.g. TherzieTranslations)
    ///   folder  - *.yml / *.json in the mod's own folder
    ///   embedded- *.yml / *.json resources inside the mod's DLL
    ///   loaded  - LocalizationManager.Localizer.loadedTexts (what the library already loaded)
    ///   code    - *.LocalizeKey.keys (texts set in code via ItemManager / PieceManager / SkillManager)
    ///   jotunn  - Jotunn CustomLocalization objects
    /// A file or resource counts for a language when one of its name parts (split on . / \ _ -) equals the language.
    /// </summary>
    internal sealed class ModTexts
    {
        private sealed class Entry { public string Text, Source; public int Rank; }
        private readonly Dictionary<string, Dictionary<string, Entry>> _byLang = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.OrdinalIgnoreCase);
        public readonly JArr SourcesRead = new JArr();
        private readonly HashSet<string> _langs;

        public ModTexts(IEnumerable<string> languages) { _langs = new HashSet<string>(languages, StringComparer.OrdinalIgnoreCase); }

        public string Get(string lang, string token, out string source)
        {
            source = null;
            Dictionary<string, Entry> d; Entry e;
            if (!_byLang.TryGetValue(lang, out d) || !d.TryGetValue(token, out e)) return null;
            source = e.Source;
            return e.Text;
        }

        private void Put(string lang, string token, string text, string source, int rank)
        {
            if (string.IsNullOrEmpty(token) || text == null) return;
            token = token.TrimStart('$');
            Dictionary<string, Entry> d;
            if (!_byLang.TryGetValue(lang, out d)) _byLang[lang] = d = new Dictionary<string, Entry>();
            Entry e;
            if (d.TryGetValue(token, out e) && e.Rank <= rank) return;
            d[token] = new Entry { Text = text, Source = source, Rank = rank };
        }

        private string LangOf(string name)
        {
            foreach (var part in name.Split('.', '/', '\\', '_', '-', ' '))
                if (_langs.Contains(part)) return _langs.First(l => l.Equals(part, StringComparison.OrdinalIgnoreCase));
            // Portuguese_European etc. contain a separator themselves
            foreach (var l in _langs) if (l.Contains("_") && name.IndexOf(l, StringComparison.OrdinalIgnoreCase) >= 0) return l;
            return null;
        }

        public void Collect(List<ModEntry> mods, string configDir, string ownOutput)
        {
            // config folder (translation packs / user overrides); never our own output folder
            var skip = ownOutput == null ? null : Path.GetFullPath(ownOutput).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (configDir != null && Directory.Exists(configDir))
                foreach (var f in Files(configDir))
                {
                    if (skip != null && Path.GetFullPath(f).StartsWith(skip, StringComparison.OrdinalIgnoreCase)) continue;
                    ReadFile(f, "config:" + Rel(configDir, f), 0);
                }
            foreach (var m in mods)
            {
                if (m.Folder != null && Directory.Exists(m.Folder))
                    foreach (var f in Files(m.Folder)) ReadFile(f, "folder:" + m.Guid + "/" + Rel(m.Folder, f), 1);
                if (m.Asm == null) continue;
                foreach (var r in m.Resources)
                {
                    var rl = r.ToLowerInvariant();
                    if (!(rl.EndsWith(".yml") || rl.EndsWith(".yaml") || rl.EndsWith(".json"))) continue;
                    var lang = LangOf(r);
                    if (lang == null) continue;
                    try
                    {
                        using (var s = m.Asm.GetManifestResourceStream(r))
                        using (var sr = new StreamReader(s, Encoding.UTF8))
                            Parse(sr.ReadToEnd(), rl.EndsWith(".json"), lang, "embedded:" + m.Guid + "/" + r, 2);
                    }
                    catch { }
                }
                FromLibraries(m);
            }
            FromJotunn();
        }

        private static string Rel(string root, string f) { return f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/'); }

        private static IEnumerable<string> Files(string dir)
        {
            string[] all;
            try { all = Directory.GetFiles(dir, "*", SearchOption.AllDirectories); } catch { yield break; }
            foreach (var f in all)
            {
                var l = f.ToLowerInvariant();
                if (l.EndsWith(".yml") || l.EndsWith(".yaml") || l.EndsWith(".json")) yield return f;
            }
        }

        private void ReadFile(string path, string source, int rank)
        {
            var lang = LangOf(Path.GetFileName(path)) ?? LangOf(Path.GetFileName(Path.GetDirectoryName(path) ?? ""));
            if (lang == null) return;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length > 4 * 1024 * 1024) return;
                Parse(File.ReadAllText(path, Encoding.UTF8), path.EndsWith(".json", StringComparison.OrdinalIgnoreCase), lang, source, rank);
            }
            catch { }
        }

        private void Parse(string text, bool json, string lang, string source, int rank)
        {
            int before = Count(lang);
            if (json)
            {
                JObj o;
                try { o = JsonParse.Parse(text) as JObj; } catch { return; }
                if (o == null) return;
                var inner = o["translations"] as JObj ?? o["Translations"] as JObj ?? o;
                foreach (var kv in inner) if (kv.Value is string) Put(lang, kv.Key, (string)kv.Value, source, rank);
            }
            else
            {
                foreach (var raw in text.Replace("\r", "").Split('\n'))
                {
                    var line = raw.TrimEnd();
                    var t = line.TrimStart();
                    if (t.Length == 0 || t[0] == '#' || t.StartsWith("---")) continue;
                    int c = KeySep(t);
                    if (c <= 0) continue;
                    var key = Unquote(t.Substring(0, c).Trim());
                    var val = t.Substring(c + 1).Trim();
                    if (val.Length == 0 || val == "|" || val == ">") continue;
                    Put(lang, key, Unquote(val), source, rank);
                }
            }
            int added = Count(lang) - before;
            if (added > 0) SourcesRead.Add(new JObj { { "source", source }, { "language", lang }, { "newTokens", added } });
        }

        private int Count(string lang) { Dictionary<string, Entry> d; return _byLang.TryGetValue(lang, out d) ? d.Count : 0; }

        /// <summary>Position of the "key: value" colon (outside a quoted key).</summary>
        private static int KeySep(string t)
        {
            if (t[0] == '"' || t[0] == '\'')
            {
                int end = t.IndexOf(t[0], 1);
                return end < 0 ? -1 : t.IndexOf(':', end);
            }
            return t.IndexOf(':');
        }

        private static string Unquote(string v)
        {
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                return v.Substring(1, v.Length - 2).Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");
            if (v.Length >= 2 && v[0] == '\'' && v[v.Length - 1] == '\'')
                return v.Substring(1, v.Length - 2).Replace("''", "'");
            return v;
        }

        /// <summary>Blaxxun LocalizationManager / ItemManager / PieceManager / SkillManager state merged into a mod.</summary>
        private void FromLibraries(ModEntry m)
        {
            Type[] types;
            try { types = m.Asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); } catch { return; }
            foreach (var t in types)
            {
                if (t.IsGenericTypeDefinition) continue;
                var ns = t.Namespace ?? "";
                if (!ns.EndsWith("Manager") || !Origin.SafeStatics(t, m, true)) continue;
                FieldInfo[] fs;
                try { fs = t.GetFields(Refl.Stat | BindingFlags.DeclaredOnly); } catch { continue; }
                foreach (var f in fs)
                {
                    object v; try { v = f.GetValue(null); } catch { continue; }
                    if (v == null) continue;
                    var src = m.Guid + ":" + t.FullName + "." + f.Name;
                    if (f.Name == "loadedTexts" && v is IDictionary)
                    {
                        foreach (DictionaryEntry kv in (IDictionary)v)
                        {
                            var lang = kv.Key as string;
                            if (lang == null || !_langs.Contains(lang) || !(kv.Value is IDictionary)) continue;
                            foreach (DictionaryEntry w in (IDictionary)kv.Value) Put(lang, w.Key as string, w.Value as string, "loaded:" + src, 3);
                        }
                    }
                    else if (f.Name == "keys")
                    {
                        foreach (var lk in Refl.Enum(v))
                        {
                            var key = Refl.S(Refl.Get(lk, "Key"));
                            var locs = Refl.Get(lk, "Localizations") as IDictionary;
                            if (key == null || locs == null) continue;
                            foreach (DictionaryEntry kv in locs)
                            {
                                var lang = kv.Key as string;
                                var text = kv.Value as string;
                                if (lang != null && _langs.Contains(lang) && !string.IsNullOrEmpty(text)) Put(lang, key, text, "code:" + src, 4);
                            }
                        }
                    }
                }
            }
        }

        private void FromJotunn()
        {
            var tLM = Refl.T("Jotunn.Managers.LocalizationManager");
            if (tLM == null) return;
            var inst = Refl.GetStatic("Jotunn.Managers.LocalizationManager", "Instance");
            var locs = Refl.Get(inst, "Localizations") ?? Refl.Get(inst, "CustomLocalizations");
            if (locs is IDictionary) locs = ((IDictionary)locs).Values;
            foreach (var cl in Refl.Enum(locs))
            {
                var mod = Refl.S(Refl.Get(Refl.Get(cl, "SourceMod"), "GUID")) ?? "?";
                foreach (var lang in _langs)
                {
                    var tr = Refl.Call(cl, "GetTranslations", lang) as IEnumerable;
                    foreach (var kv in Refl.Enum(tr))
                        Put(lang, Refl.S(Refl.Get(kv, "Key")), Refl.S(Refl.Get(kv, "Value")), "jotunn:" + mod, 5);
                }
            }
        }
    }
}
