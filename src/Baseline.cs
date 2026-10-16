using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;
using System.IO.Compression;

namespace WikiDumpMods
{
    /// <summary>
    /// Snapshot of an unmodded game: every prefab / recipe / status effect / location / event name plus flattened
    /// fingerprints of items, recipes, pieces, creatures, drop tables, traders and conversions. Written automatically
    /// when the plugin runs with no other plugins installed; used later to (a) tell modded from vanilla content
    /// exactly and (b) list every vanilla value a mod changed.
    /// </summary>
    internal sealed class Baseline
    {
        public string GameVersion, Created, Path, Source;
        /// <summary>Partial = only "content" prefabs are listed (converted WikiDump dump); absence of other prefabs proves nothing.</summary>
        public bool Partial;
        public readonly HashSet<string> Covered = new HashSet<string>();
        public readonly HashSet<string> Keys = new HashSet<string>();
        /// <summary>Only used while writing; reading streams fingerprints (EachFingerprint) instead of holding them.</summary>
        public readonly Dictionary<string, Dictionary<string, string>> Fp = new Dictionary<string, Dictionary<string, string>>();
        private Func<Stream> _open;

        public bool Contains(string key) { return Keys.Contains(key); }

        /// <summary>Format 2: gzip'd JSON lines - header, then one {"k":key,"fp":{path:value}} per entity.</summary>
        public void Save(string path)
        {
            var h = new JObj();
            h["format"] = 2; h["gameVersion"] = GameVersion; h["created"] = Created; h["source"] = Source ?? "WikiDumpMods vanilla run";
            h["partial"] = false;
            var ks = new JArr(); foreach (var k in Keys.OrderBy(x => x, StringComparer.Ordinal)) ks.Add(k); h["keys"] = ks;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            using (var fs = File.Create(path))
            using (var gz = new GZipStream(fs, CompressionMode.Compress))
            using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
            {
                Json.Write(w, h, -1); w.Write('\n');
                foreach (var kv in Fp.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    var e = new JObj(); foreach (var p in kv.Value) e[p.Key] = p.Value;
                    Json.Write(w, new JObj { { "k", kv.Key }, { "fp", e } }, -1); w.Write('\n');
                }
            }
        }

        public static Baseline Load(string path)
        {
            if (!File.Exists(path)) return null;
            return Open(() => File.OpenRead(path), path);
        }

        public static Baseline LoadEmbedded(System.Reflection.Assembly asm, string resource)
        {
            if (asm == null || asm.GetManifestResourceInfo(resource) == null) return null;
            return Open(() => asm.GetManifestResourceStream(resource), "embedded:" + resource);
        }

        private static Baseline Open(Func<Stream> open, string label)
        {
            var b = new Baseline { Path = label, _open = open };
            using (var r = b.Reader())
            {
                var h = JsonParse.Parse(r.ReadLine()) as JObj;
                if (h == null) return null;
                b.GameVersion = h["gameVersion"] as string; b.Created = h["created"] as string; b.Source = h["source"] as string;
                b.Partial = h["partial"] is bool && (bool)h["partial"];
                foreach (var k in (h["keys"] as JArr) ?? new JArr()) if (k is string) b.Keys.Add((string)k);
                foreach (var k in (h["covered"] as JArr) ?? new JArr()) if (k is string) b.Covered.Add((string)k);
            }
            return b;
        }

        private StreamReader Reader()
        {
            var s = _open();
            // gzip magic 1f 8b; plain text otherwise
            var buf = new BufferedStream(s);
            int b1 = buf.ReadByte(), b2 = buf.ReadByte();
            buf.Seek(0, SeekOrigin.Begin);
            Stream body = (b1 == 0x1f && b2 == 0x8b) ? (Stream)new GZipStream(buf, CompressionMode.Decompress) : buf;
            return new StreamReader(body, Encoding.UTF8);
        }

        public IEnumerable<KeyValuePair<string, Dictionary<string, string>>> EachFingerprint()
        {
            if (_open == null) yield break;
            using (var r = Reader())
            {
                r.ReadLine();
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    var o = JsonParse.Parse(line) as JObj;
                    var fp = o == null ? null : o["fp"] as JObj;
                    if (fp == null) continue;
                    var d = new Dictionary<string, string>();
                    foreach (var p in fp) d[p.Key] = p.Value as string;
                    yield return new KeyValuePair<string, Dictionary<string, string>>(o["k"] as string, d);
                }
            }
        }
    }

    internal static class JsonParse
    {
        public static object Parse(string s) { int i = 0; var v = Val(s, ref i); return v; }

        private static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object Val(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{')
            {
                var o = new JObj(); i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws(s, ref i); var k = Str(s, ref i); Ws(s, ref i);
                    if (s[i] != ':') throw new FormatException("':' expected at " + i); i++;
                    o[k] = Val(s, ref i); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return o; }
                    throw new FormatException("',' or '}' expected at " + i);
                }
            }
            if (c == '[')
            {
                var a = new JArr(); i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Val(s, ref i)); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return a; }
                    throw new FormatException("',' or ']' expected at " + i);
                }
            }
            if (c == '"') return Str(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(st, i - st), CultureInfo.InvariantCulture);
        }

        private static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("string expected at " + i);
            i++;
            var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            i++;
            return sb.ToString();
        }
    }
}
