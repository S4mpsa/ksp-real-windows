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
//      Interiors that move (a spinning ring) stay out of the cache and are drawn
//      whole with the kerbals every frame, so the rest stays cached.
//   4. Composite      (command buffer on the main camera): the interior where
//      the hull pixel is a window and the interior seen there is that part's or
//      one drawn in the same render, with the glass look on top. With the camera
//      inside a part, its interior is shown whole, and drawn a second time after
//      clouds and atmosphere. With true holes (seeThroughDepth) the glass of the
//      cut-outs is a third buffer, also after clouds and atmosphere.
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

        // CPU time per phase of the frame (verbose status line). GPU time can't be
        // measured from a mod (no GPU Recorder in KSP's Unity build).
        private static readonly string[] PhaseNames = { "setup", "commands", "submit", "motion", "cabin redraw", "crew pass", "other" };
        private const int PhSetup = 0, PhCommands = 1, PhSubmit = 2, PhMotion = 3, PhCabin = 4, PhCrew = 5, PhOther = 6;
        private readonly double[] phaseSum = new double[7];
        private double lapMark;
        private int phaseFrames;
        public double listsSum;    // RealWindows.Update: part lists and parking
        public int listsFrames;

        private void Lap(int phase)
        {
            double now = timer.Elapsed.TotalMilliseconds;
            phaseSum[phase] += now - lapMark;
            lapMark = now;
        }

        public string TakePhases()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendFormat("lists {0:F2}", listsFrames > 0 ? listsSum / listsFrames : 0);
            for (int i = 0; i < phaseSum.Length; i++)
            {
                sb.AppendFormat(", {0} {1:F2}", PhaseNames[i], phaseFrames > 0 ? phaseSum[i] / phaseFrames : 0);
                phaseSum[i] = 0;
            }
            phaseFrames = 0;
            listsSum = 0;
            listsFrames = 0;
            return sb.ToString();
        }

        private Camera main;
        private Camera intCam;
        private GameObject intCamGO;

        private RenderTexture idRT, surfaceRT, frontRT, colorRT;
        private RenderTexture behindRT;   // the next hull surface behind each window pixel (seeThroughWindows)
        private bool behindDrawn;
        // depth cut-outs (seeThroughWindows): per looked-through part, a quad over its windows
        // drawn by the main camera just before that part's hull (Renderer.sortingOrder)
        private Material matCover;
        // true holes (seeThroughDepth): the looked-through parts are drawn first, near to
        // far, and after each hull a second quad puts the far plane back where the
        // cut-out wrote the pane's depth. What is drawn later (the rest of the scene,
        // then sky, atmosphere, clouds) finds a real hole there. See RW_Cover.shader.
        private Material matReopen;
        private readonly List<MeshRenderer> reopens = new List<MeshRenderer>();
        private bool trueHoles;         // this frame's cut-outs are true holes
        private int holesState = -1;    // last logged: 0 plain cut-outs, 1 true holes
        private const int HoleOrderBase = -3000;
        private Mesh coverMesh;
        private readonly List<MeshRenderer> covers = new List<MeshRenderer>();
        private readonly MaterialPropertyBlock coverBlock = new MaterialPropertyBlock();
        private readonly List<PartPortal> holeParts = new List<PartPortal>();
        private readonly float[] holeIds = new float[MaxIds];
        private readonly List<Renderer> reordered = new List<Renderer>();
        private readonly List<int> reorderedWas = new List<int>();
        private readonly HashSet<Renderer> reorderSeen = new HashSet<Renderer>();
        private bool coversActive;
        private const int CoverOrderBase = 1000;
        public int holeFrames;     // for the status line
        // cached internal-space layer: colour + depth, and the per-frame depth copy
        // the kerbals layer is tested and written against
        private RenderTexture staticColorRT, staticDepthRT, dynDepthRT;
        private float staticAt = -1f;
        private Rect staticRect;
        private Matrix4x4 staticProj;
        private int staticHash;
        private readonly List<Vector3> staticPos = new List<Vector3>();
        private readonly List<Quaternion> staticRot = new List<Quaternion>();
        public int staticRedraws, staticReuses;   // for the status line
        public int kerbalSkips;                   // frames whose kerbals pass was skipped (nothing moving), for the status line
        private RenderTexture interiorTex, interiorDepthTex;   // what the composite reads this frame: the cache itself, or cache + kerbals
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
        // true holes: the glass of the cut-outs, laid after the sky, atmosphere and
        // clouds now drawn into them (they would cover a pane laid before)
        private readonly CommandBuffer cbHoles = new CommandBuffer { name = "RealWindows cut-out glass" };
        private Camera holesCamera;
        private CameraEvent holesEvent;
        private bool holesAttached;
        private readonly List<Renderer> masksHidden = new List<Renderer>();
        private readonly List<Renderer> staticHidden = new List<Renderer>();   // cached interiors' shell/props, off in the Kerbals pass
        // render group of each part id (index = id, -1 none): the composite lets a
        // window show another part's interior when both were drawn in one render
        // (an open hatch looking into the next cabin)
        public const int MaxIds = 128;
        private readonly float[] idGroup = new float[MaxIds];
        private DepthTextureMode originalDepthMode;
        private bool depthModeTouched;
        // Other mods may change the flight camera on a camera switch (IVA and back):
        // the depth-texture flag the window test needs, or our command buffers.
        // Checked after every switch and every few seconds, repaired, and logged once.
        private bool cameraSwitched;
        private float nextCameraCheck;
        private bool warnedDepth, warnedBuffer;

        private readonly List<PortalGroup> groups = new List<PortalGroup>();
        private readonly Stack<PortalGroup> groupPool = new Stack<PortalGroup>();
        private readonly List<Renderer> flippedEnabled = new List<Renderer>();
        private readonly List<Part> apiParts = new List<Part>();
        private readonly Dictionary<Part, PartPortal> byPart = new Dictionary<Part, PartPortal>();
        private readonly HashSet<PartPortal> context = new HashSet<PartPortal>();
        private PartPortal insidePart;   // the interior the camera is in, if any
        // IVA view: the IVA camera draws the part it is in (and FreeIva's kerbal may
        // float into another); those are never rendered here, and nothing is "inside"
        public bool ivaView;
        public Part ivaPart;
        public int insideId;
        private readonly Plane[] planes = new Plane[6];       // full view
        private readonly Plane[] cropPlanes = new Plane[6];   // the screen area the windows cover
        private readonly Plane[] groupPlanes = new Plane[6];  // the same, in a group's internal space
        private Rect windowRect;                              // viewport (0-1) area of all windows
        private Matrix4x4 cropProj;                           // projection of just that area
        private Matrix4x4 fullProj;                           // the main camera's projection this frame (uncropped)
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
        private static readonly int PID_Behind = Shader.PropertyToID("_RW_Behind");
        private static readonly int PID_FrontLayer = Shader.PropertyToID("_RW_FrontLayer");
        private static readonly int PID_LayerSize = Shader.PropertyToID("_RW_LayerSize");
        private static readonly int PID_BehindEps = Shader.PropertyToID("_RW_BehindEps");
        private static readonly int PID_DrawRect = Shader.PropertyToID("_RW_DrawRect");
        private static readonly int PID_LateHoles = Shader.PropertyToID("_RW_LateHoles");
        // switched off (toggle key / startEnabled): nothing attached; cleared each
        // frame by the addon while switched on
        public bool suspended = true;
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
            if (ShaderBundle.CoverShader != null)
            {
                matCover = new Material(ShaderBundle.CoverShader) { name = "RW window cut-out" };
                matReopen = new Material(ShaderBundle.CoverShader) { name = "RW window cut-out re-open" };
                matReopen.SetFloat("_RW_CoverZTest", (float)CompareFunction.Always);
                matReopen.SetFloat("_RW_CoverOpen", 1f);
            }
            // Bind the camera's own target first: a buffer that runs before ours on the
            // same event may leave something else bound. Cinematic Shaders' GTAO ends in
            // a native plugin event that binds its own targets behind Unity's back; once
            // our composite ran after it (re-attached after IVA and back), it drew into
            // GTAO's texture and every window lost its interior.
            if (RWSettings.bindCameraTarget)
            {
                cbComposite.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                cbInside.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                cbHoles.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            }
            // a quad over _RW_DrawRect (the windows' area, set every frame)
            cbComposite.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 6);
            cbInside.DrawProcedural(Matrix4x4.identity, matComposite, 1, MeshTopology.Triangles, 6);
            cbHoles.DrawProcedural(Matrix4x4.identity, matComposite, 2, MeshTopology.Triangles, 6);
            try { insideEvent = (CameraEvent)Enum.Parse(typeof(CameraEvent), RWSettings.insideCompositeEvent, true); }
            catch (Exception) { insideEvent = CameraEvent.AfterForwardAlpha; }
            try { holesEvent = (CameraEvent)Enum.Parse(typeof(CameraEvent), RWSettings.holeCompositeEvent, true); }
            catch (Exception) { holesEvent = CameraEvent.AfterForwardAlpha; }

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

            if (RWSettings.verboseLogging)
                diagAt = Time.unscaledTime + 20f;
            Camera.onPreCull += OnPreCullAny;
            Camera.onPostRender += OnPostRenderAny;
            GameEvents.OnCameraChange.Add(OnCameraChange);

            // Attach now, before other mods add theirs once the scene is up (Cinematic
            // Shaders turns GTAO on at onLevelWasLoadedGUIReady): first in line on the
            // event, drawing nothing until there is something to draw.
            matComposite.SetVector(PID_DrawRect, Vector4.zero);
            if (RWSettings.startEnabled && FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null)
            {
                main = FlightCamera.fetch.mainCamera;
                suspended = false;
                Attach();
                AttachInside();
                AttachHoles();
            }
            return true;
        }

        public void Dispose()
        {
            Camera.onPreCull -= OnPreCullAny;
            Camera.onPostRender -= OnPostRenderAny;
            GameEvents.OnCameraChange.Remove(OnCameraChange);
            DisableCovers();
            for (int i = 0; i < covers.Count; i++)
                if (covers[i] != null)
                    UnityEngine.Object.Destroy(covers[i].gameObject);
            covers.Clear();
            for (int i = 0; i < reopens.Count; i++)
                if (reopens[i] != null)
                    UnityEngine.Object.Destroy(reopens[i].gameObject);
            reopens.Clear();
            if (coverMesh != null) UnityEngine.Object.Destroy(coverMesh);
            ReleaseBaked();
            if (matCover != null) UnityEngine.Object.Destroy(matCover);
            if (matReopen != null) UnityEngine.Object.Destroy(matReopen);
            Detach();
            DetachInside();
            DetachHoles();
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
            cbHoles.Release();
        }

        // Switched off: nothing attached to the camera, nothing parked, no lists.
        public void Suspend()
        {
            wantActive = false;
            suspended = true;
            Detach();
            DetachInside();
            DetachHoles();
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
            DisableCovers(); // in case the last frame's post-render never came
            rendering = true;
            timer.Reset();
            timer.Start();
            lapMark = 0;
            try
            {
                renderedThisFrame = wantActive && interiors.Count > 0 && RenderPasses();
                if (renderedThisFrame)
                {
                    Lap(PhOther);
                    phaseFrames++;
                }
            }
            catch (Exception e)
            {
                RWLog.Error("render passes threw - disabling for this frame: {0}", e);
                renderedThisFrame = false;
            }
            finally
            {
                RestoreShadows();
                rendering = false;
                timer.Stop();
                timeSum += timer.Elapsed.TotalMilliseconds;
                timeFrames++;
            }
            if (cameraSwitched || Time.unscaledTime >= nextCameraCheck)
                CheckCamera();
            // Attached from the first frame and kept attached while switched on, so the
            // buffers keep their place among other mods' buffers on the same event: a
            // re-attach puts them last, after steps that can leave Unity's cached
            // render state wrong (Cinematic Shaders' GTAO, a native plugin step at
            // AfterForwardOpaque, after which the composite read the wrong inputs and
            // drew nothing). With nothing to draw, the quad is empty.
            if (!renderedThisFrame)
            {
                if (matComposite != null)
                    matComposite.SetVector(PID_DrawRect, Vector4.zero);
                staticAt = -1f; // whatever is cached may be out of date by the next render
            }
            if (suspended)
            {
                Detach();
                DetachInside();
                DetachHoles();
            }
            else
            {
                Attach();
                AttachInside();
                AttachHoles();
            }
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
            Lap(PhSetup);

            // Everything else only matters where the windows are: crop to that area.
            float px = 2f * Mathf.Max(1f, RWSettings.feather) + 2f;
            Rect wr = windowRect;
            wr.xMin = Mathf.Clamp01(wr.xMin - px / main.pixelWidth);
            wr.yMin = Mathf.Clamp01(wr.yMin - px / main.pixelHeight);
            wr.xMax = Mathf.Clamp01(wr.xMax + px / main.pixelWidth);
            wr.yMax = Mathf.Clamp01(wr.yMax + px / main.pixelHeight);
            windowRect = wr;
            fullProj = proj;
            cropProj = Crop(wr) * proj;
            GeometryUtility.CalculateFrustumPlanes(cropProj * view, cropPlanes);
            MarkOccluders(proj * view);

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
            cbPasses.SetGlobalFloat(PID_Behind, 0f);
            // only the windows' area can matter: everything outside stays cleared
            Rect scissor = new Rect(Mathf.Floor(wr.xMin * rtW), Mathf.Floor(wr.yMin * rtH), 0f, 0f);
            scissor.xMax = Mathf.Ceil(wr.xMax * rtW);
            scissor.yMax = Mathf.Ceil(wr.yMax * rtH);
            bool useScissor = RWSettings.scissorPasses && RWSettings.debugMode == 0 && (scissor.width < rtW || scissor.height < rtH);
            if (useScissor) cbPasses.EnableScissorRect(scissor);
            DrawHulls(cropPlanes);
            if (useScissor) cbPasses.DisableScissorRect();

            // 1b. behind layer (seeThroughWindows): the same hulls again, keeping only
            //     what lies behind a trusted window pixel of the front layer, past the
            //     window's own part. Where the ray leaves a cabin through its far window,
            //     the composite looks here: another window drawn in the same interior
            //     render shows that part's interior, real glass shows the scene, and
            //     painted glass becomes a cut-out (or stays dark).
            behindDrawn = RWSettings.seeThroughWindows && behindRT != null;
            if (behindDrawn)
            {
                cbPasses.SetRenderTarget(behindRT);
                cbPasses.ClearRenderTarget(true, true, Color.clear, 1f);
                cbPasses.SetGlobalFloat(PID_Behind, 1f);
                cbPasses.SetGlobalTexture(PID_FrontLayer, idRT);
                cbPasses.SetGlobalVector(PID_LayerSize, new Vector4(1f / rtW, 1f / rtH, 0f, 0f));
                cbPasses.SetGlobalFloat(PID_BehindEps, 0.05f);
                if (useScissor) cbPasses.EnableScissorRect(scissor);
                DrawHulls(cropPlanes);
                if (useScissor) cbPasses.DisableScissorRect();
                cbPasses.SetGlobalFloat(PID_Behind, 0f);
            }

            // 2. interior front surfaces: which part's interior each pixel sees, and how
            //    deep. The shell answers that; props and kerbals add only draw calls.
            cbPasses.SetRenderTarget(frontRT);
            cbPasses.ClearRenderTarget(true, true, Color.clear, 1f);
            if (useScissor) cbPasses.EnableScissorRect(scissor);
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
            if (useScissor) cbPasses.DisableScissorRect();

            // composite parameters (the cut-outs use the same window decision)
            PickHoleParts();
            SetCompositeParams(matComposite, sun, true);
            if (matCover != null)
            {
                SetCompositeParams(matCover, sun, false); // the cut-outs leave things in front to the depth test
                SetCompositeParams(matReopen, sun, false);
            }
            Lap(PhCommands);

            Graphics.ExecuteCommandBuffer(cbPasses);
            Lap(PhSubmit);

            // 3. lit interior colour
            RenderInteriorColour();
            BindInterior(matComposite);
            BindInterior(matCover);
            BindInterior(matReopen);

            RenderTexture.active = prevActive;

            EnableCovers();

            lastGroupCount = groups.Count;
            return true;
        }

        // Parts whose painted windows get depth cut-outs this frame: the rendered ones,
        // nearest first up to seeThroughHoleParts, then ordered for drawing: far to near
        // after the rest of the scene, or as true holes near to far before it (a part
        // seen through another's hole has to come after that hole is re-opened).
        private void PickHoleParts()
        {
            for (int i = 0; i < holeIds.Length; i++)
                holeIds[i] = 0f;
            holeParts.Clear();
            trueHoles = false;
            if (!behindDrawn || matCover == null || ivaView || insideId > 0)
                return;
            for (int g = 0; g < groups.Count; g++)
                for (int m = 0; m < groups[g].members.Count; m++)
                {
                    PartPortal p = groups[g].members[m];
                    if (p.frameId > 0 && p.frameId < MaxIds && p.part != null)
                        holeParts.Add(p);
                }
            holeParts.Sort((a, b) => a.distance.CompareTo(b.distance));
            if (holeParts.Count > RWSettings.seeThroughHoleParts)
                holeParts.RemoveRange(RWSettings.seeThroughHoleParts, holeParts.Count - RWSettings.seeThroughHoleParts);
            trueHoles = holeParts.Count > 0 && WantTrueHoles();
            if (!trueHoles)
                holeParts.Reverse();
            for (int i = 0; i < holeParts.Count; i++)
                holeIds[holeParts[i].frameId] = 1f;
            if (holeParts.Count > 0 && holesState != (trueHoles ? 1 : 0))
            {
                holesState = trueHoles ? 1 : 0;
                RWLog.Info(trueHoles
                    ? "window cut-outs are true holes: the scene behind keeps its depth ({0} rendering, glass at {1})"
                    : "window cut-outs keep the pane's depth: sky effects don't show through them ({0} rendering)",
                    main.actualRenderingPath, holesEvent);
            }
        }

        // True holes need the depth put back before anything reads it. In deferred the
        // cut-outs are in the G-buffer pass, so the depth texture gets the real depth
        // too. In forward that texture comes from a pre-pass they are not in: only
        // what tests against the depth buffer would be right, so not unless asked for.
        private bool WantTrueHoles()
        {
            if (matReopen == null)
                return false;
            if (RWSettings.seeThroughDepth == RWSettings.HolesOff)
                return false;
            if (RWSettings.seeThroughDepth == RWSettings.HolesOn)
                return true;
            return main.actualRenderingPath == RenderingPath.DeferredShading;
        }

        // Switched on for the main camera's render only (OnPostRenderAny switches them
        // off): every other renderer keeps its order and comes first; then, far to near,
        // each part's cut-out and right after it the part's opaque hull renderers. As
        // true holes these come before every other renderer instead, near to far, each
        // part followed by the quad that re-opens its cut-out.
        private void EnableCovers()
        {
            if (holeParts.Count == 0 || matCover == null)
                return;
            if (coverMesh == null)
            {
                // a unit quad; the shader stretches it over the part's window area
                coverMesh = new Mesh { name = "RW cut-out quad" };
                coverMesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(1f, 1f, 0f) };
                coverMesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
                coverMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            }
            Vector3 cp = main.transform.position;
            matCover.SetFloat("_RW_CoverEps", 0.02f);
            matCover.SetVector("_RW_CoverCam", new Vector4(cp.x, cp.y, cp.z, main.pixelWidth));
            matReopen.SetFloat("_RW_CoverEps", 0.02f);
            matReopen.SetVector("_RW_CoverCam", new Vector4(cp.x, cp.y, cp.z, main.pixelWidth));
            reorderSeen.Clear();
            for (int i = 0; i < holeParts.Count; i++)
            {
                PartPortal p = holeParts[i];
                int order = trueHoles ? HoleOrderBase + 3 * i : CoverOrderBase + 2 * i;
                MeshRenderer cr = Quad(covers, matCover, "RealWindows Cut-out ", i);
                cr.transform.position = cp;
                cr.sortingOrder = order;
                coverBlock.SetFloat("_RW_CoverId", p.frameId);
                coverBlock.SetVector("_RW_CoverRect", PadRect(p.screenArea));
                cr.SetPropertyBlock(coverBlock);
                cr.enabled = true;
                if (trueHoles)
                {
                    MeshRenderer rr = Quad(reopens, matReopen, "RealWindows Cut-out re-open ", i);
                    rr.transform.position = cp;
                    rr.sortingOrder = order + 2;
                    rr.SetPropertyBlock(coverBlock);
                    rr.enabled = true;
                }
                for (int k = 0; k < p.exterior.Count; k++)
                {
                    Renderer r = p.exterior[k].r;
                    if (r == null || !reorderSeen.Add(r) || AnyTransparent(r))
                        continue;
                    reordered.Add(r);
                    reorderedWas.Add(r.sortingOrder);
                    r.sortingOrder = order + 1;
                }
            }
            coversActive = true;
            holeFrames++;
        }

        // Viewport rect (x0, y0, x1, y1) grown by the feather and a couple of pixels.
        private Vector4 PadRect(Rect r)
        {
            float px = 2f * Mathf.Max(1f, RWSettings.feather) + 2f;
            float dx = px / Mathf.Max(1, main.pixelWidth), dy = px / Mathf.Max(1, main.pixelHeight);
            return new Vector4(Mathf.Clamp01(r.xMin - dx), Mathf.Clamp01(r.yMin - dy), Mathf.Clamp01(r.xMax + dx), Mathf.Clamp01(r.yMax + dy));
        }

        // The i-th quad of a pool (cut-outs, re-opens), made when first needed.
        private MeshRenderer Quad(List<MeshRenderer> pool, Material mat, string name, int i)
        {
            while (pool.Count <= i)
            {
                GameObject go = new GameObject(name + pool.Count);
                go.layer = 0;
                go.AddComponent<MeshFilter>().sharedMesh = coverMesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                mr.allowOcclusionWhenDynamic = false;
                mr.enabled = false;
                pool.Add(mr);
            }
            return pool[i];
        }

        private static bool AnyTransparent(Renderer r)
        {
            Material[] ms = r.sharedMaterials;
            for (int i = 0; i < ms.Length; i++)
                if (ms[i] != null && PartPortal.IsTransparent(ms[i]))
                    return true;
            return false;
        }

        private void DisableCovers()
        {
            if (!coversActive)
                return;
            for (int i = 0; i < covers.Count; i++)
                if (covers[i] != null)
                    covers[i].enabled = false;
            for (int i = 0; i < reopens.Count; i++)
                if (reopens[i] != null)
                    reopens[i].enabled = false;
            for (int i = 0; i < reordered.Count; i++)
                if (reordered[i] != null)
                    reordered[i].sortingOrder = reorderedWas[i];
            reordered.Clear();
            reorderedWas.Clear();
            coversActive = false;
        }

        private void OnPostRenderAny(Camera cam)
        {
            if (cam == main)
                DisableCovers();
        }

        private void SetCompositeParams(Material mat, Light sun, bool sceneDepth)
        {
            mat.SetTexture("_RW_IdTex", idRT);
            mat.SetTexture("_RW_SurfaceTex", surfaceRT);
            Color sunColour = sun != null ? sun.color * sun.intensity : Color.white;
            mat.SetColor("_RW_SunColor", sunColour * RWSettings.glassSpecular);
            mat.SetFloat("_RW_Reflection", RWSettings.glassReflection);
            mat.SetFloat("_RW_Dirt", RWSettings.glassDirt);
            mat.SetTexture("_RW_FrontTex", frontRT);
            mat.SetTexture("_RW_BehindTex", behindRT != null ? behindRT : idRT);
            mat.SetFloat("_RW_SeeThrough", behindDrawn ? 1f : 0f);
            mat.SetFloat("_RW_HullEps", RWSettings.hullEpsilon);
            mat.SetFloat("_RW_SceneEps", RWSettings.sceneDepthEpsilon);
            mat.SetFloat("_RW_UseSceneDepth", sceneDepth && RWSettings.useSceneDepth ? 1f : 0f);
            mat.SetFloat("_RW_Transmission", RWSettings.transmission);
            mat.SetColor("_RW_Tint", RWSettings.glassTint);
            mat.SetFloat("_RW_Feather", RWSettings.feather);
            mat.SetFloat("_RW_Debug", RWSettings.debugMode);
            mat.SetFloat("_RW_FlipY", RWSettings.flipY);
            mat.SetFloat("_RW_InsideId", insideId);
            // windowRect is already padded by the feather; the debug views and the
            // inside view need the whole screen
            mat.SetVector(PID_DrawRect, RWSettings.debugMode > 0 || insideId > 0
                ? new Vector4(0f, 0f, 1f, 1f)
                : new Vector4(windowRect.xMin, windowRect.yMin, windowRect.xMax, windowRect.yMax));
            for (int i = 0; i < MaxIds; i++)
                idGroup[i] = -1f;
            for (int g = 0; g < groups.Count; g++)
                for (int m = 0; m < groups[g].members.Count; m++)
                {
                    int fid = groups[g].members[m].frameId;
                    if (fid > 0 && fid < MaxIds)
                        idGroup[fid] = g;
                }
            mat.SetFloatArray("_RW_IdGroup", idGroup);
            mat.SetFloat("_RW_SeeNeighbours", RWSettings.seeIntoNeighbours ? 1f : 0f);
            mat.SetFloatArray("_RW_HoleIds", holeIds);
            mat.SetFloat(PID_LateHoles, trueHoles ? 1f : 0f);
            // the colour render's depth (cached shell/props + kerbals), so props and kerbals
            // in front of a far window keep the interior; only with the split colour pass
            bool intDepth = RWSettings.interiorRefresh >= 0f && staticColorRT != null && staticDepthRT != null && dynDepthRT != null;
            mat.SetFloat("_RW_HasInteriorDepth", intDepth ? 1f : 0f);
            // _RW_InteriorTex / _RW_InteriorDepth: BindInterior, once the colour pass has run
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
        // The IVA's own window glass likewise: some of it writes depth (stock
        // KSP/Alpha/Translucent, the Mk1 cockpit's canopy), so the window decision took
        // it for a wall in front of the far window, and from outside it only doubled
        // the composite's glass (and tinted the far view dark).
        private void HideMasks(PartPortal p)
        {
            if (RWSettings.hideDepthMasks)
                HideAll(p.depthMaskRenderers);
            if (RWSettings.hideInteriorGlass)
                HideAll(p.glassRenderers);
        }

        private void HideAll(List<Renderer> list)
        {
            for (int k = 0; k < list.Count; k++)
            {
                Renderer r = list[k];
                if (r != null && !r.forceRenderingOff)
                {
                    r.forceRenderingOff = true;
                    masksHidden.Add(r);
                }
            }
        }

        // Stock keeps interiors hidden outside IVA (renderers disabled): enable them
        // for this render only.
        // Only what the pass draws: enabling a renderer is expensive in Unity, and this
        // ran twice per render for every renderer of every stock-hidden interior (each
        // uncrewed cabin), the per-frame crew pass included, which draws no cabins.
        // Crew pass: the crew, plus the whole interior only if it is moving (drawn
        // whole there). Other passes: everything but props the detail level hides.
        private void FlipOn(PartPortal p, ColourPass pass, bool props)
        {
            if (!p.StockHidden)
                return;
            List<Renderer> list = pass == ColourPass.Kerbals && !p.Moving ? p.kerbalRenderers
                                : !props ? p.nonPropRenderers
                                : RWSettings.drawInstruments ? p.interiorRenderers : p.nonInstrumentRenderers;
            for (int k = 0; k < list.Count; k++)
            {
                Renderer r = list[k];
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
            if (!RWSettings.showInteriorWhenInside || ivaView)
                return null;
            for (int i = 0; i < interiors.Count; i++)
                if (CameraIn(interiors[i]))
                    return interiors[i];
            return null;
        }

        private bool CameraIn(PartPortal p)
        {
            if (p.part == null || !p.InteriorUsable || !p.haveShellBounds || p.distance > 15f)
                return false;
            RigidMap m = p.LiveMap();
            Vector3 local = p.model.transform.InverseTransformPoint(m.Apply(main.transform.position));
            Bounds b = p.shellLocalBounds;
            b.Expand(-0.1f);
            return b.Contains(local);
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
                // IVA: the IVA camera's own part (drawn by it) isn't rendered
                if (ivaView && (p.part == ivaPart || CameraIn(p)))
                {
                    lastCulled++;
                    continue;
                }
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
                p.screenArea = area;
                p.UpdateDetail(p == insidePart ? float.MaxValue
                    : Mathf.Max(area.width * main.pixelWidth, area.height * main.pixelHeight));
                p.map = p.LiveMap();
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
            shadowsGame = shadows;
            if (!RWSettings.interiorShadows)
                QualitySettings.shadows = ShadowQuality.Disable;

            if (ivaSun == null && RWSettings.aimIvaSunPerGroup)
                ivaSun = UnityEngine.Object.FindObjectOfType<IVASun>();
            Quaternion sunSaved = ivaSun != null ? ivaSun.transform.rotation : Quaternion.identity;

            byPart.Clear();
            for (int i = 0; i < allInteriors.Count; i++)
                if (allInteriors[i].part != null)
                    byPart[allInteriors[i].part] = allInteriors[i];

            // motion signatures: every frame for interiors that moved lately, every
            // 4th frame (staggered) for the rest; a ring starting to spin is noticed
            // at most 3 frames late
            float now = Time.unscaledTime;
            int frame = Time.frameCount;
            for (int i = 0; i < allInteriors.Count; i++)
                if (allInteriors[i].Moving || ((frame + i) & 3) == 0)
                    allInteriors[i].UpdateMotion(now);
            Lap(PhMotion);

            shadowDistanceGame = QualitySettings.shadowDistance;
            bool split = RWSettings.interiorRefresh >= 0f && staticColorRT != null && staticDepthRT != null && dynDepthRT != null;
            if (split)
            {
                if (StaticStale())
                {
                    // a margin around the windows' area, so that a window creeping a few
                    // pixels outward (a wobbling station) doesn't redraw the cache again
                    float mg = Mathf.Max(0f, RWSettings.interiorCacheMargin);
                    Rect sr = Rect.MinMaxRect(Mathf.Clamp01(windowRect.xMin - mg), Mathf.Clamp01(windowRect.yMin - mg),
                                              Mathf.Clamp01(windowRect.xMax + mg), Mathf.Clamp01(windowRect.yMax + mg));
                    intCam.rect = sr;
                    intCam.projectionMatrix = Crop(sr) * fullProj;
                    RenderGroups(ColourPass.Static);
                    intCam.rect = windowRect;
                    intCam.projectionMatrix = cropProj;
                    RememberStaticView(sr);
                    staticRedraws++;
                }
                else
                {
                    staticReuses++;
                }
                Lap(PhCabin);
                if (NeedKerbalPass())
                {
                    Graphics.CopyTexture(staticColorRT, colorRT);
                    Graphics.CopyTexture(staticDepthRT, dynDepthRT);
                    if (!RWSettings.kerbalShadows)
                        QualitySettings.shadows = ShadowQuality.Disable;
                    RenderGroups(ColourPass.Kerbals);
                    interiorTex = colorRT;
                    interiorDepthTex = dynDepthRT;
                }
                else
                {
                    // nothing moves in there: the cache is the picture (no copies, no render)
                    interiorTex = staticColorRT;
                    interiorDepthTex = staticDepthRT;
                    kerbalSkips++;
                }
                Lap(PhCrew);
            }
            else
            {
                RenderGroups(ColourPass.All);
                interiorTex = colorRT;
                interiorDepthTex = dynDepthRT;
                Lap(PhCabin);
            }
            intCam.targetTexture = null;

            RestoreShadows();
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
                    // both layers: cabin walls can be on the kerbals layer too (FreeIva and
                    // some IVAs); the crew is told apart by renderer (ApplyDetail), not layer
                    intCam.cullingMask = (1 << LayerKerbals) | (1 << LayerInternal);
                    break;
                default:
                    intCam.SetTargetBuffers(colorRT.colorBuffer, dynDepthRT.depthBuffer);
                    intCam.cullingMask = 1 << LayerKerbals;
                    break;
            }

            for (int g = 0; g < groups.Count; g++)
            {
                PortalGroup grp = groups[g];
                BuildContext(g);
                // only this group's interiors and their neighbours (SetForceOff skips unchanged
                // ones). Parked interiors (other vessels, hidden from the stock cameras) are
                // switched on for their own render and parked again afterwards.
                // A moving interior stays out of the cache (Static) and is drawn whole in the
                // Kerbals pass; there, everything else shows only its crew.
                bool movingHere = false;
                for (int i = 0; i < allInteriors.Count; i++)
                {
                    PartPortal q = allInteriors[i];
                    bool show = (q.frameId > 0 && q.group == g) || context.Contains(q);
                    if (show && q.Moving)
                    {
                        if (pass == ColourPass.Static) show = false;
                        else movingHere = true;
                    }
                    q.SetForceOff(!show);
                }
                staticHidden.Clear();
                if (pass == ColourPass.Kerbals)
                {
                    intCam.cullingMask = movingHere ? (1 << LayerKerbals) | (1 << LayerInternal) : 1 << LayerKerbals;
                    if (movingHere)
                        for (int i = 0; i < allInteriors.Count; i++)
                        {
                            PartPortal q = allInteriors[i];
                            if (q.Moving || !((q.frameId > 0 && q.group == g) || context.Contains(q)))
                                continue;
                            for (int k = 0; k < q.nonKerbalRenderers.Count; k++)
                            {
                                Renderer r = q.nonKerbalRenderers[k];
                                if (r != null && !r.forceRenderingOff)
                                {
                                    r.forceRenderingOff = true;
                                    staticHidden.Add(r);
                                }
                            }
                        }
                }
                // detail by the windows' size on screen; neighbours (seen through a
                // hatch) follow the most detailed member of the group
                bool grpProps = false, grpCrew = false;
                for (int m = 0; m < grp.members.Count; m++)
                {
                    grpProps |= grp.members[m].propsOn;
                    grpCrew |= grp.members[m].crewOn;
                }
                flippedEnabled.Clear();
                masksHidden.Clear();
                for (int m = 0; m < grp.members.Count; m++)
                {
                    FlipOn(grp.members[m], pass, grp.members[m].propsOn);
                    HideMasks(grp.members[m]);
                }
                foreach (PartPortal q in context)
                {
                    FlipOn(q, pass, grpProps);
                    HideMasks(q);
                }
                for (int m = 0; m < grp.members.Count; m++)
                    ApplyDetail(grp.members[m], grp.members[m].propsOn, grp.members[m].crewOn, pass);
                foreach (PartPortal q in context)
                    ApplyDetail(q, grpProps, grpCrew, pass);

                intCam.transform.SetPositionAndRotation(grp.map.Apply(camT.position), grp.map.Apply(camT.rotation));
                // the first render of the frame clears; the kerbals pass draws over the copied cache
                intCam.clearFlags = g == 0 && pass != ColourPass.Kerbals ? CameraClearFlags.SolidColor : CameraClearFlags.Nothing;
                if (ivaSun != null && sunT != null)
                    ivaSun.transform.rotation = grp.map.Apply(sunT.rotation);
                if (QualitySettings.shadows != ShadowQuality.Disable)
                    FitShadows(grp, camT);

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

                if (pass == ColourPass.Static && diagAt >= 0f && Time.unscaledTime >= diagAt)
                    LogDrawState(g, grp);

                intCam.Render();

                if (api)
                    RealWindowsApi.Fire(false, apiParts);

                for (int k = 0; k < flippedEnabled.Count; k++)
                    if (flippedEnabled[k] != null)
                        flippedEnabled[k].enabled = false;
                for (int k = 0; k < masksHidden.Count; k++)
                    if (masksHidden[k] != null)
                        masksHidden[k].forceRenderingOff = false;
                for (int k = 0; k < staticHidden.Count; k++)
                    if (staticHidden[k] != null)
                        staticHidden[k].forceRenderingOff = false;
                staticHidden.Clear();
            }
        }

        // The game's shadow distance (hundreds of metres) put every interior within it
        // into the interior camera's shadow maps, seen or not, and spread the cascades
        // over all that space. Only the interiors drawn in this render can cast a
        // shadow into it: the distance is cut to reach just past the farthest of them
        // (from the camera's place in internal space). Restored after the colour pass.
        private float shadowDistanceGame = -1f;
        private ShadowQuality? shadowsGame;

        // also from OnPreCullAny's finally: a render that threw must not leave the
        // game's own shadows changed
        private void RestoreShadows()
        {
            if (shadowsGame.HasValue)
                QualitySettings.shadows = shadowsGame.Value;
            shadowsGame = null;
            if (shadowDistanceGame > 0f)
                QualitySettings.shadowDistance = shadowDistanceGame;
            shadowDistanceGame = -1f;
        }

        private void FitShadows(PortalGroup grp, Transform camT)
        {
            if (!RWSettings.fitInteriorShadows || shadowDistanceGame <= 0f)
                return;
            Vector3 cam = grp.map.Apply(camT.position);
            float far = 0f;
            for (int m = 0; m < grp.members.Count; m++)
                far = Mathf.Max(far, ReachOf(grp.members[m], cam));
            foreach (PartPortal q in context)
                far = Mathf.Max(far, ReachOf(q, cam));
            if (far <= 0f)
                return;
            QualitySettings.shadowDistance = Mathf.Min(shadowDistanceGame, Mathf.Max(5f, far * 1.05f + 1f));
        }

        private static float ReachOf(PartPortal q, Vector3 cam)
        {
            if (q.model == null || !q.haveShellBounds)
                return 0f;
            Vector3 c = q.model.transform.TransformPoint(q.shellCenterLocal);
            return (c - cam).magnitude + q.shellRadius;
        }

        // Props of interiors whose windows are small on screen stay out of every render.
        // Their crew is drawn into the cached image instead of every frame: in the
        // Static pass (which then also takes the kerbals layer) while the crew of the
        // detailed interiors is hidden there, and hidden in the per-frame pass. Moving
        // interiors aren't in the cache, so their crew stays live.
        // Verbose diagnostics: once, a while after the scene starts (and after each
        // switch on), the state of every interior this cabin render is about to draw.
        public float diagAt = -1f;

        private void LogDrawState(int g, PortalGroup grp)
        {
            if (g == groups.Count - 1)
                diagAt = -1f;
            RWLog.Info("draw state, group {0}: camera path {1} (actual {2}), mask 0x{3:X}, rect {4}, near {5:F3} far {6:F0}, shadows {7} dist {8:F1}",
                g, intCam.renderingPath, intCam.actualRenderingPath, intCam.cullingMask, intCam.rect,
                intCam.nearClipPlane, intCam.farClipPlane, QualitySettings.shadows, QualitySettings.shadowDistance);
            for (int m = 0; m < grp.members.Count; m++)
                RWLog.Info("draw state member {0}", grp.members[m].DrawState());
            foreach (PartPortal q in context)
                RWLog.Info("draw state neighbour {0}", q.DrawState());
        }

        private void ApplyDetail(PartPortal q, bool props, bool crew, ColourPass pass)
        {
            // The crew pass draws the kerbals layer, which also holds cabin walls and props
            // in some IVAs (FreeIva moves them there): those belong to the cached cabin
            // image and are hidden here, unless the interior is moving (drawn whole here).
            bool whole = pass != ColourPass.Kerbals || q.Moving;
            if (!whole)
                HideAll(q.kerbalLayerOthers);
            else if (!props)
                HideAll(q.propRenderers);
            else if (!RWSettings.drawInstruments)
                HideAll(q.instrumentRenderers); // RPM/MAS switch some on by themselves
            // crew of small windows is drawn into the cache (Static), the rest per frame
            if (pass == ColourPass.Static)
            {
                if (crew)
                    HideAll(q.kerbalRenderers);
            }
            else if (pass == ColourPass.Kerbals && !crew && !q.Moving)
            {
                HideAll(q.kerbalRenderers);
            }
        }

        // Neighbours of group g's parts on the same vessel (into `context`): not rendered
        // for their own windows, but seen through open hatches from a rendered part.
        private void BuildContext(int g)
        {
            context.Clear();
            if (!RWSettings.renderNeighbours)
                return;
            PortalGroup grp = groups[g];
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

        // The per-frame kerbals pass is only needed when something drawn moves: crew
        // in any interior drawn this frame (neighbours too), a moving interior (spinning
        // ring), or another mod's stand-ins (API subscribers). Otherwise the cached
        // shell/props image is the whole picture and is used as it is. Crew of
        // interiors with small windows is in that image (ApplyDetail) and doesn't count.
        private bool NeedKerbalPass()
        {
            if (RealWindowsApi.HasSubscribers)
                return true;
            for (int g = 0; g < groups.Count; g++)
            {
                List<PartPortal> ms = groups[g].members;
                bool grpCrew = false;
                for (int m = 0; m < ms.Count; m++)
                {
                    grpCrew |= ms[m].crewOn;
                    if ((ms[m].HasKerbals && ms[m].crewOn) || ms[m].Moving)
                        return true;
                }
                BuildContext(g);
                foreach (PartPortal q in context)
                    if ((q.HasKerbals && grpCrew) || q.Moving)
                        return true;
            }
            return false;
        }

        // The interior image and depth the composite and cut-outs read this frame.
        private void BindInterior(Material mat)
        {
            if (mat == null)
                return;
            mat.SetTexture("_RW_InteriorTex", interiorTex != null ? interiorTex : colorRT);
            if (interiorDepthTex != null)
                mat.SetTexture("_RW_InteriorDepth", interiorDepthTex);
        }

        // The cached layer is redrawn when anything that shapes its picture
        // changed: the view of each group's interiors (camera pose relative to the
        // group's first part), the projection, which interiors are drawn and which of
        // them move (a moving interior leaves the cache, and rejoins it 2 s after
        // stopping), a crop area that now reaches past the cached one; and every
        // `interiorRefresh` seconds (lighting, animated props) or when another mod asks.
        // Why the cache was redrawn, counted for the status line.
        public enum Redraw { Start, Request, Refresh, Set, Area, Projection, View, Count }
        public readonly int[] redrawWhy = new int[(int)Redraw.Count];

        private bool StaticStale()
        {
            Redraw why;
            bool stale = StaticStaleWhy(out why);
            if (stale)
                redrawWhy[(int)why]++;
            return stale;
        }

        private bool StaticStaleWhy(out Redraw why)
        {
            why = Redraw.Start;
            if (staticAt < 0f)
                return true;
            why = Redraw.Request;
            if (RealWindowsApi.TakeRedrawRequest())
                return true;
            why = Redraw.Refresh;
            if (RWSettings.interiorRefresh > 0f && Time.unscaledTime - staticAt >= RWSettings.interiorRefresh)
                return true;
            why = Redraw.Set;
            if (groups.Count != staticPos.Count || ViewHash() != staticHash)
                return true;
            why = Redraw.Area;
            float px = 0.5f / Mathf.Max(1, rtW), py = 0.5f / Mathf.Max(1, rtH);
            if (windowRect.xMin < staticRect.xMin - px || windowRect.yMin < staticRect.yMin - py
                || windowRect.xMax > staticRect.xMax + px || windowRect.yMax > staticRect.yMax + py)
                return true;
            why = Redraw.Projection;
            Matrix4x4 proj = main.nonJitteredProjectionMatrix;
            for (int k = 0; k < 16; k++)
                if (Mathf.Abs(proj[k] - staticProj[k]) > 1e-5f)
                    return true;
            why = Redraw.View;
            float maxPx = RWSettings.interiorRedrawPixels;
            float maxMove = RWSettings.interiorRedrawMove, maxTurn = RWSettings.interiorRedrawTurn;
            for (int g = 0; g < groups.Count; g++)
            {
                Vector3 pos;
                Quaternion rot;
                CameraInGroup(g, out pos, out rot);
                if (maxPx > 0f)
                {
                    if (ViewShiftPixels(g, pos, rot, proj) > maxPx)
                        return true;
                    continue;
                }
                if ((pos - staticPos[g]).sqrMagnitude > maxMove * maxMove)
                    return true;
                if (Quaternion.Angle(rot, staticRot[g]) > maxTurn)
                    return true;
            }
            return false;
        }

        // How far (in screen pixels) the group's interiors would have moved on screen
        // since the cache was drawn: sample points of each member (its part's origin and
        // its shell radius around it, on the axes and towards the camera) seen from the
        // cached and the current camera pose relative to the group's first part. A fixed
        // distance threshold (0.5 mm, 0.01 deg) was far below a pixel from tens of
        // metres away, so a station's slight wobble redrew the cache almost every frame.
        private float ViewShiftPixels(int g, Vector3 pos, Quaternion rot, Matrix4x4 proj)
        {
            List<PartPortal> ms = groups[g].members;
            Part first = ms.Count > 0 ? ms[0].part : null;
            if (first == null)
                return float.MaxValue;
            Matrix4x4 flip = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            Matrix4x4 vpNow = proj * flip * Matrix4x4.TRS(pos, rot, Vector3.one).inverse;
            Matrix4x4 vpOld = proj * flip * Matrix4x4.TRS(staticPos[g], staticRot[g], Vector3.one).inverse;
            float hw = 0.5f * Mathf.Max(1, main.pixelWidth), hh = 0.5f * Mathf.Max(1, main.pixelHeight);
            Transform ft = first.transform;
            float worst = 0f;
            for (int m = 0; m < ms.Count; m++)
            {
                if (ms[m].part == null)
                    continue;
                Vector3 c = ft.InverseTransformPoint(ms[m].part.transform.position);
                float r = Mathf.Max(0.5f, ms[m].shellRadius);
                Vector3 toCam = pos - c;
                Vector3 near = toCam.sqrMagnitude > r * r ? c + toCam.normalized * r : c;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 p = k == 0 ? c : k == 1 ? near
                        : c + r * (k == 2 ? Vector3.right : k == 3 ? Vector3.left : k == 4 ? Vector3.up
                                 : k == 5 ? Vector3.down : k == 6 ? Vector3.forward : Vector3.back);
                    Vector4 a = vpNow * new Vector4(p.x, p.y, p.z, 1f);
                    Vector4 b = vpOld * new Vector4(p.x, p.y, p.z, 1f);
                    bool inA = a.w > 1e-3f, inB = b.w > 1e-3f;
                    if (!inA && !inB)
                        continue;
                    if (inA != inB)
                        return float.MaxValue;
                    float dx = (a.x / a.w - b.x / b.w) * hw, dy = (a.y / a.w - b.y / b.w) * hh;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d > worst)
                        worst = d;
                }
            }
            return worst;
        }

        // The camera's pose relative to the group's first part: what the cached
        // picture of the group's (rigid) interiors depends on. Not its pose in KSP's
        // internal space: that space follows the active vessel, and FreeIva re-places
        // every interior through it each Update. On EVA the active vessel is the
        // kerbal the camera follows, so the camera's internal-space pose barely
        // changed while the interiors moved and turned around it, and the cache kept
        // showing the cabin where it had been.
        private void CameraInGroup(int g, out Vector3 pos, out Quaternion rot)
        {
            Transform camT = main.transform;
            Part p = groups[g].members.Count > 0 ? groups[g].members[0].part : null;
            if (p == null)
            {
                pos = groups[g].map.Apply(camT.position);
                rot = groups[g].map.Apply(camT.rotation);
                return;
            }
            Transform pt = p.transform;
            pos = pt.InverseTransformPoint(camT.position);
            rot = Quaternion.Inverse(pt.rotation) * camT.rotation;
        }

        private void RememberStaticView(Rect drawn)
        {
            staticAt = Time.unscaledTime;
            staticRect = drawn;
            staticProj = main.nonJitteredProjectionMatrix;
            staticHash = ViewHash();
            staticPos.Clear();
            staticRot.Clear();
            for (int g = 0; g < groups.Count; g++)
            {
                Vector3 pos;
                Quaternion rot;
                CameraInGroup(g, out pos, out rot);
                staticPos.Add(pos);
                staticRot.Add(rot);
            }
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
                        h = (h * 31 + (ms[m].model != null ? ms[m].model.GetInstanceID() : 0)) * 4
                            + (ms[m].propsOn ? 1 : 0) + (ms[m].crewOn ? 2 : 0);   // detail changes what the cache holds
                }
                for (int i = 0; i < allInteriors.Count; i++)
                    h = h * 31 + (allInteriors[i].model != null ? allInteriors[i].model.GetInstanceID() : 0) * 2
                        + (allInteriors[i].Moving ? 1 : 0); // moving ones leave the cache, stopped ones rejoin it
                return h;
            }
        }

        // ------------------------------------------------------------------
        // composite attach / RTs
        // ------------------------------------------------------------------

        private void OnCameraChange(CameraManager.CameraMode mode)
        {
            cameraSwitched = true;
        }

        // The window test compares each hull pixel with the scene depth
        // (_CameraDepthTexture). Deferred rendering always makes that texture; forward
        // only while a camera flag asks for it, so a mod clearing the flag left every
        // window failing the test in forward, while the inside view (no scene depth)
        // still worked. Likewise a mod removing our command buffer left the composite
        // off for good, since it was only added again after a detach.
        private void CheckCamera()
        {
            bool switched = cameraSwitched;
            cameraSwitched = false;
            nextCameraCheck = Time.unscaledTime + 2f;
            if (main == null)
                return;
            bool depthLost = depthModeTouched && (main.depthTextureMode & DepthTextureMode.Depth) == 0;
            bool bufferLost = (compositeAttached && !HasBuffer(compositeCamera, compositeEvent, cbComposite))
                              || (insideAttached && !HasBuffer(insideCamera, insideEvent, cbInside))
                              || (holesAttached && !HasBuffer(holesCamera, holesEvent, cbHoles));
            if (switched || depthLost || bufferLost)
                RWLog.Verbose("camera {0}: {1} rendering, depth texture {2}, HDR {3}, composite {4}",
                    CameraManager.Instance != null ? CameraManager.Instance.currentCameraMode.ToString() : "?",
                    main.actualRenderingPath, main.depthTextureMode, main.allowHDR,
                    !compositeAttached ? "detached" : bufferLost ? "removed by another mod" : "attached");
            if (depthLost && !warnedDepth)
            {
                warnedDepth = true;
                RWLog.Warn("another mod cleared the flight camera's depth texture flag ({0} rendering); set again", main.actualRenderingPath);
            }
            if (bufferLost && !warnedBuffer)
            {
                warnedBuffer = true;
                RWLog.Warn("another mod removed Real Windows' command buffer from the flight camera; added again");
            }
            if (bufferLost)
            {
                // attached again below, this frame (last in line: only when it was gone)
                Detach();
                DetachInside();
                DetachHoles();
            }
            if (switched || bufferLost)
                staticAt = -1f;
        }

        private static bool HasBuffer(Camera cam, CameraEvent evt, CommandBuffer cb)
        {
            if (cam == null)
                return false;
            // Unity hands back new wrapper objects, never the one added: compare names
            CommandBuffer[] bufs = cam.GetCommandBuffers(evt);
            for (int i = 0; i < bufs.Length; i++)
                if (bufs[i] != null && bufs[i].name == cb.name)
                    return true;
            return false;
        }

        // Asked for every frame: only a cheap flag check once set.
        private void EnsureDepthMode()
        {
            if (!RWSettings.useSceneDepth || main == null)
                return;
            if (!depthModeTouched)
            {
                originalDepthMode = main.depthTextureMode;
                depthModeTouched = true;
            }
            if ((main.depthTextureMode & DepthTextureMode.Depth) == 0)
                main.depthTextureMode |= DepthTextureMode.Depth;
        }

        private void Attach()
        {
            EnsureDepthMode();
            if (compositeAttached && compositeCamera == main)
                return;
            Detach();
            if (main == null)
                return;
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

        // Always attached while switched on, like the composite: it draws nothing unless
        // this frame has true holes (or the scene depth debug view is up).
        private void AttachHoles()
        {
            if (holesAttached && holesCamera == main)
                return;
            DetachHoles();
            if (main == null || matReopen == null)
                return;
            main.AddCommandBuffer(holesEvent, cbHoles);
            holesCamera = main;
            holesAttached = true;
            RWLog.Verbose("cut-out glass attached to {0} at {1}", main.name, holesEvent);
        }

        private void DetachHoles()
        {
            if (!holesAttached)
                return;
            if (holesCamera != null)
                holesCamera.RemoveCommandBuffer(holesEvent, cbHoles);
            holesAttached = false;
            holesCamera = null;
        }

        private void RestoreDepthMode()
        {
            // take back only our flag: whatever other mods set since stays
            if (depthModeTouched && main != null && (originalDepthMode & DepthTextureMode.Depth) == 0)
                main.depthTextureMode &= ~DepthTextureMode.Depth;
            depthModeTouched = false;
        }

        // Which occluders can touch a window pixel: their hull's screen rectangle
        // overlaps some rendered part's window area. The crop only knows the union of
        // all window areas, which on a station with windows all over the screen is
        // most of it, so nearly every part was drawn in both hull passes. A part that
        // overlaps no window can neither cover one nor sit behind one.
        private readonly List<Vector4> winRects = new List<Vector4>();

        private void MarkOccluders(Matrix4x4 vp)
        {
            bool all = !RWSettings.cullOccludersPerWindow || insideId > 0 || RWSettings.debugMode > 0;
            winRects.Clear();
            if (!all)
                for (int g = 0; g < groups.Count; g++)
                    for (int m = 0; m < groups[g].members.Count; m++)
                        winRects.Add(PadRect(groups[g].members[m].screenArea));
            lastOccludersDrawn = 0;
            for (int i = 0; i < occluders.Count; i++)
            {
                PartPortal p = occluders[i];
                p.hullInView = all || p.frameId > 0 || HullOverlapsWindows(p, vp);
                if (p.hullInView)
                    lastOccludersDrawn++;
            }
        }

        public int lastOccludersDrawn;

        private bool HullOverlapsWindows(PartPortal p, Matrix4x4 vp)
        {
            Bounds b = new Bounds();
            bool any = false;
            for (int k = 0; k < p.exterior.Count; k++)
            {
                Renderer r = p.exterior[k].r;
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                    continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            if (!any)
                return false;
            Vector3 c = b.center, e = b.extents;
            float x0 = 1f, y0 = 1f, x1 = 0f, y1 = 0f;
            for (int i = 0; i < 8; i++)
            {
                Vector4 q = vp * new Vector4(c.x + ((i & 1) == 0 ? -e.x : e.x), c.y + ((i & 2) == 0 ? -e.y : e.y),
                                             c.z + ((i & 4) == 0 ? -e.z : e.z), 1f);
                if (q.w <= 1e-4f)
                    return true; // reaches behind the camera: keep it
                float vx = q.x / q.w * 0.5f + 0.5f, vy = q.y / q.w * 0.5f + 0.5f;
                x0 = Mathf.Min(x0, vx); y0 = Mathf.Min(y0, vy);
                x1 = Mathf.Max(x1, vx); y1 = Mathf.Max(y1, vy);
            }
            for (int w = 0; w < winRects.Count; w++)
            {
                Vector4 r = winRects[w];
                if (x1 >= r.x && x0 <= r.z && y1 >= r.y && y0 <= r.w)
                    return true;
            }
            return false;
        }

        // Every occluder's hull sub-meshes into the bound hull targets: part id, eye
        // depth, window state (+ glass surface on the main pass).
        private void DrawHulls(Plane[] cropPlanes)
        {
            for (int i = 0; i < occluders.Count; i++)
            {
                PartPortal p = occluders[i];
                if (p.part == null || !p.hullInView)
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
                    cbPasses.SetGlobalFloat(PID_MaskMode, HullMode(p, d, confident));
                    Mesh baked;
                    Matrix4x4 bakedMatrix;
                    if (p.isKerbal && BakedKerbal(r, out baked, out bakedMatrix))
                        cbPasses.DrawMesh(baked, bakedMatrix, matHull, d.sub, 0);
                    else
                        cbPasses.DrawRenderer(r, matHull, d.sub, 0);
                }
            }
        }

        // An EVA kerbal's skinned meshes, baked in this frame's pose (once per frame,
        // shared by the hull and behind passes). Drawn through DrawRenderer they come
        // from Unity's own skinning, which for a renderer drawn from a command buffer
        // before the camera has culled it may be last frame's pose or none: a walking
        // kerbal then left a gap in the hull pass and the window behind showed
        // through. Only for unscaled transforms (BakeMesh's scale handling differs
        // between Unity versions); anything else keeps DrawRenderer.
        private readonly Dictionary<Renderer, Mesh> bakedMeshes = new Dictionary<Renderer, Mesh>();
        private readonly Dictionary<Renderer, int> bakedFrames = new Dictionary<Renderer, int>();
        private readonly List<Renderer> bakedDead = new List<Renderer>();
        private int bakedPruneFrame;

        private bool BakedKerbal(Renderer r, out Mesh mesh, out Matrix4x4 matrix)
        {
            mesh = null;
            matrix = Matrix4x4.identity;
            SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
            if (smr == null || !RWSettings.bakeKerbals || smr.sharedMesh == null)
                return false;
            Transform t = smr.transform;
            Vector3 s = t.lossyScale;
            if (Mathf.Abs(s.x - 1f) > 1e-3f || Mathf.Abs(s.y - 1f) > 1e-3f || Mathf.Abs(s.z - 1f) > 1e-3f)
                return false;
            int frame = Time.frameCount;
            if (!bakedMeshes.TryGetValue(r, out mesh) || mesh == null)
            {
                mesh = new Mesh { name = "RW baked kerbal" };
                mesh.MarkDynamic();
                bakedMeshes[r] = mesh;
                bakedFrames[r] = -1;
            }
            if (bakedFrames[r] != frame)
            {
                smr.BakeMesh(mesh);
                bakedFrames[r] = frame;
            }
            matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            if (frame - bakedPruneFrame > 600)
                PruneBaked(frame);
            return mesh.vertexCount > 0;
        }

        // meshes of kerbals that are gone or haven't been drawn for a while
        private void PruneBaked(int frame)
        {
            bakedPruneFrame = frame;
            bakedDead.Clear();
            foreach (KeyValuePair<Renderer, int> kv in bakedFrames)
                if (kv.Key == null || frame - kv.Value > 600)
                    bakedDead.Add(kv.Key);
            for (int i = 0; i < bakedDead.Count; i++)
            {
                Mesh m;
                if (bakedMeshes.TryGetValue(bakedDead[i], out m) && m != null)
                    UnityEngine.Object.Destroy(m);
                bakedMeshes.Remove(bakedDead[i]);
                bakedFrames.Remove(bakedDead[i]);
            }
        }

        private void ReleaseBaked()
        {
            foreach (Mesh m in bakedMeshes.Values)
                if (m != null)
                    UnityEngine.Object.Destroy(m);
            bakedMeshes.Clear();
            bakedFrames.Clear();
        }

        // 0 probe per pixel, 1 baked mask, 2 the author's glass (opaque or unknown),
        // 3 solid, 4 the author's transparent glass (the scene shows through it).
        private float HullMode(PartPortal p, DrawItem d, bool confident)
        {
            if (d.solid)
                return 3f;
            if (d.mask != null && d.mask.named && d.mask.namedRejected)
                return 3f; // named like glass, but no opening behind it
            if (p.frameId > 0 && d.mask != null && d.mask.named && d.mask.tex != null)
            {
                cbPasses.SetGlobalTexture(PID_MaskTex, d.mask.tex);
                return 1f; // window-named mesh, trimmed to its glass
            }
            if (p.frameId > 0 && d.glass != null && d.glass.channel == 6)
                return d.glass.named ? 2f : 4f;
            if (p.frameId > 0 && d.mask != null && d.mask.tex != null)
            {
                cbPasses.SetGlobalTexture(PID_MaskTex, d.mask.tex);
                return 1f;
            }
            if (p.frameId > 0 && (d.mask == null || (d.mask.done && d.mask.tex == null)) && confident)
                return 3f; // nothing confident here, and the part's windows are known elsewhere: no per-pixel probe
            return 0f;
        }

        private void EnsureRTs(int w, int h, bool hdr)
        {
            // the interior image (colour and depth) can be smaller than the screen
            // (interiorScale); window outlines come from the full-size id passes
            float scale = Mathf.Clamp(RWSettings.interiorScale, 0.1f, 1f);
            int iw = Mathf.Max(16, Mathf.RoundToInt(w * scale)), ih = Mathf.Max(16, Mathf.RoundToInt(h * scale));
            if (idRT != null && rtW == w && rtH == h && rtHDR == hdr && idRT.IsCreated() && colorRT != null && colorRT.width == iw && colorRT.height == ih)
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
            behindRT = MakeRT("RW hull behind windows", w, h, RenderTextureFormat.ARGBFloat);
            colorRT = MakeRT("RW interior colour", iw, ih, hdr ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default, RenderTextureReadWrite.Default);
            colorRT.filterMode = FilterMode.Bilinear;
            if (SplitSupported)
            {
                staticColorRT = MakeRT("RW interior static colour", iw, ih, colorRT.format, RenderTextureReadWrite.Default, 0);
                staticColorRT.filterMode = FilterMode.Bilinear; // read directly when no kerbals pass runs
                staticDepthRT = MakeRT("RW interior static depth", iw, ih, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear, 24);
                dynDepthRT = MakeRT("RW interior kerbal depth", iw, ih, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear, 24);
            }
            RWLog.Info("render targets {0}x{1}, interior {2}x{3} (hdr={4}, id format {5})", w, h, iw, ih, hdr, idFmt);
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
            RenderTexture[] all = { idRT, surfaceRT, frontRT, behindRT, colorRT, staticColorRT, staticDepthRT, dynDepthRT };
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null)
                {
                    all[i].Release();
                    UnityEngine.Object.Destroy(all[i]);
                }
            }
            idRT = surfaceRT = frontRT = behindRT = colorRT = staticColorRT = staticDepthRT = dynDepthRT = null;
            interiorTex = interiorDepthTex = null;
            staticAt = -1f;
        }
    }
}
