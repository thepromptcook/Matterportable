// Port of MatterportMarker.shader: a ring with a soft fill, lying on a surface (sweep markers and the cursor).
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;

uniform vec4 color : source_color = vec4(1.0);
uniform float inner = 0.72;
uniform float fill = 0.22;

void fragment() {
	float r = length(UV * 2.0 - 1.0);
	float aa = max(fwidth(r), 1e-4) * 1.5;
	float ring = smoothstep(inner - aa, inner, r) * (1.0 - smoothstep(1.0 - aa, 1.0, r));
	float f = (1.0 - smoothstep(inner - aa, inner, r)) * fill;
	ALBEDO = color.rgb;
	ALPHA = color.a * max(ring, f);
}
