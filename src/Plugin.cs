using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace WikiDumpMods
{
    /// <summary>
    /// Read-only exporter of everything installed mods add to Valheim (items, pieces, creatures, recipes, drops,
    /// spawns, locations, status effects, icons, translations, configs), for the Ukrainian wiki.
    /// Nothing in the world, character or other mods is changed; output goes to BepInEx/config/wikidump_mods/.
    /// </summary>
    [BepInPlugin(Guid, "WikiDumpMods", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "ua.valheimwiki.wikidumpmods";
        public const string Version = "1.0.4";

        private ConfigEntry<bool> _auto;
        private ConfigEntry<KeyCode> _key;
        private ConfigEntry<float> _delay;
        private ConfigEntry<string> _languages;
        private ConfigEntry<bool> _icons;
        private ConfigEntry<string> _iconColor;
        private ConfigEntry<bool> _vanillaLocations;
        private ConfigEntry<bool> _includeVanilla;
        private ConfigEntry<bool> _tooltips;
        private ConfigEntry<bool> _statics;
        private ConfigEntry<int> _rawDepth;
        private ConfigEntry<bool> _forceBaseline;
        private bool _running, _doneThisSession;
        private float _readySince = -1f;

        private void Awake()
        {
            _auto = Config.Bind("General", "AutoDump", true, "Dump automatically once per game session after entering a world.");
            _key = Config.Bind("General", "Hotkey", KeyCode.F11, "Press in a world to dump again.");
            _delay = Config.Bind("General", "DelaySeconds", 10f, "Wait this long after the player spawns before the automatic dump (lets other mods finish registering).");
            _languages = Config.Bind("Content", "Languages", "English,Ukrainian", "Translation tables (and in-game tooltips) to export, comma separated game language names. The current language is always included.");
            _icons = Config.Bind("Content", "Icons", true, "Save icons of modded items, pieces, stations, status effects and skills as PNG, plus each mod's Thunderstore icon.");
            _iconColor = Config.Bind("Content", "IconColorMode", "LinearToSRGB", "LinearToSRGB (default, exact colour correction), sRGB (GPU conversion) or Linear (raw, too dark). Change only if icons look wrong.");
            _vanillaLocations = Config.Bind("Content", "ScanVanillaLocations", true, "Also open vanilla locations and dungeon rooms to find modded items/creatures placed in their chests, pickables and spawners (slower).");
            _includeVanilla = Config.Bind("Content", "IncludeVanilla", false, "Export vanilla content too (full game dump instead of mods only).");
            _tooltips = Config.Bind("Content", "Tooltips", true, "Export the game's own tooltip text for every quality level of modded items and for modded status effects, per language.");
            _statics = Config.Bind("Content", "ModStaticData", true, "Export static data held by each mod's code (config-driven tables etc.), size-capped.");
            _forceBaseline = Config.Bind("Baseline", "ForceBaseline", false, "Write vanilla_baseline.json on the next dump even though other plugins are installed. Only for a profile whose remaining plugins add no content; set back to false afterwards.");
            _rawDepth = Config.Bind("Content", "RawDepth", 4, "Nesting depth for the raw component dump of modded prefabs (raw/*.json).");
            Logger.LogInfo("WikiDumpMods " + Version + " loaded. Output: " + OutDir);
        }

        private static string OutDir { get { return Path.Combine(Paths.ConfigPath, "wikidump_mods"); } }

        private void Update()
        {
            if (_running) return;
            var player = Refl.GetStatic("Player", "m_localPlayer") as UnityEngine.Object;
            bool ready = player && Refl.Singleton("ZNetScene") != null && Refl.Singleton("ObjectDB") != null;
            if (!ready) { _readySince = -1f; return; }
            if (_readySince < 0f) _readySince = Time.realtimeSinceStartup;
            bool hot = false;
            try { hot = Input.GetKeyDown(_key.Value); } catch { }
            if (hot || (_auto.Value && !_doneThisSession && Time.realtimeSinceStartup - _readySince > _delay.Value))
            {
                _doneThisSession = true;
                StartCoroutine(Run());
            }
        }

        private IEnumerator Run()
        {
            _running = true;
            var opt = new DumpOptions
            {
                OutDir = OutDir,
                BaselinePath = Path.Combine(OutDir, "vanilla_baseline.jsonl.gz"),
                Languages = (_languages.Value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray(),
                Icons = _icons.Value,
                IconColor = _iconColor.Value,
                ScanVanillaLocations = _vanillaLocations.Value,
                IncludeVanilla = _includeVanilla.Value,
                Tooltips = _tooltips.Value,
                ModStatics = _statics.Value,
                RawDepth = Math.Max(1, Math.Min(8, _rawDepth.Value)),
                ForceBaseline = _forceBaseline.Value,
                ConfigDir = Paths.ConfigPath,
                PluginVersion = Version,
                SelfGuid = Guid
            };
            var d = new Dumper(opt, Mods(), s => Logger.LogInfo(s), Hud);
            var it = d.Run();
            while (true)
            {
                bool more;
                try { more = it.MoveNext(); }
                catch (Exception e) { Logger.LogError("WikiDumpMods failed: " + e); Hud("WikiDumpMods: помилка, див. LogOutput.log"); break; }
                if (!more) break;
                yield return it.Current;
            }
            _running = false;
        }

        private static List<ModEntry> Mods()
        {
            var res = new List<ModEntry>();
            var root = Path.GetFullPath(Paths.PluginPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var pi in Chainloader.PluginInfos.Values)
            {
                if (pi == null || pi.Metadata == null) continue;
                var m = new ModEntry
                {
                    Guid = pi.Metadata.GUID,
                    Name = pi.Metadata.Name,
                    Version = pi.Metadata.Version == null ? null : pi.Metadata.Version.ToString(),
                    DllPath = pi.Location,
                    Instance = pi.Instance
                };
                try { m.Asm = pi.Instance != null ? pi.Instance.GetType().Assembly : null; } catch { }
                try
                {
                    if (!string.IsNullOrEmpty(pi.Location))
                    {
                        var full = Path.GetFullPath(pi.Location);
                        if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            var rel = full.Substring(root.Length + 1);
                            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            if (parts.Length > 1) { m.FolderName = parts[0]; m.Folder = Path.Combine(root, parts[0]); }
                        }
                    }
                }
                catch { }
                try { if (m.Asm != null) m.Resources.AddRange(m.Asm.GetManifestResourceNames()); } catch { }
                res.Add(m);
            }
            return res;
        }

        private static void Hud(string text)
        {
            try
            {
                var hud = Refl.Singleton("MessageHud");
                var tEnum = Refl.T("MessageHud+MessageType");
                if (hud == null || tEnum == null) return;
                Refl.Call(hud, "ShowMessage", Enum.Parse(tEnum, "Center"), text);
            }
            catch { }
        }
    }
}
