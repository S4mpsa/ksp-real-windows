// Replacement shader for the depth-behind render (experiment, skyDepthFix 3/4): the scene
// without the cut-out parts' hulls, depth only, so the sky depth fix-up can put the real
// depth of what lies behind a cut-out back into the flight camera's depth. Opaque and
// alpha-tested objects only (Camera.RenderWithShader, tag "RenderType"); everything else
// (sky, clouds, particles, glass) is left out.
Shader "Hidden/RealWindows/BehindDepth"
{
    Properties
    {
        _MainTex ("", 2D) = "white" {}
        _Cutoff ("", Float) = 0.5
        _Color ("", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Back
            ColorMask 0

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 vert(float4 v : POSITION) : SV_POSITION
            {
                return UnityObjectToClipPos(v);
            }

            fixed4 frag() : SV_Target
            {
                return 0;
            }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Back
            ColorMask 0

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Cutoff;
            fixed4 _Color;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                clip(tex2D(_MainTex, i.uv).a * _Color.a - _Cutoff);
                return 0;
            }
            ENDCG
        }
    }
}
