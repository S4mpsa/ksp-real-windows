// SSPX centrifuges (HabUtils' ModuleDeployableCentrifuge, read by reflection).
//
// Some rings' deploy animation turns their sections about the spin axis as they
// lock (the 'Mercury' PXL-E slides its four modules about 30 degrees), but the
// interior is built for the model's stored pose, and SSPX turns the interior
// only with the ring's spin. Inside IVA nobody sees it; through windows the
// interior sits a section's slide away from its hull.
//
// The deploy twist is measured from the ring itself: the spin transform's direct
// children whose rotation differs from the part prefab's by a pure turn about
// the spin axis (more than 1 degree, under 2 degrees of anything else). When at
// least two of them agree within 2 degrees, their mean is the twist. After SSPX
// sets the interior's angle each physics step (TimingManager,
// BetterLateThanNever: after the part modules), the interior's rotation root
// (FreeIva's `model`, or SSPX's own "IVA Rotator" with its proxies) is turned
// by it about the ring axis. Everything reading the interior later in the frame
// (the stock IVA, FreeIva, Busy Kerbals, the passes) sees the corrected pose.
// `centrifugeDeployTwist = False` turns it off.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace RealWindows
{
    public static class CentrifugeTwist
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private class Ring
        {
            public Part part;
            public PartModule module;
            public FieldInfo spinF, rootF, propF;
            public Transform spin;
            public readonly List<Transform> kids = new List<Transform>();
            public readonly List<Quaternion> rest = new List<Quaternion>();
            public Transform root;              // the rotation root seen last
            public Vector3 rootBasePos;          // its local position before we moved it
            public Quaternion lastWritten;
            public bool wrote;
            public float twist;                  // degrees, last applied
            public bool logged;
        }

        private static readonly Dictionary<Part, Ring> rings = new Dictionary<Part, Ring>();
        private static readonly HashSet<Part> notRings = new HashSet<Part>();
        private static readonly List<Part> dead = new List<Part>();
        private static bool hooked;
        private static TimingManager.UpdateAction action;

        public static void Hook()
        {
            if (hooked || !RWSettings.centrifugeDeployTwist)
                return;
            action = OnFixedUpdate;
            TimingManager.FixedUpdateAdd(TimingManager.TimingStage.BetterLateThanNever, action);
            hooked = true;
        }

        public static void Unhook()
        {
            if (hooked && action != null)
                TimingManager.FixedUpdateRemove(TimingManager.TimingStage.BetterLateThanNever, action);
            hooked = false;
            rings.Clear();
            notRings.Clear();
        }

        // Every Update: which interiors belong to SSPX centrifuges (checked once per part).
        public static void Track(List<PartPortal> interiors)
        {
            if (!hooked)
                return;
            for (int i = 0; i < interiors.Count; i++)
            {
                Part p = interiors[i].part;
                if (p == null || rings.ContainsKey(p) || notRings.Contains(p))
                    continue;
                Ring r = Make(p);
                if (r != null) rings[p] = r;
                else notRings.Add(p);
            }
        }

        private static Ring Make(Part p)
        {
            PartModule m = null;
            foreach (PartModule pm in p.Modules)
                if (pm != null && pm.moduleName == "ModuleDeployableCentrifuge") { m = pm; break; }
            if (m == null)
                return null;
            Type t = m.GetType();
            Ring r = new Ring
            {
                part = p,
                module = m,
                spinF = t.GetField("spinTransform", Any),
                rootF = t.GetField("IVARotationRoot", Any),
                propF = t.GetField("propDict", Any)
            };
            if (r.spinF == null || r.rootF == null)
            {
                RWLog.Info("centrifuge {0}: SSPX fields not found, no deploy twist", p.partInfo != null ? p.partInfo.name : p.name);
                return null;
            }
            return r;
        }

        // The spin transform's children and their rotation in the part prefab.
        private static bool Bind(Ring r)
        {
            r.spin = r.spinF.GetValue(r.module) as Transform;
            r.kids.Clear();
            r.rest.Clear();
            if (r.spin == null || r.part.partInfo == null || r.part.partInfo.partPrefab == null)
                return false;
            Transform ps = r.part.partInfo.partPrefab.FindModelTransform(r.spin.name);
            if (ps == null)
                return false;
            for (int i = 0; i < r.spin.childCount; i++)
            {
                Transform k = r.spin.GetChild(i);
                Transform pk = ps.Find(k.name);
                if (pk == null)
                    continue;
                r.kids.Add(k);
                r.rest.Add(pk.localRotation);
            }
            return r.kids.Count > 0;
        }

        // Degrees the ring's sections are turned about the spin axis from the prefab, 0 if unclear.
        private static float Measure(Ring r)
        {
            float sum = 0f, min = float.MaxValue, max = float.MinValue;
            int n = 0;
            for (int i = 0; i < r.kids.Count; i++)
            {
                if (r.kids[i] == null)
                    continue;
                // the change, in the spin transform's frame (spin axis = local z)
                Quaternion d = r.kids[i].localRotation * Quaternion.Inverse(r.rest[i]);
                if (d.w < 0f) d = new Quaternion(-d.x, -d.y, -d.z, -d.w);
                float tw = 2f * Mathf.Atan2(d.z, d.w) * Mathf.Rad2Deg;
                if (Mathf.Abs(tw) < 1f)
                    continue;
                float swing = Quaternion.Angle(d, Quaternion.AngleAxis(tw, Vector3.forward));
                if (swing > 2f)
                    continue;
                sum += tw;
                min = Mathf.Min(min, tw);
                max = Mathf.Max(max, tw);
                n++;
            }
            return n >= 2 && max - min <= 2f ? sum / n : 0f;
        }

        private static void OnFixedUpdate()
        {
            if (rings.Count == 0)
                return;
            dead.Clear();
            foreach (Ring r in rings.Values)
            {
                if (r.part == null || r.module == null)
                {
                    dead.Add(r.part);
                    continue;
                }
                try { Apply(r); }
                catch (Exception e)
                {
                    RWLog.Warn("centrifuge {0}: deploy twist failed, stopped: {1}", r.part.name, e.Message);
                    dead.Add(r.part);
                }
            }
            for (int i = 0; i < dead.Count; i++)
            {
                rings.Remove(dead[i]);
                if (dead[i] != null) notRings.Add(dead[i]);
            }
        }

        private static void Apply(Ring r)
        {
            Part p = r.part;
            if (p.internalModel == null)
                return;
            Transform root = r.rootF.GetValue(r.module) as Transform;
            if (root == null)
                return;
            if (root != r.root)
            {
                r.root = root;
                r.rootBasePos = root.localPosition;
                r.wrote = false;
            }
            if (r.spin == null || r.spin != r.spinF.GetValue(r.module) as Transform)
            {
                if (!Bind(r))
                    return;
            }
            float tw = Measure(r);
            // SSPX writes the angle every step it runs: then the whole twist goes on
            // top of it. If it didn't, the root still holds our last write, and only
            // a change of the twist (deploying) is added.
            bool fresh = !r.wrote || Quaternion.Angle(root.localRotation, r.lastWritten) >= 0.001f;
            float apply = fresh ? tw : tw - r.twist;
            if (!fresh && Mathf.Abs(apply) < 0.05f)
                return;
            if (!r.logged || (Mathf.Abs(tw) >= 1f && Mathf.Abs(r.twist) < 1f))
            {
                r.logged = true;
                RWLog.Info("centrifuge {0}: spin '{1}' ({2} children compared with the prefab), rotation root '{3}': sections turned {4:F1} deg by the deploy animation{5}",
                    p.partInfo != null ? p.partInfo.name : p.name, r.spin.name, r.kids.Count, root.name, tw,
                    Mathf.Abs(tw) >= 0.05f ? "; its interior is turned with them" : "");
            }
            r.twist = tw;
            if (fresh)
                root.localPosition = r.rootBasePos;
            if (Mathf.Abs(apply) >= 0.05f)
            {
                // the ring axis and centre in internal space, through the part -> interior map
                Transform it = p.internalModel.transform;
                Quaternion toInt = Quaternion.Inverse(Alignment.PartToInterior) * Quaternion.Inverse(p.transform.rotation);
                Vector3 axis = it.rotation * (toInt * r.spin.forward);
                Vector3 c = it.TransformPoint(toInt * (r.spin.position - p.transform.position));
                Quaternion q = Quaternion.AngleAxis(apply, axis);
                root.SetPositionAndRotation(c + q * (root.position - c), q * root.rotation);
                // SSPX without FreeIva: the interior follows proxies under the rotator
                IDictionary proxies = r.propF != null ? r.propF.GetValue(r.module) as IDictionary : null;
                if (proxies != null)
                    foreach (DictionaryEntry e in proxies)
                    {
                        Transform proxy = e.Key as Transform, real = e.Value as Transform;
                        if (proxy != null && real != null && proxy.IsChildOf(root))
                            real.SetPositionAndRotation(proxy.position, proxy.rotation);
                    }
            }
            r.lastWritten = root.localRotation;
            r.wrote = true;
        }
    }
}
