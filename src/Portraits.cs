// Crew-portrait bookkeeping for interiors this mod spawns on NON-active
// vessels: spawning crew there would register their 2D portraits in the
// active vessel's portrait gallery.

using System;
using System.Collections.Generic;
using KSP.UI.Screens.Flight;

namespace RealWindows
{
    public static class RWPortraits
    {
        private static bool HasPortrait(Kerbal crew)
        {
            for (int i = 0; i < KerbalPortraitGallery.Instance.Portraits.Count; ++i)
            {
                if (KerbalPortraitGallery.Instance.Portraits[i].crewMember == crew)
                    return true;
            }
            return false;
        }

        private static bool InActiveCrew(Kerbal crew)
        {
            for (int i = 0; i < KerbalPortraitGallery.Instance.ActiveCrewItems.Count; ++i)
            {
                var item = KerbalPortraitGallery.Instance.ActiveCrewItems[i];
                if (item.kerbal != null && item.kerbal.name == crew.name)
                    return true;
            }
            return false;
        }

        private static void DestroyPortrait(Kerbal kerbal)
        {
            kerbal.InPart = null;
            kerbal.SetVisibleInPortrait(false);
            kerbal.state = Kerbal.States.NO_SIGNAL;
            for (int i = KerbalPortraitGallery.Instance.ActiveCrew.Count - 1; i >= 0; i--)
            {
                if (KerbalPortraitGallery.Instance.ActiveCrew[i].crewMemberName == kerbal.crewMemberName)
                    KerbalPortraitGallery.Instance.ActiveCrew.RemoveAt(i);
            }
            KerbalPortraitGallery.Instance.DespawnInactivePortraits();
            KerbalPortraitGallery.Instance.DespawnPortrait(kerbal);
            KerbalPortraitGallery.Instance.UIControlsUpdate();
        }

        private static void RestorePortrait(Part part, Kerbal kerbal)
        {
            if (kerbal.rosterStatus == ProtoCrewMember.RosterStatus.Dead ||
                kerbal.protoCrewMember.type == ProtoCrewMember.KerbalType.Unowned)
                return;
            kerbal.InPart = part;
            kerbal.SetVisibleInPortrait(true);
            if (!InActiveCrew(kerbal) && !HasPortrait(kerbal))
            {
                kerbal.staticOverlayDuration = 1f;
                kerbal.randomizeOnStartup = false;
                kerbal.Start();
            }
            kerbal.state = Kerbal.States.ALIVE;
        }

        public static void Process(Part part)
        {
            try
            {
                if (part.internalModel == null || KerbalPortraitGallery.Instance == null)
                    return;
                List<InternalSeat> seats = part.internalModel.seats;
                for (int i = 0; i < seats.Count; i++)
                {
                    Kerbal k = seats[i].kerbalRef;
                    if (k == null)
                        continue;
                    if (part.vessel.isActiveVessel)
                    {
                        if (k.InPart != part || !HasPortrait(k))
                            RestorePortrait(part, k);
                    }
                    else if (k.InPart != null || HasPortrait(k))
                    {
                        DestroyPortrait(k);
                    }
                }
            }
            catch (Exception e)
            {
                RWLog.Verbose("portrait processing failed for {0}: {1}", part.name, e.Message);
            }
        }
    }
}
