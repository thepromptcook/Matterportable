// Port of MatterportCommon.hlsl / matterport_common.gdshaderinc and the mesh, sky and marker shaders. Keep in sync.
// Math runs in the Core frame (toCore mirrors three's Z). Photos are decoded flipped (imageOrientation: 'flipY'),
// so V is bottom-up as in Core and Unity. Each panorama is a six-layer texture array (WebGL has 16 texture units: six
// 2D textures per photo would leave no room for the clean photos); depth maps are cube render targets sampled by
// three.js world direction.
export const common = `
layout(location = 0) out highp vec4 mpColor;
uniform highp sampler2DArray panoA;
uniform highp sampler2DArray panoB;
// The same sweeps' clean photos (furniture painted out), for the room behind moved props and "empty rooms", and which
// prop each clean pixel had painted out (R8, point-sampled: prop + 1).
uniform highp sampler2DArray cleanA;
uniform highp sampler2DArray cleanB;
uniform highp sampler2DArray paintedA;
uniform highp sampler2DArray paintedB;
uniform vec3 posA;          // three world position of sweep A
uniform vec3 posB;
uniform mat3 rotA;          // WorldFromPano of sweep A (Core frame): l = d * rotA is world -> panorama-local
uniform mat3 rotB;
uniform vec4 faceRight[6];  // per canonical face: right axis (xyz), source slice index (w)
uniform vec4 faceUp[6];
uniform vec4 mpState;       // x: blend A->B, y: projection amount, z: use depth, w: debug view
uniform vec4 mpValid;       // x: pano A loaded, y: pano B loaded, z: clip height (world y)
uniform vec4 mpClean;       // x: clean A bound, y: clean B bound, z: empty rooms (the room shows them everywhere)
uniform vec3 mpBackground;
uniform vec4 movedProps[64]; // 1 for each prop that has moved (prop i: element i / 4, component i % 4)

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

// Texture-array coordinate (u, v, slice) and analytic UV gradients for a panorama-local direction (no seams at face
// edges).
void mpPanoCoords(vec3 l, vec3 ldx, vec3 ldy, out vec3 coord, out vec2 gx, out vec2 gy) {
  int face = mpFace(l);
  vec3 F = mpAxis(face);
  vec3 R = faceRight[face].xyz;
  vec3 U = faceUp[face].xyz;
  float m = max(dot(l, F), 1e-6);
  vec2 n = vec2(dot(l, R), dot(l, U));
  coord = vec3(0.5 * n / m + 0.5, faceRight[face].w);
  float k = 0.5 / (m * m);
  gx = (vec2(dot(ldx, R), dot(ldx, U)) * m - n * dot(ldx, F)) * k;
  gy = (vec2(dot(ldy, R), dot(ldy, U)) * m - n * dot(ldy, F)) * k;
}

// A panorama sample, filtered with the analytic gradients (anisotropic) - except in a thin band along the face edges,
// where gradient sampling draws a dashed line along the seam (pixel quads there straddle two faces); there, an explicit
// level of detail from the same gradients. ldx, ldy: the direction's screen derivatives, taken outside any branch.
vec3 mpSamplePano(highp sampler2DArray pano, vec3 l, vec3 ldx, vec3 ldy) {
  vec3 coord;
  vec2 gx, gy;
  mpPanoCoords(l, ldx, ldy, coord, gx, gy);
  float size = float(textureSize(pano, 0).x), footprint = max(length(gx), length(gy)) * size;
  float edge = min(min(coord.x, 1.0 - coord.x), min(coord.y, 1.0 - coord.y)) * size;
  if (edge > 2.0 * footprint + 1.0) return textureGrad(pano, coord, gx, gy).rgb;
  return textureLod(pano, coord, max(log2(footprint), 0.0)).rgb;
}

vec3 mpSamplePanoLod(highp sampler2DArray pano, vec3 l, float lod) {
  vec3 coord;
  vec2 gx, gy;
  mpPanoCoords(l, vec3(0.0), vec3(0.0), coord, gx, gy);
  return textureLod(pano, coord, lod).rgb;
}

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

float mpMoved(float piece) {
  int i = int(piece + 0.5) - 1;
  if (i < 0 || i >= 256) return 0.0;
  vec4 v = movedProps[i / 4];
  int c = i % 4;
  return c == 0 ? v.x : c == 1 ? v.y : c == 2 ? v.z : v.w;
}

// A depth texel (distance, piece seen) hides the point when it shows a moved prop other than the piece drawn: the
// surfaces the prop stood in front of were never photographed.
bool mpHides(vec2 texel, float piece) {
  return abs(texel.y - piece) > 0.5 && mpMoved(texel.y) > 0.5;
}

// Whether the clean photos painted out a moved prop at this panorama-local direction (0 where nothing was painted). The
// photos show a prop wider than the scan does (thin legs and backs), so this, not the depth test, says where the clean
// photo replaces the photo.
bool mpPaintedMoved(highp sampler2DArray painted, vec3 l) {
  vec3 coord;
  vec2 gx, gy;
  mpPanoCoords(l, vec3(0.0), vec3(0.0), coord, gx, gy);
  return mpMoved(floor(textureLod(painted, coord, 0.0).r * 255.0 + 0.5)) > 0.5;
}

// Percentage-closer filtering, as MP_DepthVisibility in Unity and Godot: the visibility test on the four nearest texels
// of the depth cube (point-sampled), blended bilinearly. w: the direction from the sweep in three's frame; piece: what
// is being drawn (0 the room, i + 1 prop i). -1 when a moved prop hid the point (any of the four texels).
float mpDepthVisibility(samplerCube depth, vec3 w, float dist, float piece) {
  vec3 a = abs(w);
  int face; float ma; vec2 sc;
  if (a.x >= a.y && a.x >= a.z) { face = w.x >= 0.0 ? 0 : 1; ma = a.x; sc = vec2(w.x >= 0.0 ? -w.z : w.z, -w.y); }
  else if (a.y >= a.z) { face = w.y >= 0.0 ? 2 : 3; ma = a.y; sc = vec2(w.x, w.y >= 0.0 ? w.z : -w.z); }
  else { face = w.z >= 0.0 ? 4 : 5; ma = a.z; sc = vec2(w.z >= 0.0 ? w.x : -w.x, -w.y); }
  float size = float(textureSize(depth, 0).x);
  vec2 t = (0.5 * sc / ma + 0.5) * size - 0.5, f = fract(t), base = (floor(t) + 0.5) / size * 2.0 - 1.0;
  vec2 step = vec2(2.0 / size, 0.0);
  vec2 t00 = textureLod(depth, mpCubeDir(face, base), 0.0).rg, t10 = textureLod(depth, mpCubeDir(face, base + step.xy), 0.0).rg;
  vec2 t01 = textureLod(depth, mpCubeDir(face, base + step.yx), 0.0).rg, t11 = textureLod(depth, mpCubeDir(face, base + step.xx), 0.0).rg;
  if (mpHides(t00, piece) || mpHides(t10, piece) || mpHides(t01, piece) || mpHides(t11, piece)) return -1.0;
  return mix(mix(mpVisibility(t00.x, dist), mpVisibility(t10.x, dist), f.x), mix(mpVisibility(t01.x, dist), mpVisibility(t11.x, dist), f.x), f.y);
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
uniform float propId;       // which piece this is: 0 the room, i + 1 prop i (the depth cubes record the piece seen)
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
  vec3 lA = dA * rotA, lB = dB * rotB;
  vec3 lAdx = dFdx(lA), lAdy = dFdy(lA), lBdx = dFdx(lB), lBdy = dFdy(lB);
  // The depth test: -1 where a moved (or hidden) prop hid the point from that sweep.
  float a = 1.0, b = 1.0;
  if (mpState.z > 0.5) {
    a = mpDepthVisibility(depthA, vWorld - posA, length(dA), propId);
    b = mpDepthVisibility(depthB, vWorld - posB, length(dB), propId);
  }
  // The room shows the clean photos (furniture painted out) where a moved prop was painted out or hid it, and everywhere
  // in "empty rooms".
  bool room = propId < 0.5;
  bool useCleanA = room && mpClean.x > 0.5 && (a < 0.0 || mpClean.z > 0.5 || mpPaintedMoved(paintedA, lA));
  bool useCleanB = room && mpClean.y > 0.5 && (b < 0.0 || mpClean.z > 0.5 || mpPaintedMoved(paintedB, lB));
  vec3 cA = useCleanA ? mpSamplePano(cleanA, lA, lAdx, lAdy) : mpSamplePano(panoA, lA, lAdx, lAdy);
  vec3 cB = useCleanB ? mpSamplePano(cleanB, lB, lBdx, lBdy) : mpSamplePano(panoB, lB, lBdx, lBdy);
  float visA = mpValid.x * (a < 0.0 ? (useCleanA ? 1.0 : 0.0) : a);
  float visB = mpValid.y * (b < 0.0 ? (useCleanB ? 1.0 : 0.0) : b);
  bool holeA = a < 0.0 && !useCleanA; // no clean photo: fill from the photo's surroundings
  float wA, wB;
  mpBlendWeights(mpState.x, visA, visB, wA, wB);
  vec3 pano = (cA * wA + cB * wB) / max(wA + wB, 1e-5);
  float coverage = clamp(max(visA, visB), 0.0, 1.0);
  // Where a moved prop hid this from the photo, fill from the photo's surroundings rather than the baked texture: the
  // nearest directions (rings of 8, 2° to 30° away) where the sweep saw something else, averaged at a coarse level.
  vec3 fallback = tex;
  if (holeA && coverage < 1.0) {
    vec3 n = normalize(vWorld - posA);
    vec3 u = normalize(cross(n, abs(n.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
    vec3 v = cross(n, u), sum = vec3(0.0);
    float found = 0.0;
    for (int ring = 1; ring <= 7 && found == 0.0; ring++) {
      float r = 0.012 * float(ring * ring) + 0.02;
      for (int k = 0; k < 8; k++) {
        float a = float(k) * 0.7853982 + float(ring) * 0.39;
        vec3 dir = n + (u * cos(a) + v * sin(a)) * r;
        if (mpHides(textureLod(depthA, dir, 0.0).rg, propId)) continue;
        sum += mpSamplePanoLod(panoA, toCore(dir) * rotA, 3.0);
        found += 1.0;
      }
    }
    if (found > 0.0) fallback = sum / found;
  }
  vec3 col = mix(tex, mix(fallback, pano, coverage), mpState.y);
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

// Background where there is no mesh: the current panorama(s) by view direction, flat colour from outside. Through the
// holes a moved prop leaves in the scan, the clean photos, like the room's.
export const skyFragment = `
${common}
varying vec3 vWorld;
void main() {
  vec3 d = toCore(normalize(vWorld - cameraPosition));
  vec3 lA = d * rotA, lB = d * rotB;
  vec3 lAdx = dFdx(lA), lAdy = dFdy(lA), lBdx = dFdx(lB), lBdy = dFdy(lB);
  bool useCleanA = mpClean.x > 0.5 && (mpClean.z > 0.5 || mpPaintedMoved(paintedA, lA));
  bool useCleanB = mpClean.y > 0.5 && (mpClean.z > 0.5 || mpPaintedMoved(paintedB, lB));
  vec3 cA = useCleanA ? mpSamplePano(cleanA, lA, lAdx, lAdy) : mpSamplePano(panoA, lA, lAdx, lAdy);
  vec3 cB = useCleanB ? mpSamplePano(cleanB, lB, lBdx, lBdy) : mpSamplePano(panoB, lB, lBdx, lBdy);
  float wA, wB;
  mpBlendWeights(mpState.x, mpValid.x, mpValid.y, wA, wB);
  vec3 pano = (cA * wA + cB * wB) / max(wA + wB, 1e-5);
  float coverage = clamp(max(mpValid.x, mpValid.y), 0.0, 1.0) * mpState.y;
  mpColor = vec4(mix(mpBackground, pano, coverage), 1.0);
}`;

// Distance from a sweep to the first surface, and the piece it belongs to, rendered into a cube map for the occlusion
// test (each piece is drawn with its own pieceId).
export const depthFragment = `
layout(location = 0) out highp vec4 mpColor;
uniform vec3 sweepPos;
uniform float pieceId;
varying vec3 vWorld;
void main() { mpColor = vec4(length(vWorld - sweepPos), pieceId, 0.0, 1.0); }`;

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
