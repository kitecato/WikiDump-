using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace WikiDumpMods
{
    /// <summary>Ordered JSON object.</summary>
    internal sealed class JObj : IEnumerable<KeyValuePair<string, object>>
    {
        private readonly List<KeyValuePair<string, object>> _l = new List<KeyValuePair<string, object>>();
        private readonly Dictionary<string, int> _ix = new Dictionary<string, int>();
        public int Count { get { return _l.Count; } }
        public object this[string k]
        {
            get { int i; return _ix.TryGetValue(k, out i) ? _l[i].Value : null; }
            set
            {
                int i;
                if (_ix.TryGetValue(k, out i)) _l[i] = new KeyValuePair<string, object>(k, value);
                else { _ix[k] = _l.Count; _l.Add(new KeyValuePair<string, object>(k, value)); }
            }
        }
        public bool Has(string k) { return _ix.ContainsKey(k); }
        public void Add(string k, object v) { this[k] = v; }
        public void Remove(string k)
        {
            int i;
            if (!_ix.TryGetValue(k, out i)) return;
            _l.RemoveAt(i);
            _ix.Clear();
            for (int n = 0; n < _l.Count; n++) _ix[_l[n].Key] = n;
        }
        /// <summary>Adds only when the value is meaningful (not null, not empty list/object).</summary>
        public JObj Opt(string k, object v)
        {
            if (v == null) return this;
            if (v is JArr && ((JArr)v).Count == 0) return this;
            if (v is JObj && ((JObj)v).Count == 0) return this;
            this[k] = v;
            return this;
        }
        public JArr Arr(string k)
        {
            var a = this[k] as JArr;
            if (a == null) { a = new JArr(); this[k] = a; }
            return a;
        }
        public JObj Obj(string k)
        {
            var o = this[k] as JObj;
            if (o == null) { o = new JObj(); this[k] = o; }
            return o;
        }
        public IEnumerable<string> Keys { get { foreach (var kv in _l) yield return kv.Key; } }
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() { return _l.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return _l.GetEnumerator(); }
    }

    internal sealed class JArr : List<object> { }

    internal static class Json
    {
        public static void WriteFile(string path, object node, bool pretty)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var sw = new StreamWriter(path, false, new UTF8Encoding(false)))
                Write(sw, node, pretty ? 0 : -1);
        }

        public static string ToString(object node)
        {
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            Write(sw, node, -1);
            return sw.ToString();
        }

        private static void NL(TextWriter w, int ind) { if (ind < 0) return; w.Write('\n'); w.Write(new string(' ', ind)); }

        public static void Write(TextWriter w, object v, int ind)
        {
            if (v == null) { w.Write("null"); return; }
            if (v is string) { Str(w, (string)v); return; }
            if (v is bool) { w.Write((bool)v ? "true" : "false"); return; }
            if (v is JObj)
            {
                var o = (JObj)v;
                if (o.Count == 0) { w.Write("{}"); return; }
                w.Write('{'); bool first = true;
                foreach (var kv in o)
                {
                    if (!first) w.Write(','); first = false;
                    NL(w, ind < 0 ? -1 : ind + 1);
                    Str(w, kv.Key); w.Write(ind < 0 ? ":" : ": ");
                    Write(w, kv.Value, ind < 0 ? -1 : ind + 1);
                }
                NL(w, ind); w.Write('}');
                return;
            }
            if (v is JArr)
            {
                var a = (JArr)v;
                if (a.Count == 0) { w.Write("[]"); return; }
                w.Write('['); bool first = true;
                foreach (var x in a)
                {
                    if (!first) w.Write(','); first = false;
                    NL(w, ind < 0 ? -1 : ind + 1);
                    Write(w, x, ind < 0 ? -1 : ind + 1);
                }
                NL(w, ind); w.Write(']');
                return;
            }
            if (v is double || v is float)
            {
                var d = Convert.ToDouble(v);
                if (double.IsNaN(d) || double.IsInfinity(d)) { Str(w, d.ToString(CultureInfo.InvariantCulture)); return; }
                // float -> shortest round-trippable form, so 0.1f prints as 0.1 not 0.100000001490116
                w.Write(v is float ? ((float)v).ToString("R", CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is int || v is long || v is short || v is byte || v is sbyte || v is ushort || v is uint || v is ulong)
            {
                w.Write(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            Str(w, v.ToString());
        }

        private static void Str(TextWriter w, string s)
        {
            w.Write('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': w.Write("\\\""); break;
                    case '\\': w.Write("\\\\"); break;
                    case '\n': w.Write("\\n"); break;
                    case '\r': w.Write("\\r"); break;
                    case '\t': w.Write("\\t"); break;
                    default:
                        if (c < 0x20 || c == '\u2028' || c == '\u2029') w.Write("\\u" + ((int)c).ToString("x4"));
                        else w.Write(c);
                        break;
                }
            }
            w.Write('"');
        }
    }

    /// <summary>
    /// Turns any game object graph into JObj/JArr/primitives. Unity objects nested inside become small
    /// reference objects ({"$go": name}, {"$sprite": name}, ...) instead of being expanded, so prefab
    /// cross-links stay readable and cycles cannot explode.
    /// </summary>
    internal static class Ser
    {
        public static int MaxList = 5000;
        private static readonly HashSet<string> SkipFieldTypes = new HashSet<string>
        {
            "Mesh", "Material", "Shader", "AudioClip", "AnimationClip", "RuntimeAnimatorController", "Animator", "Renderer",
            "MeshRenderer", "SkinnedMeshRenderer", "ParticleSystem", "Collider", "Rigidbody", "ZNetView", "ZSyncTransform",
            "Texture", "Texture2D", "RenderTexture", "AudioSource", "Light", "Camera", "LODGroup", "AudioMixerGroup"
        };

        public static object Ref(UnityEngine.Object u)
        {
            if (ReferenceEquals(u, null) || !u) return null;
            var o = new JObj();
            if (u is UnityEngine.GameObject) { o["$go"] = u.name; return o; }
            var c = u as UnityEngine.Component;
            if (!ReferenceEquals(c, null)) { o["$comp"] = c.GetType().Name; o["go"] = c.gameObject ? c.gameObject.name : null; return o; }
            var tn = u.GetType().Name;
            if (tn == "Sprite") { o["$sprite"] = u.name; return o; }
            if (u is UnityEngine.ScriptableObject) { o["$so"] = tn; o["name"] = u.name; return o; }
            o["$asset"] = tn; o["name"] = u.name;
            return o;
        }

        /// <summary>Serialises v. depth counts nesting; Unity objects below the root are written as references.</summary>
        public static object Node(object v, int maxDepth, bool privateToo = false)
        {
            return Node(v, 0, maxDepth, new HashSet<object>(RefEq.I), privateToo);
        }

        public static object Node(object v, int depth, int maxDepth, HashSet<object> seen, bool privateToo)
        {
            if (v == null) return null;
            var t = v.GetType();
            if (v is string || v is bool) return v;
            if (t.IsPrimitive) return (v is char) ? (object)v.ToString() : v;
            if (v is decimal) return Convert.ToDouble(v);
            if (t.IsEnum) return v.ToString();
            if (v is Type) return ((Type)v).FullName;
            if (v is Delegate || v is MemberInfo) return null;

            var u = v as UnityEngine.Object;
            if (!ReferenceEquals(u, null))
            {
                if (!u) return null;
                if (depth > 0) return Ref(u);
            }

            var dict = v as IDictionary;
            if (dict != null)
            {
                var o = new JObj(); int n = 0;
                foreach (DictionaryEntry kv in dict)
                {
                    if (n++ >= MaxList) { o["$truncated"] = dict.Count; break; }
                    var key = kv.Key is UnityEngine.Object ? (Refl.UName(kv.Key) ?? "null") : Convert.ToString(kv.Key, CultureInfo.InvariantCulture);
                    o[key ?? "null"] = depth + 1 > maxDepth ? Short(kv.Value) : Node(kv.Value, depth + 1, maxDepth, seen, privateToo);
                }
                return o;
            }

            var list = v as IEnumerable;
            if (list != null)
            {
                var a = new JArr(); int n = 0;
                foreach (var x in Refl.Enum(list))
                {
                    if (n++ >= MaxList) { a.Add("$truncated"); break; }
                    a.Add(depth + 1 > maxDepth ? Short(x) : Node(x, depth + 1, maxDepth, seen, privateToo));
                }
                return a;
            }

            if (depth >= maxDepth) return Short(v);
            if (!t.IsValueType)
            {
                if (seen.Contains(v)) return "$cycle";
                seen.Add(v);
            }
            var res = Fields(v, depth, maxDepth, seen, privateToo);
            if (!t.IsValueType) seen.Remove(v);
            // Unity structs keep their data in private fields (Rect, Bounds, LayerMask, Keyframe...): fall back to ToString.
            if (res.Count == 0 && t.Namespace != null && t.Namespace.StartsWith("UnityEngine")) return v.ToString();
            return res;
        }

        private static object Short(object v)
        {
            if (v == null) return null;
            var u = v as UnityEngine.Object;
            if (!ReferenceEquals(u, null)) return Ref(u);
            var t = v.GetType();
            if (v is string || v is bool || t.IsPrimitive) return v;
            if (t.IsEnum) return v.ToString();
            return "$depth";
        }

        private static bool StopAt(Type t)
        {
            return t == null || t == typeof(object) || t == typeof(UnityEngine.MonoBehaviour) || t == typeof(UnityEngine.Behaviour)
                   || t == typeof(UnityEngine.Component) || t == typeof(UnityEngine.Object) || t == typeof(UnityEngine.ScriptableObject)
                   || t == typeof(ValueType);
        }

        public static JObj Fields(object o, int depth, int maxDepth, HashSet<object> seen, bool privateToo)
        {
            var res = new JObj();
            var done = new HashSet<string>();
            for (var t = o.GetType(); !StopAt(t); t = t.BaseType)
            {
                FieldInfo[] fs;
                try { fs = t.GetFields(Refl.Inst | BindingFlags.DeclaredOnly); } catch { continue; }
                foreach (var f in fs)
                {
                    var fname = f.Name;
                    if (fname.StartsWith("<"))
                    {
                        // auto-property backing field: keep (under the property name) only for plain C# objects
                        int e = fname.IndexOf(">k__BackingField", StringComparison.Ordinal);
                        if (!privateToo || e < 0) continue;
                        fname = fname.Substring(1, e - 1);
                    }
                    if (!privateToo && !f.IsPublic && f.GetCustomAttributes(typeof(UnityEngine.SerializeField), true).Length == 0) continue;
                    if (f.IsNotSerialized && !privateToo) continue;
                    if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    if (f.FieldType.IsPointer || f.FieldType == typeof(IntPtr)) continue;
                    if (SkipFieldTypes.Contains(ElemName(f.FieldType))) continue;
                    if (!done.Add(fname)) continue;
                    object val;
                    try { val = f.GetValue(o); } catch { continue; }
                    object n;
                    try { n = Node(val, depth + 1, maxDepth, seen, privateToo); } catch (Exception e) { n = "$error: " + e.GetType().Name; }
                    res[fname] = n;
                }
            }
            return res;
        }

        private static string ElemName(Type t)
        {
            if (t.IsArray) return t.GetElementType().Name;
            if (t.IsGenericType && t.GetGenericArguments().Length == 1) return t.GetGenericArguments()[0].Name;
            return t.Name;
        }

        /// <summary>Flattens a node tree into "a.b[3].c" =&gt; scalar text, for field-level diffs against the baseline.</summary>
        public static void Flatten(object n, string prefix, Dictionary<string, string> into, int limit = 4000)
        {
            if (into.Count >= limit) return;
            var o = n as JObj;
            if (o != null)
            {
                if (o.Has("$go") || o.Has("$comp") || o.Has("$so") || o.Has("$sprite") || o.Has("$asset")) { into[prefix] = Json.ToString(o); return; }
                foreach (var kv in o) Flatten(kv.Value, prefix.Length == 0 ? kv.Key : prefix + "." + kv.Key, into, limit);
                return;
            }
            var a = n as JArr;
            if (a != null)
            {
                into[prefix + ".#"] = a.Count.ToString(CultureInfo.InvariantCulture);
                for (int i = 0; i < a.Count; i++) Flatten(a[i], prefix + "[" + i + "]", into, limit);
                return;
            }
            into[prefix] = n == null ? "null" : Json.ToString(n);
        }
    }

    internal sealed class RefEq : IEqualityComparer<object>
    {
        public static readonly RefEq I = new RefEq();
        public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
        public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
    }
}
