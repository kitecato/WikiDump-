using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace WikiDumpMods
{
    /// <summary>
    /// Saves UnityEngine.Sprite objects as PNG, entirely through reflection (no compile-time Unity texture API).
    /// Game/mod textures are usually not CPU-readable, so each texture is blitted into a RenderTexture and read back.
    ///
    /// Colour handling: Valheim renders in linear colour space. The default mode "LinearToSRGB" reads the blit
    /// into a float linear target and applies the exact sRGB transfer curve per pixel - the same correction that
    /// produced pixel-accurate icons from the WikiDump 1.1 dump (mean error ~6/255 against the wiki's originals).
    /// "sRGB" lets the GPU do the conversion, "Linear" writes the raw linear values (dark), both kept for checks.
    /// </summary>
    internal sealed class Icons : IDisposable
    {
        private readonly Type _tSprite, _tTex2D, _tRT, _tRTFmt, _tRTRW, _tTexFmt, _tRect, _tGraphics, _tImgConv, _tObject, _tTexture, _tColor;
        private readonly string _mode;
        private readonly LinkedList<KeyValuePair<object, object>> _cache = new LinkedList<KeyValuePair<object, object>>();
        private readonly Dictionary<string, string> _savedBySprite = new Dictionary<string, string>();
        private bool _floatFailed;
        public int Saved, Failed, Reused;
        public string LastError;

        public Icons(string mode)
        {
            _mode = (mode ?? "LinearToSRGB").Trim();
            _tSprite = Refl.T("UnityEngine.Sprite");
            _tTex2D = Refl.T("UnityEngine.Texture2D");
            _tTexture = Refl.T("UnityEngine.Texture");
            _tRT = Refl.T("UnityEngine.RenderTexture");
            _tRTFmt = Refl.T("UnityEngine.RenderTextureFormat");
            _tRTRW = Refl.T("UnityEngine.RenderTextureReadWrite");
            _tTexFmt = Refl.T("UnityEngine.TextureFormat");
            _tRect = Refl.T("UnityEngine.Rect");
            _tGraphics = Refl.T("UnityEngine.Graphics");
            _tImgConv = Refl.T("UnityEngine.ImageConversion");
            _tObject = Refl.T("UnityEngine.Object");
            _tColor = Refl.T("UnityEngine.Color");
        }

        public bool Ready
        {
            get { return _tSprite != null && _tTex2D != null && _tRT != null && _tGraphics != null && _tImgConv != null && _tRect != null && _tColor != null; }
        }

        private static object SCall(Type t, string name, Type[] sig, params object[] args)
        {
            var m = t.GetMethod(name, BindingFlags.Static | BindingFlags.Public, null, sig, null);
            if (m == null) throw new MissingMethodException(t.Name + "." + name);
            return m.Invoke(null, args);
        }

        private static object ICall(object o, string name, Type[] sig, params object[] args)
        {
            var m = o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, sig, null);
            if (m == null) throw new MissingMethodException(o.GetType().Name + "." + name);
            return m.Invoke(o, args);
        }

        private bool LinearRead { get { return !_mode.Equals("sRGB", StringComparison.OrdinalIgnoreCase); } }
        private bool Convert { get { return _mode.Equals("LinearToSRGB", StringComparison.OrdinalIgnoreCase); } }

        private object Readable(object tex)
        {
            foreach (var kv in _cache)
                if (ReferenceEquals(kv.Key, tex)) { _cache.Remove(kv); _cache.AddFirst(kv); return kv.Value; }
            int w = Refl.I(Refl.Get(tex, "width")), h = Refl.I(Refl.Get(tex, "height"));
            object r = null;
            if (LinearRead && Convert && !_floatFailed)
            {
                try { r = Blit(tex, w, h, "ARGBFloat", "RGBAFloat"); }
                catch { _floatFailed = true; }
            }
            if (r == null) r = Blit(tex, w, h, "ARGB32", "RGBA32");
            _cache.AddFirst(new KeyValuePair<object, object>(tex, r));
            while (_cache.Count > 4) { Destroy(_cache.Last.Value.Value); _cache.RemoveLast(); }
            return r;
        }

        private object Blit(object tex, int w, int h, string rtFormat, string texFormat)
        {
            var rw = System.Enum.Parse(_tRTRW, LinearRead ? "Linear" : "sRGB");
            var rt = SCall(_tRT, "GetTemporary", new[] { typeof(int), typeof(int), typeof(int), _tRTFmt, _tRTRW },
                w, h, 0, System.Enum.Parse(_tRTFmt, rtFormat), rw);
            var activeProp = _tRT.GetProperty("active");
            var prev = activeProp.GetValue(null, null);
            try
            {
                SCall(_tGraphics, "Blit", new[] { _tTexture, _tRT }, tex, rt);
                activeProp.SetValue(null, rt, null);
                var t2 = Activator.CreateInstance(_tTex2D, w, h, System.Enum.Parse(_tTexFmt, texFormat), false, LinearRead);
                var rect = Activator.CreateInstance(_tRect, 0f, 0f, (float)w, (float)h);
                ICall(t2, "ReadPixels", new[] { _tRect, typeof(int), typeof(int) }, rect, 0, 0);
                ICall(t2, "Apply", Type.EmptyTypes);
                return t2;
            }
            finally
            {
                activeProp.SetValue(null, prev, null);
                SCall(_tRT, "ReleaseTemporary", new[] { _tRT }, rt);
            }
        }

        private static float ToSrgb(float c)
        {
            if (c <= 0f) return 0f;
            if (c >= 1f) return 1f;
            return c <= 0.0031308f ? c * 12.92f : 1.055f * (float)Math.Pow(c, 1.0 / 2.4) - 0.055f;
        }

        /// <summary>Writes one sprite as PNG. Same sprite object twice =&gt; same file (returned path).</summary>
        public string Save(object sprite, string outRoot, string relPath)
        {
            if (sprite == null || !_tSprite.IsInstanceOfType(sprite)) return null;
            var u = sprite as UnityEngine.Object;
            if (ReferenceEquals(u, null) || !u) return null;
            var key = u.GetInstanceID().ToString();
            string prevRel;
            if (_savedBySprite.TryGetValue(key, out prevRel)) { Reused++; return prevRel; }
            try
            {
                var tex = Refl.Get(sprite, "texture");
                if (tex == null) { Failed++; LastError = "sprite has no texture"; return null; }
                object rect = null;
                try { rect = Refl.Get(sprite, "textureRect"); } catch { }
                if (rect == null) rect = Refl.Get(sprite, "rect");
                int x = (int)Math.Round(Refl.F(Refl.Get(rect, "x"))), y = (int)Math.Round(Refl.F(Refl.Get(rect, "y")));
                int w = (int)Math.Round(Refl.F(Refl.Get(rect, "width"))), h = (int)Math.Round(Refl.F(Refl.Get(rect, "height")));
                if (w <= 0 || h <= 0) { Failed++; LastError = "empty sprite rect"; return null; }
                var src = Readable(tex);
                var px = (Array)ICall(src, "GetPixels", new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) }, x, y, w, h, 0);
                if (Convert)
                {
                    var arr = (UnityEngine.Color[])px;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i].r = ToSrgb(arr[i].r);
                        arr[i].g = ToSrgb(arr[i].g);
                        arr[i].b = ToSrgb(arr[i].b);
                    }
                }
                var outTex = Activator.CreateInstance(_tTex2D, w, h, System.Enum.Parse(_tTexFmt, "RGBA32"), false, LinearRead);
                ICall(outTex, "SetPixels", new[] { _tColor.MakeArrayType() }, px);
                ICall(outTex, "Apply", Type.EmptyTypes);
                var png = (byte[])SCall(_tImgConv, "EncodeToPNG", new[] { _tTex2D }, outTex);
                Destroy(outTex);
                var full = Path.Combine(outRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllBytes(full, png);
                Saved++;
                _savedBySprite[key] = relPath;
                return relPath;
            }
            catch (Exception e)
            {
                Failed++;
                var ie = e.InnerException ?? e;
                LastError = ie.GetType().Name + ": " + ie.Message;
                return null;
            }
        }

        private void Destroy(object o)
        {
            try { SCall(_tObject, "Destroy", new[] { _tObject }, o); } catch { }
        }

        public void Dispose()
        {
            foreach (var kv in _cache) Destroy(kv.Value);
            _cache.Clear();
        }

        public static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }
    }
}
