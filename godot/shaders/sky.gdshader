// Port of MatterportSkybox.shader: where there is no mesh (windows, outdoors) show the current panorama(s) by view
// direction, fading to a flat colour in dollhouse/floor plan.
shader_type sky;

#include "res://shaders/matterport_common.gdshaderinc"

void sky() {
	if (AT_CUBEMAP_PASS) {
		COLOR = mp_background; // radiance/reflections are unused (everything is unshaded)
	} else {
		vec3 d = to_core(EYEDIR);
		vec3 coord;
		vec2 gx, gy;
		mp_pano_coords(d * rot_a, vec3(0.0), vec3(0.0), coord, gx, gy);
		vec3 cA = textureLod(pano_a, coord, 0.0).rgb;
		mp_pano_coords(d * rot_b, vec3(0.0), vec3(0.0), coord, gx, gy);
		vec3 cB = textureLod(pano_b, coord, 0.0).rgb;
		float wA, wB;
		mp_blend_weights(mp_state.x, mp_valid.x, mp_valid.y, wA, wB);
		vec3 pano = (cA * wA + cB * wB) / max(wA + wB, 1e-5);
		float coverage = clamp(max(mp_valid.x, mp_valid.y), 0.0, 1.0) * mp_state.y;
		COLOR = mix(mp_background, pano, coverage);
	}
}
