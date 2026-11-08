// Probe bake: 1 / distance (m, internal space) from a view point to the NEAREST
// IVA surface in each direction. Max blending keeps the largest inverse, which
// is the nearest surface, so no depth buffer is needed; 0 stays "nothing there".
// Double-sided: seen from a view point, a wall blocks whichever way it faces.
Shader "Hidden/RealWindows/Probe"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
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
            #include "UnityCG.cginc"

            float4 _RW_ProbeCenter;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.world = mul(unity_ObjectToWorld, vertex).xyz;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(1.0 / max(length(i.world - _RW_ProbeCenter.xyz), 0.001), 0, 0, 1);
            }
            ENDCG
        }
    }
}
