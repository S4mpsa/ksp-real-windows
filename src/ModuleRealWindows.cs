// Per-part switch in the part's right-click menu: "Windows: Transparent/Opaque".
// Added to every part with an interior by GameData\RealWindows\RealWindows.cfg
// (Module Manager); saved with the vessel and the craft. An opaque part is
// treated like an excluded one: its windows stay the hull's own paint and its
// interior is never rendered for them. Without the module (no Module Manager),
// every part is transparent.
// The group is KSPCommunityFixes' "Part" group (PAWStockGroups: name "CF_Part",
// title #autoLOC_6100048, collapsed), so the switch joins Aim Camera, Autostrut
// and the rest; the PAW merges groups by name. Without KSPCF it is a "Part"
// group of its own.

namespace RealWindows
{
    public class ModuleRealWindows : PartModule
    {
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Windows",
            groupName = "CF_Part", groupDisplayName = "#autoLOC_6100048", groupStartCollapsed = true)]
        [UI_Toggle(enabledText = "Transparent", disabledText = "Opaque", affectSymCounterparts = UI_Scene.Editor)]
        public bool transparent = true;

        public override string GetInfo()
        {
            return ""; // no entry in the editor's part info
        }
    }
}
