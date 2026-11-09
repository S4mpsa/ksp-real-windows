// Interior <-> part alignment.
//
// How stock places an interior (from InternalModel.Initialize / OnUpdate,
// KSP 1.12.5):
//
//   tmpPos = v.transform.position + v.transform.rotation * part.orgPos
//   tmpRot = v.transform.rotation * part.orgRot
//   interior.position = InternalSpace.WorldToInternal(tmpPos)
//   interior.rotation = InternalSpace.WorldToInternal(tmpRot) * Euler(90,180,0)
//
// where WorldToInternal goes through the ACTIVE vessel's transform at that
// moment. Consequences:
//   * the interior is frozen at the part's DESIGN pose (orgPos/orgRot), so
//     physics flex between parts is never reflected;
//   * interiors of non-active vessels are frozen relative to whatever the
//     active vessel's pose was when they were placed.
//
// So this mod never trusts a shared internal-space mapping. The only fixed
// relation is the constant Euler(90,180,0) between the part frame and the
// interior frame. Every frame we read the part's LIVE world pose and the
// interior's CURRENT internal-space pose and build the exact rigid map
//
//   K(p): world -> internal space,  K(p) * (partPose * E) = interiorPose
//
// Rendering the interior with the camera pose K(p) * cameraPose makes the
// interior sit exactly on the part, whatever the vessel, flex, docking or
// re-placement history. Scale is deliberately ignored (rigid maps only).

using UnityEngine;

namespace RealWindows
{
    public struct RigidMap
    {
        public Quaternion rot;
        public Vector3 pos;

        public Vector3 Apply(Vector3 p) { return rot * p + pos; }
        public Quaternion Apply(Quaternion q) { return rot * q; }

        public Matrix4x4 Matrix { get { return Matrix4x4.TRS(pos, rot, Vector3.one); } }

        public Matrix4x4 InverseMatrix
        {
            get
            {
                Quaternion ir = Quaternion.Inverse(rot);
                return Matrix4x4.TRS(ir * -pos, ir, Vector3.one);
            }
        }
    }

    public static class Alignment
    {
        // Stock's private InternalModel.internalToWorld constant.
        public static readonly Quaternion PartToInterior = Quaternion.Euler(90f, 180f, 0f);

        public static RigidMap Compute(Transform part, Transform interior)
        {
            Quaternion partFrame = part.rotation * PartToInterior;
            RigidMap k;
            k.rot = interior.rotation * Quaternion.Inverse(partFrame);
            k.pos = interior.position - k.rot * part.position;
            return k;
        }

        // How far the interior of `p` would be displaced if rendered with map `m`
        // instead of its own map (`own`): position error at the part, and angle.
        // KSP's own world -> internal-space map (through the active vessel). Used for
        // interiors whose whole root spins with a centrifuge (Kerbalism's GravityRing;
        // FreeIva turns the root with the ring): the root's pose then holds the spin,
        // and a map built from it would cancel the spin out. FreeIva re-places every
        // interior from this same map each frame, so it is exact there.
        public static bool Stock(out RigidMap k)
        {
            k = default(RigidMap);
            if (InternalSpace.Instance == null || FlightGlobals.ActiveVessel == null)
                return false;
            k.rot = InternalSpace.WorldToInternal(Quaternion.identity);
            k.pos = InternalSpace.WorldToInternal(Vector3.zero);
            return true;
        }

        public static void Disagreement(RigidMap m, RigidMap own, Vector3 partPos, out float meters, out float degrees)
        {
            meters = (m.Apply(partPos) - own.Apply(partPos)).magnitude;
            degrees = Quaternion.Angle(m.rot, own.rot);
        }

        // Where the stock overlay mapping (through the active vessel) would put
        // this part's interior relative to where it really is. Diagnostic only:
        // shows how wrong a shared mapping would be (flex / inactive vessels).
        public static bool StockMappingError(Part p, out float meters, out float degrees)
        {
            meters = 0f;
            degrees = 0f;
            if (InternalSpace.Instance == null || FlightGlobals.ActiveVessel == null || p.internalModel == null)
                return false;
            Vector3 stockPos = InternalSpace.WorldToInternal(p.transform.position);
            Quaternion stockRot = InternalSpace.WorldToInternal(p.transform.rotation) * PartToInterior;
            Transform it = p.internalModel.transform;
            meters = (stockPos - it.position).magnitude;
            degrees = Quaternion.Angle(stockRot, it.rotation);
            return true;
        }
    }
}
