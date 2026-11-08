// Window masks: decided once per part type and hull texture, in hull UV space.
//
// The GPU draws the hull's sub-meshes flattened into UV space, so each texel
// gets the gloss, lightmap and IVA-opening evidence of its point on the hull.
// That is read back and MaskRegions turns it into a mask. The hull pass then
// only samples the mask, so outlines are fixed to the surface and cost nothing
// per frame beyond one texture read.
//
// Sub-meshes with no usable lightmap or gloss get an evidence mask: candidate
// regions are the IVA's openings behind the hull, and only tinted (glass-
// coloured) paint counts; the outline comes from the paint as usual. If that
// finds nothing, the sub-mesh is solid when the part already has windows from
// its other textures, and uses the probe per pixel only when nothing else on
// the part knows (PortalPipeline). Sub-meshes with no colour map have no mask
// and use the probe per pixel. Sub-meshes whose UVs run outside 0..1 are drawn
// once per UV tile they touch, shifted into 0..1; the bake blends with max, so
// a texel shared by several hull points is open if any of them is, and the mask
// wraps (Repeat) when sampled. Only beyond `maxUvTiles` tiles does a sub-mesh go
// without a mask. A lightmap/gloss mask that looked and found no window keeps
// the sub-mesh solid, and so does a window-named mesh whose islands were all
// rejected.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealWindows
{
    public class MaskData
    {
        public string key;
        public Texture2D tex;   // null until baked, and for ever if the probe decides instead
        public bool done;
        public bool hasWindow;  // the baked mask has at least one window texel
        public bool fromOpenings; // evidence mask: no usable lightmap or gloss on this texture
        public bool named;        // a window-named mesh: islands with an opening behind them, trimmed to their glass-coloured core
        public bool tiled;        // drawn from UVs outside 0..1: sampled with Repeat
        public bool namedRejected; // a window-named mesh whose every island was rejected: solid
    }

    public static class HullMasks
    {
        private static readonly Dictionary<string, MaskData> cache = new Dictionary<string, MaskData>();
        private static readonly Dictionary<Mesh, RectInt> uvTiles = new Dictionary<Mesh, RectInt>();

        private static readonly int PID_ProbeTex = Shader.PropertyToID("_RW_ProbeTex");
        private static readonly int PID_WorldToProbe = Shader.PropertyToID("_RW_WorldToProbe");
        private static readonly int PID_Sources = Shader.PropertyToID("_RW_Sources");
        private static readonly int PID_SourceCount = Shader.PropertyToID("_RW_SourceCount");
        private static readonly int PID_GlassTex = Shader.PropertyToID("_RW_GlassTex");
        private static readonly int PID_GlassST = Shader.PropertyToID("_RW_GlassST");
        private static readonly int PID_GlassChannel = Shader.PropertyToID("_RW_GlassChannel");
        private static readonly int PID_LightTex = Shader.PropertyToID("_RW_LightTex");
        private static readonly int PID_LightST = Shader.PropertyToID("_RW_LightST");
        private static readonly int PID_UvOffset = Shader.PropertyToID("_RW_UvOffset");
        private static readonly int PID_HasLight = Shader.PropertyToID("_RW_HasLight");

        public static MaskData For(PartPortal p, Renderer r, GlassInfo g)
        {
            if (g == null || !UvMaskable(r))
                return null;
            if (g.channel == 6)
            {
                // author's transparent glass is trusted whole; a window-named mesh gets
                // a mask when it has a colour map (else it is trusted whole too)
                if (!g.named || g.albedo == null || RWSettings.materialMode == 0)
                    return null;
                string nk = p.probeKey + "|" + r.name + "|" + Id(g.albedo) + "|named";
                MaskData nd;
                if (!cache.TryGetValue(nk, out nd))
                {
                    nd = new MaskData { key = nk, named = true };
                    cache[nk] = nd;
                }
                return nd;
            }
            bool fromOpenings = !g.informative;
            if (fromOpenings && (g.albedo == null || RWSettings.materialMode == 0))
                return null; // nothing to judge the paint by: probe per pixel
            string key = p.probeKey + "|" + Id(g.tex) + "|" + Id(g.light) + "|" + Id(g.albedo) + (fromOpenings ? "|open" : "");
            MaskData d;
            if (!cache.TryGetValue(key, out d))
            {
                d = new MaskData { key = key, fromOpenings = fromOpenings };
                cache[key] = d;
            }
            return d;
        }

        // For the log: why a sub-mesh has no mask.
        public static string WhyNoMask(Renderer r, GlassInfo g)
        {
            if (g == null) return "no material info";
            if (r == null) return "no renderer";
            if (!UvMaskable(r)) return "UVs spread over more than " + RWSettings.maxUvTiles + " tiles (per-pixel probe, or solid if the part has other windows)";
            if (g.albedo == null) return "no colour map (per-pixel probe, or solid if the part has other windows)";
            if (RWSettings.materialMode == 0) return "materialMode Off";
            return "unknown";
        }

        private static int Id(Texture t)
        {
            return t != null ? t.GetInstanceID() : 0;
        }

        // The UV tiles a mesh touches: [x0..x1] x [y0..y1], (0,0,0,0) for UVs in 0..1.
        private static bool UvTiles(Renderer r, out RectInt tiles)
        {
            tiles = new RectInt(0, 0, 0, 0);
            SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
            MeshFilter mf = smr == null ? r.GetComponent<MeshFilter>() : null;
            Mesh mesh = smr != null ? smr.sharedMesh : mf != null ? mf.sharedMesh : null;
            if (mesh == null)
                return false;
            if (uvTiles.TryGetValue(mesh, out tiles))
                return true;
            tiles = new RectInt(0, 0, 0, 0);
            if (mesh.isReadable)
            {
                Vector2[] uv = mesh.uv;
                if (uv.Length > 0)
                {
                    float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                    for (int i = 0; i < uv.Length; i++)
                    {
                        x0 = Mathf.Min(x0, uv[i].x); x1 = Mathf.Max(x1, uv[i].x);
                        y0 = Mathf.Min(y0, uv[i].y); y1 = Mathf.Max(y1, uv[i].y);
                    }
                    int tx0 = Mathf.FloorToInt(x0 + 0.001f), ty0 = Mathf.FloorToInt(y0 + 0.001f);
                    int tx1 = Mathf.Max(tx0, Mathf.CeilToInt(x1 - 0.001f) - 1), ty1 = Mathf.Max(ty0, Mathf.CeilToInt(y1 - 0.001f) - 1);
                    // RectInt as min corner + (count - 1)
                    tiles = new RectInt(tx0, ty0, tx1 - tx0, ty1 - ty0);
                }
            }
            uvTiles[mesh] = tiles;
            return true;
        }

        private static bool UvMaskable(Renderer r)
        {
            RectInt t;
            return UvTiles(r, out t) && (t.width + 1) * (t.height + 1) <= RWSettings.maxUvTiles;
        }

        // Bakes the first unfinished mask of this part. False if none is left.
        public static bool BakeNext(PartPortal p, ProbeData probe, Material hullMat)
        {
            for (int i = 0; i < p.exterior.Count; i++)
            {
                MaskData mask = p.exterior[i].mask;
                if (mask == null || mask.done)
                    continue;
                mask.done = true; // whatever happens, never retry every frame
                try
                {
                    Bake(p, probe, hullMat, mask, p.exterior[i].glass);
                }
                catch (Exception e)
                {
                    RWLog.Error("mask bake failed for {0}: {1}", mask.key, e);
                }
                return true;
            }
            return false;
        }

        private static void Bake(PartPortal p, ProbeData probe, Material hullMat, MaskData mask, GlassInfo first)
        {
            int w, h;
            Size(first, out w, out h);
            int n = w * h;

            RenderTexture prev = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Color32[] px;
            using (CommandBuffer cb = new CommandBuffer { name = "RealWindows mask bake" })
            {
                cb.SetRenderTarget(rt);
                cb.ClearRenderTarget(false, true, Color.clear, 1f);
                cb.SetGlobalTexture(PID_ProbeTex, probe.tex);
                cb.SetGlobalMatrix(PID_WorldToProbe, Probes.WorldToProbe(p, probe));
                cb.SetGlobalVectorArray(PID_Sources, probe.sources);
                cb.SetGlobalFloat(PID_SourceCount, probe.sourceCount);
                cb.SetGlobalFloat("_RW_WallMargin", RWSettings.probeWallMargin);
                for (int i = 0; i < p.exterior.Count; i++)
                {
                    DrawItem d = p.exterior[i];
                    if (d.mask != mask || d.r == null || !d.r.gameObject.activeInHierarchy)
                        continue;
                    GlassInfo g = d.glass;
                    cb.SetGlobalFloat(PID_GlassChannel, g.channel == 6 ? 0 : g.channel);
                    cb.SetGlobalTexture(PID_GlassTex, g.tex != null ? g.tex : Texture2D.blackTexture);
                    cb.SetGlobalVector(PID_GlassST, g.st);
                    cb.SetGlobalFloat(PID_HasLight, g.light != null ? 1f : 0f);
                    cb.SetGlobalTexture(PID_LightTex, g.light != null ? g.light : Texture2D.blackTexture);
                    cb.SetGlobalVector(PID_LightST, g.lightST);
                    RectInt tiles;
                    UvTiles(d.r, out tiles);
                    for (int ty = tiles.y; ty <= tiles.y + tiles.height; ty++)
                        for (int tx = tiles.x; tx <= tiles.x + tiles.width; tx++)
                        {
                            if (tx != 0 || ty != 0)
                                mask.tiled = true;
                            cb.SetGlobalVector(PID_UvOffset, new Vector4(-tx, -ty, 0, 0));
                            cb.DrawRenderer(d.r, hullMat, d.sub, 1);
                        }
                }
                Graphics.ExecuteCommandBuffer(cb);
            }
            px = ReadBack(rt, w, h);
            RenderTexture.ReleaseTemporary(rt);

            byte[] gloss = new byte[n], light = new byte[n], open = new byte[n], cover = new byte[n];
            for (int i = 0; i < n; i++)
            {
                gloss[i] = px[i].r;
                open[i] = px[i].g;
                cover[i] = px[i].b;
                light[i] = px[i].a;
            }

            // Colour straight from the texture: Blit keeps texture orientation by
            // construction, so it also tells us if the UV-space draw came out flipped.
            byte[] rgb = new byte[n * 3];
            int tolerance = RWSettings.colourTolerance;
            if (first.albedo != null)
            {
                Color32[] c = BlitRead(first.albedo, w, h);
                for (int i = 0; i < n; i++)
                {
                    rgb[i * 3] = c[i].r;
                    rgb[i * 3 + 1] = c[i].g;
                    rgb[i * 3 + 2] = c[i].b;
                }
            }
            else
            {
                tolerance = -1; // no colour, no boundary search
            }

            bool flipped = IsFlipped(first, gloss, light, cover, w, h);
            if (flipped)
            {
                FlipRows(gloss, w, h);
                FlipRows(light, w, h);
                FlipRows(open, w, h);
                FlipRows(cover, w, h);
            }
            RenderTexture.active = prev;

            MaskParams mp = new MaskParams
            {
                seedGloss = (byte)Mathf.Clamp(RWSettings.glassThreshold * 255f, 1f, 255f),
                seedLight = (byte)Mathf.Clamp(RWSettings.lightThreshold * 255f, 1f, 255f),
                openEvidence = (byte)Mathf.Clamp(Mathf.Ceil(RWSettings.probeOpenFraction * 255f), 1f, 255f),
                minOpenFraction = RWSettings.regionMinOpen,
                minOpenLit = RWSettings.regionMinOpenLit,
                tintThreshold = RWSettings.tintThreshold,
                hasLightmap = first.light != null,
                seedFromOpenings = mask.fromOpenings,
                seedAllCovered = mask.named,
                minArea = Mathf.Max(8, n / 40000),
                colourTolerance = tolerance,
                growLimit = RWSettings.growLimit,
                islandFill = RWSettings.islandFill,
                acceptAll = RWSettings.materialMode == 3,
                addOpenings = RWSettings.materialMode == 2
            };
            List<string> report = new List<string>();
            byte[] result = MaskRegions.Build(w, h, gloss, light, open, cover, rgb, mp, report);

            int windowTexels = 0;
            for (int i = 0; i < n; i++)
                if (result[i] >= 128) windowTexels++;
            mask.hasWindow = windowTexels > 0;

            if (RWSettings.dumpMasks)
                Dump(mask.key, w, h, result, open, gloss, light, cover);

            int rejected = 0;
            string windows = "";
            for (int i = 0; i < report.Count; i++)
            {
                if (report[i].StartsWith("rejected")) rejected++;
                else windows += " [" + report[i] + "]";
            }
            if (windows.Length > 600)
                windows = windows.Substring(0, 600) + " ...";
            RWLog.Info("mask {0}: {1}x{2}{3}, {4:P1} window, {5} regions rejected:{6}",
                mask.key, w, h, flipped ? " (flipped)" : "", (float)windowTexels / n, rejected, windows);

            if (mask.fromOpenings && windowTexels == 0)
                return; // no glass-coloured opening: solid or per-pixel probe, decided per part at render time
            if (mask.named && windowTexels == 0)
            {
                // islands were judged and none has an IVA opening behind it: not a window
                // (a mesh with no islands at all stays trusted whole)
                mask.namedRejected = report.Count > 0;
                return;
            }
            if (report.Count == 0 && first.light == null)
                return; // nothing to go on: this sub-mesh stays on the per-pixel probe

            Texture2D tex = new Texture2D(w, h, TextureFormat.R8, false, true)
            {
                name = "RW mask " + mask.key,
                filterMode = FilterMode.Bilinear,
                wrapMode = mask.tiled ? TextureWrapMode.Repeat : TextureWrapMode.Clamp
            };
            tex.LoadRawTextureData(result);
            tex.Apply(false, true);
            mask.tex = tex;
        }

        private static void Size(GlassInfo g, out int w, out int h)
        {
            Texture t = g.albedo != null ? g.albedo : g.tex != null ? g.tex : g.light;
            w = t != null ? t.width : 512;
            h = t != null ? t.height : 512;
            int cap = Mathf.Clamp(RWSettings.maskResolution, 64, 4096);
            while (w > cap || h > cap)
            {
                w = Mathf.Max(1, w / 2);
                h = Mathf.Max(1, h / 2);
            }
        }

        private static Color32[] ReadBack(RenderTexture rt, int w, int h)
        {
            Texture2D read = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                return read.GetPixels32();
            }
            finally
            {
                UnityEngine.Object.Destroy(read);
            }
        }

        private static Color32[] BlitRead(Texture src, int w, int h)
        {
            RenderTexture tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                Graphics.Blit(src, tmp);
                return ReadBack(tmp, w, h);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(tmp);
            }
        }

        // Compares the UV-space draw with the source texture, both ways up.
        private static bool IsFlipped(GlassInfo g, byte[] gloss, byte[] light, byte[] cover, int w, int h)
        {
            bool useLight = g.light != null;
            Texture src = useLight ? g.light : g.tex;
            if (src == null)
                return false;
            Vector4 st = useLight ? g.lightST : g.st;
            if (st != new Vector4(1, 1, 0, 0))
                return false;
            Color32[] c = BlitRead(src, w, h);
            int channel = useLight ? 5 : g.channel;
            byte[] drawn = useLight ? light : gloss;
            long same = 0, mirrored = 0;
            for (int y = 0; y < h; y += 2)
            {
                for (int x = 0; x < w; x += 2)
                {
                    int i = y * w + x;
                    if (cover[i] == 0)
                        continue;
                    same += Math.Abs(drawn[i] - (int)(MaterialGlass.Channel(c[i], channel) * 255f));
                    mirrored += Math.Abs(drawn[i] - (int)(MaterialGlass.Channel(c[(h - 1 - y) * w + x], channel) * 255f));
                }
            }
            return mirrored * 2 < same;
        }

        private static void FlipRows(byte[] v, int w, int h)
        {
            byte[] row = new byte[w];
            for (int y = 0; y < h / 2; y++)
            {
                int a = y * w, b = (h - 1 - y) * w;
                Array.Copy(v, a, row, 0, w);
                Array.Copy(v, b, v, a, w);
                Array.Copy(row, 0, v, b, w);
            }
        }

        // R = result, G = IVA opening evidence, B = strongest of gloss and lightmap,
        // A = 255 where the hull mesh covers the texel
        private static void Dump(string key, int w, int h, byte[] result, byte[] open, byte[] gloss, byte[] light, byte[] cover)
        {
            Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            try
            {
                Color32[] px = new Color32[w * h];
                for (int i = 0; i < px.Length; i++)
                    px[i] = new Color32(result[i], open[i], Math.Max(gloss[i], light[i]), (byte)(cover[i] != 0 ? 255 : 96));
                t.SetPixels32(px);
                string dir = Path.Combine(RWSettings.RootPath, "PluginData/masks");
                Directory.CreateDirectory(dir);
                string name = key;
                foreach (char c in Path.GetInvalidFileNameChars())
                    name = name.Replace(c, '_');
                File.WriteAllBytes(Path.Combine(dir, name + ".png"), t.EncodeToPNG());
            }
            catch (Exception e)
            {
                RWLog.Warn("mask dump failed for {0}: {1}", key, e.Message);
            }
            finally
            {
                UnityEngine.Object.Destroy(t);
            }
        }
    }
}
