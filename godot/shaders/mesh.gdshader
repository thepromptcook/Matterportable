// Port of MatterportMesh.shader: projects the current panorama(s) onto the scanned mesh (exactly the photo at a
// sweep, cross-faded during moves with occlusion awareness); the baked mesh texture in dollhouse/floor plan. Unlit.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;

#include "res://shaders/matterport_common.gdshaderinc"

uniform sampler2D mesh_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable, hint_default_black;
uniform bool cull_back = false;
// The space node's transform. Photos are projected from where a vertex was scanned (space_matrix * VERTEX), so a moved
// prop (drawn with its own MODEL_MATRIX) keeps its own pixels.
uniform mat4 space_matrix = mat4(1.0);
// Walk mode's highlight of the prop under the pointer, and the "Props" render view (alpha: how much).
instance uniform vec4 prop_tint = vec4(0.0);

varying vec3 wpos;

void vertex() {
	wpos = (space_matrix * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (wpos.y > mp_valid.z || (cull_back && !FRONT_FACING)) discard;
	vec3 tex = texture(mesh_tex, UV).rgb;

	vec3 dA = to_core(wpos - pos_a);
	vec3 dB = to_core(wpos - pos_b);
	vec3 lA = dA * rot_a;
	vec3 lB = dB * rot_b;

	vec3 cA = mp_sample_pano(pano_a, lA);
	vec3 cB = mp_sample_pano(pano_b, lB);

	float visA = mp_valid.x;
	float visB = mp_valid.y;
	if (mp_state.z > 0.5) {
		visA *= mp_depth_visibility(depth_a, dA);
		visB *= mp_depth_visibility(depth_b, dB);
	}

	float wA, wB;
	mp_blend_weights(mp_state.x, visA, visB, wA, wB);
	vec3 pano = (cA * wA + cB * wB) / max(wA + wB, 1e-5);
	float coverage = clamp(max(visA, visB), 0.0, 1.0);
	vec3 col = mix(tex, mix(tex, pano, coverage), mp_state.y);

	int view = int(mp_state.w + 0.5);
	if (view == 1) col = tex;
	else if (view == 2) col = pano;
	else if (view == 3) col = vec3(visA, visB, 0.0);
	else if (view == 4) col = vec3(wA, wB, 1.0 - coverage);
	ALBEDO = mix(col, prop_tint.rgb, prop_tint.a);
}
