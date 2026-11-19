// Depth cut-outs for painted windows (seeThroughWindows). One quad over each
// looked-through part's window area on screen, drawn by the main camera right before that part's hull
// (Renderer.sortingOrder). Where the window decision (Classify, shared with the composite) says this
// part's painted window looks out through its far window, it writes depth just in
// front of the pane and no colour: the hull fails there. The composite lays the glass over it.
//
// Two ways to use it (seeThroughDepth):
//
// Plain cut-outs: everything else first, then cut-out + hull per part, far to near.
// The scene already drawn behind the hull stays, but the depth left there is the
// pane's, so whatever is drawn after the opaque pass and looks at depth (Scatterer's
// sky, atmosphere and ocean, EVE's clouds, sun flares) takes the cut-out for a wall:
// the sky there stays black. Nothing reads the stencil for this.
//
// True holes: the looked-through parts are drawn first, near to far, while the depth
// behind them is still the cleared far plane: cut-out, hull, then this shader again
// as the re-open draw (_RW_CoverOpen, ZTest Always) over the same pixels, putting the
// far plane back. The rest of the scene then draws into a real hole, with its own
// colour and depth, and everything that comes later sees what is really there. In
// deferred this all happens in the G-buffer pass, before the depth texture is taken.
// In forward the depth texture comes from a pre-pass these quads are not in.
Shader "Hidden/RealWindows/Cover"
{
    Properties
    {
        _RW_CoverZTest ("ZTest", Float) = 4   // LEqual; the re-open material sets Always
    }

    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" }

        CGINCLUDE
            #include "RW_Common.cginc"

            float _RW_CoverId;     // this cut-out's part
            float _RW_CoverEps;    // metres in front of the pane
            float4 _RW_CoverCam;   // the main camera's position and pixel width: no cut-outs in any other camera
            float4 _RW_CoverRect;  // viewport area covered (x0, y0, x1, y1): this part's windows
            float _RW_CoverOpen;   // 1: the re-open draw, far plane over the cut-out's pixels

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
                if (_RW_CoverOpen > 0.5)
                {
                    // the same pixels the cut-out wrote (same decision, and nothing else
                    // has been drawn in front of this part yet): back to the cleared depth
                #if defined(UNITY_REVERSED_Z)
                    depth = 0.0;
                #else
                    depth = 1.0;
                #endif
                    return;
                }
                float eye = max(hull.y - _RW_CoverEps, _ProjectionParams.y * 1.01);
                depth = (1.0 / eye - _ZBufferParams.w) / _ZBufferParams.z;
                return;
            }
        ENDCG

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            ZWrite On
            ZTest [_RW_CoverZTest]
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
            ZTest [_RW_CoverZTest]
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
