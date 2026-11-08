// The per-frame render work, run from the main flight camera's onPreCull so
// every pass uses exactly the pose/projection that frame is rendered with.
//
//   0. Bakes          (once per IVA type / hull texture, at most one per frame):
//      the interior probe (Probe.cs), then the window masks (HullMasks.cs).
//   1. Hull pass      (command buffer, two targets): every nearby part writes
//      (partId, eyeDepth, window state, reason) and its glass surface (Fresnel,
//      glint, dirt). partId = this frame's id for parts whose interior is
//      rendered, 0 for everything else (still occludes). The window state comes
//      from the baked mask, the author's glass, the per-pixel probe, or solid.
//   2. Front pass     (command buffer): interior shells, back faces culled,
//      writing (partId, eyeDepth) of the first interior wall along each ray.
//   3. Colour pass    (real camera, lit, cropped to the windows' screen area):
//      the interiors, with their neighbours behind open hatches. Split in two:
//      the internal-space layer (shell and props) is cached with its depth and
//      redrawn only when the view of it changes, every `interiorRefresh`
//      seconds, or on RealWindowsApi.RequestRedraw(); the kerbals layer is drawn
//      every frame over a copy of it, depth-tested against the cached depth.
//   4. Composite      (command buffer on the main camera): the interior where
//      the hull pixel is a window and the interior seen there is that part's or
//      one drawn in the same render, with the glass look on top. With the camera
//      inside a part, its interior is shown whole, and drawn a second time after
//      clouds and atmosphere.
//
// Passes 2-3 run once per alignment group with the group's own exact
// world->internal map (see Alignment.cs); all groups share one depth buffer
// per texture, so overlaps between parts resolve by true depth.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealWindows
{
    public class PortalGroup
    {
        public RigidMap map;
        public Vessel vessel;
        public readonly List<PartPortal> members = new List<PartPortal>();
    }

    public class PortalPipeline : IDisposable
    {
        // inputs, refreshed by the addon every Update
        public readonly List<PartPortal> interiors = new List<PartPortal>();      // to render, nearest first
        public readonly List<PartPortal> occluders = new List<PartPortal>();      // hull ID pass
        public readonly List<PartPortal> allInteriors = new List<PartPortal>();   // every usable interior in the scene
        public bool wantActive;

        // average CPU time of the render passes since the last call, ms
        public double TakeAverageMs()
        {
            double r = timeFrames > 0 ? timeSum / timeFrames : 0;
            timeSum = 0;
            timeFrames = 0;
            return r;
        }

        private Camera main;
        private Camera intCam;
        private GameObject intCamGO;

        private RenderTexture idRT, surfaceRT, frontRT, colorRT;
        // cached internal-space layer: colour + depth, and the per-frame depth copy
        // the kerbals layer is tested and written against
        private RenderTexture staticColorRT, staticDepthRT, dynDepthRT;
        private float staticAt = -1f;
        private Rect staticRect;
        private Matrix4x4 staticProj;
        private int staticHash;
        private Vector4 staticShellSum;
        private readonly List<Vector3> staticPos = new List<Vector3>();
        private readonly List<Quaternion> staticRot = new List<Quaternion>();
        public int staticRedraws, staticReuses;   // for the status line
        private const int LayerKerbals = 16, LayerInternal = 20;
        private readonly RenderTargetIdentifier[] hullTargets = new RenderTargetIdentifier[2];
        private int rtW, rtH;
        private bool rtHDR;

        private Material matCullBack, matHull, matProbe, matComposite;
        private readonly CommandBuffer cbPasses = new CommandBuffer { name = "RealWindows passes" };
        private readonly CommandBuffer cbComposite = new CommandBuffer { name = "RealWindows composite" };
        private Camera compositeCamera;
        private CameraEvent compositeEvent;
        private bool compositeAttached;
        // camera inside a part: its interior drawn again after clouds and atmosphere,
        // which take the inside of a hull for open sky (no hull depth from inside)
        private readonly CommandBuffer cbInside = new CommandBuffer { name = "RealWindows inside view" };
        private Camera insideCamera;
        private CameraEvent insideEvent;
        private bool insideAttached;
        private readonly List<Renderer> masksHidden = new List<Renderer>();
        // render group of each part id (index = id, -1 none): the composite lets a
        // window show another part's interior when both were drawn in one render
        // (an open hatch looking into the next cabin)
        public const int MaxIds = 128;
        private readonly float[] idGroup = new float[MaxIds];
        private DepthTextureMode originalDepthMode;
        private bool depthModeTouched;

        private readonly List<PortalGroup> groups = new List<PortalGroup>();
        private readonly Stack<PortalGroup> groupPool = new Stack<PortalGroup>();
        private readonly List<Renderer> flippedEnabled = new List<Renderer>();
        private readonly List<Part> apiParts = new List<Part>();
        private readonly Dictionary<Part, PartPortal> byPart = new Dictionary<Part, PartPortal>();
        private readonly HashSet<PartPortal> context = new HashSet<PartPortal>();
        private PartPortal insidePart;   // the interior the camera is in, if any
        public int insideId;
        private readonly Plane[] planes = new Plane[6];       // full view
        private readonly Plane[] cropPlanes = new Plane[6];   // the screen area the windows cover
        private readonly Plane[] groupPlanes = new Plane[6];  // the same, in a group's internal space
        private Rect windowRect;                              // viewport (0-1) area of all windows
        private Matrix4x4 cropProj;                           // projection of just that area
        private readonly System.Diagnostics.Stopwatch timer = new System.Diagnostics.Stopwatch();
        private double timeSum;
        private int timeFrames;
        public int lastCulled;

        private IVASun ivaSun;
        private bool rendering;
        private bool renderedThisFrame;
        public int lastGroupCount;
        public int lastInteriorCount;

        private static readonly int PID_PartId = Shader.PropertyToID("_RW_PartId");
        private static readonly int PID_Cull = Shader.PropertyToID("_RW_Cull");
        private static readonly int PID_HasProbe = Shader.PropertyToID("_RW_HasProbe");
        private static readonly int PID_ProbeTex = Shader.PropertyToID("_RW_ProbeTex");
        private static readonly int PID_WorldToProbe = Shader.PropertyToID("_RW_WorldToProbe");
        private static readonly int PID_Sources = Shader.PropertyToID("_RW_Sources");
        private static readonly int PID_SourceCount = Shader.PropertyToID("_RW_SourceCount");
        private static readonly int PID_MaskMode = Shader.PropertyToID("_RW_MaskMode");
        private static readonly int PID_MaskTex = Shader.PropertyToID("_RW_MaskTex");
        public int probesBakedTotal;

        public bool Init()
        {
            if (!ShaderBundle.Ready)
                return false;
            matCullBack = new Material(ShaderBundle.IdShader) { name = "RW id cull back" };
            matCullBack.SetFloat(PID_Cull, (float)CullMode.Back);
            matHull = new Material(ShaderBundle.HullShader) { name = "RW hull" };
            matProbe = new Material(ShaderBundle.ProbeShader) { name = "RW probe" };
            matComposite = new Material(ShaderBundle.CompositeShader) { name = "RW composite" };
            cbComposite.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3);
            cbInside.DrawProcedural(Matrix4x4.identity, matComposite, 1, MeshTopology.Triangles, 3);
            try { insideEvent = (CameraEvent)Enum.Parse(typeof(CameraEvent), RWSettings.insideCompositeEvent, true); }
            catch (Exception) { insideEvent = CameraEvent.AfterForwardAlpha; }

            try { compositeEvent = (CameraEvent)Enum.Parse(typeof(CameraEvent), RWSettings.compositeEvent, true); }
            catch (Exception) { compositeEvent = CameraEvent.AfterForwardOpaque; }

            intCamGO = new GameObject("RealWindows InteriorCamera");
            intCam = intCamGO.AddComponent<Camera>();
            intCam.enabled = false;
            intCam.cullingMask = (1 << 16) | (1 << 20); // kerbals + internal space, as stock's IVA overlay
            intCam.clearFlags = CameraClearFlags.SolidColor;
            intCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            intCam.allowMSAA = false;
            intCam.useOcclusionCulling = false;
            RenderingPath rp;
            try { rp = (RenderingPath)Enum.Parse(typeof(RenderingPath), RWSettings.interiorRenderingPath, true); }
            catch (Exception) { rp = RenderingPath.Forward; }
            intCam.renderingPath = rp;

            Camera.onPreCull += OnPreCullAny;
            return true;
        }

        public void Dispose()
        {
            Camera.onPreCull -= OnPreCullAny;
            Detach();
            DetachInside();
            RestoreDepthMode();
            for (int i = 0; i < allInteriors.Count; i++)
            {
                PartPortal p = allInteriors[i];
                if (p.parkedHidden)
                {
                    p.SetForceOff(false);
                    p.parkedHidden = false;
                }
            }
            ReleaseRTs();
            if (intCamGO != null) UnityEngine.Object.Destroy(intCamGO);
            if (matCullBack != null) UnityEngine.Object.Destroy(matCullBack);
            if (matHull != null) UnityEngine.Object.Destroy(matHull);
            if (matProbe != null) UnityEngine.Object.Destroy(matProbe);
            if (matComposite != null) UnityEngine.Object.Destroy(matComposite);
            cbPasses.Release();
            cbComposite.Release();
            cbInside.Release();
        }

        // Switched off: nothing attached to the camera, nothing parked, no lists.
        public void Suspend()
        {
            wantActive = false;
            Detach();
            DetachInside();
            RestoreDepthMode();
            for (int i = 0; i < allInteriors.Count; i++)
            {
                if (allInteriors[i].parkedHidden)
                {
                    allInteriors[i].SetForceOff(false);
                    allInteriors[i].parkedHidden = false;
                }
            }
            interiors.Clear();
            occluders.Clear();
            allInteriors.Clear();
            staticAt = -1f;
        }

        // Parking: interiors of non-active vessels are kept forceRenderingOff
        // outside our passes so the stock IVA / overlay cameras never see them
        // (they share internal space with the active vessel's interiors).
        public void UpdateParking()
        {
            Vessel active = FlightGlobals.ActiveVessel;
            for (int i = 0; i < allInteriors.Count; i++)
            {
                PartPortal p = allInteriors[i];
                bool want = p.part.vessel != active;
                if (want != p.parkedHidden)
                {
                    p.SetForceOff(want);
                    p.parkedHidden = want;
                }
            }
        }

        // ------------------------------------------------------------------
        // frame
        // ------------------------------------------------------------------

        private void OnPreCullAny(Camera cam)
        {
            if (rendering)
                return;
            if (main == null || cam != main)
            {
                if (main == null && FlightCamera.fetch != null)
                    main = FlightCamera.fetch.mainCamera;
                if (cam != main)
                    return;
            }
            rendering = true;
            timer.Reset();
            timer.Start();
            try
            {
                renderedThisFrame = wantActive && interiors.Count > 0 && RenderPasses();
            }
            catch (Exception e)
            {
                RWLog.Error("render passes threw - disabling for this frame: {0}", e);
                renderedThisFrame = false;
            }
            finally
            {
                rendering = false;
                timer.Stop();
                timeSum += timer.Elapsed.TotalMilliseconds;
                timeFrames++;
            }
            if (renderedThisFrame) Attach(); else Detach();
            if (renderedThisFrame && insideId > 0) AttachInside(); else DetachInside();
            if (!renderedThisFrame)
                staticAt = -1f; // whatever is cached may be out of date by the next render
        }

        private bool RenderPasses()
        {
            int w = Mathf.Max(16, Mathf.RoundToInt(main.pixelWidth * RWSettings.renderScale));
            int h = Mathf.Max(16, Mathf.RoundToInt(main.pixelHeight * RWSettings.renderScale));
            EnsureRTs(w, h, main.allowHDR);

            Matrix4x4 view = main.worldToCameraMatrix;
            Matrix4x4 proj = main.projectionMatrix;
            GeometryUtility.CalculateFrustumPlanes(main, planes);

            // ids + alignment maps + groups, only for interiors with a window in view
            for (int i = 0; i < occluders.Count; i++)
                occluders[i].frameId = 0;
            for (int i = 0; i < allInteriors.Count; i++)
                allInteriors[i].frameId = 0; // also those beyond occluder range
            BuildGroups();
            if (groups.Count == 0)
                return false;
            insideId = insidePart != null ? insidePart.frameId : 0;

            // Everything else only matters where the windows are: crop to that area.
            float px = 2f * Mathf.Max(1f, RWSettings.feather) + 2f;
            Rect wr = windowRect;
            wr.xMin = Mathf.Clamp01(wr.xMin - px / main.pixelWidth);
            wr.yMin = Mathf.Clamp01(wr.yMin - px / main.pixelHeight);
            wr.xMax = Mathf.Clamp01(wr.xMax + px / main.pixelWidth);
            wr.yMax = Mathf.Clamp01(wr.yMax + px / main.pixelHeight);
            windowRect = wr;
            cropProj = Crop(wr) * proj;
            GeometryUtility.CalculateFrustumPlanes(cropProj * view, cropPlanes);

            RenderTexture prevActive = RenderTexture.active;
            cbPasses.Clear();

            // window masks: one per frame, from probes finished in an earlier frame
            BakeNextMask();

            // 0. probe bakes (once per IVA type, one per frame: each is many draws)
            int bakes = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                for (int m = 0; m < groups[g].members.Count; m++)
                {
                    PartPortal p = groups[g].members[m];
                    p.probe = Probes.Get(p);
                    if (!p.probe.baked && bakes < 1 && Probes.Bake(cbPasses, p, p.probe, matProbe))
                    {
                        bakes++;
                        probesBakedTotal++;
                    }
                }
            }

            // 1. hull pass: part id, depth, window mask + glass surface (Fresnel, glint, dirt)
            hullTargets[0] = idRT;
            hullTargets[1] = surfaceRT;
            cbPasses.SetRenderTarget(hullTargets, idRT.depthBuffer);
            cbPasses.ClearRenderTarget(true, true, Color.clear, 1f);
            cbPasses.SetViewProjectionMatrices(view, proj);
            cbPasses.SetGlobalVector("_RW_CamPos", main.transform.position);
            Light sun = Sun.Instance != null ? Sun.Instance.sunLight : null;
            cbPasses.SetGlobalVector("_RW_SunDir", sun != null ? -sun.transform.forward : Vector3.up);
            cbPasses.SetGlobalFloat("_RW_Smoothness", RWSettings.glassSmoothness);
            cbPasses.SetGlobalFloat("_RW_DirtScale", RWSettings.glassDirtScale);
            cbPasses.SetGlobalFloat("_RW_OpenFraction", RWSettings.probeOpenFraction);
            cbPasses.SetGlobalFloat("_RW_WallMargin", RWSettings.probeWallMargin);
            for (int i = 0; i < occluders.Count; i++)
            {
                PartPortal p = occluders[i];
                if (p.part == null)
                    continue;
                cbPasses.SetGlobalFloat(PID_PartId, p.frameId);
                bool confident = p.frameId > 0 && p.HasConfidentWindows;
                cbPasses.SetGlobalFloat("_RW_SunLit", p.part.vessel != null && p.part.vessel.directSunlight ? 1f : 0f);
                if (p.frameId > 0 && p.probe != null && p.probe.baked)
                {
                    cbPasses.SetGlobalFloat(PID_HasProbe, 1f);
                    cbPasses.SetGlobalTexture(PID_ProbeTex, p.probe.tex);
                    cbPasses.SetGlobalMatrix(PID_WorldToProbe, Probes.WorldToProbe(p, p.probe));
                    cbPasses.SetGlobalVectorArray(PID_Sources, p.probe.sources);
                    cbPasses.SetGlobalFloat(PID_SourceCount, p.probe.sourceCount);
                }
                else
                {
                    cbPasses.SetGlobalFloat(PID_HasProbe, 0f);
                }
                for (int k = 0; k < p.exterior.Count; k++)
                {
                    DrawItem d = p.exterior[k];
                    Renderer r = d.r;
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                        continue;
                    if (!GeometryUtility.TestPlanesAABB(cropPlanes, r.bounds))
                        continue; // can't cover a window
                    // 0 probe per pixel, 1 baked mask, 2 the author's glass, 3 solid
                    float maskMode = 0f;
                    if (d.solid)
                    {
                        maskMode = 3f;
                    }
                    else if (d.mask != null && d.mask.named && d.mask.namedRejected)
                    {
                        maskMode = 3f; // named like glass, but no opening behind it
                    }
                    else if (p.frameId > 0 && d.mask != null && d.mask.named && d.mask.tex != null)
                    {
                        maskMode = 1f; // window-named mesh, trimmed to its glass
                        cbPasses.SetGlobalTexture(PID_MaskTex, d.mask.tex);
                    }
                    else if (p.frameId > 0 && d.glass != null && d.glass.channel == 6)
                    {
                        maskMode = 2f;
                    }
                    else if (p.frameId > 0 && d.mask != null && d.mask.tex != null)
                    {
                        maskMode = 1f;
                        cbPasses.SetGlobalTexture(PID_MaskTex, d.mask.tex);
                    }
                    else if (p.frameId > 0 && (d.mask == null || (d.mask.done && d.mask.tex == null)) && confident)
                    {
                        maskMode = 3f; // nothing confident here, and the part's windows are known elsewhere: no per-pixel probe
                    }
                    cbPasses.SetGlobalFloat(PID_MaskMode, maskMode);
                    cbPasses.DrawRenderer(r, matHull, d.sub, 0);
                }
            }

            // 2. interior front surfaces: which part's interior each pixel sees, and how
            //    deep. The shell answers that; props and kerbals add only draw calls.
            cbPasses.SetRenderTarget(frontRT);
            cbPasses.ClearRenderTarget(true, true, Color.clear, 1f);
            for (int g = 0; g < groups.Count; g++)
            {
                PortalGroup grp = groups[g];
                Matrix4x4 groupView = view * grp.map.InverseMatrix;
                cbPasses.SetViewProjectionMatrices(groupView, proj);
                GeometryUtility.CalculateFrustumPlanes(cropProj * groupView, groupPlanes);
                for (int m = 0; m < grp.members.Count; m++)
                {
                    PartPortal p = grp.members[m];
                    cbPasses.SetGlobalFloat(PID_PartId, p.frameId);
                    DrawList(p.interiorShell, matCullBack, p.StockHidden);
                }
            }

            Graphics.ExecuteCommandBuffer(cbPasses);

            // 3. lit interior colour
            RenderInteriorColour();

            RenderTexture.active = prevActive;

            // 4. composite parameters
            matComposite.SetTexture("_RW_IdTex", idRT);
            matComposite.SetTexture("_RW_SurfaceTex", surfaceRT);
            Color sunColour = sun != null ? sun.color * sun.intensity : Color.white;
            matComposite.SetColor("_RW_SunColor", sunColour * RWSettings.glassSpecular);
            matComposite.SetFloat("_RW_Reflection", RWSettings.glassReflection);
            matComposite.SetFloat("_RW_Dirt", RWSettings.glassDirt);
            matComposite.SetTexture("_RW_FrontTex", frontRT);
            matComposite.SetTexture("_RW_InteriorTex", colorRT);
            matComposite.SetFloat("_RW_HullEps", RWSettings.hullEpsilon);
            matComposite.SetFloat("_RW_SceneEps", RWSettings.sceneDepthEpsilon);
            matComposite.SetFloat("_RW_UseSceneDepth", RWSettings.useSceneDepth ? 1f : 0f);
            matComposite.SetFloat("_RW_Transmission", RWSettings.transmission);
            matComposite.SetColor("_RW_Tint", RWSettings.glassTint);
            matComposite.SetFloat("_RW_Feather", RWSettings.feather);
            matComposite.SetFloat("_RW_Debug", RWSettings.debugMode);
            matComposite.SetFloat("_RW_FlipY", RWSettings.flipY);
            matComposite.SetFloat("_RW_InsideId", insideId);
            for (int i = 0; i < MaxIds; i++)
                idGroup[i] = -1f;
            for (int g = 0; g < groups.Count; g++)
                for (int m = 0; m < groups[g].members.Count; m++)
                {
                    int fid = groups[g].members[m].frameId;
                    if (fid > 0 && fid < MaxIds)
                        idGroup[fid] = g;
                }
            matComposite.SetFloatArray("_RW_IdGroup", idGroup);
            matComposite.SetFloat("_RW_SeeNeighbours", RWSettings.seeIntoNeighbours ? 1f : 0f);

            lastGroupCount = groups.Count;
            return true;
        }

        private void BakeNextMask()
        {
            for (int g = 0; g < groups.Count; g++)
            {
                for (int m = 0; m < groups[g].members.Count; m++)
                {
                    PartPortal p = groups[g].members[m];
                    ProbeData d = Probes.Get(p);
                    if (d.baked && d.bakedFrame < Time.frameCount && HullMasks.BakeNext(p, d, matHull))
                        return;
                }
            }
        }

        private void AddNeighbour(Part n, int g, PortalGroup grp)
        {
            PartPortal q;
            if (n == null || n.vessel != grp.vessel || !byPart.TryGetValue(n, out q))
                return;
            if ((q.frameId > 0 && q.group == g) || !q.InteriorUsable)
                return;
            context.Add(q);
        }

        // Depth masks write depth but no colour: FreeIva's hatch doorways and window
        // plugs are meant for a viewer in IVA and would hide whatever lies behind them
        // (the next part through an open hatch, kerbals walking through it).
        private void HideMasks(PartPortal p)
        {
            if (!RWSettings.hideDepthMasks)
                return;
            for (int k = 0; k < p.depthMaskRenderers.Count; k++)
            {
                Renderer r = p.depthMaskRenderers[k];
                if (r != null && !r.forceRenderingOff)
                {
                    r.forceRenderingOff = true;
                    masksHidden.Add(r);
                }
            }
        }

        // Stock keeps interiors hidden outside IVA (renderers disabled): enable them
        // for this render only.
        private void FlipOn(PartPortal p)
        {
            if (!p.StockHidden)
                return;
            for (int k = 0; k < p.interiorRenderers.Count; k++)
            {
                Renderer r = p.interiorRenderers[k];
                if (r != null && !r.enabled)
                {
                    r.enabled = true;
                    flippedEnabled.Add(r);
                }
            }
        }

        private void DrawList(List<DrawItem> list, Material mat, bool stockHidden)
        {
            for (int k = 0; k < list.Count; k++)
            {
                DrawItem d = list[k];
                Renderer r = d.r;
                if (r == null || !r.gameObject.activeInHierarchy)
                    continue;
                if (!r.enabled && !stockHidden)
                    continue; // individually hidden prop/helmet
                // bounds of disabled renderers are unreliable, so cull only enabled ones
                if (r.enabled && !GeometryUtility.TestPlanesAABB(groupPlanes, r.bounds))
                    continue;
                cbPasses.DrawRenderer(r, mat, d.sub, 0);
            }
        }

        // Maps the viewport area r (0-1) onto the whole clip space, so a projection
        // P becomes Crop(r) * P: the same picture, only the part inside r.
        private static Matrix4x4 Crop(Rect r)
        {
            float sx = 1f / Mathf.Max(r.width, 1e-4f), sy = 1f / Mathf.Max(r.height, 1e-4f);
            float cx = r.xMin + r.xMax - 1f, cy = r.yMin + r.yMax - 1f; // area centre, clip space
            Matrix4x4 m = Matrix4x4.identity;
            m.m00 = sx;
            m.m03 = -cx * sx;
            m.m11 = sy;
            m.m13 = -cy * sy;
            return m;
        }

        // Viewport area of a world box; false if it is off screen or too small to see.
        private bool ScreenArea(Bounds b, out Rect area)
        {
            area = new Rect(0, 0, 1, 1);
            if (!GeometryUtility.TestPlanesAABB(planes, b))
                return false;
            Vector3 c = b.center, e = b.extents;
            float x0 = 1, y0 = 1, x1 = 0, y1 = 0;
            for (int i = 0; i < 8; i++)
            {
                Vector3 w = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                Vector3 v = main.WorldToViewportPoint(w);
                if (v.z <= main.nearClipPlane)
                    return true; // reaches behind the camera: treat as the whole screen
                x0 = Mathf.Min(x0, v.x); y0 = Mathf.Min(y0, v.y);
                x1 = Mathf.Max(x1, v.x); y1 = Mathf.Max(y1, v.y);
            }
            area = Rect.MinMaxRect(Mathf.Clamp01(x0), Mathf.Clamp01(y0), Mathf.Clamp01(x1), Mathf.Clamp01(y1));
            float size = Mathf.Max((x1 - x0) * main.pixelWidth, (y1 - y0) * main.pixelHeight);
            return size >= RWSettings.minWindowPixels && area.width > 0 && area.height > 0;
        }

        // The nearest interior whose shell box contains the camera (mapped into its
        // interior frame), within a few metres. Zooming into a part then shows its
        // interior whole, like the stock interior overlay.
        private PartPortal FindCameraInside()
        {
            if (!RWSettings.showInteriorWhenInside)
                return null;
            Vector3 cam = main.transform.position;
            for (int i = 0; i < interiors.Count; i++)
            {
                PartPortal p = interiors[i];
                if (p.part == null || !p.InteriorUsable || !p.haveShellBounds || p.distance > 15f)
                    continue;
                RigidMap m = Alignment.Compute(p.part.transform, p.model.transform);
                Vector3 local = p.model.transform.InverseTransformPoint(m.Apply(cam));
                Bounds b = p.shellLocalBounds;
                b.Expand(-0.1f);
                if (b.Contains(local))
                    return p;
            }
            return null;
        }

        private void BuildGroups()
        {
            for (int i = 0; i < groups.Count; i++)
            {
                groups[i].members.Clear();
                groupPool.Push(groups[i]);
            }
            groups.Clear();

            int id = 0;
            lastInteriorCount = 0;
            lastCulled = 0;
            bool haveRect = false;
            insidePart = FindCameraInside();
            for (int i = 0; i < interiors.Count; i++)
            {
                PartPortal p = interiors[i];
                if (p.part == null || p.part.vessel == null || !p.InteriorUsable)
                    continue;
                // no window in view, too small to see, or none at all: skip the interior
                // (never the one the camera is in: it is seen whole, see the composite)
                Bounds wb;
                Rect area = new Rect(0, 0, 1, 1);
                if (p != insidePart && (!p.MayHaveWindows || !p.WindowBounds(out wb) || !ScreenArea(wb, out area)))
                {
                    lastCulled++;
                    continue;
                }
                if (!haveRect) { windowRect = area; haveRect = true; }
                else windowRect = Rect.MinMaxRect(Mathf.Min(windowRect.xMin, area.xMin), Mathf.Min(windowRect.yMin, area.yMin),
                                                   Mathf.Max(windowRect.xMax, area.xMax), Mathf.Max(windowRect.yMax, area.yMax));
                p.map = Alignment.Compute(p.part.transform, p.model.transform);
                p.frameId = ++id;
                lastInteriorCount++;

                PortalGroup target = null;
                for (int g = 0; g < groups.Count; g++)
                {
                    PortalGroup grp = groups[g];
                    if (grp.vessel != p.part.vessel)
                        continue;
                    float meters, degrees;
                    Alignment.Disagreement(grp.map, p.map, p.part.transform.position, out meters, out degrees);
                    if (meters <= RWSettings.groupPositionTolerance && degrees <= RWSettings.groupAngleTolerance)
                    {
                        target = grp;
                        break;
                    }
                }
                if (target == null)
                {
                    target = groupPool.Count > 0 ? groupPool.Pop() : new PortalGroup();
                    target.map = p.map;
                    target.vessel = p.part.vessel;
                    groups.Add(target);
                }
                target.members.Add(p);
                p.group = groups.IndexOf(target);
            }
        }

        private static bool SplitSupported
        {
            get { return SystemInfo.copyTextureSupport != CopyTextureSupport.None && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.Depth); }
        }

        private enum ColourPass { All, Static, Kerbals }

        private void RenderInteriorColour()
        {
            intCam.allowHDR = main.allowHDR;
            intCam.nearClipPlane = main.nearClipPlane;
            intCam.farClipPlane = main.farClipPlane;
            intCam.fieldOfView = main.fieldOfView;
            intCam.aspect = main.aspect;
            // only the windows' screen area: Unity culls everything outside it
            intCam.rect = windowRect;
            intCam.projectionMatrix = cropProj;
            ShadowQuality shadows = QualitySettings.shadows;
            if (!RWSettings.interiorShadows)
                QualitySettings.shadows = ShadowQuality.Disable;

            if (ivaSun == null && RWSettings.aimIvaSunPerGroup)
                ivaSun = UnityEngine.Object.FindObjectOfType<IVASun>();
            Quaternion sunSaved = ivaSun != null ? ivaSun.transform.rotation : Quaternion.identity;

            byPart.Clear();
            for (int i = 0; i < allInteriors.Count; i++)
                if (allInteriors[i].part != null)
                    byPart[allInteriors[i].part] = allInteriors[i];

            bool split = RWSettings.interiorRefresh >= 0f && staticColorRT != null && staticDepthRT != null && dynDepthRT != null;
            if (split)
            {
                if (StaticStale())
                {
                    RenderGroups(ColourPass.Static);
                    RememberStaticView();
                    staticRedraws++;
                }
                else
                {
                    staticReuses++;
                }
                Graphics.CopyTexture(staticColorRT, colorRT);
                Graphics.CopyTexture(staticDepthRT, dynDepthRT);
                if (!RWSettings.kerbalShadows)
                    QualitySettings.shadows = ShadowQuality.Disable;
                RenderGroups(ColourPass.Kerbals);
            }
            else
            {
                RenderGroups(ColourPass.All);
            }
            intCam.targetTexture = null;

            QualitySettings.shadows = shadows;
            if (ivaSun != null)
                ivaSun.transform.rotation = sunSaved;

            // back to the resting state: parked ones off, the rest on
            for (int i = 0; i < allInteriors.Count; i++)
                allInteriors[i].SetForceOff(allInteriors[i].parkedHidden);
        }

        // One camera render per alignment group. All: both layers into colorRT.
        // Static: the internal-space layer into the cache. Kerbals: the kerbals
        // layer into colorRT, over the copied cache, against the copied depth;
        // only this pass is bracketed by the API events (stand-ins are kerbals).
        private void RenderGroups(ColourPass pass)
        {
            Transform camT = main.transform;
            Transform sunT = ivaSun != null ? ivaSun.sunT : null;
            switch (pass)
            {
                case ColourPass.All:
                    intCam.targetTexture = colorRT;
                    intCam.cullingMask = (1 << LayerKerbals) | (1 << LayerInternal);
                    break;
                case ColourPass.Static:
                    intCam.SetTargetBuffers(staticColorRT.colorBuffer, staticDepthRT.depthBuffer);
                    intCam.cullingMask = 1 << LayerInternal;
                    break;
                default:
                    intCam.SetTargetBuffers(colorRT.colorBuffer, dynDepthRT.depthBuffer);
                    intCam.cullingMask = 1 << LayerKerbals;
                    break;
            }

            for (int g = 0; g < groups.Count; g++)
            {
                PortalGroup grp = groups[g];
                // Neighbours of the group's parts on the same vessel: not rendered for
                // their own windows, but seen through open hatches from a rendered part.
                context.Clear();
                if (RWSettings.renderNeighbours)
                {
                    for (int m = 0; m < grp.members.Count; m++)
                    {
                        Part mp = grp.members[m].part;
                        AddNeighbour(mp.parent, g, grp);
                        for (int c = 0; c < mp.children.Count; c++)
                            AddNeighbour(mp.children[c], g, grp);
                        for (int a = 0; a < mp.attachNodes.Count; a++)
                            AddNeighbour(mp.attachNodes[a].attachedPart, g, grp);
                    }
                }
                // only this group's interiors and their neighbours (SetForceOff skips unchanged
                // ones). Parked interiors (other vessels, hidden from the stock cameras) are
                // switched on for their own render and parked again afterwards.
                for (int i = 0; i < allInteriors.Count; i++)
                {
                    PartPortal q = allInteriors[i];
                    q.SetForceOff(!((q.frameId > 0 && q.group == g) || context.Contains(q)));
                }
                flippedEnabled.Clear();
                masksHidden.Clear();
                for (int m = 0; m < grp.members.Count; m++)
                {
                    FlipOn(grp.members[m]);
                    HideMasks(grp.members[m]);
                }
                foreach (PartPortal q in context)
                {
                    FlipOn(q);
                    HideMasks(q);
                }

                intCam.transform.SetPositionAndRotation(grp.map.Apply(camT.position), grp.map.Apply(camT.rotation));
                // the first render of the frame clears; the kerbals pass draws over the copied cache
                intCam.clearFlags = g == 0 && pass != ColourPass.Kerbals ? CameraClearFlags.SolidColor : CameraClearFlags.Nothing;
                if (ivaSun != null && sunT != null)
                    ivaSun.transform.rotation = grp.map.Apply(sunT.rotation);

                bool api = pass != ColourPass.Static && RealWindowsApi.HasSubscribers;
                if (api)
                {
                    apiParts.Clear();
                    for (int m = 0; m < grp.members.Count; m++)
                        apiParts.Add(grp.members[m].part);
                    foreach (PartPortal q in context)
                        apiParts.Add(q.part);
                    RealWindowsApi.Fire(true, apiParts);
                }

                intCam.Render();

                if (api)
                    RealWindowsApi.Fire(false, apiParts);

                for (int k = 0; k < flippedEnabled.Count; k++)
                    if (flippedEnabled[k] != null)
                        flippedEnabled[k].enabled = false;
                for (int k = 0; k < masksHidden.Count; k++)
                    if (masksHidden[k] != null)
                        masksHidden[k].forceRenderingOff = false;
            }
        }

        // The cached layer is redrawn when anything that shapes its picture
        // changed: the view of each group's interiors (camera pose mapped into
        // internal space), the projection, which interiors are drawn, a crop
        // area that now reaches past the cached one, shell meshes moving inside
        // their interior (centrifuges); and every `interiorRefresh` seconds
        // (lighting, animated props) or when another mod asks.
        private bool StaticStale()
        {
            if (staticAt < 0f || RealWindowsApi.TakeRedrawRequest())
                return true;
            if (RWSettings.interiorRefresh > 0f && Time.unscaledTime - staticAt >= RWSettings.interiorRefresh)
                return true;
            if (groups.Count != staticPos.Count || ViewHash() != staticHash)
                return true;
            float px = 0.5f / Mathf.Max(1, rtW), py = 0.5f / Mathf.Max(1, rtH);
            if (windowRect.xMin < staticRect.xMin - px || windowRect.yMin < staticRect.yMin - py
                || windowRect.xMax > staticRect.xMax + px || windowRect.yMax > staticRect.yMax + py)
                return true;
            Matrix4x4 proj = main.nonJitteredProjectionMatrix;
            for (int k = 0; k < 16; k++)
                if (Mathf.Abs(proj[k] - staticProj[k]) > 1e-5f)
                    return true;
            Vector4 shell = ShellSum();
            if ((shell - staticShellSum).sqrMagnitude > 1e-6f)
                return true;
            Transform camT = main.transform;
            float maxMove = RWSettings.interiorRedrawMove, maxTurn = RWSettings.interiorRedrawTurn;
            for (int g = 0; g < groups.Count; g++)
            {
                if ((groups[g].map.Apply(camT.position) - staticPos[g]).sqrMagnitude > maxMove * maxMove)
                    return true;
                if (Quaternion.Angle(groups[g].map.Apply(camT.rotation), staticRot[g]) > maxTurn)
                    return true;
            }
            return false;
        }

        private void RememberStaticView()
        {
            staticAt = Time.unscaledTime;
            staticRect = windowRect;
            staticProj = main.nonJitteredProjectionMatrix;
            staticHash = ViewHash();
            staticShellSum = ShellSum();
            staticPos.Clear();
            staticRot.Clear();
            Transform camT = main.transform;
            for (int g = 0; g < groups.Count; g++)
            {
                staticPos.Add(groups[g].map.Apply(camT.position));
                staticRot.Add(groups[g].map.Apply(camT.rotation));
            }
        }

        // Where the shell meshes of the drawn interiors sit inside their interiors,
        // summed: changes when part of an interior moves (a spinning centrifuge).
        private Vector4 ShellSum()
        {
            Vector4 s = Vector4.zero;
            for (int g = 0; g < groups.Count; g++)
            {
                List<PartPortal> ms = groups[g].members;
                for (int m = 0; m < ms.Count; m++)
                {
                    PartPortal p = ms[m];
                    if (p.model == null)
                        continue;
                    Transform root = p.model.transform;
                    Renderer last = null;
                    for (int k = 0; k < p.interiorShell.Count; k++)
                    {
                        Renderer r = p.interiorShell[k].r;
                        if (r == null || r == last)
                            continue; // sub-meshes of one renderer share a transform
                        last = r;
                        Transform t = r.transform;
                        Vector3 pos = root.InverseTransformPoint(t.position);
                        Vector3 fwd = root.InverseTransformDirection(t.forward);
                        s += new Vector4(pos.x + fwd.x, pos.y + fwd.y, pos.z + fwd.z, (k + 1) * (fwd.x - fwd.z));
                    }
                }
            }
            return s;
        }

        // Which interiors are drawn, in which group, and which instance each is.
        private int ViewHash()
        {
            unchecked
            {
                int h = 17;
                for (int g = 0; g < groups.Count; g++)
                {
                    h = h * 31 + g;
                    List<PartPortal> ms = groups[g].members;
                    for (int m = 0; m < ms.Count; m++)
                        h = h * 31 + (ms[m].model != null ? ms[m].model.GetInstanceID() : 0);
                }
                for (int i = 0; i < allInteriors.Count; i++)
                    h = h * 31 + (allInteriors[i].model != null ? allInteriors[i].model.GetInstanceID() : 0);
                return h;
            }
        }

        // ------------------------------------------------------------------
        // composite attach / RTs
        // ------------------------------------------------------------------

        private void Attach()
        {
            if (compositeAttached && compositeCamera == main)
                return;
            Detach();
            if (main == null)
                return;
            if (RWSettings.useSceneDepth && !depthModeTouched)
            {
                originalDepthMode = main.depthTextureMode;
                main.depthTextureMode |= DepthTextureMode.Depth;
                depthModeTouched = true;
            }
            main.AddCommandBuffer(compositeEvent, cbComposite);
            compositeCamera = main;
            compositeAttached = true;
            RWLog.Verbose("composite attached to {0} at {1}", main.name, compositeEvent);
        }

        private void Detach()
        {
            if (!compositeAttached)
                return;
            if (compositeCamera != null)
                compositeCamera.RemoveCommandBuffer(compositeEvent, cbComposite);
            compositeAttached = false;
            compositeCamera = null;
        }

        private void AttachInside()
        {
            if (insideAttached && insideCamera == main)
                return;
            DetachInside();
            if (main == null || !RWSettings.insideRedraw)
                return;
            main.AddCommandBuffer(insideEvent, cbInside);
            insideCamera = main;
            insideAttached = true;
            RWLog.Verbose("inside view attached to {0} at {1}", main.name, insideEvent);
        }

        private void DetachInside()
        {
            if (!insideAttached)
                return;
            if (insideCamera != null)
                insideCamera.RemoveCommandBuffer(insideEvent, cbInside);
            insideAttached = false;
            insideCamera = null;
        }

        private void RestoreDepthMode()
        {
            if (depthModeTouched && main != null)
                main.depthTextureMode = originalDepthMode;
            depthModeTouched = false;
        }

        private void EnsureRTs(int w, int h, bool hdr)
        {
            if (idRT != null && rtW == w && rtH == h && rtHDR == hdr && idRT.IsCreated())
                return;
            ReleaseRTs();
            rtW = w;
            rtH = h;
            rtHDR = hdr;
            RenderTextureFormat idFmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGFloat)
                ? RenderTextureFormat.RGFloat : RenderTextureFormat.ARGBFloat;
            idRT = MakeRT("RW hull id+mask", w, h, RenderTextureFormat.ARGBFloat);
            surfaceRT = MakeRT("RW glass surface", w, h, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear, 0);
            frontRT = MakeRT("RW interior front", w, h, idFmt);
            colorRT = MakeRT("RW interior colour", w, h, hdr ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default, RenderTextureReadWrite.Default);
            colorRT.filterMode = FilterMode.Bilinear;
            if (SplitSupported)
            {
                staticColorRT = MakeRT("RW interior static colour", w, h, colorRT.format, RenderTextureReadWrite.Default, 0);
                staticDepthRT = MakeRT("RW interior static depth", w, h, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear, 24);
                dynDepthRT = MakeRT("RW interior kerbal depth", w, h, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear, 24);
            }
            RWLog.Info("render targets {0}x{1} (hdr={2}, id format {3})", w, h, hdr, idFmt);
        }

        private static RenderTexture MakeRT(string name, int w, int h, RenderTextureFormat fmt,
            RenderTextureReadWrite rw = RenderTextureReadWrite.Linear, int depth = 24)
        {
            RenderTexture rt = new RenderTexture(w, h, depth, fmt, rw)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            rt.Create();
            return rt;
        }

        private void ReleaseRTs()
        {
            RenderTexture[] all = { idRT, surfaceRT, frontRT, colorRT, staticColorRT, staticDepthRT, dynDepthRT };
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null)
                {
                    all[i].Release();
                    UnityEngine.Object.Destroy(all[i]);
                }
            }
            idRT = surfaceRT = frontRT = colorRT = staticColorRT = staticDepthRT = dynDepthRT = null;
            staticAt = -1f;
        }
    }
}
