// Material-based window candidates, confirmed later against the IVA (HullMasks).
//
// Lightmaps are the best source: what lights up is nearly always the
// exact window shape. _Emissive (KSP) and _EmissiveMap/_EmissionMap/_Emission
// (other shaders) are read as luminance.
//
// Hull textures also usually mark glass as highly reflective. Which texture/channel
// holds that depends on the shader, so we detect it from the material's
// properties (not shader names - texture mods rename shaders):
//
//   _MetallicGlossMap (metallic workflow)    -> alpha = smoothness
//   _SpecGlossMap     (specular workflow)    -> alpha = smoothness
//   _SpecMap          (KSP Bumped Spec Mapped)-> luminance of rgb
//   specular shader with _MainTex            -> _MainTex alpha (KSP spec mask)
//   transparent sub-mesh on an IVA part      -> always glass (author's window mesh)
//   mesh named like a window, material says nothing -> always glass (an opaque or
//                                               reflective pane, e.g. a reflection
//                                               shader on a mesh called "Windows")
//
// A channel only helps if it actually singles something out. Once per texture
// we read back a 64x64 copy and keep the channel only if a modest fraction of
// texels is marked and the channel isn't flat. If neither lightmap nor gloss
// qualifies, the material is "uninformative" and that sub-mesh uses the probe.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealWindows
{
    public class GlassInfo
    {
        public Texture tex;          // null with channel 6 (always glass)
        public Vector4 st = new Vector4(1, 1, 0, 0);
        public int channel;          // 0 none, 1 r, 2 g, 3 b, 4 a, 5 luminance, 6 always glass
        public string source = "none";
        public float glossyFraction;
        public bool glossUsable;     // the gloss channel qualifies (tex/channel above)
        public Texture light;        // lightmap, null if none qualifies
        public Vector4 lightST = new Vector4(1, 1, 0, 0);
        public float litFraction;
        public Texture albedo;       // colour map, for the window boundary
        public bool informative;     // gloss or lightmap usable
        public bool named;           // a mesh named like a window (see Named)

        public static readonly GlassInfo None = new GlassInfo();
        public static readonly GlassInfo Always = new GlassInfo { channel = 6, source = "transparent sub-mesh", informative = true, glossUsable = true, glossyFraction = 1f };
        public static readonly GlassInfo Named = new GlassInfo { channel = 6, source = "mesh named as window", informative = true, glossUsable = true, glossyFraction = 1f, named = true };

        // A window-named mesh with its own colour map, so its mask can trim the rims.
        public static GlassInfo NamedWith(Texture albedo)
        {
            return new GlassInfo { channel = 6, source = "mesh named as window", informative = true, glossUsable = true, glossyFraction = 1f, named = true, albedo = albedo };
        }
    }

    public static class MaterialGlass
    {
        private static readonly Dictionary<Material, GlassInfo> byMaterial = new Dictionary<Material, GlassInfo>();
        private static readonly Dictionary<string, float[]> statsByTexture = new Dictionary<string, float[]>();

        public static GlassInfo For(Material m, bool transparentOnIvaPart)
        {
            if (transparentOnIvaPart)
                return GlassInfo.Always;
            if (m == null || RWSettings.materialMode == 0)
                return GlassInfo.None;
            GlassInfo g;
            if (byMaterial.TryGetValue(m, out g))
                return g;
            g = Detect(m);
            byMaterial[m] = g;
            return g;
        }

        private static readonly string[] GlassNames = { "window", "glass", "canopy", "windshield", "windscreen" };
        private static readonly string[] NotGlassNames = { "frame", "shutter", "cover", "door", "light", "glow", "rim", "mount", "blind", "trim" };

        public static bool NamedAsGlass(string name)
        {
            bool glass = false;
            for (int i = 0; i < GlassNames.Length && !glass; i++)
                glass = name.IndexOf(GlassNames[i], StringComparison.OrdinalIgnoreCase) >= 0;
            if (!glass)
                return false;
            for (int i = 0; i < NotGlassNames.Length; i++)
                if (name.IndexOf(NotGlassNames[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            return true;
        }

        private static readonly string[] LightProps = { "_Emissive", "_EmissiveMap", "_EmissionMap", "_Emission" };

        private static GlassInfo Detect(Material m)
        {
            GlassInfo g = new GlassInfo();
            DetectGloss(m, g);
            DetectLight(m, g);
            g.albedo = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
            g.informative = g.glossUsable || g.light != null;
            return g;
        }

        private static void DetectLight(Material m, GlassInfo g)
        {
            for (int i = 0; i < LightProps.Length; i++)
            {
                string prop = LightProps[i];
                if (!m.HasProperty(prop))
                    continue;
                Texture t = m.GetTexture(prop);
                if (t == null)
                    continue;
                float[] stats = Stats(t, 5, RWSettings.lightThreshold);
                if (stats[0] < RWSettings.lightMinFraction || stats[0] > RWSettings.glassMaxFraction || stats[2] - stats[1] < 0.3f)
                    continue;
                Vector2 sc = m.GetTextureScale(prop), off = m.GetTextureOffset(prop);
                g.light = t;
                g.lightST = new Vector4(sc.x, sc.y, off.x, off.y);
                g.litFraction = stats[0];
                return;
            }
        }

        private static void DetectGloss(Material m, GlassInfo g)
        {
            string prop = null;
            int channel = 0;
            if (m.HasProperty("_MetallicGlossMap") && m.GetTexture("_MetallicGlossMap") != null) { prop = "_MetallicGlossMap"; channel = 4; }
            else if (m.HasProperty("_SpecGlossMap") && m.GetTexture("_SpecGlossMap") != null) { prop = "_SpecGlossMap"; channel = 4; }
            else if (m.HasProperty("_SpecMap") && m.GetTexture("_SpecMap") != null) { prop = "_SpecMap"; channel = 5; }
            else
            {
                string sn = m.shader != null ? m.shader.name : "";
                bool specular = sn.IndexOf("Specular", StringComparison.OrdinalIgnoreCase) >= 0
                             || m.HasProperty("_Shininess") || m.HasProperty("_SpecColor");
                if (specular && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null) { prop = "_MainTex"; channel = 4; }
            }
            if (prop == null)
                return;

            Texture tex = m.GetTexture(prop);
            Vector2 sc = m.GetTextureScale(prop), off = m.GetTextureOffset(prop);
            float[] stats = Stats(tex, channel, RWSettings.glassThreshold); // { fraction, min, max }
            g.glossyFraction = stats[0];
            g.source = prop + (channel == 4 ? ".a" : ".lum");
            g.glossUsable = stats[0] >= RWSettings.glassMinFraction && stats[0] <= RWSettings.glassMaxFraction
                            && stats[2] - stats[1] >= 0.3f;
            if (!g.glossUsable)
                return;
            g.tex = tex;
            g.st = new Vector4(sc.x, sc.y, off.x, off.y);
            g.channel = channel;
        }

        // GPU readback of a small copy: KSP textures aren't CPU-readable.
        private static float[] Stats(Texture tex, int channel, float threshold)
        {
            string key = tex.GetInstanceID() + ":" + channel + ":" + threshold;
            float[] s;
            if (statsByTexture.TryGetValue(key, out s))
                return s;
            s = new float[] { 0f, 0f, 0f };
            RenderTexture prev = RenderTexture.active;
            RenderTexture tmp = null;
            Texture2D read = null;
            try
            {
                const int N = 64;
                tmp = RenderTexture.GetTemporary(N, N, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(tex, tmp);
                RenderTexture.active = tmp;
                read = new Texture2D(N, N, TextureFormat.RGBA32, false, true);
                read.ReadPixels(new Rect(0, 0, N, N), 0, 0, false);
                Color32[] px = read.GetPixels32();
                int glossy = 0;
                float mn = 1f, mx = 0f;
                for (int i = 0; i < px.Length; i++)
                {
                    float v = Channel(px[i], channel);
                    if (v >= threshold) glossy++;
                    if (v < mn) mn = v;
                    if (v > mx) mx = v;
                }
                s[0] = (float)glossy / px.Length;
                s[1] = mn;
                s[2] = mx;
            }
            catch (Exception e)
            {
                RWLog.Warn("gloss readback failed for {0}: {1}", tex.name, e.Message);
            }
            finally
            {
                RenderTexture.active = prev;
                if (tmp != null) RenderTexture.ReleaseTemporary(tmp);
                if (read != null) UnityEngine.Object.Destroy(read);
            }
            statsByTexture[key] = s;
            return s;
        }

        public static float Channel(Color32 c, int channel)
        {
            switch (channel)
            {
                case 1: return c.r / 255f;
                case 2: return c.g / 255f;
                case 3: return c.b / 255f;
                case 4: return c.a / 255f;
                case 5: return (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
                default: return 0f;
            }
        }
    }
}
