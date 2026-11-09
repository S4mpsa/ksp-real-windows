// Per-part cached state: exterior renderers (for the hull ID pass) and, for
// parts with an IVA, the interior's renderers classified for the passes.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealWindows
{
    public struct DrawItem
    {
        public Renderer r;
        public int sub;
        public GlassInfo glass;   // hull sub-meshes of IVA parts only
        public MaskData mask;     // baked window mask of this sub-mesh, if it can have one
        public bool solid;        // never a window (flag decals)
        public DrawItem(Renderer r, int sub) { this.r = r; this.sub = sub; glass = null; mask = null; solid = false; }
    }

    public class PartPortal
    {
        public readonly Part part;
        public readonly bool hasIva;     // part config has an INTERNAL
        public readonly bool excluded;
        public readonly bool rootSpins;  // Kerbalism GravityRing: the whole interior root turns with the ring
        private readonly ModuleRealWindows toggle; // the part menu's Transparent/Opaque switch, if patched in

        // exterior (all parts): drawn into the hull ID pass
        public readonly List<DrawItem> exterior = new List<DrawItem>();
        private float nextExteriorScan;

        // interior (IVA parts only)
        public InternalModel model;      // reference the caches below belong to
        public readonly List<Renderer> interiorRenderers = new List<Renderer>();   // every Mesh/Skinned renderer
        public readonly List<Renderer> depthMaskRenderers = new List<Renderer>();  // only depth-mask materials (FreeIva hatch doorways, window plugs)
        public readonly List<DrawItem> interiorShell = new List<DrawItem>();       // opaque, no kerbals or props: front pass and probe bake
        public readonly List<Renderer> nonKerbalRenderers = new List<Renderer>();  // everything but the crew (shell, props)
        // motion: an interior whose shell moves (a spinning ring) leaves the cached
        // layer and is drawn every frame with the kerbals (PortalPipeline)
        private readonly List<Transform> motionProbe = new List<Transform>();
        private Vector4 motionSig;
        private bool motionInit;
        private float movingUntil;
        public Vector3 shellCenterLocal;  // probe point, interior-local
        public float shellRadius;
        public Bounds shellLocalBounds;   // interior-local box of the shell: "is the camera inside?"
        public bool haveShellBounds;
        public readonly string probeKey;
        private Renderer shellProbe;     // a non-kerbal interior renderer, tells us if stock hid the interior
        private float nextInteriorScan;
        public bool createdByUs;
        public bool hadInterior;          // an interior was seen on this part (to log one destroyed elsewhere)

        // per-frame
        public int frameId;              // 1-based id this frame, 0 = not rendered
        public RigidMap map;
        public int group;
        public float distance;
        public bool parkedHidden;        // forceRenderingOff held outside our passes
        public ProbeData probe;

        public PartPortal(Part p)
        {
            part = p;
            ConfigNode ic = p.partInfo != null ? p.partInfo.internalConfig : null;
            hasIva = ic != null && ic.HasValue("name");
            excluded = RWSettings.IsExcluded(p);
            foreach (PartModule pm in p.Modules)
            {
                if (pm == null)
                    continue;
                if (pm.moduleName == "GravityRing")
                    rootSpins = true;
                if (toggle == null)
                    toggle = pm as ModuleRealWindows;
            }
            probeKey = (p.partInfo != null ? p.partInfo.name : p.name) + "|" + (hasIva ? ic.GetValue("name") : "");
        }

        // The part -> interior map for this frame (Alignment.cs). An interior whose
        // root spins with the part's ring uses KSP's internal-space map instead, or
        // its own spin would be cancelled out (it showed standing still).
        public RigidMap LiveMap()
        {
            RigidMap k;
            if (rootSpins && Alignment.Stock(out k))
                return k;
            return Alignment.Compute(part.transform, model.transform);
        }

        // Switched to "Opaque" in the part menu: windows stay the hull's paint.
        public bool Opaque
        {
            get { return toggle != null && !toggle.transparent; }
        }

        public bool InteriorUsable
        {
            // excluded parts are still scanned so their interiors can be hidden
            // from other parts' renders; they are just never rendered themselves
            get { return hasIva && part.internalModel != null && part.internalModel == model && interiorRenderers.Count > 0; }
        }

        public bool StockHidden
        {
            get { return shellProbe != null && !shellProbe.enabled; }
        }

        // False only once every hull sub-mesh is known to be solid (all masks baked
        // and empty), so the interior is never seen and need not be rendered.
        public bool MayHaveWindows
        {
            get
            {
                if (exterior.Count == 0)
                    return true; // not scanned yet
                for (int i = 0; i < exterior.Count; i++)
                    if (MayBeWindow(exterior[i]))
                        return true;
                return false;
            }
        }

        private static bool MayBeWindow(DrawItem d)
        {
            if (d.glass == null || d.solid)
                return false;            // not a candidate at all (non-IVA part, flag)
            if (d.mask != null && d.mask.namedRejected)
                return false;            // named like glass, no opening behind it
            if (d.glass.channel == 6)
                return true;             // author's glass or a mesh named as a window
            if (d.mask == null)
                return true;             // no mask: the per-pixel probe decides
            if (!d.mask.done || d.mask.tex == null)
                return true;             // not baked yet, or baked to "use the probe"
            return d.mask.hasWindow;
        }

        // Windows from the author's glass, a window-named mesh, or a lightmap/gloss
        // mask: then sub-meshes whose openings show no glass paint are solid.
        public bool HasConfidentWindows
        {
            get
            {
                for (int i = 0; i < exterior.Count; i++)
                {
                    DrawItem d = exterior[i];
                    if (d.solid || d.glass == null || (d.mask != null && d.mask.namedRejected))
                        continue;
                    if (d.glass.channel == 6)
                        return true;
                    if (d.mask != null && !d.mask.fromOpenings && d.mask.done && d.mask.hasWindow)
                        return true;
                }
                return false;
            }
        }

        // World bounds of the sub-meshes that can show a window this frame.
        public bool WindowBounds(out Bounds b)
        {
            b = new Bounds();
            bool any = false;
            for (int i = 0; i < exterior.Count; i++)
            {
                DrawItem d = exterior[i];
                if (d.r == null || !d.r.enabled || !d.r.gameObject.activeInHierarchy || !MayBeWindow(d))
                    continue;
                if (!any) { b = d.r.bounds; any = true; }
                else b.Encapsulate(d.r.bounds);
            }
            return any;
        }

        // ------------------------------------------------------------------
        // exterior
        // ------------------------------------------------------------------

        public void MaybeScanExterior(float now)
        {
            if (now < nextExteriorScan)
                return;
            // crewed parts rescan often (variants, flags); the rest only occlude
            nextExteriorScan = now + (hasIva ? 5f : 20f) + UnityEngine.Random.value; // stagger
            exterior.Clear();
            if (hasIva && !excluded)
                CollectFlagRenderers();
            Renderer[] rs = part.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer))
                    continue;
                // child parts can be nested under this part's transform: keep only our own
                if (r.GetComponentInParent<Part>() != part)
                    continue;
                int subs = SubMeshCount(r);
                Material[] mats = r.sharedMaterials;
                for (int s = 0; s < subs && s < mats.Length; s++)
                {
                    Material m = mats[s];
                    if (m == null || SkipExteriorShader(m))
                        continue;
                    // Transparent sub-meshes of IVA parts are usually the author's glass:
                    // they belong to the part's surface for the ID test. On other parts
                    // they are effects/decals - skip.
                    bool transparent = IsTransparent(m);
                    if (transparent && !hasIva)
                        continue;
                    DrawItem di = new DrawItem(r, s);
                    if (hasIva && !excluded)
                    {
                        if (flagRenderers.Contains(r))
                        {
                            // flag decals are transparent quads on the hull, not glass
                            di.glass = GlassInfo.None;
                            di.solid = true;
                        }
                        else
                        {
                            di.glass = MaterialGlass.For(m, transparent);
                            if (!di.glass.informative && MaterialGlass.NamedAsGlass(r.name))
                                di.glass = GlassInfo.NamedWith(di.glass.albedo);
                            di.mask = HullMasks.For(this, r, di.glass);
                        }
                    }
                    exterior.Add(di);
                }
            }
            if (hasIva && !excluded && loggedGlass.Add(probeKey))
            {
                int informative = 0, total = 0;
                string sources = "";
                for (int i = 0; i < exterior.Count; i++)
                {
                    GlassInfo g = exterior[i].glass;
                    if (g == null) continue;
                    total++;
                    if (g.informative) informative++;
                    string why = exterior[i].solid ? ", flag decal: solid" : exterior[i].mask == null && g.channel != 6 ? ", no mask: " + HullMasks.WhyNoMask(exterior[i].r, g) : "";
                    sources += string.Format(" [{0} '{1}'{2} {3:P1}{4}{5}{6}]", g.source, exterior[i].r != null ? exterior[i].r.name : "?",
                        g.tex != null ? " " + g.tex.name : "", g.glossyFraction,
                        g.light != null ? string.Format(", lightmap {0} {1:P1}", g.light.name, g.litFraction) : "",
                        g.informative ? "" : " ignored", why);
                }
                RWLog.Info("{0}: hull glass info on {1}/{2} sub-meshes:{3}", part.name, informative, total, sources);
            }
        }

        private static readonly HashSet<string> loggedGlass = new HashSet<string>();
        private readonly HashSet<Renderer> flagRenderers = new HashSet<Renderer>();

        // The quads FlagDecal modules (incl. FlagDecalBackground) paint the flag on,
        // plus anything named like a flag or decal.
        private void CollectFlagRenderers()
        {
            flagRenderers.Clear();
            List<FlagDecal> decals = part.FindModulesImplementing<FlagDecal>();
            for (int i = 0; i < decals.Count; i++)
            {
                string q = decals[i].textureQuadName;
                if (string.IsNullOrEmpty(q))
                    continue;
                Transform[] ts = part.FindModelTransforms(q);
                for (int t = 0; t < ts.Length; t++)
                    foreach (Renderer r in ts[t].GetComponentsInChildren<Renderer>(true))
                        flagRenderers.Add(r);
            }
            foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
            {
                string n = r.name;
                if (n.IndexOf("flag", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("decal", StringComparison.OrdinalIgnoreCase) >= 0)
                    flagRenderers.Add(r);
            }
        }

        // ------------------------------------------------------------------
        // interior
        // ------------------------------------------------------------------

        public void InvalidateInterior()
        {
            model = null;
        }

        public void MaybeScanInterior(float now)
        {
            if (!hasIva)
                return;
            InternalModel im = part.internalModel;
            if (im == null)
            {
                Clear();
                return;
            }
            if (im == model && now < nextInteriorScan)
                return;
            bool changed = im != model;
            model = im;
            nextInteriorScan = now + 4f + UnityEngine.Random.value; // kerbals/props come and go
            Clear();

            Renderer[] rs = im.GetComponentsInChildren<Renderer>(true);
            bool haveBounds = false;
            Bounds shellBounds = new Bounds();
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer))
                    continue;
                interiorRenderers.Add(r);
                bool kerbal = r.GetComponentInParent<Kerbal>() != null;
                if (!kerbal)
                    nonKerbalRenderers.Add(r);
                bool prop = !kerbal && r.GetComponentInParent<InternalProp>() != null;
                if (!kerbal && shellProbe == null && r is MeshRenderer)
                    shellProbe = r;
                int subs = SubMeshCount(r);
                Material[] mats = r.sharedMaterials;
                bool addedToShell = false;
                bool allMasks = false;
                for (int s = 0; s < mats.Length; s++)
                {
                    if (mats[s] == null)
                        continue;
                    if (!IsDepthMask(mats[s])) { allMasks = false; break; }
                    allMasks = true;
                }
                if (allMasks)
                    depthMaskRenderers.Add(r);
                for (int s = 0; s < subs && s < mats.Length; s++)
                {
                    Material m = mats[s];
                    if (m == null)
                        continue;
                    if (IsDepthMask(m))
                        continue; // invisible helpers; some plug the windows (other IVA mods' window masks)
                    if (IsTransparent(m))
                        continue; // IVA glass/decals: openings for the probe, invisible to the id passes
                    // shell = everything but kerbals and props (the probe keeps the farthest
                    // surface, so seats/consoles in the shell model are harmless)
                    if (!kerbal && !prop)
                    {
                        interiorShell.Add(new DrawItem(r, s));
                        addedToShell = true;
                    }
                }
                Bounds wb;
                if (addedToShell && WorldBounds(r, out wb))
                {
                    if (!haveBounds) { shellBounds = wb; haveBounds = true; }
                    else shellBounds.Encapsulate(wb);
                }
            }
            if (haveBounds)
            {
                shellCenterLocal = im.transform.InverseTransformPoint(shellBounds.center);
                shellRadius = shellBounds.extents.magnitude;
                Vector3 c = shellBounds.center, e = shellBounds.extents;
                shellLocalBounds = new Bounds(shellCenterLocal, Vector3.zero);
                for (int k = 0; k < 8; k++)
                    shellLocalBounds.Encapsulate(im.transform.InverseTransformPoint(c + new Vector3((k & 1) == 0 ? -e.x : e.x, (k & 2) == 0 ? -e.y : e.y, (k & 4) == 0 ? -e.z : e.z)));
                haveShellBounds = true;
            }
            else
            {
                shellRadius = 0f;
                haveShellBounds = false;
                if (changed)
                    RWLog.Warn("{0}: interior has no shell geometry outside props - no windows can be found", part.name);
            }
            if (changed)
                RWLog.Verbose("{0}: interior scanned ({1} renderers, {2} shell draws, radius {3:F2} m)",
                    part.name, interiorRenderers.Count, interiorShell.Count, shellRadius);
        }

        // Only touches the renderers when the state actually changes: in the usual
        // case (one rigid vessel, one render group) nothing changes from frame to frame.
        // Moved within the last 2 s: its shell (or the root itself, for a ring that
        // spins its whole interior) moved inside internal space.
        public bool Moving { get { return Time.unscaledTime < movingUntil; } }

        // Once a frame: a signature of the root's orientation and up to 24 shell
        // transforms (relative to the root); any change marks the interior moving.
        public void UpdateMotion(float now)
        {
            if (model == null)
                return;
            if (motionProbe.Count == 0 && interiorShell.Count > 0)
            {
                int step = Math.Max(1, interiorShell.Count / 24);
                Transform last = null;
                for (int k = 0; k < interiorShell.Count && motionProbe.Count < 24; k += step)
                {
                    Renderer r = interiorShell[k].r;
                    if (r != null && r.transform != last)
                        motionProbe.Add(last = r.transform);
                }
            }
            Transform root = model.transform;
            Vector3 rf = root.forward, ru = root.up;
            Vector4 s = new Vector4(rf.x + 0.5f * ru.x, rf.y + 0.5f * ru.y, rf.z + 0.5f * ru.z, ru.y - rf.x);
            for (int k = 0; k < motionProbe.Count; k++)
            {
                Transform t = motionProbe[k];
                if (t == null)
                    continue;
                Vector3 pos = root.InverseTransformPoint(t.position);
                Vector3 fwd = root.InverseTransformDirection(t.forward);
                Vector3 up = root.InverseTransformDirection(t.up);
                s += new Vector4(pos.x + fwd.x + 0.5f * up.x, pos.y + fwd.y + 0.5f * up.y, pos.z + fwd.z + 0.5f * up.z,
                                 (k + 1) * (fwd.x - fwd.z + up.y));
            }
            if (motionInit && (s - motionSig).sqrMagnitude > 1e-6f)
                movingUntil = now + 2f;
            motionSig = s;
            motionInit = true;
        }

        public void SetForceOff(bool off)
        {
            if (off == forcedOff)
                return;
            forcedOff = off;
            for (int i = 0; i < interiorRenderers.Count; i++)
            {
                Renderer r = interiorRenderers[i];
                if (r != null)
                    r.forceRenderingOff = off;
            }
        }

        private bool forcedOff;

        private void Clear()
        {
            // never leave renderers we parked switched off behind a rescan
            SetForceOff(false);
            interiorRenderers.Clear();
            depthMaskRenderers.Clear();
            interiorShell.Clear();
            nonKerbalRenderers.Clear();
            motionProbe.Clear();
            motionInit = false;
            shellProbe = null;
            parkedHidden = false;
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        // Renderer.bounds is unreliable while stock has the interior hidden
        // (disabled renderers), so derive world bounds from the mesh itself.
        private static bool WorldBounds(Renderer r, out Bounds b)
        {
            b = new Bounds();
            Mesh mesh = null;
            SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
            if (smr != null) mesh = smr.sharedMesh;
            else
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null) mesh = mf.sharedMesh;
            }
            if (mesh == null)
                return false;
            Bounds lb = mesh.bounds;
            Matrix4x4 m = r.transform.localToWorldMatrix;
            Vector3 c = lb.center, e = lb.extents;
            b = new Bounds(m.MultiplyPoint3x4(c), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                b.Encapsulate(m.MultiplyPoint3x4(corner));
            }
            return true;
        }

        public static int SubMeshCount(Renderer r)
        {
            Mesh mesh = null;
            SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
            if (smr != null)
                mesh = smr.sharedMesh;
            else
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null)
                    mesh = mf.sharedMesh;
            }
            return mesh != null ? mesh.subMeshCount : 0;
        }

        public static bool IsTransparent(Material m)
        {
            if (m.renderQueue >= 2450)
                return true;
            string n = m.shader != null ? m.shader.name : "";
            return n.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Translucent", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("/Alpha/", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsDepthMask(Material m)
        {
            string n = m.shader != null ? m.shader.name : "";
            return n.IndexOf("DepthMask", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Depth Mask", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool SkipExteriorShader(Material m)
        {
            string n = m.shader != null ? m.shader.name : "";
            return n.StartsWith("Waterfall", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("Hidden/", StringComparison.OrdinalIgnoreCase)
                || n.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0
                || IsDepthMask(m);
        }
    }
}
