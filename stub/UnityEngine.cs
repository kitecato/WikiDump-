// Minimal stand-in for UnityEngine. Built twice:
//  * strict (no TESTAPI): only members that exist with identical signatures in the real UnityEngine facade.
//    The plugin is compiled against this build.
//  * test (TESTAPI): adds tiny working implementations so the dumper can be exercised in plain Mono.
using System;
using System.Collections;
using System.Collections.Generic;
[assembly: System.Reflection.AssemblyVersion("0.0.0.0")]
namespace UnityEngine
{
    public class Object
    {
        public string name { get { return _name; } set { _name = value; } }
        public int GetInstanceID() { return _id; }
        public static implicit operator bool(Object o) { return !ReferenceEquals(o, null) && !o._destroyed; }
        public static bool operator ==(Object a, Object b)
        {
            bool an = ReferenceEquals(a, null) || a._destroyed, bn = ReferenceEquals(b, null) || b._destroyed;
            if (an || bn) return an && bn;
            return ReferenceEquals(a, b);
        }
        public static bool operator !=(Object a, Object b) { return !(a == b); }
        public override bool Equals(object o) { return ReferenceEquals(this, o); }
        public override int GetHashCode() { return _id; }
        public override string ToString() { return _name + " (" + GetType().Name + ")"; }
        public static void Destroy(Object obj) { if (!ReferenceEquals(obj, null)) obj._destroyed = true; }
        public HideFlags hideFlags { get; set; }
        internal string _name; internal int _id; internal bool _destroyed;
#if TESTAPI
        static int _next = 1;
        public Object() { _id = _next++; }
        public static Object[] FindObjectsOfTypeAllTest(Type t) { return TestWorld.All.FindAll(o => t.IsInstanceOfType(o)).ToArray(); }
        public void MakeRuntimeClone() { _id = -Math.Abs(_id); }
#endif
    }
    [Flags] public enum HideFlags { None = 0 }
    public class Component : Object
    {
        public GameObject gameObject { get { return _go; } }
        public Transform transform { get { return _go == null ? null : _go.transform; } }
        public Component GetComponent(Type type) { return _go == null ? null : _go.GetComponent(type); }
        internal GameObject _go;
    }
    public class Transform : Component, IEnumerable
    {
        public int childCount { get { return _children.Count; } }
        public Transform GetChild(int index) { return _children[index]; }
        public Transform parent { get { return _parent; } }
        public IEnumerator GetEnumerator() { return _children.GetEnumerator(); }
        internal List<Transform> _children = new List<Transform>(); internal Transform _parent;
    }
    public class Behaviour : Component { public bool enabled { get { return true; } set { } } }
    public class MonoBehaviour : Behaviour
    {
        public Coroutine StartCoroutine(IEnumerator routine) { while (routine.MoveNext()) { } return null; }
    }
    public sealed class Coroutine { }
    public class ScriptableObject : Object
    {
#if TESTAPI
        public static T CreateInstanceTest<T>() where T : ScriptableObject, new() { var t = new T(); TestWorld.All.Add(t); return t; }
#endif
    }
    public sealed class GameObject : Object
    {
        public Transform transform { get { return _t; } }
        public Component GetComponent(Type type)
        {
            foreach (var c in _comps) if (type.IsInstanceOfType(c) && c) return c;
            return null;
        }
        public Component[] GetComponents(Type type) { return _comps.FindAll(c => type.IsInstanceOfType(c) && c).ToArray(); }
        public Component[] GetComponentsInChildren(Type type, bool includeInactive)
        {
            var l = new List<Component>(); Collect(this, type, l); return l.ToArray();
        }
        static void Collect(GameObject g, Type type, List<Component> l)
        {
            l.AddRange(g.GetComponents(type));
            foreach (var ch in g._t._children) Collect(ch._go, type, l);
        }
        internal List<Component> _comps = new List<Component>(); internal Transform _t;
#if TESTAPI
        public GameObject(string n) { _name = n; _t = new Transform(); _t._go = this; _comps.Add(_t); TestWorld.All.Add(this); }
        public T Add<T>() where T : Component, new() { var c = new T(); c._go = this; c._name = _name; _comps.Add(c); TestWorld.All.Add(c); return c; }
        public GameObject Child(string n) { var g = new GameObject(n); g._t._parent = _t; _t._children.Add(g._t); return g; }
#endif
    }
    public static class Time { public static float realtimeSinceStartup { get { return 0f; } } }
    public static class Input { public static bool GetKeyDown(KeyCode key) { return false; } }
    public static class Application { public static string persistentDataPath { get { return System.IO.Path.GetTempPath(); } } }
    public enum KeyCode { None = 0, F9 = 290, F10 = 291, F11 = 292, F12 = 293 }
    [AttributeUsage(AttributeTargets.Field)] public sealed class SerializeField : Attribute { }
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    public struct Color { public float r, g, b, a; }
    public sealed class Sprite : Object { }
#if TESTAPI
    public static class TestWorld { public static List<Object> All = new List<Object>(); }
    public static class Resources { public static Object[] FindObjectsOfTypeAll(Type type) { return Object.FindObjectsOfTypeAllTest(type); } }
    public sealed class AssetBundle : Object
    {
        public static List<AssetBundle> Loaded = new List<AssetBundle>();
        public string[] Assets = new string[0];
        public static IEnumerable<AssetBundle> GetAllLoadedAssetBundles() { return Loaded; }
        public string[] GetAllAssetNames() { return Assets; }
        public Dictionary<string, Object> Objects = new Dictionary<string, Object>();
        public Object LoadAsset(string name) { Object o; return Objects.TryGetValue(name, out o) ? o : null; }
    }
#endif
}
