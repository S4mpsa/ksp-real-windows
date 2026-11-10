// Settings: GameData/RealWindows/settings.cfg
// Every key is optional; missing keys keep the defaults below.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace RealWindows
{
    public static class RWSettings
    {
        // master
        public static bool enabled = true;

        // scope
        public static float maxDistance = 150f;          // m, camera -> part, for interiors
        public static float occluderDistance = 400f;     // m, camera -> part, for the hull ID pass
        public static int maxInteriors = 32;             // interiors rendered per frame (nearest first)
        public static bool includeInactiveVessels = true;
        public static bool createMissingInteriors = true;
        public static int createPerFrame = 1;            // interior instantiations per frame (hitch spreading)
        public static HashSet<string> excludeParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // alignment
        public static float groupPositionTolerance = 0.002f; // m  - parts whose live mapping agrees within this share a render
        public static float groupAngleTolerance = 0.05f;     // deg

        // rendering
        public static float renderScale = 1f;
        public static string interiorRenderingPath = "Forward";  // Forward | Deferred | UsePlayerSettings
        public static string compositeEvent = "AfterForwardOpaque";
        public static bool useSceneDepth = true;
        public static bool aimIvaSunPerGroup = true;
        public static float minWindowPixels = 12f;       // windows smaller than this on screen aren't rendered
        public static bool interiorShadows = true;       // shadows inside interiors (a whole shadow pass)
        public static float interiorRefresh = 1f;        // s between redraws of the cached shell/props layer while the view holds still; 0 = only on change, -1 = no cache
        public static float interiorRedrawMove = 0.0005f; // m the camera may move (in the interior's frame) before the cache is redrawn
        public static float interiorRedrawTurn = 0.01f;  // deg the camera may turn before the cache is redrawn
        public static bool kerbalShadows = false;        // shadows in the per-frame kerbals pass (a second shadow pass)
        public static bool renderNeighbours = true;      // interiors of attached parts, seen through open hatches
        public static bool showInteriorWhenInside = true; // camera inside a part: its interior is shown whole
        public static bool ivaView = true;               // also in IVA: other parts' windows seen through the IVA's windows
        public static bool centrifugeDeployTwist = false; // untested: SSPX centrifuge module (absent with Kerbalism) whose deploy animation turns the sections: the interior turned with them
        public static bool insideRedraw = true;          // ...drawn again after clouds/atmosphere (they see open sky there)
        public static string insideCompositeEvent = "AfterForwardAlpha";
        public static int maxUvTiles = 16;               // a sub-mesh whose UVs touch more UV tiles than this gets no mask
        public static bool seeIntoNeighbours = true;     // a window may show another part's interior drawn in the same render (open hatches)
        public static bool seeThroughWindows = true;     // where a window's view leaves the cabin, look at the hull behind (another window, the scene through real glass)
        public static bool seeThroughHoles = true;       // painted windows looked through become depth cut-outs in the main render
        public static int seeThroughHoleParts = 12;      // ...for at most this many parts (nearest first)
        public static bool hideDepthMasks = true;        // interior depth masks (FreeIva hatch doorways) skipped in our render

        // look
        public static float transmission = 0.85f;
        public static Color glassTint = new Color(0.92f, 0.96f, 1.0f, 1f);
        public static float feather = 1f;                // px
        public static float glassReflection = 1f;       // x Fresnel: how much the glass reflects at grazing angles
        public static float glassSpecular = 1f;         // sun glint strength
        public static float glassSmoothness = 0.85f;     // 0-1: glint size (1 = pin-point)
        public static float glassDirt = 0.3f;            // smudges and dust, 0 = spotless
        public static float glassDirtScale = 3f;         // smudges per metre

        // window regions from the hull's textures
        public static int materialMode = 1;              // 0 Off, 1 Assist, 2 Union, 3 Trust
        public static float lightThreshold = 0.5f;       // lightmap value counted as lit
        public static float lightMinFraction = 0.0005f;  // lightmap used only if this much of it is lit
        public static float glassThreshold = 0.4f;       // gloss value that makes a texel a candidate
        public static float glassMinFraction = 0.002f;   // gloss channel informative only if this ...
        public static float glassMaxFraction = 0.7f;     // ... to this fraction of the texture is marked
        public static float regionMinOpen = 0.5f;        // share of an unlit or neutral region that needs an IVA opening right behind it
        public static float regionMinOpenLit = 0.02f;    // share of a lit, tinted region with an opening behind or near it
        public static int tintThreshold = 8;             // blue minus red (0-255) that makes a region's colour glass-like
        public static int colourTolerance = 18;          // 0-255 per channel: largest colour step inside one window
        public static float growLimit = 0.75f;           // boundary search range, x sqrt(region area)
        public static float islandFill = 0.8f;           // a region this share of its UV island takes the whole island
        public static int maskResolution = 1024;         // max texels per side of a baked window mask
        public static bool dumpMasks = false;            // write baked masks to PluginData/masks as PNG

        // IVA probe
        public static float probeOpenFraction = 0.12f;   // share of view points that must see through a hull point
        public static float probeWallMargin = 0.1f;      // m: IVA surface this close past the hull still counts as wall
        public static int probeResolution = 128;          // px per probe face
        public static float hullEpsilon = 0.25f;
        public static float sceneDepthEpsilon = 0.15f;

        // debug
        public static int debugMode = 0;                 // 0 off, 1 mask, 2 part IDs, 3 x-ray, 4 interior raw
        public static KeyCombo toggleKey = KeyCombo.Parse("Alt+F8");   // switches the whole mod on and off in flight
        public static bool startEnabled = true;          // state at the start of each flight scene
        public static KeyCode debugCycleKey = KeyCode.None;
        public static KeyCode alignmentDumpKey = KeyCode.None;
        public static bool verboseLogging = false;
        public static float flipY = 0f;                  // 0 auto, 1 force-flip sampling of our textures

        private static bool loaded;

        public static string RootPath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/RealWindows"); }
        }

        public static void Load()
        {
            if (loaded)
                return;
            loaded = true;
            string path = Path.Combine(RootPath, "settings.cfg");
            try
            {
                if (!File.Exists(path))
                {
                    RWLog.Warn("settings.cfg not found at {0} - using defaults", path);
                    return;
                }
                ConfigNode root = ConfigNode.Load(path);
                ConfigNode n = root != null ? root.GetNode("RealWindows") : null;
                if (n == null)
                {
                    RWLog.Warn("settings.cfg has no RealWindows node - using defaults");
                    return;
                }
                B(n, "enabled", ref enabled);
                F(n, "maxDistance", ref maxDistance);
                F(n, "occluderDistance", ref occluderDistance);
                I(n, "maxInteriors", ref maxInteriors);
                B(n, "includeInactiveVessels", ref includeInactiveVessels);
                B(n, "createMissingInteriors", ref createMissingInteriors);
                I(n, "createPerFrame", ref createPerFrame);
                F(n, "groupPositionTolerance", ref groupPositionTolerance);
                F(n, "groupAngleTolerance", ref groupAngleTolerance);
                F(n, "renderScale", ref renderScale);
                S(n, "interiorRenderingPath", ref interiorRenderingPath);
                S(n, "compositeEvent", ref compositeEvent);
                B(n, "useSceneDepth", ref useSceneDepth);
                B(n, "aimIvaSunPerGroup", ref aimIvaSunPerGroup);
                F(n, "minWindowPixels", ref minWindowPixels);
                B(n, "interiorShadows", ref interiorShadows);
                F(n, "interiorRefresh", ref interiorRefresh);
                F(n, "interiorRedrawMove", ref interiorRedrawMove);
                F(n, "interiorRedrawTurn", ref interiorRedrawTurn);
                B(n, "kerbalShadows", ref kerbalShadows);
                B(n, "renderNeighbours", ref renderNeighbours);
                B(n, "showInteriorWhenInside", ref showInteriorWhenInside);
                B(n, "ivaView", ref ivaView);
                B(n, "centrifugeDeployTwist", ref centrifugeDeployTwist);
                B(n, "insideRedraw", ref insideRedraw);
                S(n, "insideCompositeEvent", ref insideCompositeEvent);
                B(n, "hideDepthMasks", ref hideDepthMasks);
                B(n, "seeIntoNeighbours", ref seeIntoNeighbours);
                B(n, "seeThroughWindows", ref seeThroughWindows);
                B(n, "seeThroughHoles", ref seeThroughHoles);
                I(n, "seeThroughHoleParts", ref seeThroughHoleParts);
                I(n, "maxUvTiles", ref maxUvTiles);
                F(n, "transmission", ref transmission);
                F(n, "feather", ref feather);
                F(n, "glassReflection", ref glassReflection);
                F(n, "glassSpecular", ref glassSpecular);
                F(n, "glassSmoothness", ref glassSmoothness);
                F(n, "glassDirt", ref glassDirt);
                F(n, "glassDirtScale", ref glassDirtScale);
                string mm = n.GetValue("materialMode");
                if (!string.IsNullOrEmpty(mm))
                {
                    mm = mm.Trim().ToLowerInvariant();
                    materialMode = mm == "off" ? 0 : mm == "union" ? 2 : mm == "trust" ? 3 : 1;
                }
                F(n, "lightThreshold", ref lightThreshold);
                F(n, "lightMinFraction", ref lightMinFraction);
                F(n, "glassThreshold", ref glassThreshold);
                F(n, "glassMinFraction", ref glassMinFraction);
                F(n, "glassMaxFraction", ref glassMaxFraction);
                F(n, "regionMinOpen", ref regionMinOpen);
                F(n, "regionMinOpenLit", ref regionMinOpenLit);
                I(n, "tintThreshold", ref tintThreshold);
                I(n, "colourTolerance", ref colourTolerance);
                F(n, "growLimit", ref growLimit);
                F(n, "islandFill", ref islandFill);
                I(n, "maskResolution", ref maskResolution);
                B(n, "dumpMasks", ref dumpMasks);
                F(n, "probeOpenFraction", ref probeOpenFraction);
                F(n, "probeWallMargin", ref probeWallMargin);
                I(n, "probeResolution", ref probeResolution);
                F(n, "hullEpsilon", ref hullEpsilon);
                F(n, "sceneDepthEpsilon", ref sceneDepthEpsilon);
                I(n, "debugMode", ref debugMode);
                B(n, "verboseLogging", ref verboseLogging);
                F(n, "flipY", ref flipY);

                string s = n.GetValue("glassTint");
                if (!string.IsNullOrEmpty(s))
                {
                    string[] c = s.Split(',');
                    if (c.Length >= 3)
                        glassTint = new Color(P(c[0]), P(c[1]), P(c[2]), 1f);
                }
                s = n.GetValue("excludeParts");
                if (!string.IsNullOrEmpty(s))
                {
                    foreach (string e in s.Split('|', ','))
                    {
                        string t = e.Trim();
                        if (t.Length > 0)
                            excludeParts.Add(NormalizePartName(t));
                    }
                }
                debugCycleKey = Key(n, "debugCycleKey", debugCycleKey);
                string tk = n.GetValue("toggleKey");
                if (tk != null)
                {
                    KeyCombo c = KeyCombo.Parse(tk);
                    if (c != null) toggleKey = c;
                    else RWLog.Warn("unknown toggleKey '{0}'", tk);
                }
                B(n, "startEnabled", ref startEnabled);
                alignmentDumpKey = Key(n, "alignmentDumpKey", alignmentDumpKey);

                renderScale = Mathf.Clamp(renderScale, 0.25f, 2f);
                createPerFrame = Math.Max(1, createPerFrame);
                maxInteriors = Math.Max(1, maxInteriors);
                RWLog.Info("settings loaded (maxDistance={0}, maxInteriors={1}, path={2}, event={3}, excluded={4})",
                    maxDistance, maxInteriors, interiorRenderingPath, compositeEvent, excludeParts.Count);
            }
            catch (Exception e)
            {
                RWLog.Error("settings.cfg failed to load - using defaults: {0}", e.Message);
            }
        }

        // KSP's runtime Part.name replaces the cfg name's underscores with dots.
        public static string NormalizePartName(string name)
        {
            return name == null ? "" : name.Replace('.', '_').Trim();
        }

        public static bool IsExcluded(Part p)
        {
            return excludeParts.Count > 0 && excludeParts.Contains(NormalizePartName(p.name));
        }

        private static float P(string s)
        {
            return float.Parse(s.Trim(), CultureInfo.InvariantCulture);
        }

        private static void F(ConfigNode n, string k, ref float v)
        {
            string s = n.GetValue(k);
            float r;
            if (!string.IsNullOrEmpty(s) && float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r))
                v = r;
        }

        private static void I(ConfigNode n, string k, ref int v)
        {
            string s = n.GetValue(k);
            int r;
            if (!string.IsNullOrEmpty(s) && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out r))
                v = r;
        }

        private static void B(ConfigNode n, string k, ref bool v)
        {
            string s = n.GetValue(k);
            bool r;
            if (!string.IsNullOrEmpty(s) && bool.TryParse(s.Trim(), out r))
                v = r;
        }

        private static void S(ConfigNode n, string k, ref string v)
        {
            string s = n.GetValue(k);
            if (!string.IsNullOrEmpty(s))
                v = s.Trim();
        }

        // "F8", "Alt+F8", "LeftControl+Shift+W": Alt/Ctrl/Control/Shift match either
        // side; anything else is a Unity KeyCode name. "None" = no key.
        public class KeyCombo
        {
            public KeyCode key;
            public bool alt, ctrl, shift;
            public readonly List<KeyCode> exact = new List<KeyCode>();

            public static KeyCombo Parse(string s)
            {
                KeyCombo c = new KeyCombo();
                foreach (string raw in s.Split('+'))
                {
                    string p = raw.Trim();
                    if (p.Length == 0) continue;
                    string l = p.ToLowerInvariant();
                    if (l == "alt") c.alt = true;
                    else if (l == "ctrl" || l == "control") c.ctrl = true;
                    else if (l == "shift") c.shift = true;
                    else
                    {
                        KeyCode k;
                        try { k = (KeyCode)Enum.Parse(typeof(KeyCode), p, true); }
                        catch (Exception) { return null; }
                        if (c.key != KeyCode.None) c.exact.Add(c.key); // earlier keys are modifiers
                        c.key = k;
                    }
                }
                return c;
            }

            public bool Down()
            {
                if (key == KeyCode.None || !Input.GetKeyDown(key)) return false;
                if (alt != (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))) return false;
                if (ctrl != (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) return false;
                if (shift != (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) return false;
                for (int i = 0; i < exact.Count; i++)
                    if (!Input.GetKey(exact[i])) return false;
                return true;
            }

            public override string ToString()
            {
                string s = (alt ? "Alt+" : "") + (ctrl ? "Ctrl+" : "") + (shift ? "Shift+" : "");
                for (int i = 0; i < exact.Count; i++) s += exact[i] + "+";
                return s + key;
            }
        }

        private static KeyCode Key(ConfigNode n, string k, KeyCode def)
        {
            string s = n.GetValue(k);
            if (string.IsNullOrEmpty(s))
                return def;
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), s.Trim(), true); }
            catch (Exception) { RWLog.Warn("unknown key '{0}' for {1}", s, k); return def; }
        }
    }
}
