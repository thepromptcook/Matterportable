// Port of MatterportCommon.hlsl (Unity package). Keep the two in sync.
// Values are set every frame by MatterportSpace.ApplyView (C#). Math runs in the Core/Unity frame
// (left-handed, Y up): to_core() mirrors Godot's Z. Image files have row 0 at the top in Godot, so file-backed
// textures (panoramas) flip V; the depth maps are written by C# in Core row order and do not.

uniform sampler2DArray pano_a : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
uniform sampler2DArray pano_b : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
uniform sampler2DArray depth_a : filter_nearest, repeat_disable;
uniform sampler2DArray depth_b : filter_nearest, repeat_disable;
uniform vec3 pos_a;            // Godot world position of sweep A
uniform vec3 pos_b;
uniform mat3 rot_a;            // WorldFromPano of sweep A (Core frame); l = d * rot_a is world -> panorama-local
uniform mat3 rot_b;
uniform vec4 face_right[6];    // per canonical face: right axis (xyz), source slice index (w)
uniform vec4 face_up[6];       // per canonical face: up axis (xyz)
uniform vec4 mp_state;         // x: blend A->B, y: projection amount, z: use depth, w: debug view
uniform vec4 mp_valid;         // x: pano A loaded, y: pano B loaded, z: clip height (world y)
uniform vec3 mp_background : source_color = vec3(0.11, 0.11, 0.12);

vec3 to_core(vec3 d) { return vec3(d.x, d.y, -d.z); }

int mp_face(vec3 l) {
	vec3 a = abs(l);
	if (a.x >= a.y && a.x >= a.z) return l.x >= 0.0 ? 0 : 1;
	if (a.y >= a.z) return l.y >= 0.0 ? 2 : 3;
	return l.z >= 0.0 ? 4 : 5;
}

vec3 mp_axis(int face) {
	vec3 v = vec3(0.0);
	v[face / 2] = (face % 2 == 0) ? 1.0 : -1.0;
	return v;
}

// Texture-array coordinate and analytic UV gradients (no seams at face edges), V flipped for Godot images.
void mp_pano_coords(vec3 l, vec3 ldx, vec3 ldy, out vec3 coord, out vec2 gx, out vec2 gy) {
	int face = mp_face(l);
	vec3 F = mp_axis(face);
	vec3 R = face_right[face].xyz;
	vec3 U = face_up[face].xyz;
	float m = max(dot(l, F), 1e-6);
	vec2 n = vec2(dot(l, R), dot(l, U));
	vec2 uv = 0.5 * n / m + 0.5;
	coord = vec3(uv.x, 1.0 - uv.y, face_right[face].w);
	float k = 0.5 / (m * m);
	gx = (vec2(dot(ldx, R), dot(ldx, U)) * m - n * dot(ldx, F)) * k;
	gy = (vec2(dot(ldy, R), dot(ldy, U)) * m - n * dot(ldy, F)) * k;
	gx.y = -gx.y;
	gy.y = -gy.y;
}

// A panorama sample, filtered with the analytic gradients (anisotropic) - except in a thin band along the face edges,
// where gradient sampling draws a dashed line along the seam (pixel quads there straddle two faces); an explicit level
// of detail from the same gradients does not. Measured: even isotropic gradients bring the line back.
vec3 mp_sample_pano(sampler2DArray pano, vec3 l) {
	vec3 coord;
	vec2 gx, gy;
	mp_pano_coords(l, dFdx(l), dFdy(l), coord, gx, gy);
	float size = float(textureSize(pano, 0).x), footprint = max(length(gx), length(gy)) * size;
	float edge = min(min(coord.x, 1.0 - coord.x), min(coord.y, 1.0 - coord.y)) * size;
	if (edge > 2.0 * footprint + 1.0) return textureGrad(pano, coord, gx, gy).rgb;
	return textureLod(pano, coord, max(log2(footprint), 0.0)).rgb;
}

// Canonical face coordinates for the space-aligned depth maps (CubeFaces.Canonical in C#).
vec3 mp_depth_coord(vec3 d) {
	vec3 a = abs(d);
	float ma;
	vec2 st;
	float face;
	if (a.x >= a.y && a.x >= a.z) {
		ma = a.x;
		if (d.x >= 0.0) { face = 0.0; st = vec2(-d.z, d.y); } else { face = 1.0; st = vec2(d.z, d.y); }
	} else if (a.y >= a.z) {
		ma = a.y;
		if (d.y >= 0.0) { face = 2.0; st = vec2(d.x, -d.z); } else { face = 3.0; st = vec2(d.x, d.z); }
	} else {
		ma = a.z;
		if (d.z >= 0.0) { face = 4.0; st = vec2(d.x, d.y); } else { face = 5.0; st = vec2(-d.x, d.y); }
	}
	return vec3(0.5 * st / max(ma, 1e-6) + 0.5, face);
}

float mp_visibility(float stored, float dist) {
	float bias = 0.05 + 0.03 * dist;
	return clamp(1.0 - (dist - stored - bias) / (0.5 * bias + 0.03), 0.0, 1.0);
}

// Percentage-closer filtering: the visibility test on the four nearest depth texels, blended bilinearly (bilinear
// depth would mix near and far depths at silhouettes and dot the outlines of objects).
float mp_depth_visibility(sampler2DArray depth, vec3 d) {
	vec3 c = mp_depth_coord(d);
	float size = float(textureSize(depth, 0).x), dist = length(d);
	vec2 t = c.xy * size - 0.5, f = fract(t), base = (floor(t) + 0.5) / size, step = vec2(1.0 / size, 0.0);
	float v00 = mp_visibility(textureLod(depth, vec3(base, c.z), 0.0).r, dist);
	float v10 = mp_visibility(textureLod(depth, vec3(base + step.xy, c.z), 0.0).r, dist);
	float v01 = mp_visibility(textureLod(depth, vec3(base + step.yx, c.z), 0.0).r, dist);
	float v11 = mp_visibility(textureLod(depth, vec3(base + step.xx, c.z), 0.0).r, dist);
	return mix(mix(v00, v10, f.x), mix(v01, v11, f.x), f.y);
}

void mp_blend_weights(float t, float vis_a, float vis_b, out float w_a, out float w_b) {
	w_a = (1.0 - t) * vis_a + t * (1.0 - vis_b) * vis_a;
	w_b = t * vis_b + (1.0 - t) * (1.0 - vis_a) * vis_b;
}
