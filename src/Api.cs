// Public API for other mods.
//
// Real Windows renders interiors with its own camera
// from the main flight camera's onPreCull. These events bracket each of those
// renders and name the parts whose interiors are in it. Anything shown or
// hidden between Before and After affects only what is seen through windows:
// stock IVA, the interior overlay and crew portraits render at other times.
//
// Subscribers must undo in AfterInteriorRender exactly what they changed in
// BeforeInteriorRender (e.g. restore Renderer.forceRenderingOff to its old
// value), and must not keep the list.

using System;
using System.Collections.Generic;

namespace RealWindows
{
    public static class RealWindowsApi
    {
        public const int Version = 1;

        public static event Action<IList<Part>> BeforeInteriorRender;
        public static event Action<IList<Part>> AfterInteriorRender;

        // The interiors' shell and props (internal-space layer) are cached and
        // redrawn only when the view changes or every `interiorRefresh` seconds;
        // kerbals are drawn every frame. A mod that moves something in that layer
        // (a hatch swinging open) calls this every frame while it moves.
        public static void RequestRedraw()
        {
            redrawRequested = true;
        }

        private static bool redrawRequested;

        internal static bool TakeRedrawRequest()
        {
            bool r = redrawRequested;
            redrawRequested = false;
            return r;
        }

        internal static bool HasSubscribers
        {
            get { return BeforeInteriorRender != null || AfterInteriorRender != null; }
        }

        internal static void Fire(bool before, List<Part> parts)
        {
            Action<IList<Part>> h = before ? BeforeInteriorRender : AfterInteriorRender;
            if (h == null)
                return;
            try
            {
                h(parts);
            }
            catch (Exception e)
            {
                RWLog.Error("API subscriber threw in {0}InteriorRender: {1}", before ? "Before" : "After", e);
            }
        }
    }
}
