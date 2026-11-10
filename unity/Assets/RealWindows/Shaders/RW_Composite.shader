// Full-screen composite, drawn as one procedural triangle from a command
// buffer on the main flight camera. Blends the lit interior over the hull
// wherever the pixel is a window of that part:
//
//   hull window mask set                     (hull pass: this hull point is a window)
//   interior seen there is this part's       (or one drawn in the same render)
//   nothing in the scene depth is in front   (terrain, buildings, ...)
//
// Windows from the per-pixel probe (hull.z = 1) are less certain, so they also
// need an interior behind them, inside the hull. Trusted windows (hull.z = 2,
// baked mask or author's glass) keep their whole outline: where the IVA has no
// surface behind the glass the pane is simply dark.
//
// Glass (_RW_SurfaceTex from the hull pass): the interior shows through less at
// grazing angles (Fresnel), where the painted window underneath reads as the
// reflection; smudges cloud it a little; the sun leaves a glint on top. Output is
// premultiplied (Blend One OneMinusSrcAlpha) so the glint can add light.
//
// See-through (_RW_SeeThrough): where a trusted window's view leaves the cabin
// before meeting an interior surface (out through its far window), the hull layer
// behind the window (_RW_BehindTex) decides: another part's window drawn in the same
// interior render shows its interior; behind real transparent glass (reason 4) the
// scene is left as it is; behind painted glass the window cut-out (RW_Cover) has
// already let the main render draw the scene there, or the pane stays dark.
//
// Pass 1 (camera inside a part, drawn after clouds and atmosphere): only the
// inside view, everything else left as it is.
//
// _RW_Debug: 5 window verdict (lime window from the mask or author's glass / green window from
//            the probe / cyan candidate region rejected, no IVA opening / blue solid)
// _RW_Debug: 0 normal, 1 window mask (yellow shown / red hull-only), 2 part ids, 3 x-ray (interior wherever
// the part matches - alignment check), 4 interior colour only.
Shader "Hidden/RealWindows/Composite"
{
    SubShader
    {
        CGINCLUDE
            #include "RW_Common.cginc"

            bool HasHole(float id)
            {
                int i = (int)(id + 0.5);
                return i > 0 && i < 128 && _RW_HoleIds[i] > 0.5;
            }

            // x: window strength, y: interior image weight,
            // z: cut-out weight (the main render already shows the scene there); dark pane: neither
            float3 Mask(float2 uv)
            {
                float4 c = Classify(uv);
                if (c.z < 0.5) return float3(c.xy, 0);
                return HasHole(c.w) ? float3(1, 0, 1) : float3(1, 0, 0);
            }

            // What the window decision mostly turns on, from two reads: the hull part,
            // its window state (none / probe / trusted) and the interior part seen.
            float3 Sig(float2 uv)
            {
                float3 hull = tex2Dlod(_RW_IdTex, float4(uv, 0, 0)).rgb;
                float fr = tex2Dlod(_RW_FrontTex, float4(uv, 0, 0)).r;
                return float3(hull.x, step(0.75, hull.z) + step(1.5, hull.z), fr);
            }

            float3 IdColor(float id)
            {
                if (id < 0.5) return float3(0, 0, 0);
                float h = frac(id * 0.618034);
                return saturate(abs(frac(h + float3(0, 2.0 / 3.0, 1.0 / 3.0)) * 6 - 3) - 1);
            }

            float4 PM(float4 c)
            {
                return float4(c.rgb * c.a, c.a);
            }

            bool Inside(float2 uv, out float4 c)
            {
                c = 0;
                if (_RW_InsideId < 0.5 || _RW_Debug > 0.5)
                    return false;
                float2 fr = tex2Dlod(_RW_FrontTex, float4(uv, 0, 0)).rg;
                if (fr.y <= 0 || !Matches(_RW_InsideId, fr.x))
                    return false;
                float2 hh = tex2Dlod(_RW_IdTex, float4(uv, 0, 0)).rg;
                if (hh.x > 0.5 && hh.y < fr.y - _RW_HullEps)
                    return false;
                c = float4(tex2Dlod(_RW_InteriorTex, float4(uv, 0, 0)).rgb, 1);
                return true;
            }

            float4 fragInside(v2f i) : SV_Target
            {
                float2 uv = i.spos.xy / i.spos.w;
                if (_RW_FlipY > 0.5) uv.y = 1 - uv.y;
                float4 c;
                return Inside(uv, c) ? c : float4(0, 0, 0, 0);
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.spos.xy / i.spos.w;
                if (_RW_FlipY > 0.5) uv.y = 1 - uv.y;

                if (_RW_Debug > 1.5 && _RW_Debug < 2.5)
                {
                    float hid = tex2Dlod(_RW_IdTex, float4(uv, 0, 0)).r;
                    return PM(float4(IdColor(hid), hid > 0.5 ? 0.6 : 0));
                }
                if (_RW_Debug > 2.5 && _RW_Debug < 3.5)
                {
                    float2 hull = tex2Dlod(_RW_IdTex, float4(uv, 0, 0)).rg;
                    float2 front = tex2Dlod(_RW_FrontTex, float4(uv, 0, 0)).rg;
                    bool same = hull.x > 0.5 && front.y > 0 && Matches(hull.x, front.x);
                    float3 c = tex2Dlod(_RW_InteriorTex, float4(uv, 0, 0)).rgb;
                    return PM(float4(c, same ? 0.75 : 0));
                }
                if (_RW_Debug > 5.5)
                {
                    // see-through decision per window pixel (ClassifyEx's rule)
                    float br;
                    float4 c = ClassifyEx(uv, br);
                    int r = (int)(br + 0.5);
                    if (r == 0) return float4(0, 0, 0, 0);
                    float3 col = r == 1 ? float3(0.5, 0.5, 0.5)       // scene in front: grey
                               : r == 2 ? float3(1, 0.55, 0)          // interior by colour-render depth: orange
                               : r == 3 ? float3(0.1, 1, 0.2)         // interior shell surface: green
                               : r == 4 ? float3(1, 0, 0)             // another part's interior, not drawn with it: red
                               : r == 5 ? float3(0, 1, 1)             // another window in the same render: cyan
                               : r == 6 ? float3(0.2, 0.4, 1)         // real glass, scene as it is: blue
                               : r == 7 ? (HasHole(c.w) ? float3(1, 0, 1) : float3(0.4, 0, 0.4))  // cut-out: magenta / none: dark purple
                               : r == 8 ? float3(0.6, 1, 0.1)         // interior, no see-through test: lime
                               :          float3(1, 1, 0);            // probe window, no interior behind: yellow
                    return PM(float4(col, 0.6));
                }
                if (_RW_Debug > 4.5)
                {
                    // verdict per hull pixel; clear = no probe
                    float4 h = tex2Dlod(_RW_IdTex, float4(uv, 0, 0));
                    if (h.x < 0.5 || h.z <= 0) return float4(0, 0, 0, 0);
                    if (h.z > 0.75) return h.w > 2.5 ? PM(float4(0.6, 1, 0.1, 0.6))   // window from the mask
                                                     : PM(float4(0.1, 1, 0.2, 0.6));  // window from the probe
                    if (h.w > 1.5 && h.w < 2.5) return PM(float4(0, 0.9, 1, 0.6));    // candidate region, rejected
                    return PM(float4(0.2, 0.4, 1, 0.5));
                }
                if (_RW_Debug > 3.5)
                {
                    float4 c = tex2Dlod(_RW_InteriorTex, float4(uv, 0, 0));
                    return float4(c.rgb, 1);
                }

                // camera inside a part: its interior shown whole wherever its walls are seen
                // and no other hull is in front; windows and openings still show outside
                float4 ins;
                if (Inside(uv, ins))
                    return ins;

                // most of the screen isn't a rendered part: one read and out
                if (_RW_Debug < 0.5 && tex2Dlod(_RW_IdTex, float4(uv, 0, 0)).r < 0.5)
                    return float4(0, 0, 0, 0);

                float3 mk = Mask(uv);
                if (_RW_Feather > 0)
                {
                    // The five-tap softening only changes anything where the taps
                    // disagree: at the window outline, or where the view inside
                    // switches between interiors / a cut-out. Two cheap reads per
                    // neighbour find those pixels; only there the full decision runs
                    // four more times. Inside a pane all taps agree and the centre
                    // result stands (same as the average of five equal taps).
                    float2 d = _RW_IdTex_TexelSize.xy * _RW_Feather;
                    float3 s0 = Sig(uv);
                    bool edge = any(Sig(uv + float2(d.x, 0)) != s0) || any(Sig(uv - float2(d.x, 0)) != s0)
                             || any(Sig(uv + float2(0, d.y)) != s0) || any(Sig(uv - float2(0, d.y)) != s0);
                    if (edge)
                        mk = (mk * 4
                            + Mask(uv + float2(d.x, 0)) + Mask(uv - float2(d.x, 0))
                            + Mask(uv + float2(0, d.y)) + Mask(uv - float2(0, d.y))) / 8.0;
                }
                float m = mk.x;
                float show = m > 0 ? mk.y / m : 0;
                float hole = m > 0 ? mk.z / m : 0;

                if (_RW_Debug > 0.5)
                {
                    // yellow = window shown; red = hull says window but no matching interior behind it
                    float hm = tex2Dlod(_RW_IdTex, float4(uv, 0, 0)).b > 0.75 ? 1 : 0;
                    return PM(float4(hm, m, 0.1, max(hm, m) * 0.6));
                }

                if (m <= 0) return float4(0, 0, 0, 0);
                float4 s = tex2Dlod(_RW_SurfaceTex, float4(uv, 0, 0));
                float dirt = saturate(s.b * _RW_Dirt);
                float t = _RW_Transmission * (1 - saturate(s.r * _RW_Reflection)) * (1 - dirt * 0.6);
                float a = m * t;
                float3 interior = tex2Dlod(_RW_InteriorTex, float4(uv, 0, 0)).rgb * show * _RW_Tint.rgb;
                float3 glint = _RW_SunColor.rgb * s.g * (1 - dirt * 0.7);
                // through a cut-out the scene is already behind: dim it like the interior would be
                float alpha = m * (t * (1 - hole) + (1 - t) * hole);
                return float4(interior * a + glint * m, alpha);
            }
        ENDCG

        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            ENDCG
        }

        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragInside
            #pragma target 3.5
            ENDCG
        }
    }
}
