// Conformal Decals' decals on parts with window cut-outs.
//
// Conformal Decals draws a decal by drawing the target part's mesh again with the decal
// material (Graphics.DrawMesh, per camera, sorting order 0); it relies on the decal's
// render queue (2100-2400) to come after the part's own hull (2000). Unity sorts opaque
// draws by sorting order before render queue, so the cut-outs' raised hull orders
// (PortalPipeline.EnableCovers) put the hull after the decal, which it then paints over,
// in forward and deferred alike (the decal shaders have both kinds of passes).
//
// For the main camera's render only, each decal target that is a reordered hull renderer
// gets a stand-in: a MeshRenderer drawing the same mesh with the decal's own material and
// property block at the hull's sorting order, where the render queue puts it after the
// hull again. Conformal Decals' own draw stays underneath, covered by the hull. Its
// internals are read by reflection (no dependency, nothing patched); if anything is
// missing the stand-ins simply never appear.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealWindows
{
    public class DecalStandIns
    {
        private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static bool resolved;
        private static Type moduleType;
        private static FieldInfo fTargets, fMaterial, fAttached;
        private static FieldInfo fRenderer, fMesh, fEnabled, fBlock;

        private readonly List<PartModule> decals = new List<PartModule>();
        private readonly List<MeshRenderer> pool = new List<MeshRenderer>();
        private readonly List<MeshFilter> filters = new List<MeshFilter>();
        private int used;
        private float nextScan;
        private bool warned;
        public int framesDrawn;   // for the status line

        private static bool Resolve()
        {
            if (resolved)
                return moduleType != null;
            resolved = true;
            try
            {
                foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
                {
                    Type t = la.assembly != null ? la.assembly.GetType("ConformalDecals.ModuleConformalDecal", false) : null;
                    if (t == null)
                        continue;
                    Type pt = la.assembly.GetType("ConformalDecals.ProjectionTarget", false);
                    fTargets = t.GetField("_targets", F);
                    fMaterial = t.GetField("_decalMaterial", F);
                    fAttached = t.GetField("_isAttached", F);
                    if (pt != null)
                    {
                        fRenderer = pt.GetField("_targetRenderer", F);
                        fMesh = pt.GetField("_targetMesh", F);
                        fEnabled = pt.GetField("_projectionEnabled", F);
                        fBlock = pt.GetField("_decalMPB", F);
                    }
                    if (fTargets == null || fMaterial == null || fAttached == null || fRenderer == null || fMesh == null || fEnabled == null || fBlock == null)
                    {
                        RWLog.Warn("Conformal Decals found, but not the fields its decals are drawn from; decals on parts with window cut-outs may be hidden");
                        return false;
                    }
                    moduleType = t;
                    RWLog.Info("Conformal Decals found: its decals are redrawn over parts with window cut-outs");
                    return true;
                }
            }
            catch (Exception e)
            {
                RWLog.Warn("Conformal Decals lookup failed: {0}", e.Message);
            }
            return false;
        }

        // The decal modules on loaded vessels, every 2 s.
        private void Scan()
        {
            float now = Time.unscaledTime;
            if (now < nextScan)
                return;
            nextScan = now + 2f;
            decals.Clear();
            List<Vessel> vs = FlightGlobals.VesselsLoaded;
            for (int v = 0; v < vs.Count; v++)
            {
                if (vs[v] == null)
                    continue;
                List<Part> parts = vs[v].parts;
                for (int p = 0; p < parts.Count; p++)
                {
                    Part part = parts[p];
                    if (part == null)
                        continue;
                    for (int m = 0; m < part.Modules.Count; m++)
                    {
                        PartModule pm = part.Modules[m];
                        if (pm != null && moduleType.IsInstanceOfType(pm))
                            decals.Add(pm);
                    }
                }
            }
        }

        // Stand-ins for every decal drawn on one of the reordered renderers (their sorting
        // orders are already raised). Called right after the cut-outs are switched on.
        public void Show(HashSet<Renderer> reordered, int minOrder)
        {
            if (!RWSettings.decalStandIns || reordered.Count == 0 || !Resolve())
                return;
            try
            {
                Scan();
                for (int d = 0; d < decals.Count; d++)
                {
                    PartModule pm = decals[d];
                    if (pm == null || !(bool)fAttached.GetValue(pm))
                        continue;
                    Material mat = fMaterial.GetValue(pm) as Material;
                    IList targets = fTargets.GetValue(pm) as IList;
                    if (mat == null || targets == null)
                        continue;
                    for (int i = 0; i < targets.Count; i++)
                    {
                        object t = targets[i];
                        if (t == null || !(bool)fEnabled.GetValue(t))
                            continue;
                        Renderer r = fRenderer.GetValue(t) as Renderer;
                        if (r == null || r.sortingOrder < minOrder || !reordered.Contains(r) || !r.enabled || !r.gameObject.activeInHierarchy)
                            continue;
                        Mesh mesh = fMesh.GetValue(t) as Mesh;
                        if (mesh == null)
                            continue;
                        Place(r, mesh, mat, fBlock.GetValue(t) as MaterialPropertyBlock);
                    }
                }
                if (used > 0)
                    framesDrawn++;
            }
            catch (Exception e)
            {
                if (!warned)
                {
                    warned = true;
                    RWLog.Warn("Conformal Decals stand-ins failed: {0}", e.Message);
                }
            }
        }

        private void Place(Renderer target, Mesh mesh, Material mat, MaterialPropertyBlock block)
        {
            if (used < pool.Count && pool[used] == null)  // destroyed by something else
            {
                pool.RemoveAt(used);
                filters.RemoveAt(used);
            }
            if (used == pool.Count)
            {
                GameObject go = new GameObject("RealWindows Decal " + pool.Count);
                go.layer = 0;
                MeshFilter mf = go.AddComponent<MeshFilter>();
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;  // as Conformal Decals draws them
                mr.receiveShadows = true;
                mr.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
                mr.allowOcclusionWhenDynamic = false;
                mr.enabled = false;
                pool.Add(mr);
                filters.Add(mf);
            }
            MeshRenderer s = pool[used];
            Transform tt = target.transform, st = s.transform;
            st.SetPositionAndRotation(tt.position, tt.rotation);
            st.localScale = tt.lossyScale;
            filters[used].sharedMesh = mesh;
            s.sharedMaterial = mat;
            if (block != null)
                s.SetPropertyBlock(block);
            else
                s.SetPropertyBlock(null);
            s.sortingOrder = target.sortingOrder;  // the hull's raised order; the queue puts the decal after it
            s.enabled = true;
            used++;
        }

        public void Hide()
        {
            for (int i = 0; i < used; i++)
                if (pool[i] != null)
                    pool[i].enabled = false;
            used = 0;
        }

        public void Dispose()
        {
            Hide();
            for (int i = 0; i < pool.Count; i++)
                if (pool[i] != null)
                    UnityEngine.Object.Destroy(pool[i].gameObject);
            pool.Clear();
            filters.Clear();
            decals.Clear();
        }
    }
}
