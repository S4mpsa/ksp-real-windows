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
            #include "UnityCG.cginc"

            sampler2D _RW_IdTex;
            sampler2D _RW_FrontTex;
            sampler2D _RW_InteriorTex;
            sampler2D _RW_SurfaceTex;
            float4 _RW_SunColor;
            float _RW_Reflection;
            float _RW_Dirt;
            float4 _RW_IdTex_TexelSize;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            float _RW_HullEps;
            float _RW_SceneEps;
            float _RW_UseSceneDepth;
            float _RW_Transmission;
            float4 _RW_Tint;
            float _RW_Feather;
            float _RW_Debug;
            float _RW_FlipY;
            float _RW_InsideId;   // the interior the camera is in (0 = none)
            float _RW_IdGroup[128];   // render group per part id, -1 none
            float _RW_SeeNeighbours;

            // a and b are the same part, or (seeIntoNeighbours) two parts drawn in
            // the same interior render, so the colour there really is b's interior
            bool Matches(float a, float b)
            {
                if (abs(a - b) < 0.5) return true;
                if (_RW_SeeNeighbours < 0.5) return false;
                int ia = (int)(a + 0.5), ib = (int)(b + 0.5);
                if (ia <= 0 || ib <= 0 || ia >= 128 || ib >= 128) return false;
                float ga = _RW_IdGroup[ia], gb = _RW_IdGroup[ib];
                return ga >= 0 && abs(ga - gb) < 0.5;
            }

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 spos : TEXCOORD0;
            };

            v2f vert(uint id : SV_VertexID)
            {
                // one triangle covering the screen
                float2 t = float2((id << 1) & 2, id & 2);
                v2f o;
                o.pos = float4(t * 2.0 - 1.0, 0.5, 1.0);
                o.spos = ComputeScreenPos(o.pos);
                return o;
            }

            // 1 if uv is a window pixel of a rendered part, else 0
            float Mask(float2 uv)
            {
                float3 hull = tex2Dlod(_RW_IdTex, float4(uv, 0, 0)).rgb;
                float2 front = tex2Dlod(_RW_FrontTex, float4(uv, 0, 0)).rg;

                if (hull.x < 0.5) return 0;                              // not a rendered part
                if (hull.z < 0.75) return 0;                             // hull point is not a window
                if (front.x > 0.5 && !Matches(hull.x, front.x)) return 0;   // interior belongs to a part not drawn with this one
                if (hull.z < 1.5)
                {
                    if (front.y <= 0) return 0;                          // no interior along this ray
                    if (front.y < hull.y - _RW_HullEps) return 0;        // interior pokes out of the hull
                }
                if (_RW_UseSceneDepth > 0.5)
                {
                    float sd = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                    if (sd < hull.y - _RW_SceneEps) return 0;            // something in front (terrain...)
                }
                return 1;
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

                float m = Mask(uv);
                if (_RW_Feather > 0)
                {
                    float2 d = _RW_IdTex_TexelSize.xy * _RW_Feather;
                    m = (m * 4
                        + Mask(uv + float2(d.x, 0)) + Mask(uv - float2(d.x, 0))
                        + Mask(uv + float2(0, d.y)) + Mask(uv - float2(0, d.y))) / 8.0;
                }

                if (_RW_Debug > 0.5)
                {
                    // yellow = window shown; red = hull says window but no matching interior behind it
                    float hm = tex2Dlod(_RW_IdTex, float4(uv, 0, 0)).b > 0.75 ? 1 : 0;
                    return PM(float4(hm, m, 0.1, max(hm, m) * 0.6));
                }

                if (m <= 0) return float4(0, 0, 0, 0);
                float4 s = tex2Dlod(_RW_SurfaceTex, float4(uv, 0, 0));
                float dirt = saturate(s.b * _RW_Dirt);
                float a = m * _RW_Transmission * (1 - saturate(s.r * _RW_Reflection)) * (1 - dirt * 0.6);
                float3 interior = tex2Dlod(_RW_InteriorTex, float4(uv, 0, 0)).rgb * _RW_Tint.rgb;
                float3 glint = _RW_SunColor.rgb * s.g * (1 - dirt * 0.7);
                return float4(interior * a + glint * m, a);
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
