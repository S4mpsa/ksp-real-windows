// Interior probe: view-independent window detection.
//
// For each IVA type we render, ONCE, the distance from a set of view points to
// the NEAREST IVA shell surface in every direction, into a texture array (six
// 90-degree faces per view point, in the interior's own frame). View points are
// the seated kerbals' eyes, which are inside the cabin with (hopefully) the windows
// in view, plus points between them. Stored as 1/distance with max blending, so
// no depth buffer is needed and 0 means "nothing that way".
//
// A hull point is open if some view point sees it: nothing of the IVA lies
// between them, and there is no IVA wall right at the hull point. Several view
// points mean a seat or pillar can't hide an opening from all of them.
//
// Excluded from the bake: kerbals, props, glass/transparent materials and
// depth masks (some IVA mods use them to mask the windows).

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealWindows
{
    public class ProbeData
    {
        public RenderTexture tex;
        public Vector3 centerLocal;   // probe frame origin in the interior's local frame
        public float radius;          // shell bounding radius (m)
        public readonly Vector4[] sources = new Vector4[Probes.MaxSources]; // view points, probe frame
        public int sourceCount;
        public bool baked;
        public int bakedFrame;        // the bake runs in this frame's command buffer
        public string key;

        public bool Valid { get { return baked && tex != null && tex.IsCreated(); } }
    }

    public static class Probes
    {
        public const int MaxSources = 8; // MUST match RW_Hull.shader

        // Face tables - MUST match the ones in RW_Hull.shader.
        public static readonly Vector3[] Fwd =
        {
            new Vector3(1, 0, 0), new Vector3(-1, 0, 0),
            new Vector3(0, 1, 0), new Vector3(0, -1, 0),
            new Vector3(0, 0, 1), new Vector3(0, 0, -1),
        };
        public static readonly Vector3[] Right =
        {
            new Vector3(0, 0, -1), new Vector3(0, 0, 1),
            new Vector3(1, 0, 0), new Vector3(1, 0, 0),
            new Vector3(1, 0, 0), new Vector3(-1, 0, 0),
        };
        public static readonly Vector3[] Up =
        {
            new Vector3(0, 1, 0), new Vector3(0, 1, 0),
            new Vector3(0, 0, -1), new Vector3(0, 0, 1),
            new Vector3(0, 1, 0), new Vector3(0, 1, 0),
        };

        private static readonly Dictionary<string, ProbeData> cache = new Dictionary<string, ProbeData>();
        private static readonly int PID_ProbeCenter = Shader.PropertyToID("_RW_ProbeCenter");

        public static ProbeData Get(PartPortal p)
        {
            ProbeData d;
            if (!cache.TryGetValue(p.probeKey, out d))
            {
                d = new ProbeData { key = p.probeKey };
                cache[p.probeKey] = d;
            }
            if (!d.Valid)
                d.baked = false;
            return d;
        }

        // World (exterior) -> probe frame, rigid: through the part's live map K
        // into internal space, then into the interior frame centred on the probe.
        public static Matrix4x4 WorldToProbe(PartPortal p, ProbeData d)
        {
            Transform it = p.model.transform;
            Vector3 c = it.TransformPoint(d.centerLocal);
            Matrix4x4 probeToInternal = Matrix4x4.TRS(c, it.rotation, Vector3.one);
            return probeToInternal.inverse * p.map.Matrix;
        }

        // Records the bake into cb. Uses the interior's current placement in
        // internal space; the result is in the interior's own frame, so it is
        // valid for every instance of the same IVA.
        public static bool Bake(CommandBuffer cb, PartPortal p, ProbeData d, Material probeMat)
        {
            if (p.interiorShell.Count == 0 || p.shellRadius <= 0f)
                return false;
            int res = Mathf.Clamp(RWSettings.probeResolution, 32, 512);
            if (d.tex == null || !d.tex.IsCreated() || d.tex.width != res)
            {
                if (d.tex != null) { d.tex.Release(); Object.Destroy(d.tex); }
                RenderTextureFormat fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RHalf)
                    ? RenderTextureFormat.RHalf : RenderTextureFormat.RFloat;
                d.tex = new RenderTexture(res, res, 0, fmt, RenderTextureReadWrite.Linear)
                {
                    name = "RW probe " + d.key,
                    dimension = TextureDimension.Tex2DArray,
                    volumeDepth = 6 * MaxSources,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false
                };
                d.tex.Create();
            }
            d.centerLocal = p.shellCenterLocal;
            d.radius = p.shellRadius;

            Transform it = p.model.transform;
            Vector3 origin = it.TransformPoint(d.centerLocal);
            Quaternion rot = it.rotation;
            viewPoints.Clear();
            CollectViewPoints(p, origin, viewPoints);
            d.sourceCount = viewPoints.Count;
            Matrix4x4 proj = Matrix4x4.Perspective(90f, 1f, 0.01f, d.radius * 2f + 0.5f);
            bool stockHidden = p.StockHidden;
            for (int s = 0; s < viewPoints.Count; s++)
            {
                Vector3 c = viewPoints[s];
                Vector3 local = Quaternion.Inverse(rot) * (c - origin);
                d.sources[s] = new Vector4(local.x, local.y, local.z, 0f);
                cb.SetGlobalVector(PID_ProbeCenter, c);
                for (int f = 0; f < 6; f++)
                {
                    Vector3 right = rot * Right[f], up = rot * Up[f], fwd = rot * Fwd[f];
                    Matrix4x4 view = Matrix4x4.identity;
                    view.SetRow(0, new Vector4(right.x, right.y, right.z, -Vector3.Dot(right, c)));
                    view.SetRow(1, new Vector4(up.x, up.y, up.z, -Vector3.Dot(up, c)));
                    view.SetRow(2, new Vector4(-fwd.x, -fwd.y, -fwd.z, Vector3.Dot(fwd, c)));
                    view.SetRow(3, new Vector4(0, 0, 0, 1));
                    cb.SetRenderTarget(new RenderTargetIdentifier(d.tex, 0, CubemapFace.Unknown, s * 6 + f));
                    cb.ClearRenderTarget(false, true, Color.clear, 1f);
                    cb.SetViewProjectionMatrices(view, proj);
                    for (int k = 0; k < p.interiorShell.Count; k++)
                    {
                        DrawItem di = p.interiorShell[k];
                        Renderer r = di.r;
                        // skip switched-off helper meshes (other IVA mods park tools there);
                        // when stock has hidden the whole interior every renderer is off,
                        // so only check activeness
                        if (r == null || !r.gameObject.activeInHierarchy || (!stockHidden && !r.enabled))
                            continue;
                        cb.DrawRenderer(r, probeMat, di.sub, 0);
                    }
                }
            }
            d.baked = true;
            d.bakedFrame = Time.frameCount;
            RWLog.Info("probe baked for {0} ({1} shell draws, {2} view points, radius {3:F2} m, {4}px)",
                d.key, p.interiorShell.Count, d.sourceCount, d.radius, res);
            return true;
        }

        private static readonly List<Vector3> viewPoints = new List<Vector3>();

        // Internal-space positions. Seat eyes first; then the points between them,
        // which are inside the cabin too; then small sideways steps if that leaves
        // few. The shell's centre is only a last resort: on an odd-shaped cabin it
        // can lie outside the walls, and from there everything looks open.
        private static void CollectViewPoints(PartPortal p, Vector3 centre, List<Vector3> pts)
        {
            List<InternalSeat> seats = p.model.seats;
            Vector3 sideways = p.model.transform.right;
            for (int i = 0; seats != null && i < seats.Count && pts.Count < 6; i++)
            {
                InternalSeat seat = seats[i];
                if (seat == null || seat.seatTransform == null)
                    continue;
                Transform t = seat.seatTransform;
                Vector3 eye;
                if (seat.kerbalRef != null && seat.kerbalRef.eyeTransform != null)
                    eye = seat.kerbalRef.eyeTransform.position + t.rotation * seat.kerbalEyeOffset;
                else
                    eye = t.TransformPoint(seat.kerbalOffset) + t.up * 0.4f;
                if (pts.Count == 0)
                    sideways = t.right;
                pts.Add(eye);
            }
            int eyes = pts.Count;
            if (eyes == 0)
            {
                pts.Add(centre);
                return;
            }
            if (eyes > 1)
            {
                Vector3 mean = Vector3.zero;
                for (int i = 0; i < eyes; i++)
                    mean += pts[i];
                pts.Add(mean / eyes);
            }
            for (int i = 0; i + 1 < eyes && pts.Count < MaxSources; i++)
                pts.Add((pts[i] + pts[i + 1]) * 0.5f);
            if (pts.Count < 3)
            {
                Vector3 eye = pts[0];
                pts.Add(eye + sideways * 0.2f);
                pts.Add(eye - sideways * 0.2f);
            }
        }
    }
}
