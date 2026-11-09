// Region logic of the window mask, in hull UV space.
//
//   1. Candidates: connected regions the lightmap lights up or the gloss map
//      marks as shiny.
//   2. Confirmation, per region, kept or dropped whole, so glossy metal goes
//      away completely and a gap in the IVA evidence can't tear a window apart.
//      Lit and tinted (glass is bluish): any opening nearby will do.
//      Lit but neutral (lamps, strips): a real opening behind most of it, and
//      only if nothing lit and tinted exists, which would mark the real windows.
//      Gloss only: a real opening behind most of it, and only on a texture with
//      no lightmap. Where one exists, what it leaves dark isn't a window.
//      Unlit warm paint (gold foil, copper, brown) is never a window, and
//      untinted gloss is one only on a part with no lightmap anywhere.
//      Named as a window (mesh name): every UV island is a candidate, kept if an
//      opening is behind most of it; if one of them is tinted, dark unlit neutral
//      ones are frame. If none is kept, the mesh is solid.
//   3. Boundary, from the mesh: a pane is often its own UV island. A confirmed
//      region that fills most of its island takes the whole island, which is
//      the exact outline even where the paint just fades out. When the window is
//      tinted glass, unlit dark neutral texels of the island are frame and stay
//      out (panes sharing one island with their black frames and mullions).
//   4. Boundary, from the paint: a confirmed region keeps only the texels painted
//      like its glass (its core); a lightmap or gloss blob that overhangs the
//      pane onto the frame is cut back, so no blocky outline survives. The core
//      then floods outward over similar colour until the colour jumps. Glass is roughly one colour inside
//      a dark frame, so the frame gives the outline, however soft the gloss is.
//      Around tinted glass the flood never enters bright neutral paint (lightmap
//      glow on the panel) or dark neutral paint the lightmap leaves unlit (the
//      frame, even where the pane's paint fades softly into it).
//   5. Clean-up: close small gaps, soften the edge for bilinear sampling.

using System;
using System.Collections.Generic;

namespace RealWindows
{
    public class MaskParams
    {
        public byte seedGloss = 102;       // gloss (0-255) that makes a texel a candidate
        public byte seedLight = 128;       // lightmap value (0-255) that makes a texel a candidate
        public byte openEvidence = 1;      // evidence (0-255) that counts as "opening behind this texel"
        public float minOpenFraction = 0.5f;  // share of a region that must have an opening right behind it
        public float minOpenLit = 0.02f;      // share of a lit, tinted region with an opening behind or near it
        public int tintThreshold = 8;      // blue minus red (0-255) that makes a region's colour tinted
        public bool hasLightmap;           // the lightmap rules: gloss alone proposes nothing
        public bool seedFromOpenings;      // no usable lightmap or gloss: candidates are the IVA openings, glass must be tinted
        public bool seedAllCovered;        // a window-named mesh: every UV island is a candidate, kept if an opening is behind it
        public int minArea = 24;           // texels; smaller candidates are noise
        public int colourTolerance = 18;   // 0-255, per channel: largest colour step inside one window
        public float growLimit = 0.75f;    // flood distance, as a multiple of sqrt(region area)
        public float islandFill = 0.8f;    // a region this share of its UV island takes the whole island
        public bool acceptAll;             // Trust: skip the IVA confirmation
        public bool addOpenings;           // Union: openings without gloss are windows too
        public bool partLit;               // another texture of the part has a lightmap: its windows are lit there
    }

    public static class MaskRegions
    {
        public const byte Window = 255;
        public const byte Rejected = 64;   // below the 0.5 cut: solid, but debug view 5 can show it

