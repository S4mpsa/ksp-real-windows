// Pass 0, every frame, two targets.
// Target 0: R = part id, G = linear eye depth, B = window state, A = debug reason.
// Target 1, the glass surface: R = Fresnel reflectance, G = sun glint (x light
// colour in the composite), B = dirt, from the hull's own normal and object space.
//   B = 2    window, trusted (baked mask, or the author's glass sub-mesh)
//   B = 1    window from the per-pixel probe (the composite still wants an interior behind it)
//   B = 0.5  solid
//   A = 3 from the mask, 2 candidate region rejected by the IVA, 1 otherwise (debug view 5)
//
// _RW_MaskMode: 0 probe per pixel, 1 baked mask, 2 the author's glass, 3 solid (flag decals).
//
// Pass 1, once per hull texture (HullMasks.cs): draws the sub-mesh flattened
// into UV space. R = gloss, G = share of view points that see this hull point,
// B = covered, A = lightmap. Max blending, so hull points sharing a texel
// (mirrored halves, UVs tiled past 0..1) keep the strongest evidence.
//
// A view point sees a hull point if the nearest IVA surface along the ray lies
// past the hull point by more than _RW_WallMargin, or there is none at all. A
// surface right at the hull point is the wall the hull is wrapped around.
Shader "Hidden/RealWindows/Hull"
{
    CGINCLUDE
    #pragma target 3.5
    #pragma require 2darray
    #include "UnityCG.cginc"

    #define MAX_SOURCES 8   // MUST match Probes.MaxSources

    float4x4 _RW_WorldToProbe;
    float _RW_WallMargin;
    float _RW_SourceCount;
    float4 _RW_Sources[MAX_SOURCES];
    UNITY_DECLARE_TEX2DARRAY(_RW_ProbeTex);

    // MUST match Probes.Fwd/Right/Up in Probe.cs
    static const float3 FWD[6]   = { float3(1,0,0), float3(-1,0,0), float3(0,1,0), float3(0,-1,0), float3(0,0,1), float3(0,0,-1) };
    static const float3 RIGHT[6] = { float3(0,0,-1), float3(0,0,1), float3(1,0,0), float3(1,0,0), float3(1,0,0), float3(-1,0,0) };
    static const float3 UP[6]    = { float3(0,1,0), float3(0,1,0), float3(0,0,-1), float3(0,0,1), float3(0,1,0), float3(0,1,0) };

    // 1 / distance to the nearest IVA surface from view point k along d, 0 = nothing
    float NearInverse(float3 d, int k)
    {
        float3 a = abs(d);
        int f;
        if (a.x >= a.y && a.x >= a.z) f = d.x > 0 ? 0 : 1;
        else if (a.y >= a.z)          f = d.y > 0 ? 2 : 3;
        else                          f = d.z > 0 ? 4 : 5;
        float fw = dot(d, FWD[f]);
        float2 uv = float2(dot(d, RIGHT[f]), dot(d, UP[f])) / fw * 0.5 + 0.5;
        return UNITY_SAMPLE_TEX2DARRAY_LOD(_RW_ProbeTex, float3(uv, k * 6 + f), 0).r;
    }

    // share of the view points that see the point q (probe frame)
    float Openness(float3 q)
    {
        float open = 0, used = 0;
        [loop]
        for (int k = 0; k < MAX_SOURCES; k++)
        {
            if (k >= (int)_RW_SourceCount) break;
            float3 v = q - _RW_Sources[k].xyz;
            float dist = length(v);
            if (dist < 1e-3) continue;
            used += 1;
            float inv = NearInverse(v / dist, k);
            if (inv <= 0 || 1.0 / inv > dist + _RW_WallMargin)
                open += 1;
        }
        return used > 0 ? open / used : 0;
    }
    ENDCG

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Cull Back
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float _RW_PartId;
            float _RW_HasProbe;
            float _RW_MaskMode;
            float _RW_OpenFraction;
            sampler2D _RW_MaskTex;
            float3 _RW_CamPos;      // world; not _WorldSpaceCameraPos, which command buffers don't set
            float3 _RW_SunDir;      // world, towards the sun
            float _RW_SunLit;       // 0 when this part's vessel is in a planet's shadow
            float _RW_Smoothness;
            float _RW_DirtScale;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float eye : TEXCOORD0;
                float3 world : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float3 wnormal : TEXCOORD3;
                float3 local : TEXCOORD4;
            };

            struct targets
            {
                float4 id : SV_Target0;
                float4 surface : SV_Target1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.eye = -UnityObjectToViewPos(v.vertex).z;
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.uv = v.uv;
                o.wnormal = UnityObjectToWorldNormal(v.normal);
                o.local = v.vertex.xyz;
                return o;
            }

            float Hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float Noise(float3 x)
            {
                float3 i = floor(x), f = frac(x);
                f = f * f * (3 - 2 * f);
                return lerp(lerp(lerp(Hash(i), Hash(i + float3(1, 0, 0)), f.x),
                                 lerp(Hash(i + float3(0, 1, 0)), Hash(i + float3(1, 1, 0)), f.x), f.y),
                            lerp(lerp(Hash(i + float3(0, 0, 1)), Hash(i + float3(1, 0, 1)), f.x),
                                 lerp(Hash(i + float3(0, 1, 1)), Hash(i + float3(1, 1, 1)), f.x), f.y), f.z);
            }

            // Smudges in the part's own space, so they stay put on the glass.
            float Dirt(float3 p)
            {
                p *= _RW_DirtScale;
                float n = Noise(p) * 0.55 + Noise(p * 2.7 + 13.1) * 0.3 + Noise(p * 9.1 + 7.7) * 0.15;
                return smoothstep(0.45, 0.8, n);
            }

            float4 Surface(v2f i)
            {
                float3 n = normalize(i.wnormal);
                float3 v = normalize(_RW_CamPos - i.world);
                if (dot(n, v) < 0) n = -n;
                float c = saturate(dot(n, v));
                float fresnel = 0.04 + 0.96 * pow(1 - c, 5);
                float3 l = normalize(_RW_SunDir);
                float3 h = normalize(v + l);
                float power = exp2(_RW_Smoothness * 11 + 1);
                float glint = pow(saturate(dot(n, h)), power) * (power + 8) / 25.1327 * saturate(dot(n, l)) * fresnel * 4;
                return float4(fresnel, min(glint, 16) * _RW_SunLit, Dirt(i.local), 1);
            }

            targets Out(float a, float b, float state, float reason, v2f i)
            {
                targets t;
                t.id = float4(a, b, state, reason);
                t.surface = state > 0.75 ? Surface(i) : float4(0, 0, 0, 1);
                return t;
            }

            targets frag(v2f i)
            {
                // gradients outside any branch (the mask lookup happens inside one)
                float2 uvdx = ddx(i.uv), uvdy = ddy(i.uv);
                if (_RW_HasProbe < 0.5)
                    return Out(_RW_PartId, i.eye, 0, 1, i);
                if (_RW_MaskMode > 2.5)
                    return Out(_RW_PartId, i.eye, 0.5, 1, i);
                if (_RW_MaskMode > 1.5)
                    return Out(_RW_PartId, i.eye, 2, 3, i);
                if (_RW_MaskMode > 0.5)
                {
                    float m = tex2Dgrad(_RW_MaskTex, i.uv, uvdx, uvdy).r;
                    if (m >= 0.5)
                        return Out(_RW_PartId, i.eye, 2, 3, i);
                    return Out(_RW_PartId, i.eye, 0.5, m > 0.15 ? 2 : 1, i);
                }
                float3 q = mul(_RW_WorldToProbe, float4(i.world, 1)).xyz;
                float state = Openness(q) >= _RW_OpenFraction ? 1 : 0.5;
                return Out(_RW_PartId, i.eye, state, 1, i);
            }
            ENDCG
        }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            BlendOp Max
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            sampler2D _RW_GlassTex;
            float4 _RW_GlassST;
            float _RW_GlassChannel;
            sampler2D _RW_LightTex;
            float4 _RW_LightST;
            float _RW_HasLight;
            float4 _RW_UvOffset;   // tiled UVs: the mesh is drawn once per tile, shifted into 0..1

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float2 uv : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float2 uv = v.uv + _RW_UvOffset.xy;
                o.pos = float4(uv * 2 - 1, 0.5, 1);
                #if UNITY_UV_STARTS_AT_TOP
                o.pos.y = -o.pos.y;
                #endif
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.uv = uv;   // the texel written: gloss and lightmap read where the mask is
                return o;
            }

            float Gloss(float2 uv)
            {
                if (_RW_GlassChannel < 0.5) return 0;
                float4 t = tex2D(_RW_GlassTex, uv * _RW_GlassST.xy + _RW_GlassST.zw);
                if (_RW_GlassChannel < 1.5) return t.r;
                if (_RW_GlassChannel < 2.5) return t.g;
                if (_RW_GlassChannel < 3.5) return t.b;
                if (_RW_GlassChannel < 4.5) return t.a;
                return dot(t.rgb, float3(0.299, 0.587, 0.114));
            }

            float4 frag(v2f i) : SV_Target
            {
                float gloss = Gloss(i.uv);
                float3 l = tex2D(_RW_LightTex, i.uv * _RW_LightST.xy + _RW_LightST.zw).rgb;
                float light = _RW_HasLight > 0.5 ? dot(l, float3(0.299, 0.587, 0.114)) : 0;
                float3 q = mul(_RW_WorldToProbe, float4(i.world, 1)).xyz;
                return float4(gloss, Openness(q), 1, light);
            }
            ENDCG
        }
    }
}
