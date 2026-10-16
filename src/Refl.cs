using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace WikiDumpMods
{
    /// <summary>Name-based access to game types so the plugin never binds to assembly_valheim at compile time.</summary>
    internal static class Refl
    {
        public const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        /// <summary>Resolves a type by full name. assembly_valheim wins over mod assemblies that reuse a name (e.g. a mod's own "Recipe").</summary>
        public static Type T(string fullName)
        {
            Type t;
            if (TypeCache.TryGetValue(fullName, out t)) return t;
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var pass in new[] { 0, 1, 2 })
            {
                foreach (var a in asms)
                {
                    var n = a.GetName().Name;
                    bool game = n == "assembly_valheim", unity = n.StartsWith("UnityEngine");
                    if ((pass == 0 && !game) || (pass == 1 && !unity) || (pass == 2 && (game || unity))) continue;
                    try { t = a.GetType(fullName, false); } catch { t = null; }
                    if (t != null) { TypeCache[fullName] = t; return t; }
                }
            }
            TypeCache[fullName] = null;
            return null;
        }

        public static MemberInfo Member(Type t, string name, BindingFlags flags)
        {
            for (var x = t; x != null; x = x.BaseType)
            {
                var f = x.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                PropertyInfo p = null;
                try { p = x.GetProperty(name, flags | BindingFlags.DeclaredOnly); } catch (AmbiguousMatchException) { }
                if (p != null && p.GetIndexParameters().Length == 0 && p.CanRead) return p;
            }
            return null;
        }

        /// <summary>Field or property value; null on any problem.</summary>
        public static object Get(object o, string name)
        {
            if (o == null) return null;
            if (o is UnityEngine.Object && !(UnityEngine.Object)o) return null;
            try
            {
                var m = Member(o.GetType(), name, Inst);
                if (m is FieldInfo) return ((FieldInfo)m).GetValue(o);
                if (m is PropertyInfo) return ((PropertyInfo)m).GetValue(o, null);
            }
            catch { }
            return null;
        }

        public static bool Has(object o, string name) { return o != null && Member(o.GetType(), name, Inst) != null; }

        public static object GetStatic(string typeName, string name)
        {
            var t = T(typeName);
            if (t == null) return null;
            try
            {
                var m = Member(t, name, Stat);
                if (m is FieldInfo) return ((FieldInfo)m).GetValue(null);
                if (m is PropertyInfo) return ((PropertyInfo)m).GetValue(null, null);
            }
            catch { }
            return null;
        }

        /// <summary>Game singletons expose either a static "instance" property or an "m_instance" field.</summary>
        public static object Singleton(string typeName)
        {
            var v = GetStatic(typeName, "instance") ?? GetStatic(typeName, "m_instance") ?? GetStatic(typeName, "Instance");
            if (v is UnityEngine.Object && !(UnityEngine.Object)v) return null;
            return v;
        }

        /// <summary>Calls the first instance method with this name whose parameter count fits; missing trailing args use defaults.</summary>
        public static object Call(object o, string name, params object[] args)
        {
            if (o == null) return null;
            return Invoke(o.GetType(), o, name, Inst, args);
        }

        public static object CallStatic(string typeName, string name, params object[] args)
        {
            var t = T(typeName);
            return t == null ? null : Invoke(t, null, name, Stat, args);
        }

        public static object Invoke(Type t, object target, string name, BindingFlags flags, object[] args)
        {
            args = args ?? new object[0];
            foreach (var m in t.GetMethods(flags).Where(x => x.Name == name && !x.IsGenericMethodDefinition)
                                .OrderBy(x => x.GetParameters().Length))
            {
                var ps = m.GetParameters();
                if (ps.Length < args.Length) continue;
                bool ok = true;
                var full = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i < args.Length)
                    {
                        if (args[i] != null && !ps[i].ParameterType.IsInstanceOfType(args[i])) { ok = false; break; }
                        full[i] = args[i];
                    }
                    else if (ps[i].IsOptional) full[i] = ps[i].DefaultValue == DBNull.Value ? null : ps[i].DefaultValue;
                    else { ok = false; break; }
                }
                if (!ok) continue;
                try { return m.Invoke(target, full); }
                catch { return null; }
            }
            return null;
        }

        public static IEnumerable<object> Enum(object v)
        {
            if (v == null || v is string) yield break;
            var e = v as IEnumerable;
            if (e == null) yield break;
            IEnumerator it;
            try { it = e.GetEnumerator(); } catch { yield break; }
            while (true)
            {
                object cur;
                try { if (!it.MoveNext()) break; cur = it.Current; } catch { break; }
                yield return cur;
            }
        }

        /// <summary>Unity name of an object, or a GameObject's name for components; null for destroyed / non-Unity.</summary>
        public static string UName(object o)
        {
            var u = o as UnityEngine.Object;
            if (ReferenceEquals(u, null) || !u) return null;
            var c = u as UnityEngine.Component;
            if (!ReferenceEquals(c, null) && c.gameObject) return c.gameObject.name;
            return u.name;
        }

        public static UnityEngine.GameObject GO(object o)
        {
            var g = o as UnityEngine.GameObject;
            if (g) return g;
            var c = o as UnityEngine.Component;
            return c ? c.gameObject : null;
        }

        public static UnityEngine.Component Comp(UnityEngine.GameObject go, string typeName)
        {
            if (!go) return null;
            var t = T(typeName);
            return t == null ? null : go.GetComponent(t);
        }

        public static UnityEngine.Component[] Comps(UnityEngine.GameObject go, string typeName, bool children)
        {
            if (!go) return new UnityEngine.Component[0];
            var t = T(typeName);
            if (t == null) return new UnityEngine.Component[0];
            try { return children ? go.GetComponentsInChildren(t, true) : go.GetComponents(t); }
            catch { return new UnityEngine.Component[0]; }
        }

        public static string S(object o) { return o == null ? null : o.ToString(); }

        public static double? Num(object o)
        {
            if (o == null) return null;
            try { return Convert.ToDouble(o); } catch { return null; }
        }

        public static int I(object o, int def = 0) { var d = Num(o); return d.HasValue ? (int)d.Value : def; }
        public static float F(object o, float def = 0f) { var d = Num(o); return d.HasValue ? (float)d.Value : def; }
        public static bool B(object o) { return o is bool && (bool)o; }
    }
}