        // gloss, light, open, cover: one byte per texel. rgb: three bytes per texel.
        public static byte[] Build(int w, int h, byte[] gloss, byte[] light, byte[] open, byte[] cover, byte[] rgb,
            MaskParams p, List<string> report)
        {
            int n = w * h;
            int[] label = new int[n];                  // 0 none, else region id
            byte[] state = new byte[n];
            bool[] openHere = Threshold(open, cover, p.openEvidence);
            bool[] openNear = Dilate(openHere, w, h, Math.Max(2, w / 128));

            List<Region> regions = new List<Region>();
            int[] queue = new int[n];
            for (int i = 0; i < n; i++)
            {
                if (cover[i] == 0 || label[i] != 0 || !IsSeed(i, gloss, light, open, p))
                    continue;
                Region r = new Region { id = regions.Count + 1 };
                Collect(i, r, label, queue, w, h, gloss, light, open, cover, p, openHere, openNear, rgb);
                regions.Add(r);
            }

            bool tintedLitExists = false, tintedGlossExists = false, tintedNamedExists = false;
            for (int k = 0; k < regions.Count; k++)
            {
                Region r = regions[k];
                r.lit = r.litCount * 2 >= r.area;
                r.tinted = r.meanB - r.meanR >= p.tintThreshold;
                if (r.area >= p.minArea && r.lit && r.tinted && (float)r.openNear / r.area >= p.minOpenLit)
                    tintedLitExists = true;
                if (r.area >= p.minArea && !r.lit && r.tinted && (float)r.openHere / r.area >= p.minOpenFraction)
                    tintedGlossExists = true;
                if (r.area >= p.minArea && r.tinted && (float)r.openNear / r.area >= p.minOpenLit)
                    tintedNamedExists = true;
            }

            int accepted = 0, rejected = 0;
            for (int k = 0; k < regions.Count; k++)
            {
                Region r = regions[k];
                if (r.area < p.minArea)
                {
                    r.fate = Fate.Noise;
                    continue;
                }
                float near = (float)r.openNear / r.area, here = (float)r.openHere / r.area;
                bool ok;
                // Named as a window: most of the island needs an opening right behind it,
                // as for gloss-only windows, since a mesh named like glass can sit where
                // the IVA has no opening at all (an extending telescope: under 25 % open;
                // real panes 65-100 %). Where the mesh has tinted glass, its dark, unlit,
                // neutral islands are frame (black rings modelled in the same mesh as
                // round panes). Light neutral islands stay: some panes are UV-mapped onto
                // plain hull paint.
                bool darkFrame = !r.tinted && !r.lit && Math.Max(r.meanR, Math.Max(r.meanG, r.meanB)) <= p.colourTolerance * 3;
                // Warm paint (red well above blue: gold foil, copper, brown) is never glass.
                // A shiny gold insulation foil passed as a gloss-only window wherever a big,
                // sparse IVA had openings behind it (the SSPX 'Cronus' centrifuge's end caps).
                bool warm = !r.lit && r.meanR - r.meanB >= p.tintThreshold * 3 && r.meanR >= r.meanG;
                if (p.seedAllCovered) ok = here >= p.minOpenFraction && !(darkFrame && tintedNamedExists) && !warm;
                else if (p.seedFromOpenings) ok = r.tinted; // the opening is given; only glass-coloured paint counts
                else if (p.acceptAll) ok = true;
                else if (r.lit && r.tinted) ok = near >= p.minOpenLit;
                else if (r.lit) ok = !tintedLitExists && here >= p.minOpenFraction;
                // Neutral gloss counts only on parts with no lightmap at all: where the
                // author lit the windows on another texture, shiny grey paint here is
                // metal (the SSPX 'Cronus' centrifuge's struts and spin core).
                else ok = !p.hasLightmap && here >= p.minOpenFraction && (r.tinted || (!tintedGlossExists && !p.partLit)) && !warm;
                r.fate = ok ? Fate.Window : Fate.Rejected;
                if (ok) accepted++; else rejected++;
                if (report != null)
                    report.Add(string.Format("{0}{1} area {2} open {3:P0} colour ({4},{5},{6})",
                        ok ? "window" : "rejected", r.lit ? " lit" : "", r.area, here, r.meanR, r.meanG, r.meanB));
            }

            for (int i = 0; i < n; i++)
            {
                if (label[i] == 0) continue;
                Fate f = regions[label[i] - 1].fate;
                if (f == Fate.Window && IsCore(i, regions[label[i] - 1], rgb, p)) state[i] = Window;
                else if (f == Fate.Rejected) state[i] = Rejected;
            }

            if (accepted > 0)
            {
                // a named mesh's islands are the candidates already; refilling them would undo the rim trim
                int filled = p.seedAllCovered ? 0 : FillIslands(w, h, state, cover, rgb, light, p, queue);
                if (report != null && filled > 0)
                    report.Add(string.Format("islands {0}", filled));
                Grow(w, h, state, label, regions, cover, rgb, light, p, queue);
            }

            if (p.addOpenings)
            {
                for (int i = 0; i < n; i++)
                    if (state[i] == 0 && cover[i] != 0 && open[i] >= p.openEvidence)
                    {
                        state[i] = Window;
                        accepted++;
                    }
            }

            if (accepted == 0)
                return state;

            Close(state, w, h, Math.Max(1, w / 512));
            return Soften(state, w, h);
        }

