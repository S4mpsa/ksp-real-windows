// Shared by RW_Composite and RW_Cover: the inputs of the window decision and the
// decision itself (Classify), so the cut-outs and the composite always agree.
#ifndef RW_COMMON_INCLUDED
#define RW_COMMON_INCLUDED
#include "UnityCG.cginc"

sampler2D _RW_IdTex;
sampler2D _RW_FrontTex;
sampler2D _RW_InteriorTex;
sampler2D _RW_SurfaceTex;
sampler2D _RW_BehindTex;
float _RW_SeeThrough;
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
float _RW_HoleIds[128];   // 1 for parts whose painted windows get depth cut-outs (seeThroughHoles)
UNITY_DECLARE_DEPTH_TEXTURE(_RW_InteriorDepth);   // depth of the whole interior render: shell, props, kerbals
float _RW_HasInteriorDepth;

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

// Eye depth of the nearest interior surface of any kind in the colour render
// (shell, props, kerbals), 0 where it saw nothing or there is no such depth.
float InteriorEye(float2 uv)
{
    if (_RW_HasInteriorDepth < 0.5) return 0;
    float d = SAMPLE_DEPTH_TEXTURE(_RW_InteriorDepth, uv);
#if defined(UNITY_REVERSED_Z)
    if (d <= 1e-7) return 0;
#else
    if (d >= 1 - 1e-7) return 0;
#endif
    return LinearEyeDepth(d);
}

// x: 1 if uv is a window pixel of a rendered part, else 0
// y: 1 to show the interior image there
// z: 1 where painted glass needs the scene behind it (out through the far window)
// w: the part id
float4 Classify(float2 uv)
{
    float4 hull = tex2Dlod(_RW_IdTex, float4(uv, 0, 0));
    float2 front = tex2Dlod(_RW_FrontTex, float4(uv, 0, 0)).rg;

    if (hull.x < 0.5) return 0;                              // not a rendered part
    if (hull.z < 0.75) return 0;                             // hull point is not a window
    if (_RW_UseSceneDepth > 0.5)
    {
        float sd = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
        if (sd < hull.y - _RW_SceneEps) return 0;            // something in front (terrain...)
    }
    if (hull.z > 1.5 && _RW_SeeThrough > 0.5)
    {
        float4 b = tex2Dlod(_RW_BehindTex, float4(uv, 0, 0));
        // props and kerbals aren't in the front pass: one in front of the far window
        // (and of the next hull) keeps the interior there
        float ie = InteriorEye(uv);
        if (ie > 0 && (b.y <= 0 || ie <= b.y + _RW_HullEps) && (front.y <= 0 || ie < front.y - _RW_HullEps))
            return float4(1, 1, 0, hull.x);
        if (front.y > 0 && (b.y <= 0 || front.y <= b.y + _RW_HullEps))
            return Matches(hull.x, front.x) ? float4(1, 1, 0, hull.x) : 0;   // an interior surface comes first
        // out through the far window: another window drawn in the same render shows its interior
        if (b.x > 0.5 && b.z > 0.75 && front.y > 0 && Matches(hull.x, b.x) && Matches(b.x, front.x)
            && front.y >= b.y - _RW_HullEps)
            return float4(1, 1, 0, hull.x);
        return hull.w > 3.5 ? 0 : float4(1, 0, 1, hull.x);  // real glass: the scene is already there; painted: needs it
    }
    if (front.x > 0.5 && !Matches(hull.x, front.x)) return 0;   // interior belongs to a part not drawn with this one
    if (hull.z < 1.5)
    {
        if (front.y <= 0) return 0;                          // no interior along this ray
        if (front.y < hull.y - _RW_HullEps) return 0;        // interior pokes out of the hull
    }
    return float4(1, 1, 0, hull.x);
}
#endif
