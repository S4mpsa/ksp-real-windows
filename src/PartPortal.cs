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
        public readonly bool isKerbal;   // an EVA kerbal: an occluder whose meshes change and animate
        private readonly ModuleRealWindows toggle; // the part menu's Transparent/Opaque switch, if patched in

        // exterior (all parts): drawn into the hull ID pass
        public readonly List<DrawItem> exterior = new List<DrawItem>();
        // Other mods' copies of hull meshes drawn over the hull (Losket's scorch and
        // dust): not part of the hull passes, but moved with the hull's sorting order
        // for the cut-outs so they are still drawn after it.
        public readonly List<Renderer> overlays = new List<Renderer>();
        private float nextExteriorScan;

        // interior (IVA parts only)
        public InternalModel model;      // reference the caches below belong to
        public readonly List<Renderer> interiorRenderers = new List<Renderer>();   // every Mesh/Skinned renderer
        public readonly List<Renderer> depthMaskRenderers = new List<Renderer>();  // only depth-mask materials (FreeIva hatch doorways, window plugs)
        public readonly List<Renderer> glassRenderers = new List<Renderer>();      // only transparent (and depth-mask) materials, not props or kerbals: the IVA's window glass
        public readonly List<Renderer> propRenderers = new List<Renderer>();       // props (InternalProp), left out while the windows are small on screen
        public readonly List<Renderer> kerbalRenderers = new List<Renderer>();     // crew, cached instead of drawn every frame while the windows are small
        public readonly List<Renderer> nonPropRenderers = new List<Renderer>();    // everything but props (shell, glass, masks, crew)
        public readonly List<DrawItem> interiorShell = new List<DrawItem>();       // opaque, no kerbals or props: front pass and probe bake
        public readonly List<Renderer> nonKerbalRenderers = new List<Renderer>();  // everything but the crew (shell, props)
        // Instruments: props driven by an interior module other than FreeIva's (RPM,
        // MAS, stock gauges and switches). Most of an IVA's draw calls, invisible from
        // outside; left out unless `drawInstruments`. Plain props (seats, ladders,
        // decor) and FreeIva hatches stay.
        public readonly List<Renderer> instrumentRenderers = new List<Renderer>();
        public readonly List<Renderer> nonInstrumentRenderers = new List<Renderer>(); // all but instruments
        // Not crew, but on the kerbals layer (16): FreeIva and some IVAs put cabin walls
        // and props there. The cached cabin render (both layers) draws them; the per-frame
        // crew pass (kerbals layer) hides them. Before 1.1.2.3 the cabin render only drew
        // layer 20, so such walls were drawn by the crew pass alone, every frame and
        // without the cabin's lighting: the "black void" cabins with lit props in them.
        public readonly List<Renderer> kerbalLayerOthers = new List<Renderer>();
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
        // Tells us if stock hid the interior: an active shell renderer. Stock's
        // InternalModel.SetVisible only reaches renderers on active objects, and RPM/MAS
        // switch their props' renderers on by themselves, so a prop or an inactive
        // renderer kept reading "visible" while the cabin itself was off: nothing was
        // switched on for our render, and only the lit RPM/MAS props floated in a void.
        private Renderer shellProbe, probeFallback;
        private float nextInteriorScan;
        public bool createdByUs;
        public bool hadInterior;          // an interior was seen on this part (to log one destroyed elsewhere)

        // per-frame
        public int frameId;              // 1-based id this frame, 0 = not rendered
        public RigidMap map;
        public int group;
        public float distance;
        public Rect screenArea;          // viewport area of its windows (whole screen when the camera is inside)
        public bool hullInView = true;   // this frame: its hull overlaps some window on screen (drawn in the hull passes)
        public bool parkedHidden;        // forceRenderingOff held outside our passes

        // crew (or anything else on the kerbals layer) in the interior: drawn every frame
        public bool HasKerbals
        {
            get { return interiorRenderers.Count > nonKerbalRenderers.Count; }
        }

        // Detail by the windows' size on screen (largest side, px): props are drawn
        // from propDetailPixels up, crew animated every frame from crewDetailPixels up
        // (below that it is drawn into the cached image, refreshed with it). Switched
        // back off only at 3/4 of the threshold, so it doesn't flicker at the edge.
        public bool propsOn = true, crewOn = true;

        public void UpdateDetail(float px)
        {
            propsOn = Above(px, RWSettings.propDetailPixels, propsOn);
            // another mod's stand-ins (API) are drawn in the per-frame pass only
            crewOn = RealWindowsApi.HasSubscribers || Above(px, RWSettings.crewDetailPixels, crewOn);
        }

        private static bool Above(float px, float limit, bool was)
        {
            if (limit <= 0f)
                return true;
            return px >= (was ? limit * 0.75f : limit);
        }
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
                if (pm is KerbalEVA)
                    isKerbal = true;
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
            // crewed parts rescan often (variants, flags); the rest only occlude.
            // EVA kerbals every second: helmets, suits and visors come and go
            nextExteriorScan = now + (isKerbal ? 1f : hasIva ? 5f : 20f) + UnityEngine.Random.value * (isKerbal ? 0.2f : 1f); // stagger
            exterior.Clear();
            overlays.Clear();
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
                // A copy of the hull mesh at the hull's depth: in the hull pass it would
                // overwrite the hull's window state with its own (solid).
                if (IsHullOverlay(r))
                {
                    overlays.Add(r);
                    continue;
                }
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
                    // An EVA kerbal's suit can sit in the alpha-test queue (2450), which
                    // the queue test counts as transparent: the kerbal then wasn't in the
                    // hull pass, and the window behind showed through it. For kerbals only
                    // the shader decides (the helmet visor is still skipped).
                    bool transparent = isKerbal ? IsTransparentShader(m) : IsTransparent(m);
                    if (transparent && !hasIva)
                        continue;
                    DrawItem di = new DrawItem(r, s);
                    if (hasIva && !excluded)
                    {
                        if (flagRenderers.Contains(r) || MaterialGlass.NamedAsLamp(r.name))
                        {
                            // flag decals are transparent quads on the hull, lamp lenses are
                            // lit like windows: neither is glass
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
                    string why = exterior[i].solid ? ", flag decal or lamp: solid" : exterior[i].mask == null && g.channel != 6 ? ", no mask: " + HullMasks.WhyNoMask(exterior[i].r, g) : "";
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
            var instrumentCache = new Dictionary<InternalProp, bool>();
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
                {
                    nonKerbalRenderers.Add(r);
                    if (r.gameObject.layer == 16)
                        kerbalLayerOthers.Add(r);
                }
                InternalProp ip = kerbal ? null : r.GetComponentInParent<InternalProp>();
                bool prop = ip != null;
                if (kerbal)
                    kerbalRenderers.Add(r);
                else if (prop)
                    propRenderers.Add(r);
                if (prop && IsInstrument(ip, instrumentCache))
                    instrumentRenderers.Add(r);
                else
                    nonInstrumentRenderers.Add(r);
                if (!prop)
                    nonPropRenderers.Add(r);
                if (!kerbal && probeFallback == null && r is MeshRenderer && r.gameObject.activeInHierarchy)
                    probeFallback = r;
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
                else if (!kerbal && !prop && AllGlass(mats))
                    glassRenderers.Add(r);
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
                if (addedToShell && shellProbe == null && r is MeshRenderer && r.gameObject.activeInHierarchy)
                    shellProbe = r;
                Bounds wb;
                if (addedToShell && WorldBounds(r, out wb))
                {
                    if (!haveBounds) { shellBounds = wb; haveBounds = true; }
                    else shellBounds.Encapsulate(wb);
                }
            }
            if (shellProbe == null)
                shellProbe = probeFallback;
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
                RWLog.Verbose("{0}: interior scanned ({1} renderers, {2} shell draws, {3} prop renderers of which {4} instruments, radius {5:F2} m)",
                    part.name, interiorRenderers.Count, interiorShell.Count, propRenderers.Count, instrumentRenderers.Count, shellRadius);
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
            // the root's turn relative to its part, through this frame's map: none for
            // an ordinary interior, however internal space moves (it follows the active
            // vessel, an EVA kerbal turning included); a ring's spin where KSP's own
            // map is used (rootSpins)
            Quaternion rel = part != null
                ? Quaternion.Inverse(LiveMap().rot * part.transform.rotation * Alignment.PartToInterior) * root.rotation
                : root.rotation;
            Vector3 rf = rel * Vector3.forward, ru = rel * Vector3.up;
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

        // Diagnostics (verbose, once): what the interior camera can actually draw of
        // this interior right now, and why the rest won't be drawn.
        public string DrawState()
        {
            var shell = new HashSet<Renderer>();
            for (int i = 0; i < interiorShell.Count; i++)
                if (interiorShell[i].r != null)
                    shell.Add(interiorShell[i].r);
            int act = 0, en = 0, forced = 0, draw = 0, propDraw = 0;
            int layerMask = 0;
            var notDrawn = new System.Text.StringBuilder();
            var drawn = new System.Text.StringBuilder();
            int nNot = 0, nDrawn = 0;
            foreach (Renderer r in shell)
            {
                bool a = r.gameObject.activeInHierarchy, e = r.enabled, f = r.forceRenderingOff;
                if (a) act++;
                if (e) en++;
                if (f) forced++;
                layerMask |= 1 << r.gameObject.layer;
                if (a && e && !f) { draw++; if (nDrawn++ < 2) drawn.Append(" ").Append(Describe(r)); }
                else if (nNot++ < 4) notDrawn.Append(" ").Append(Describe(r));
            }
            for (int i = 0; i < propRenderers.Count; i++)
            {
                Renderer r = propRenderers[i];
                if (r != null && r.gameObject.activeInHierarchy && r.enabled && !r.forceRenderingOff)
                    propDraw++;
            }
            return string.Format("{0}: stockHidden {1}, probe {2}; shell renderers {3}: active {4}, enabled {5}, forced off {6}, will draw {7} (layers 0x{8:X}); props {9}, will draw {10}; glass {11}, masks {12}; moving {13}, props on {14}, crew on {15}; drawn e.g.{16}; not drawn e.g.{17}",
                part.name, StockHidden, shellProbe != null ? Describe(shellProbe) : "none",
                shell.Count, act, en, forced, draw, layerMask, propRenderers.Count, propDraw,
                glassRenderers.Count, depthMaskRenderers.Count, Moving, propsOn, crewOn,
                drawn.ToString(), notDrawn.ToString());
        }

        private static string Describe(Renderer r)
        {
            Material m = r.sharedMaterial;
            return string.Format("[{0} a{1} e{2} f{3} L{4} {5} q{6}]", r.name,
                r.gameObject.activeInHierarchy ? 1 : 0, r.enabled ? 1 : 0, r.forceRenderingOff ? 1 : 0,
                r.gameObject.layer, m != null && m.shader != null ? m.shader.name : "-", m != null ? m.renderQueue : -1);
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
            glassRenderers.Clear();
            propRenderers.Clear();
            instrumentRenderers.Clear();
            nonInstrumentRenderers.Clear();
            kerbalLayerOthers.Clear();
            kerbalRenderers.Clear();
            interiorShell.Clear();
            nonKerbalRenderers.Clear();
            nonPropRenderers.Clear();
            motionProbe.Clear();
            motionInit = false;
            shellProbe = null;
            probeFallback = null;
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

        private static bool IsInstrument(InternalProp ip, Dictionary<InternalProp, bool> cache)
        {
            bool yes;
            if (cache.TryGetValue(ip, out yes))
                return yes;
            yes = false;
            if (ip.internalModules != null)
                for (int i = 0; i < ip.internalModules.Count && !yes; i++)
                {
                    InternalModule m = ip.internalModules[i];
                    if (m != null && m.GetType().Assembly.GetName().Name != "FreeIva")
                        yes = true;
                }
            cache[ip] = yes;
            return yes;
        }

        public static bool IsTransparent(Material m)
        {
            if (m.renderQueue >= 2450)
                return true;
            return IsTransparentShader(m);
        }

        public static bool IsTransparentShader(Material m)
        {
            string n = m.shader != null ? m.shader.name : "";
            return n.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Translucent", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("/Alpha/", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Window glass modelled in the IVA: every material transparent (FreeIva moves
        // these into the opaque queue and, with Deferred, adds a depth mask, so the
        // shader name decides), at least one not a depth mask.
        private static bool AllGlass(Material[] mats)
        {
            bool any = false;
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                if (m == null || IsDepthMask(m))
                    continue;
                if (!IsTransparent(m))
                    return false;
                any = true;
            }
            return any;
        }

        public static bool IsDepthMask(Material m)
        {
            string n = m.shader != null ? m.shader.name : "";
            return n.IndexOf("DepthMask", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Depth Mask", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Losket (burn, dust and exhaust marks): a MeshRenderer or SkinnedMeshRenderer
        // named losket...Overlay on the same mesh as its hull renderer, a child of it,
        // drawn with a Losket/ shader (Geometry+100 and up, alpha-blended, no depth write).
        private static bool IsHullOverlay(Renderer r)
        {
            string n = r.name;
            if (n.StartsWith("losket", StringComparison.OrdinalIgnoreCase)
                && n.EndsWith("Overlay", StringComparison.OrdinalIgnoreCase))
                return true;
            Material m = r.sharedMaterial;
            return m != null && m.shader != null
                && m.shader.name.StartsWith("Losket/", StringComparison.OrdinalIgnoreCase);
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
