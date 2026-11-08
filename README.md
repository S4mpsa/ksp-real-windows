# Real Windows for Kerbal Space Program 1.12
<img width="1300" height="720" alt="SSTO" src="https://github.com/user-attachments/assets/1bbacb9a-8ea4-43bc-aca3-f702d5751d0c" />

## Features

- **Interiors shown through windows.** In flight, the windows of every crewed
  part show its interior (IVA) from outside.
- **No configuration, any part.** Windows are found automatically from each
  part's own textures and interior, so stock and modded parts work out of the
  box.
- **Glass windows.** Reflections at grazing angles, a sun glint
  and faint smudges to simulate a glass layer.
- **All vessels in view.** All vessels within range show their interiors, even when not the active vessel.
- **Open hatches look through.** With FreeIva installed, an open hatch shows the next
  part's interior through the hatch.
- **Optimized.** Interiors are rendered only where a window is
  actually on screen, only over that area, and the static cabin is cached
  between camera moves; crew still animate every frame. `Alt+F8` switches the
  mod off and on in flight. Expect around 10-15% less FPS with the mod active.
  (The stock "Show Interiors" option is about twice as expensive!)

**Requires** KSP 1.12.x.
**Optional:** FreeIva.
**Shaders** are included for Windows, macOS and Linux;
only Windows has been tested in game so far.
**Licence:** GPL v3.

## How it works

- **Finding the windows (once per part type).** The hull is drawn flat into its
  texture's UV space and read back. Candidate panes are the regions its
  lightmap or gloss map marks; each is kept only if the IVA, seen from the crew
  seats, really has an opening behind it. Kept regions take their exact outline
  from the UV island or the glass-coloured paint, and the result is a cached
  window mask per texture. Glass sub-meshes and window-named meshes are handled
  directly. A per-pixel interior probe is the fallback for parts with no usable
  texture information.
- **Keeping interiors aligned.** KSP places each interior once, frozen at the
  design pose and relative to the active vessel. To keep the interiors aligned,
  it builds the exact rigid map from each part's live transform to
  its interior's current placement and renders through it.
  Nothing is moved, so stock IVA is untouched.
- **Four passes per frame,** from the flight camera:
  1. the hulls, writing part id, depth, window state and the glass surface;
  2. the interior shells only, to know whose interior each pixel sees;
  3. a real camera on the interiors, cropped to the windows' screen area;
  4. a composite blending interior and glass into the scene where it's a window.
- **Caching.** The cabin shell and props are rendered into a cached colour and
  depth image, redrawn only when the view of them changes (or about once a
  second for lights and screens). Kerbals are drawn over it every frame,
  depth-tested against the cache.
- **Neighbours and inside views.** Parts behind open hatches are drawn in the
  same render, so their interior counts as seen. From inside a part, the
  composite runs a second time after clouds and atmosphere.
