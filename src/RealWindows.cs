// Flight-scene addon: keeps the part/interior bookkeeping current and feeds
// the render pipeline. Editor scenes are deliberately untouched.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealWindows
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class RealWindowsFlight : MonoBehaviour
    {
        private PortalPipeline pipeline;
        private readonly Dictionary<Part, PartPortal> portals = new Dictionary<Part, PartPortal>();
        private readonly List<PartPortal> candidates = new List<PartPortal>();
        private readonly List<Part> deadKeys = new List<Part>();
        private float nextPrune, nextPortraits, nextOverlayCheck, nextStatus;

        // verbose diagnostics: interiors destroyed under us or rebuilt, and a
        // per-vessel summary 3 s and 15 s after every vessel switch
        private readonly Dictionary<Part, int> vanished = new Dictionary<Part, int>();
        private readonly Dictionary<Part, int> rebuilt = new Dictionary<Part, int>();
        private float summaryAt1 = -1f, summaryAt2 = -1f;
        private bool stockOverlayOn;
        private bool ok;
        private bool switchedOn = true;   // the toggle key's state

        private static readonly Comparison<PartPortal> ByDistance = (a, b) => a.distance.CompareTo(b.distance);

        public void Awake()
        {
            RWSettings.Load();
            if (!RWSettings.enabled)
            {
                RWLog.Info("disabled in settings.cfg");
                return;
            }
            if (!ShaderBundle.Load())
                return;
            pipeline = new PortalPipeline();
            if (!pipeline.Init())
            {
                pipeline = null;
                return;
            }
            GameEvents.onVesselSwitching.Add(OnVesselSwitching);
            GameEvents.onVesselChange.Add(OnVesselChanged);
            GameEvents.onVesselCrewWasModified.Add(OnCrewModified);
            GameEvents.onVesselWasModified.Add(OnCrewModified);
            ok = true;
            switchedOn = RWSettings.startEnabled;
            RWLog.Info("v{0} active in flight ({1}, toggle key {2})", AssemblyInfo.Version, switchedOn ? "on" : "off", RWSettings.toggleKey);
        }

        public void OnDestroy()
        {
            GameEvents.onVesselSwitching.Remove(OnVesselSwitching);
            GameEvents.onVesselChange.Remove(OnVesselChanged);
            GameEvents.onVesselCrewWasModified.Remove(OnCrewModified);
            GameEvents.onVesselWasModified.Remove(OnCrewModified);
            if (pipeline != null)
                pipeline.Dispose();
            pipeline = null;
        }

        // ------------------------------------------------------------------
        // events
        // ------------------------------------------------------------------

        // Vessel switches (EVA included): KSP destroys the old vessel's interiors and
        // FreeIva destroys every non-active vessel's interiors when it refreshes.
        // RefreshLists rebuilds them; with verboseLogging a summary per vessel follows.
        private void OnVesselChanged(Vessel v)
        {
            if (!RWSettings.verboseLogging)
                return;
            float now = Time.unscaledTime;
            summaryAt1 = now + 3f;
            summaryAt2 = now + 15f;
            RWLog.Info("vessel change: now controlling {0}{1}", v != null ? v.vesselName : "?", v != null && v.isEVA ? " (EVA)" : "");
        }

        private static bool Throttle(Dictionary<Part, int> counts, Part p)
        {
            int n;
            counts.TryGetValue(p, out n);
            counts[p] = ++n;
            return n <= 5 || n % 25 == 0;
        }

        private void LogSummary(string when)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.parts == null || v.GetCrewCapacity() == 0)
                    continue;
                int iva = 0, withInterior = 0, ours = 0, rendered = 0, usable = 0;
                foreach (Part p in v.parts)
                {
                    PartPortal pp;
                    if (p == null || !portals.TryGetValue(p, out pp) || !pp.hasIva)
                        continue;
                    iva++;
                    if (p.internalModel != null) withInterior++;
                    if (pp.createdByUs && p.internalModel != null) ours++;
                    if (pp.InteriorUsable) usable++;
                    if (pp.frameId > 0) rendered++;
                }
                if (iva == 0)
                    continue;
                RWLog.Info("summary {0} after switch: {1}{2}: {3} parts with an IVA, {4} have an interior ({5} built by Real Windows), {6} usable, {7} rendered last frame; {8} interiors skipped overall (no window in view / too small)",
                    when, v.vesselName, v == active ? " (active)" : "", iva, withInterior, ours, usable, rendered, pipeline != null ? pipeline.lastCulled : 0);
            }
        }

        // Fires before the new vessel's MakeActive -> SpawnCrew. Stock's
        // SpawnIVA re-uses an existing interior and seats crew into it again
        // without despawning the kerbals we spawned -> duplicates. Remove the
        // interiors we created on the incoming vessel so stock builds its own.
        private void OnVesselSwitching(Vessel from, Vessel to)
        {
            if (to == null)
                return;
            for (int i = 0; i < to.parts.Count; i++)
            {
                Part p = to.parts[i];
                PartPortal pp;
                if (p != null && portals.TryGetValue(p, out pp) && pp.createdByUs)
                    RemoveOurInterior(p, pp);
            }
        }

        private void RemoveOurInterior(Part p, PartPortal pp)
        {
            try
            {
                if (pp.parkedHidden)
                {
                    pp.SetForceOff(false);
                    pp.parkedHidden = false;
                }
                if (p.internalModel != null)
                {
                    p.internalModel.DespawnCrew();
                    DestroyImmediate(p.internalModel.gameObject);
                    p.internalModel = null;
                }
            }
            catch (Exception e)
            {
                RWLog.Warn("{0}: removing our interior failed: {1}", p.name, e.Message);
            }
            pp.createdByUs = false;
            pp.hadInterior = false;
            pp.InvalidateInterior();
        }

        // Toggle key off: no passes, no composite, no per-frame scanning, and the
        // interiors built for other vessels are removed (their props stop running).
        // Everything is rebuilt when switched on again.
        private void SwitchOff()
        {
            pipeline.Suspend();
            foreach (KeyValuePair<Part, PartPortal> kv in portals)
            {
                if (kv.Key == null)
                    continue;
                if (kv.Value.createdByUs)
                    RemoveOurInterior(kv.Key, kv.Value);
                else if (kv.Value.parkedHidden)
                {
                    kv.Value.SetForceOff(false);
                    kv.Value.parkedHidden = false;
                }
            }
            portals.Clear();
            candidates.Clear();
        }

        private void OnCrewModified(Vessel v)
        {
            if (v == null)
                return;
            for (int i = 0; i < v.parts.Count; i++)
            {
                PartPortal pp;
                if (v.parts[i] != null && portals.TryGetValue(v.parts[i], out pp))
                    pp.InvalidateInterior(); // rescan next frame (kerbals moved, parts docked)
            }
        }

        // ------------------------------------------------------------------
        // per frame
        // ------------------------------------------------------------------

        public void Update()
        {
            if (!ok || pipeline == null)
                return;
            float now = Time.unscaledTime;

            if (RWSettings.toggleKey.Down())
            {
                switchedOn = !switchedOn;
                if (!switchedOn)
                    SwitchOff();
                ScreenMessages.PostScreenMessage("Real Windows " + (switchedOn ? "on" : "off"), 2f, ScreenMessageStyle.UPPER_CENTER);
                RWLog.Info("switched {0}", switchedOn ? "on" : "off");
            }
            if (!switchedOn)
                return; // off: nothing per frame beyond the key check

            HandleKeys();

            if (now >= nextOverlayCheck)
            {
                nextOverlayCheck = now + 0.5f;
                stockOverlayOn = GameObject.Find("InternalSpaceOverlay Host") != null;
            }

            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            CameraManager.CameraMode mode = CameraManager.Instance != null
                ? CameraManager.Instance.currentCameraMode : CameraManager.CameraMode.Flight;
            bool exteriorView = mode == CameraManager.CameraMode.Flight || mode == CameraManager.CameraMode.External;
            bool active = cam != null && exteriorView && !MapView.MapIsEnabled && !stockOverlayOn
                          && FlightGlobals.ActiveVessel != null && InternalSpace.Instance != null;

            if (now >= nextPrune)
            {
                nextPrune = now + 5f;
                Prune();
            }

            RefreshLists(cam, active, now);
            pipeline.UpdateParking();
            pipeline.wantActive = active;

            if (now >= nextPortraits)
            {
                nextPortraits = now + 1f;
                for (int i = 0; i < pipeline.allInteriors.Count; i++)
                    if (pipeline.allInteriors[i].createdByUs)
                        RWPortraits.Process(pipeline.allInteriors[i].part);
            }

            if (summaryAt1 > 0 && now >= summaryAt1) { summaryAt1 = -1f; LogSummary("3 s"); }
            if (summaryAt2 > 0 && now >= summaryAt2) { summaryAt2 = -1f; LogSummary("15 s"); }

            if (RWSettings.verboseLogging && now >= nextStatus)
            {
                nextStatus = now + 30f;
                RWLog.Info("status: {0} interiors known, {1} rendered in {2} group(s), {3} skipped (no window in view), {4} occluder parts, {5:F2} ms CPU per frame; shell/props cache: {6} redraws, {7} frames reused",
                    pipeline.allInteriors.Count, pipeline.lastInteriorCount, pipeline.lastGroupCount, pipeline.lastCulled,
                    pipeline.occluders.Count, pipeline.TakeAverageMs(), pipeline.staticRedraws, pipeline.staticReuses);
                pipeline.staticRedraws = pipeline.staticReuses = 0;
            }
        }

        private void RefreshLists(Camera cam, bool active, float now)
        {
            pipeline.interiors.Clear();
            pipeline.occluders.Clear();
            pipeline.allInteriors.Clear();
            candidates.Clear();

            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            int created = 0;
            float occD = Mathf.Max(RWSettings.occluderDistance, RWSettings.maxDistance);

            List<Vessel> loaded = FlightGlobals.VesselsLoaded;
            for (int vi = 0; vi < loaded.Count; vi++)
            {
                Vessel v = loaded[vi];
                if (v == null || v.parts == null)
                    continue;
                bool vesselEligible = v == activeVessel || RWSettings.includeInactiveVessels;
                for (int pi = 0; pi < v.parts.Count; pi++)
                {
                    Part p = v.parts[pi];
                    if (p == null)
                        continue;
                    PartPortal pp;
                    if (!portals.TryGetValue(p, out pp))
                    {
                        pp = new PartPortal(p);
                        portals[p] = pp;
                    }

                    if (pp.hadInterior && p.internalModel == null)
                    {
                        pp.hadInterior = false;
                        if (RWSettings.verboseLogging && Throttle(vanished, p))
                            RWLog.Info("interior of {0} on {1}{2} was destroyed by something else ({3}x so far)",
                                p.partInfo != null ? p.partInfo.name : p.name, v.vesselName, v == activeVessel ? " (active)" : "", vanished[p]);
                    }

                    // keep every existing interior known (for render isolation/parking)
                    if (pp.hasIva && p.internalModel != null)
                    {
                        pp.hadInterior = true;
                        pp.MaybeScanInterior(now);
                        if (pp.InteriorUsable)
                            pipeline.allInteriors.Add(pp);
                    }

                    if (!active || cam == null)
                        continue;
                    float d = (p.transform.position - camPos).magnitude;
                    if (d > occD)
                        continue;
                    pp.distance = d;
                    pp.MaybeScanExterior(now);
                    pipeline.occluders.Add(pp);

                    if (!pp.hasIva || pp.excluded || !vesselEligible || d > RWSettings.maxDistance)
                        continue;
                    if (p.internalModel == null)
                    {
                        if (!RWSettings.createMissingInteriors || created >= RWSettings.createPerFrame)
                            continue;
                        created++;
                        if (!CreateInterior(p, pp))
                            continue;
                        pp.MaybeScanInterior(now);
                        if (pp.InteriorUsable)
                            pipeline.allInteriors.Add(pp);
                    }
                    if (pp.InteriorUsable && !pp.excluded)
                        candidates.Add(pp);
                }
            }

            candidates.Sort(ByDistance);
            int n = Mathf.Min(candidates.Count, RWSettings.maxInteriors);
            for (int i = 0; i < n; i++)
                pipeline.interiors.Add(candidates[i]);
        }

        private bool CreateInterior(Part p, PartPortal pp)
        {
            try
            {
                p.CreateInternalModel();
                if (p.internalModel == null)
                    return false;
                p.internalModel.Initialize(p);
                p.internalModel.SpawnCrew();
                p.internalModel.SetVisible(false);
                pp.createdByUs = true;
                pp.InvalidateInterior();
                if (RWSettings.verboseLogging && Throttle(rebuilt, p))
                    RWLog.Info("interior built for {0} on {1} ({2}x so far)", p.partInfo != null ? p.partInfo.name : p.name,
                        p.vessel != null ? p.vessel.vesselName : "?", rebuilt[p]);
                return true;
            }
            catch (Exception e)
            {
                RWLog.Warn("{0}: interior creation failed: {1}", p.name, e.Message);
                return false;
            }
        }

        private void Prune()
        {
            deadKeys.Clear();
            foreach (KeyValuePair<Part, PartPortal> kv in portals)
                if (kv.Key == null || kv.Key.vessel == null || !kv.Key.vessel.loaded)
                    deadKeys.Add(kv.Key);
            for (int i = 0; i < deadKeys.Count; i++)
            {
                PartPortal pp = portals[deadKeys[i]];
                if (pp.parkedHidden)
                    pp.SetForceOff(false);
                portals.Remove(deadKeys[i]);
            }
        }

        // ------------------------------------------------------------------
        // debug
        // ------------------------------------------------------------------

        private static readonly string[] DebugNames = { "off", "window mask", "part IDs", "x-ray (alignment check)", "interior only", "window verdict" };

        private void HandleKeys()
        {
            if (RWSettings.debugCycleKey != KeyCode.None && Input.GetKeyDown(RWSettings.debugCycleKey))
            {
                RWSettings.debugMode = (RWSettings.debugMode + 1) % DebugNames.Length;
                ScreenMessages.PostScreenMessage("RealWindows debug: " + DebugNames[RWSettings.debugMode], 2f, ScreenMessageStyle.UPPER_CENTER);
            }
            if (RWSettings.alignmentDumpKey != KeyCode.None && Input.GetKeyDown(RWSettings.alignmentDumpKey))
                DumpAlignment();
        }

        private void DumpAlignment()
        {
            RWLog.Info("alignment dump: {0} interiors rendered in {1} group(s)", pipeline.lastInteriorCount, pipeline.lastGroupCount);
            for (int i = 0; i < pipeline.interiors.Count; i++)
            {
                PartPortal pp = pipeline.interiors[i];
                if (!pp.InteriorUsable)
                    continue;
                RigidMap own = Alignment.Compute(pp.part.transform, pp.model.transform);
                float sm, sd;
                bool haveStock = Alignment.StockMappingError(pp.part, out sm, out sd);
                RWLog.Info("  {0} [{1}{2}] dist {3:F1} m, group {4}: stock overlay mapping would be off by {5} ",
                    pp.part.name, pp.part.vessel.vesselName, pp.part.vessel.isActiveVessel ? ", active" : "",
                    pp.distance, pp.group,
                    haveStock ? string.Format("{0:F3} m / {1:F2} deg", sm, sd) : "n/a");
                Vector3 e = own.rot.eulerAngles;
                RWLog.Info("    live map: pos ({0:F3},{1:F3},{2:F3}) rot ({3:F2},{4:F2},{5:F2}); createdByUs={6} stockHidden={7} parked={8} renderers={9}",
                    own.pos.x, own.pos.y, own.pos.z, e.x, e.y, e.z, pp.createdByUs, pp.StockHidden, pp.parkedHidden, pp.interiorRenderers.Count);
                RWLog.Info("    probe {0}: baked={1} shellDraws={2} radius={3:F2} m centre ({4:F2},{5:F2},{6:F2})",
                    pp.probeKey, pp.probe != null && pp.probe.baked, pp.interiorShell.Count, pp.shellRadius,
                    pp.shellCenterLocal.x, pp.shellCenterLocal.y, pp.shellCenterLocal.z);
            }
            ScreenMessages.PostScreenMessage("RealWindows: alignment written to KSP.log", 2f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
