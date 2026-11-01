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
float4x4 _MP_SpaceToWorld; // the space's transform: photos are projected from where a vertex was scanned (props move)
float4 _MP_FaceRight[6]; // per canonical face: right axis (xyz), source slice index (w)
float4 _MP_FaceUp[6];    // per canonical face: up axis (xyz)
float4 _MP_State;        // x: blend A->B, y: projection amount (1 inside, 0 dollhouse), z: use depth, w: debug view
float4 _MP_Valid;        // x: pano A loaded, y: pano B loaded, z: clip height (world y)
float4 _MP_Background;   // background colour outside the panoramas
float4 _MP_Moved[64];    // 1 for each prop that has moved (prop i: element i / 4, component i % 4)

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

// A panorama sample, filtered with the analytic gradients (anisotropic) - except in a thin band along the face edges,
// where gradient sampling draws a dashed line along the seam (pixel quads there straddle two faces); an explicit level
// of detail from the same gradients does not.
float3 MP_SamplePano(Texture2DArray pano, SamplerState s, float3 l, float3 ldx, float3 ldy)
{
    float3 coord;
    float2 gx, gy;
    MP_PanoCoords(l, ldx, ldy, coord, gx, gy);
    float size, height, slices;
    pano.GetDimensions(size, height, slices);
    float footprint = max(length(gx), length(gy)) * size;
    float edge = min(min(coord.x, 1 - coord.x), min(coord.y, 1 - coord.y)) * size;
    if (edge > 2 * footprint + 1) return pano.SampleGrad(s, coord, gx, gy).rgb;
    return pano.SampleLevel(s, coord, max(log2(footprint), 0)).rgb;
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

float MP_Moved(float piece)
{
    int i = (int)(piece + 0.5) - 1;
    if (i < 0 || i >= 256) return 0;
    float4 v = _MP_Moved[i >> 2];
    int c = i & 3;
    return c == 0 ? v.x : c == 1 ? v.y : c == 2 ? v.z : v.w;
}

// A depth texel (distance, piece seen) hides the point when it shows a moved prop other than the piece drawn: the
// surfaces the prop stood in front of were never photographed.
bool MP_Hides(float2 texel, float piece)
{
    return abs(texel.y - piece) > 0.5 && MP_Moved(texel.y) > 0.5;
}

// Percentage-closer filtering: the visibility test on the four nearest depth texels (point-sampled), blended
// bilinearly. Bilinear depth would mix near and far depths at silhouettes and dot the outlines of objects.
// s: the direction from the sweep in the space frame (the depth maps are space-aligned); piece: what is being drawn
// (0 the room, i + 1 prop i). -1 when a moved prop hid the point (any of the four texels, so the photo's pixels along
// the prop's outline go too).
float MP_DepthVisibility(Texture2DArray depth, SamplerState smp, float3 s, float dist, float piece)
{
    float3 c = MP_DepthCoord(s);
    float size, height, slices;
    depth.GetDimensions(size, height, slices);
    float2 t = c.xy * size - 0.5, f = frac(t), base = (floor(t) + 0.5) / size;
    float2 step = float2(1 / size, 0);
    float2 t00 = depth.SampleLevel(smp, float3(base, c.z), 0).rg, t10 = depth.SampleLevel(smp, float3(base + step.xy, c.z), 0).rg;
    float2 t01 = depth.SampleLevel(smp, float3(base + step.yx, c.z), 0).rg, t11 = depth.SampleLevel(smp, float3(base + step.xx, c.z), 0).rg;
    if (MP_Hides(t00, piece) || MP_Hides(t10, piece) || MP_Hides(t01, piece) || MP_Hides(t11, piece)) return -1;
    return lerp(lerp(MP_Visibility(t00.x, dist), MP_Visibility(t10.x, dist), f.x), lerp(MP_Visibility(t01.x, dist), MP_Visibility(t11.x, dist), f.x), f.y);
}

// What the photo shows around the hole a moved prop left: the nearest directions (rings of 8, from 2° to 30° away)
// where the sweep saw something else, averaged at a coarse level. A smear of the surroundings reads as empty floor and
// wall; the scan's baked texture there holds the prop. d: world direction from the sweep; worldToPano: its _MP_Rot.
float3 MP_HoleFill(Texture2DArray pano, SamplerState ps, Texture2DArray depth, SamplerState ds, float3 d, float4x4 worldToPano, float piece, out float found)
{
    float3 n = normalize(d);
    float3 u = normalize(cross(n, abs(n.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0)));
    float3 v = cross(n, u);
    float3 sum = 0;
    found = 0;
    for (int ring = 1; ring <= 7 && found == 0; ring++)
    {
        float r = 0.012 * ring * ring + 0.02;
        for (int k = 0; k < 8; k++)
        {
            float a = k * 0.7853982 + ring * 0.39;
            float3 dir = n + (u * cos(a) + v * sin(a)) * r;
            if (MP_Hides(depth.SampleLevel(ds, MP_DepthCoord(mul((float3x3)_MP_SpaceRot, dir)), 0).rg, piece)) continue;
            float3 coord;
            float2 gx, gy;
            MP_PanoCoords(mul((float3x3)worldToPano, dir), float3(0, 0, 0), float3(0, 0, 0), coord, gx, gy);
            sum += pano.SampleLevel(ps, coord, 3).rgb;
            found += 1;
        }
    }
    return found > 0 ? sum / found : float3(0, 0, 0);
}

// Blend weights for panoramas A and B: B's share grows with t, and each takes over where the other is occluded.
void MP_BlendWeights(float t, float visA, float visB, out float wA, out float wB)
{
    wA = (1 - t) * visA + t * (1 - visB) * visA;
    wB = t * visB + (1 - t) * (1 - visA) * visB;
}

#endif