        // Bright neutral paint inside tinted glass's reach: a lightmap's glow spilling
        // onto the panel next to the pane. Dark neutral rims of the glass don't count.
        private static bool IsPanel(int i, Region r, byte[] rgb, MaskParams p)
        {
            return r.tinted && rgb[i * 3 + 2] - rgb[i * 3] < p.tintThreshold / 2
                && rgb[i * 3] > r.meanR + p.colourTolerance * 3;
        }

        // Dark neutral paint next to tinted glass, where the lightmap doesn't light
        // it: the frame. A soft edge on the pane fades into it, and the flood would
        // follow that fade and spread over the whole frame (the NFS 'Rhea' pod's
        // black window frames). Dark reflections painted into the glass are lit
        // with the pane (the stock Cupola), so they stay.
        private static bool IsFrame(int i, Region r, byte[] rgb, byte[] light, MaskParams p)
        {
            return r.tinted && Unlit(i, light, p) && DarkNeutral(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2],
                Math.Max(r.meanR, Math.Max(r.meanG, r.meanB)), p);
        }

        private static bool Unlit(int i, byte[] light, MaskParams p)
        {
            return p.hasLightmap && light != null && light[i] < p.seedLight;
        }

        // No channel within the colour tolerance of the glass's brightest, and blue
        // not above red: frame paint, not glass.
        private static bool DarkNeutral(int r, int g, int b, int glassTop, MaskParams p)
        {
            return b - r < p.tintThreshold / 2 && Math.Max(r, Math.Max(g, b)) < glassTop - p.colourTolerance;
        }

        // Painted like the region's glass: within twice the colour tolerance of its
        // mean colour. The rest of the region (frame, rim, panel under a blocky or
        // soft lightmap) only becomes window if the colour flood reaches it.
        private static bool IsCore(int i, Region r, byte[] rgb, MaskParams p)
        {
            if (p.colourTolerance < 0)
                return true; // no colour map: keep the region as found
            if (IsPanel(i, r, rgb, p))
                return false;
            int t = p.colourTolerance * 2;
            return Math.Abs(rgb[i * 3] - r.meanR) <= t
                && Math.Abs(rgb[i * 3 + 1] - r.meanG) <= t
                && Math.Abs(rgb[i * 3 + 2] - r.meanB) <= t;
        }

        private static bool IsSeed(int i, byte[] gloss, byte[] light, byte[] open, MaskParams p)
        {
            if (p.seedAllCovered)
                return true;
            if (p.seedFromOpenings)
                return open[i] >= p.openEvidence;
            return gloss[i] >= p.seedGloss || light[i] >= p.seedLight;
        }

        private enum Fate { Noise, Window, Rejected }

        private class Region
        {
            public int id, area, openHere, openNear, litCount;
            public bool lit, tinted;
            public long sumR, sumG, sumB;
            public int meanR, meanG, meanB;
            public Fate fate;
        }

