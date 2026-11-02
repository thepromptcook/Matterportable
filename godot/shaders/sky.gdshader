// Port of MatterportSkybox.shader: where there is no mesh (windows, outdoors) show the current panorama(s) by view
// direction, fading to a flat colour in dollhouse/floor plan. Through the holes a moved prop leaves in the scan, the
// clean photos (furniture painted out), like the room's.
shader_type sky;

#include "res://shaders/matterport_common.gdshaderinc"

void sky() {
	if (AT_CUBEMAP_PASS) {
		COLOR = mp_background; // radiance/reflections are unused (everything is unshaded)
	} else {
		vec3 d = to_core(EYEDIR), lA = d * rot_a, lB = d * rot_b;
		vec3 coord;
		vec2 gx, gy;
		mp_pano_coords(lA, vec3(0.0), vec3(0.0), coord, gx, gy);
		bool cleanA = mp_clean.x > 0.5 && (mp_clean.z > 0.5 || mp_painted_moved(painted_a, lA));
		vec3 cA = cleanA ? textureLod(clean_a, coord, 0.0).rgb : textureLod(pano_a, coord, 0.0).rgb;
		mp_pano_coords(lB, vec3(0.0), vec3(0.0), coord, gx, gy);
		bool cleanB = mp_clean.y > 0.5 && (mp_clean.z > 0.5 || mp_painted_moved(painted_b, lB));
		vec3 cB = cleanB ? textureLod(clean_b, coord, 0.0).rgb : textureLod(pano_b, coord, 0.0).rgb;
		float wA, wB;
		mp_blend_weights(mp_state.x, mp_valid.x, mp_valid.y, wA, wB);
		vec3 pano = (cA * wA + cB * wB) / max(wA + wB, 1e-5);
		float coverage = clamp(max(mp_valid.x, mp_valid.y), 0.0, 1.0) * mp_state.y;
		COLOR = mix(mp_background, pano, coverage);
	}
}
