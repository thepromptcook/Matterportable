// Port of MatterportCommon.hlsl / matterport_common.gdshaderinc and the mesh, sky and marker shaders. Keep in sync.
// Math runs in the Core frame (toCore mirrors three's Z). Photos are decoded flipped (imageOrientation: 'flipY'),
// so V is bottom-up as in Core and Unity. Each panorama is six 2D textures (sampler arrays can't be indexed at run
// time in WebGL); depth maps are cube render targets sampled by three.js world direction.
const faceSamplers = (p) => [0, 1, 2, 3, 4, 5].map((i) => `uniform sampler2D pano${p}${i};`).join('\n');
// samplePanoA/B(l): the panorama's colour in the panorama-local direction l (see mpSeamLod).
const faceSample = (p, i) => `lod < 0.0 ? textureGrad(pano${p}${i}, uv, gx, gy).rgb : textureLod(pano${p}${i}, uv, lod).rgb`;
const faceSwitch = (p) => `vec3 samplePano${p}(vec3 l) {
  int s; vec2 uv, gx, gy;
  mpPanoCoords(l, dFdx(l), dFdy(l), s, uv, gx, gy);
  float lod = mpSeamLod(uv, gx, gy, float(textureSize(pano${p}0, 0).x));
  if (s == 0) return ${faceSample(p, 0)};
  if (s == 1) return ${faceSample(p, 1)};
  if (s == 2) return ${faceSample(p, 2)};
  if (s == 3) return ${faceSample(p, 3)};
  if (s == 4) return ${faceSample(p, 4)};
  return ${faceSample(p, 5)};
}`;

