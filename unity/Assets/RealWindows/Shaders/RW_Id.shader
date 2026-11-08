// Flat "which part / how far" shader for the interior front pass. Output:
// R = part id (global _RW_PartId, set per draw from the command buffer),
// G = linear eye depth in metres. The cull mode comes from the material
// (_RW_Cull).
Shader "Hidden/RealWindows/Id"
{
    Properties
    {
        _RW_Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Cull [_RW_Cull]
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _RW_PartId;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float eye : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.eye = -UnityObjectToViewPos(v.vertex).z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(_RW_PartId, i.eye, 0, 1);
            }
            ENDCG
        }
    }
}
