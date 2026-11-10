// Depth cut-outs for painted windows (seeThroughWindows). One quad over each
// looked-through part's window area on screen, drawn by the main camera right before that part's hull
// (Renderer.sortingOrder: everything else first, then cut-out + hull per part, far
// to near). Where the window decision (Classify, shared with the composite) says this
// part's painted window looks out through its far window, it writes depth just in
// front of the pane and no colour: the hull fails there and the scene already drawn
// behind it stays. The composite then lays the glass over it.
//
// Known limit: effects drawn after the opaque pass from the depth buffer (Scatterer's
// atmosphere and sky, EVE's clouds, sun flares) don't appear through a cut-out; the
// sky there stays black. Putting the depth behind back (a depth-only render plus a
// restore draw) and clearing the deferred stencil there were tried and changed nothing.
Shader "Hidden/RealWindows/Cover"
{
    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" }

        CGINCLUDE
            #include "RW_Common.cginc"

            float _RW_CoverId;     // this cut-out's part
            float _RW_CoverEps;    // metres in front of the pane
            float4 _RW_CoverCam;   // the main camera's position and pixel width: no cut-outs in any other camera
            float4 _RW_CoverRect;  // viewport area covered (x0, y0, x1, y1): this part's windows

            struct appc
            {
                float4 vertex : POSITION;
            };

            struct v2c
            {
                float4 pos : SV_POSITION;
                float4 spos : TEXCOORD0;
            };

            v2c vertCover(appc v)
            {
                v2c o;
                // unit quad onto the viewport area; clip y follows the target's flip so
                // ComputeScreenPos gives back the same viewport position
                float2 v01 = lerp(_RW_CoverRect.xy, _RW_CoverRect.zw, v.vertex.xy);
                o.pos = float4(v01.x * 2 - 1, (v01.y * 2 - 1) * _ProjectionParams.x, 0.5, 1.0);
                o.spos = ComputeScreenPos(o.pos);
                return o;
            }

            // depth only, no colour output at all: in deferred four targets are bound (the
            // fourth is the camera image), and a returned colour could land in any of them
            void fragCover(v2c i, out float depth : SV_Depth)
            {
                if (distance(_WorldSpaceCameraPos, _RW_CoverCam.xyz) > 0.05 || abs(_ScreenParams.x - _RW_CoverCam.w) > 0.5)
                    discard;
                float2 uv = i.spos.xy / i.spos.w;
                if (_RW_FlipY > 0.5) uv.y = 1 - uv.y;
                float4 hull = tex2Dlod(_RW_IdTex, float4(uv, 0, 0));
                if (abs(hull.x - _RW_CoverId) > 0.5)
                    discard;
                float4 c = Classify(uv);
                if (c.z < 0.5)
                    discard;
                float eye = max(hull.y - _RW_CoverEps, _ProjectionParams.y * 1.01);
                depth = (1.0 / eye - _ZBufferParams.w) / _ZBufferParams.z;
                return;
            }
        ENDCG

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            ZWrite On
            ZTest LEqual
            Cull Off
            ColorMask 0

            CGPROGRAM
            #pragma vertex vertCover
            #pragma fragment fragCover
            #pragma target 3.5
            ENDCG
        }

        Pass
        {
            Tags { "LightMode" = "Deferred" }
            ZWrite On
            ZTest LEqual
            Cull Off
            ColorMask 0

            CGPROGRAM
            #pragma vertex vertCover
            #pragma fragment fragCover
            #pragma target 3.5
            #pragma exclude_renderers nomrt
            ENDCG
        }
    }
}