export const common = `
layout(location = 0) out highp vec4 mpColor;
${faceSamplers('A')}
${faceSamplers('B')}
uniform vec3 posA;          // three world position of sweep A
uniform vec3 posB;
uniform mat3 rotA;          // WorldFromPano of sweep A (Core frame): l = d * rotA is world -> panorama-local
uniform mat3 rotB;
uniform vec4 faceRight[6];  // per canonical face: right axis (xyz), source slice index (w)
uniform vec4 faceUp[6];
uniform vec4 mpState;       // x: blend A->B, y: projection amount, z: use depth, w: debug view
uniform vec4 mpValid;       // x: pano A loaded, y: pano B loaded, z: clip height (world y)
uniform vec3 mpBackground;

vec3 toCore(vec3 d) { return vec3(d.x, d.y, -d.z); }

int mpFace(vec3 l) {
  vec3 a = abs(l);
  if (a.x >= a.y && a.x >= a.z) return l.x >= 0.0 ? 0 : 1;
  if (a.y >= a.z) return l.y >= 0.0 ? 2 : 3;
  return l.z >= 0.0 ? 4 : 5;
}

vec3 mpAxis(int face) {
  vec3 v = vec3(0.0);
  v[face / 2] = (face % 2 == 0) ? 1.0 : -1.0;
  return v;
}

// Face slice, UV and analytic UV gradients for a panorama-local direction (no seams at face edges).
void mpPanoCoords(vec3 l, vec3 ldx, vec3 ldy, out int slice, out vec2 uv, out vec2 gx, out vec2 gy) {
  int face = mpFace(l);
  vec3 F = mpAxis(face);
  vec3 R = faceRight[face].xyz;
  vec3 U = faceUp[face].xyz;
  float m = max(dot(l, F), 1e-6);
  vec2 n = vec2(dot(l, R), dot(l, U));
  uv = 0.5 * n / m + 0.5;
  slice = int(faceRight[face].w + 0.5);
  float k = 0.5 / (m * m);
  gx = (vec2(dot(ldx, R), dot(ldx, U)) * m - n * dot(ldx, F)) * k;
  gy = (vec2(dot(ldy, R), dot(ldy, U)) * m - n * dot(ldy, F)) * k;
}

// Panoramas are filtered with the analytic gradients (anisotropic, lod -1) - except in a thin band along the face
// edges, where gradient sampling draws a dashed line along the seam (pixel quads there straddle two faces); there, an
// explicit level of detail from the same gradients.
float mpSeamLod(vec2 uv, vec2 gx, vec2 gy, float size) {
  float footprint = max(length(gx), length(gy)) * size;
  float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y)) * size;
  return edge > 2.0 * footprint + 1.0 ? -1.0 : max(log2(footprint), 0.0);
}

${faceSwitch('A')}
${faceSwitch('B')}

float mpVisibility(float stored, float dist) {
  float bias = 0.05 + 0.03 * dist;
  return clamp(1.0 - (dist - stored - bias) / (0.5 * bias + 0.03), 0.0, 1.0);
}

// A direction for cube-map face coordinates sc in [-1, 1], in GL's face layout (how the hardware looks directions up).
vec3 mpCubeDir(int face, vec2 sc) {
  if (face == 0) return vec3(1.0, -sc.y, -sc.x);
  if (face == 1) return vec3(-1.0, -sc.y, sc.x);
  if (face == 2) return vec3(sc.x, 1.0, sc.y);
  if (face == 3) return vec3(sc.x, -1.0, -sc.y);
  if (face == 4) return vec3(sc.x, -sc.y, 1.0);
  return vec3(-sc.x, -sc.y, -1.0);
}

// Percentage-closer filtering, as MP_DepthVisibility in Unity and Godot: the visibility test on the four nearest texels
// of the depth cube (point-sampled), blended bilinearly. w: the direction from the sweep in three's frame.
float mpDepthVisibility(samplerCube depth, vec3 w, float dist) {
  vec3 a = abs(w);
  int face; float ma; vec2 sc;
  if (a.x >= a.y && a.x >= a.z) { face = w.x >= 0.0 ? 0 : 1; ma = a.x; sc = vec2(w.x >= 0.0 ? -w.z : w.z, -w.y); }
  else if (a.y >= a.z) { face = w.y >= 0.0 ? 2 : 3; ma = a.y; sc = vec2(w.x, w.y >= 0.0 ? w.z : -w.z); }
  else { face = w.z >= 0.0 ? 4 : 5; ma = a.z; sc = vec2(w.z >= 0.0 ? w.x : -w.x, -w.y); }
  float size = float(textureSize(depth, 0).x);
  vec2 t = (0.5 * sc / ma + 0.5) * size - 0.5, f = fract(t), base = (floor(t) + 0.5) / size * 2.0 - 1.0;
  vec2 step = vec2(2.0 / size, 0.0);
  float v00 = mpVisibility(textureLod(depth, mpCubeDir(face, base), 0.0).r, dist);
  float v10 = mpVisibility(textureLod(depth, mpCubeDir(face, base + step.xy), 0.0).r, dist);
  float v01 = mpVisibility(textureLod(depth, mpCubeDir(face, base + step.yx), 0.0).r, dist);
  float v11 = mpVisibility(textureLod(depth, mpCubeDir(face, base + step.xx), 0.0).r, dist);
  return mix(mix(v00, v10, f.x), mix(v01, v11, f.x), f.y);
}

void mpBlendWeights(float t, float visA, float visB, out float wA, out float wB) {
  wA = (1.0 - t) * visA + t * (1.0 - visB) * visA;
  wB = t * visB + (1.0 - t) * (1.0 - visA) * visB;
}
`;

// Photos are projected from where a vertex was scanned (spaceMatrix: the space's transform), so a moved prop - drawn
// with its own modelMatrix - keeps its own pixels.
export const worldVertex = `
uniform mat4 spaceMatrix;
varying vec3 vWorld;
varying vec2 vUv;
void main() {
  vWorld = (spaceMatrix * vec4(position, 1.0)).xyz;
  vUv = uv;
  gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(position, 1.0);
}`;