        private static void Collect(int start, Region r, int[] label, int[] queue, int w, int h,
            byte[] gloss, byte[] light, byte[] open, byte[] cover, MaskParams p, bool[] openHere, bool[] openNear, byte[] rgb)
        {
            int head = 0, tail = 0;
            queue[tail++] = start;
            label[start] = r.id;
            while (head < tail)
            {
                int i = queue[head++];
                r.area++;
                if (openHere[i]) r.openHere++;
                if (openNear[i]) r.openNear++;
                if (light[i] >= p.seedLight) r.litCount++;
                r.sumR += rgb[i * 3];
                r.sumG += rgb[i * 3 + 1];
                r.sumB += rgb[i * 3 + 2];
                int x = i % w, y = i / w;
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y < h - 1) Visit(i + w);
            }
            r.meanR = (int)(r.sumR / r.area);
            r.meanG = (int)(r.sumG / r.area);
            r.meanB = (int)(r.sumB / r.area);

            void Visit(int j)
            {
                if (label[j] == 0 && cover[j] != 0 && IsSeed(j, gloss, light, open, p))
                {
                    label[j] = r.id;
                    queue[tail++] = j;
                }
            }
        }

        // Walks every UV island; a window region taking up enough of one fills it.
        // Texels added here have no region label, so Grow leaves them alone. Where
        // the island's window texels are tinted glass, unlit dark neutral texels
        // are frame (as in IsFrame) and aren't filled.
        private static int FillIslands(int w, int h, byte[] state, byte[] cover, byte[] rgb, byte[] light, MaskParams p, int[] queue)
        {
            int filled = 0;
            bool[] seen = new bool[state.Length];
            for (int start = 0; start < state.Length; start++)
            {
                if (cover[start] == 0 || seen[start])
                    continue;
                int head = 0, tail = 0, windows = 0, rejected = 0;
                long sr = 0, sg = 0, sb = 0;
                queue[tail++] = start;
                seen[start] = true;
                while (head < tail)
                {
                    int i = queue[head++];
                    if (state[i] == Window)
                    {
                        windows++;
                        if (rgb != null) { sr += rgb[i * 3]; sg += rgb[i * 3 + 1]; sb += rgb[i * 3 + 2]; }
                    }
                    else if (state[i] == Rejected) rejected++;
                    int x = i % w, y = i / w;
                    if (x > 0) Visit(i - 1);
                    if (x < w - 1) Visit(i + 1);
                    if (y > 0) Visit(i - w);
                    if (y < h - 1) Visit(i + w);
                }
                if (windows < p.minArea || windows < tail * p.islandFill || rejected > windows)
                    continue;
                filled++;
                bool frames = false;
                int glassTop = 0;
                if (rgb != null && p.colourTolerance >= 0)
                {
                    int mr = (int)(sr / windows), mg = (int)(sg / windows), mb = (int)(sb / windows);
                    frames = mb - mr >= p.tintThreshold;
                    glassTop = Math.Max(mr, Math.Max(mg, mb));
                }
                for (int k = 0; k < tail; k++)
                {
                    int i = queue[k];
                    if (frames && state[i] != Window)
                    {
                        if (Unlit(i, light, p) && DarkNeutral(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], glassTop, p))
                            continue; // the frame around tinted glass
                    }
                    state[i] = Window;
                }

                void Visit(int j)
                {
                    if (cover[j] != 0 && !seen[j])
                    {
                        seen[j] = true;
                        queue[tail++] = j;
                    }
                }
            }
            return filled;
        }

        // Breadth-first flood from each window region. Every texel carries a slowly
        // adapting reference colour: a gentle gradient across the glass is followed,
        // a sharp step (the frame) stops the flood.
        private static void Grow(int w, int h, byte[] state, int[] label, List<Region> regions,
            byte[] cover, byte[] rgb, byte[] light, MaskParams p, int[] queue)
        {
            int n = w * h;
            float[] refc = new float[n * 3];
            int[] depth = new int[n];
            int head = 0, tail = 0;
            int tol = p.colourTolerance;

            for (int i = 0; i < n; i++)
            {
                if (state[i] != Window || label[i] == 0)
                    continue;
                Region r = regions[label[i] - 1];
                // seeds on the frame itself must not start a flood along the frame
                if (Math.Abs(rgb[i * 3] - r.meanR) > tol * 2
                    || Math.Abs(rgb[i * 3 + 1] - r.meanG) > tol * 2
                    || Math.Abs(rgb[i * 3 + 2] - r.meanB) > tol * 2)
                    continue;
                refc[i * 3] = rgb[i * 3];
                refc[i * 3 + 1] = rgb[i * 3 + 1];
                refc[i * 3 + 2] = rgb[i * 3 + 2];
                queue[tail++] = i;
            }

            while (head < tail)
            {
                int i = queue[head++];
                Region r = regions[label[i] - 1];
                if (depth[i] >= (int)(Math.Sqrt(r.area) * p.growLimit) + 2)
                    continue;
                int x = i % w, y = i / w;
                if (x > 0) Step(i, i - 1);
                if (x < w - 1) Step(i, i + 1);
                if (y > 0) Step(i, i - w);
                if (y < h - 1) Step(i, i + w);
            }

            void Step(int from, int to)
            {
                if (state[to] != 0 || cover[to] == 0)
                    return;
                int c = to * 3, f = from * 3;
                Region rf = regions[label[from] - 1];
                if (IsPanel(to, rf, rgb, p) || IsFrame(to, rf, rgb, light, p))
                    return;
                if (Math.Abs(rgb[c] - refc[f]) > tol
                    || Math.Abs(rgb[c + 1] - refc[f + 1]) > tol
                    || Math.Abs(rgb[c + 2] - refc[f + 2]) > tol)
                    return;
                state[to] = Window;
                label[to] = label[from];
                depth[to] = depth[from] + 1;
                refc[c] = refc[f] + (rgb[c] - refc[f]) * 0.25f;
                refc[c + 1] = refc[f + 1] + (rgb[c + 1] - refc[f + 1]) * 0.25f;
                refc[c + 2] = refc[f + 2] + (rgb[c + 2] - refc[f + 2]) * 0.25f;
                queue[tail++] = to;
            }
        }

        private static bool[] Threshold(byte[] v, byte[] cover, byte t)
        {
            bool[] o = new bool[v.Length];
            for (int i = 0; i < v.Length; i++)
                o[i] = cover[i] != 0 && v[i] >= t;
            return o;
        }

        private static bool[] Dilate(bool[] src, int w, int h, int r)
        {
            bool[] tmp = new bool[src.Length], dst = new bool[src.Length];
            for (int y = 0; y < h; y++)
            {
                int last = -1000000;
                for (int x = 0; x < w + r; x++)
                {
                    if (x < w && src[y * w + x]) last = x;
                    int o = x - r;
                    if (o >= 0 && o < w && x - last <= 2 * r) tmp[y * w + o] = true;
                }
            }
            for (int x = 0; x < w; x++)
            {
                int last = -1000000;
                for (int y = 0; y < h + r; y++)
                {
                    if (y < h && tmp[y * w + x]) last = y;
                    int o = y - r;
                    if (o >= 0 && o < h && y - last <= 2 * r) dst[o * w + x] = true;
                }
            }
            return dst;
        }

        // Morphological closing of the window texels; rejected texels are left alone.
        private static void Close(byte[] state, int w, int h, int r)
        {
            bool[] win = new bool[state.Length];
            for (int i = 0; i < state.Length; i++)
                win[i] = state[i] == Window;
            bool[] grown = Dilate(win, w, h, r);
            bool[] inv = new bool[state.Length];
            for (int i = 0; i < state.Length; i++)
                inv[i] = !grown[i];
            bool[] shrunkInv = Dilate(inv, w, h, r);
            for (int i = 0; i < state.Length; i++)
                if (!shrunkInv[i] && state[i] == 0)
                    state[i] = Window;
        }

        // 3x3 box over the window texels; the jagged texel edge becomes a ramp that
        // bilinear sampling cuts at 0.5 into a smooth outline.
        private static byte[] Soften(byte[] state, int w, int h)
        {
            byte[] o = new byte[state.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int sum = 0, count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= h) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= w) continue;
                            count++;
                            if (state[yy * w + xx] == Window) sum += 255;
                        }
                    }
                    int i = y * w + x;
                    byte v = (byte)(sum / count);
                    o[i] = state[i] == Rejected && v < Rejected ? Rejected : v;
                }
            }
            return o;
        }
    }
}
