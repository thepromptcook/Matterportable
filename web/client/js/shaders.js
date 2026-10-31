// Port of MatterportCommon.hlsl / matterport_common.gdshaderinc and the mesh, sky and marker shaders. Keep in sync.
// Math runs in the Core frame (toCore mirrors three's Z). Photos are decoded flipped (imageOrientation: 'flipY'),
// so V is bottom-up as in Core and Unity. Each panorama is six 2D textures (sampler arrays can't be indexed at run
// time in WebGL); depth maps are cube render targets sampled by three.js world direction.
const faceSamplers = (p) => [0, 1, 2, 3, 4, 5].map((i) => `uniform sampler2D pano${p}${i};`).join('\n');
const faceSwitch = (p) => `vec3 samplePano${p}(int s, vec2 uv, vec2 gx, vec2 gy) {
  if (s == 0) return textureGrad(pano${p}0, uv, gx, gy).rgb;
  if (s == 1) return textureGrad(pano${p}1, uv, gx, gy).rgb;
  if (s == 2) return textureGrad(pano${p}2, uv, gx, gy).rgb;
  if (s == 3) return textureGrad(pano${p}3, uv, gx, gy).rgb;
  if (s == 4) return textureGrad(pano${p}4, uv, gx, gy).rgb;
  return textureGrad(pano${p}5, uv, gx, gy).rgb;
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

${faceSwitch('A')}
${faceSwitch('B')}

float mpVisibility(float stored, float dist) {
  float bias = 0.05 + 0.03 * dist;
  return clamp(1.0 - (dist - stored - bias) / (0.5 * bias + 0.03), 0.0, 1.0);
}

void mpBlendWeights(float t, float visA, float visB, out float wA, out float wB) {
  wA = (1.0 - t) * visA + t * (1.0 - visB) * visA;
  wB = t * visB + (1.0 - t) * (1.0 - visA) * visB;
}
`;

export const worldVertex = `
varying vec3 vWorld;
varying vec2 vUv;
void main() {
  vec4 w = modelMatrix * vec4(position, 1.0);
  vWorld = w.xyz;
  vUv = uv;
  gl_Position = projectionMatrix * viewMatrix * w;
}`;

// The scanned mesh: the current panorama(s) projected onto it, the baked texture in dollhouse/floor plan. Unlit.
export const meshFragment = `
${common}
uniform sampler2D meshTex;
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
  vec3 lA = dA * rotA;
  vec3 lB = dB * rotB;
  int s; vec2 uv, gx, gy;
  mpPanoCoords(lA, dFdx(lA), dFdy(lA), s, uv, gx, gy);
  vec3 cA = samplePanoA(s, uv, gx, gy);
  mpPanoCoords(lB, dFdx(lB), dFdy(lB), s, uv, gx, gy);
  vec3 cB = samplePanoB(s, uv, gx, gy);
  float visA = mpValid.x;
  float visB = mpValid.y;
  if (mpState.z > 0.5) {
    visA *= mpVisibility(texture(depthA, vWorld - posA).r, length(dA));
    visB *= mpVisibility(texture(depthB, vWorld - posB).r, length(dB));
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
  mpColor = vec4(col, 1.0);
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
  vec3 lA = d * rotA;
  vec3 lB = d * rotB;
  int s; vec2 uv, gx, gy;
  mpPanoCoords(lA, dFdx(lA), dFdy(lA), s, uv, gx, gy);
  vec3 cA = samplePanoA(s, uv, gx, gy);
  mpPanoCoords(lB, dFdx(lB), dFdy(lB), s, uv, gx, gy);
  vec3 cB = samplePanoB(s, uv, gx, gy);
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