// The scanned mesh: the current panorama(s) projected onto it, the baked texture in dollhouse/floor plan. Unlit.
export const meshFragment = `
${common}
uniform sampler2D meshTex;
uniform vec4 propTint;      // Walk mode: the prop under the pointer, or every prop in the "Props" view (alpha: how much)
uniform samplerCube depthA;
uniform samplerCube depthB;
uniform bool cullBack;
varying vec3 vWorld;
varying vec2 vUv;
void main() {
  // Core triangle order is clockwise from the front once Z is mirrored; three.js calls counter-clockwise front.
  if (vWorld.y > mpValid.z || (cullBack && gl_FrontFacing)) discard;
  vec3 tex = texture(meshTex, vUv).rgb;
  vec3 dA = toCore(vWorld - posA);
  vec3 dB = toCore(vWorld - posB);
  vec3 cA = samplePanoA(dA * rotA);
  vec3 cB = samplePanoB(dB * rotB);
  float visA = mpValid.x;
  float visB = mpValid.y;
  if (mpState.z > 0.5) {
    visA *= mpDepthVisibility(depthA, vWorld - posA, length(dA));
    visB *= mpDepthVisibility(depthB, vWorld - posB, length(dB));
  }
  float wA, wB;
  mpBlendWeights(mpState.x, visA, visB, wA, wB);
  vec3 pano = (cA * wA + cB * wB) / max(wA + wB, 1e-5);
  float coverage = clamp(max(visA, visB), 0.0, 1.0);
  vec3 col = mix(tex, mix(tex, pano, coverage), mpState.y);
  int view = int(mpState.w + 0.5);
  if (view == 1) col = tex;
  else if (view == 2) col = pano;
  else if (view == 3) col = vec3(visA, visB, 0.0);
  else if (view == 4) col = vec3(wA, wB, 1.0 - coverage);
  mpColor = vec4(mix(col, propTint.rgb, propTint.a), 1.0);
}`;

// The sky box sits on the far plane (z = w), so it is drawn after the mesh and only where nothing covers it.
export const skyVertex = `
varying vec3 vWorld;
void main() {
  vec4 w = modelMatrix * vec4(position, 1.0);
  vWorld = w.xyz;
  gl_Position = (projectionMatrix * viewMatrix * w).xyww;
}`;

// Background where there is no mesh: the current panorama(s) by view direction, flat colour from outside.
export const skyFragment = `
${common}
varying vec3 vWorld;
void main() {
  vec3 d = toCore(normalize(vWorld - cameraPosition));
  vec3 cA = samplePanoA(d * rotA);
  vec3 cB = samplePanoB(d * rotB);
  float wA, wB;
  mpBlendWeights(mpState.x, mpValid.x, mpValid.y, wA, wB);
  vec3 pano = (cA * wA + cB * wB) / max(wA + wB, 1e-5);
  float coverage = clamp(max(mpValid.x, mpValid.y), 0.0, 1.0) * mpState.y;
  mpColor = vec4(mix(mpBackground, pano, coverage), 1.0);
}`;

// Distance from a sweep to the first surface, rendered into a cube map for the occlusion test.
export const depthFragment = `
layout(location = 0) out highp vec4 mpColor;
uniform vec3 sweepPos;
varying vec3 vWorld;
void main() { mpColor = vec4(length(vWorld - sweepPos), 0.0, 0.0, 1.0); }`;

// Sweep markers and the move cursor: a ring with a soft fill.
export const markerFragment = `
layout(location = 0) out highp vec4 mpColor;
uniform vec4 color;
uniform float inner;
uniform float fill;
varying vec2 vUv;
void main() {
  float r = length(vUv * 2.0 - 1.0);
  float aa = max(fwidth(r), 1e-4) * 1.5;
  float ring = smoothstep(inner - aa, inner, r) * (1.0 - smoothstep(1.0 - aa, 1.0, r));
  float f = (1.0 - smoothstep(inner - aa, inner, r)) * fill;
  mpColor = vec4(color.rgb, color.a * max(ring, f));
}`;
