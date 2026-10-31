#ifndef MATTERPORTABLE_COMMON_INCLUDED
#define MATTERPORTABLE_COMMON_INCLUDED

// Global state written by Matterportable.PanoramaRenderer (Shader.SetGlobal*).
// Panoramas are 6-slice texture arrays in the source face order; depth maps are 6-slice arrays of
// distances from the sweep, using the canonical space-aligned faces (CubeFaces.Canonical in C#).

Texture2DArray _MP_PanoA;  SamplerState sampler_MP_PanoA;
Texture2DArray _MP_PanoB;  SamplerState sampler_MP_PanoB;
Texture2DArray _MP_DepthA; SamplerState sampler_MP_DepthA;
Texture2DArray _MP_DepthB; SamplerState sampler_MP_DepthB;

float4 _MP_PosA;        // xyz: world position of sweep A
float4 _MP_PosB;        // xyz: world position of sweep B
float4x4 _MP_RotA;      // world -> panorama-local rotation of sweep A
float4x4 _MP_RotB;      // world -> panorama-local rotation of sweep B
float4x4 _MP_SpaceRot;  // world -> space-local rotation (depth maps are space-aligned)
float4 _MP_FaceRight[6]; // per canonical face: right axis (xyz), source slice index (w)
float4 _MP_FaceUp[6];    // per canonical face: up axis (xyz)
float4 _MP_State;        // x: blend A->B, y: projection amount (1 inside, 0 dollhouse), z: use depth, w: debug view
float4 _MP_Valid;        // x: pano A loaded, y: pano B loaded, z: clip height (world y)
float4 _MP_Background;   // background colour outside the panoramas

// Major axis of a direction; face index matches SignedAxis (0 +X, 1 -X, 2 +Y, 3 -Y, 4 +Z, 5 -Z).
float3 MP_FaceAxis(float3 l, out int face)
{
    float3 a = abs(l);
    if (a.x >= a.y && a.x >= a.z) { face = l.x >= 0 ? 0 : 1; return float3(l.x >= 0 ? 1 : -1, 0, 0); }
    if (a.y >= a.z)               { face = l.y >= 0 ? 2 : 3; return float3(0, l.y >= 0 ? 1 : -1, 0); }
    face = l.z >= 0 ? 4 : 5;
    return float3(0, 0, l.z >= 0 ? 1 : -1);
}

// Texture-array coordinate (u, v, slice) and analytic UV gradients for a panorama-local direction.
// Gradients come from the (continuous) direction derivatives, so mip selection has no seams at face edges.
void MP_PanoCoords(float3 l, float3 ldx, float3 ldy, out float3 coord, out float2 gx, out float2 gy)
{
    int face;
    float3 F = MP_FaceAxis(l, face);
    float3 R = _MP_FaceRight[face].xyz;
    float3 U = _MP_FaceUp[face].xyz;
    float m = max(dot(l, F), 1e-6);
    float2 n = float2(dot(l, R), dot(l, U));
    coord = float3(0.5 * n / m + 0.5, _MP_FaceRight[face].w);
    float k = 0.5 / (m * m);
    gx = (float2(dot(ldx, R), dot(ldx, U)) * m - n * dot(ldx, F)) * k;
    gy = (float2(dot(ldy, R), dot(ldy, U)) * m - n * dot(ldy, F)) * k;
}

// Canonical face coordinates for the space-aligned depth maps.
float3 MP_DepthCoord(float3 d)
{
    float3 a = abs(d);
    float ma;
    float2 st;
    float face;
    if (a.x >= a.y && a.x >= a.z)
    {
        ma = a.x;
        if (d.x >= 0) { face = 0; st = float2(-d.z, d.y); } else { face = 1; st = float2(d.z, d.y); }
    }
    else if (a.y >= a.z)
    {
        ma = a.y;
        if (d.y >= 0) { face = 2; st = float2(d.x, -d.z); } else { face = 3; st = float2(d.x, d.z); }
    }
    else
    {
        ma = a.z;
        if (d.z >= 0) { face = 4; st = float2(d.x, d.y); } else { face = 5; st = float2(-d.x, d.y); }
    }
    return float3(0.5 * st / max(ma, 1e-6) + 0.5, face);
}

// 1 when the surface point is the first thing the sweep sees in that direction, fading to 0 when occluded.
float MP_Visibility(float stored, float dist)
{
    float bias = 0.05 + 0.03 * dist;
    return saturate(1.0 - (dist - stored - bias) / (0.5 * bias + 0.03));
}

// Blend weights for panoramas A and B: B's share grows with t, and each takes over where the other is occluded.
void MP_BlendWeights(float t, float visA, float visB, out float wA, out float wB)
{
    wA = (1 - t) * visA + t * (1 - visB) * visA;
    wB = t * visB + (1 - t) * (1 - visA) * visB;
}

#endif
