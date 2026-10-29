#include "orb/shaders.hpp"

#include <QString>

#include "orb/led_math.hpp"

namespace orb::shaders {

namespace {

// Points in, y down; uViewport = width, height (points), pixels per point, unused; uTransform = scale about (y, z);
// uClip = clip radius around (y, z) of uTransform (points, 0: none), soft edge (points, 0: antialiased).
const char* kCommon = R"(#version 330 core
uniform vec4 uViewport;
uniform vec4 uTransform;
uniform vec4 uClip;
const float TAU = 6.28318530718;

float clipCoverage(vec2 canvas) {
    if (uClip.x <= 0.0) return 1.0;
    float d = length(canvas - uTransform.yz) - uClip.x;
    if (uClip.y > 0.0) return clamp(-d / uClip.y, 0.0, 1.0);
    return clamp(0.5 - d * uViewport.z, 0.0, 1.0);
}

vec4 clipPosition(vec2 point) {
    vec2 center = uTransform.yz;
    vec2 scaled = center + (point - center) * uTransform.x;
    return vec4(scaled.x / uViewport.x * 2.0 - 1.0, 1.0 - scaled.y / uViewport.y * 2.0, 0.0, 1.0);
}

vec2 rotate2(vec2 p, float angle) {
    float c = cos(angle);
    float s = sin(angle);
    return vec2(c * p.x - s * p.y, s * p.x + c * p.y);
}
)";

const char* kShapeVertex = R"(
layout(location = 0) in vec4 iA;
layout(location = 1) in vec4 iB;
layout(location = 2) in vec4 iColor;
layout(location = 3) in vec4 iColor2;
layout(location = 4) in vec4 iExtra;
layout(location = 5) in vec4 iExtra2;
out vec2 vLocal;
out vec2 vCanvas;
flat out vec4 vA;
flat out vec4 vB;
flat out vec4 vColor;
flat out vec4 vColor2;
flat out vec4 vExtra;
flat out vec4 vExtra2;

const vec2 corners[6] = vec2[6](vec2(-1, -1), vec2(1, -1), vec2(-1, 1), vec2(1, -1), vec2(1, 1), vec2(-1, 1));

// Bounds of an annular sector, so an arc's quad covers only the arc.
vec4 arcBounds(float radius, float halfWidth, float start, float sweep) {
    float outer = radius + halfWidth;
    if (sweep >= TAU - 0.001) return vec4(-outer, -outer, outer, outer);
    float inner = max(radius - halfWidth, 0.0);
    vec2 a = vec2(cos(start), sin(start));
    vec2 b = vec2(cos(start + sweep), sin(start + sweep));
    vec2 lo = min(min(a * outer, a * inner), min(b * outer, b * inner));
    vec2 hi = max(max(a * outer, a * inner), max(b * outer, b * inner));
    float base = start - TAU * floor(start / TAU);
    for (int k = 0; k < 8; k++) {
        float axis = float(k) * 1.5707963268;
        float rel = axis - base;
        if (rel >= 0.0 && rel <= sweep) {
            vec2 d = vec2(cos(axis), sin(axis)) * outer;
            lo = min(lo, d);
            hi = max(hi, d);
        }
    }
    return vec4(lo, hi);
}

void main() {
    float shape = iA.w;
    float glow = iB.w >= 0.0 ? iB.w * 3.2 : -iB.w * 7.0;
    float pad = 2.0 / uViewport.z;
    float extent;
    if (shape < 0.5) extent = length(iB.xy) + glow;
    else if (shape < 1.5) extent = max(iB.x, iB.z) + glow;
    else if (shape < 2.5) extent = iB.x + iB.y + glow;
    else if (shape < 3.5) extent = iB.x * 0.5 + iB.y + glow;
    else if (shape < 4.5) extent = iExtra.w;
    else if (shape > 6.5 && shape < 7.5) extent = max(iB.x, iB.y) + glow;
    else if (shape > 7.5 && shape < 8.5) extent = max(iB.x, iB.y) + max(iExtra.z, iB.z) + glow;
    else extent = iB.x + iB.y + glow;
    extent += pad;
    vec2 corner = corners[gl_VertexID];
    vec2 local = corner * extent;
    if (shape > 9.5 && shape < 10.5) {
        vec2 halfSize = 5.0 * max(iB.xy, iExtra.wz) + pad;
        local = rotate2(corner * halfSize, iA.z);
    }
    if (shape > 4.5 && shape < 6.5) {
        float apad = iB.y * 0.5 + glow + pad;
        vec4 box = arcBounds(iB.x, iB.y * 0.5, iExtra.z, iExtra.w);
        local = mix(box.xy - apad, box.zw + apad, corner * 0.5 + 0.5);
    }
    vLocal = local;
    vCanvas = iA.xy + local;
    vA = iA;
    vB = iB;
    vColor = iColor;
    vColor2 = iColor2;
    vExtra = iExtra;
    vExtra2 = iExtra2;
    gl_Position = clipPosition(iA.xy + local);
}
)";

const char* kShapeFragment = R"(
uniform sampler2D uPalette;
in vec2 vLocal;
in vec2 vCanvas;
flat in vec4 vA;
flat in vec4 vB;
flat in vec4 vColor;
flat in vec4 vColor2;
flat in vec4 vExtra;
flat in vec4 vExtra2;
out vec4 fragColor;

vec3 palette(float n) { return texture(uPalette, vec2(clamp(n, 0.0, 1.0) * (255.0 / 256.0) + 0.5 / 256.0, 0.5)).rgb; }

// iq's uneven capsule along +y: radius r1 at the origin, r2 at height h.
float unevenCapsule(vec2 p, float r1, float r2, float h) {
    p.x = abs(p.x);
    float b = (r1 - r2) / max(h, 0.0001);
    float a = sqrt(max(1.0 - b * b, 0.0));
    float k = dot(p, vec2(-b, a));
    if (k < 0.0) return length(p) - r1;
    if (k > a * h) return length(p - vec2(0.0, h)) - r2;
    return dot(p, vec2(a, b)) - r1;
}

void main() {
    vec2 p = vLocal;
    float keep = clipCoverage(vCanvas);
    float shape = vA.w;
    float aa = 1.0 / uViewport.z;
    vec4 color = vColor;
    float fill = vExtra2.x;
    float d;

    if (shape < 0.5) {
        vec2 q = rotate2(p, -vA.z);
        float r = vB.z;
        vec2 k = abs(q) - vB.xy + r;
        d = length(max(k, 0.0)) + min(max(k.x, k.y), 0.0) - r;
    } else if (shape < 1.5) {
        d = length(p) - vB.x;
        if (vB.z > 0.0) {
            float t = clamp((length(p) - vB.z) / (vB.x - vB.z), 0.0, 1.0);
            float alpha = color.a * t * t * (3.0 - 2.0 * t) * keep;
            if (alpha <= 0.0005) discard;
            fragColor = vec4(color.rgb * alpha, alpha);
            return;
        }
    } else if (shape < 2.5) {
        d = abs(length(p) - vB.x) - vB.y * 0.5;
    } else if (shape < 3.5) {
        vec2 direction = vec2(cos(vA.z), sin(vA.z));
        float halfLength = vB.x * 0.5;
        float t = clamp(dot(p, direction), -halfLength, halfLength);
        d = length(p - direction * t) - vB.y * 0.5;
        color = mix(vColor2, vColor, (t + halfLength) / max(vB.x, 0.0001));
    } else if (shape < 4.5) {
        float r = length(p);
        float inner = vExtra.z;
        float outer = vExtra.w;
        float t = clamp((r - inner) / max(outer - inner, 0.0001), 0.0, 1.0);
        float falloff = exp(-t * t * 4.2) * (1.0 - t * t);
        float edge = clamp((r - inner) / aa + 0.5, 0.0, 1.0);
        float alpha = color.a * falloff * edge * vExtra.x * keep;
        if (alpha <= 0.0005) discard;
        fragColor = vec4(color.rgb * alpha, alpha);
        return;
    } else if (shape < 5.5) {
        float r = length(p);
        float rel = atan(p.y, p.x) - vExtra.z;
        rel -= TAU * floor(rel / TAU);
        if (rel > vExtra.w) discard;
        float t = rel / vExtra.w;
        d = abs(r - vB.x) - vB.y * 0.5 * (0.3 + 0.7 * t);
        color = mix(vColor2, vColor, t);
        color.a *= pow(t, 1.4);
    } else if (shape < 6.5) {
        float r = length(p);
        float start = vExtra.z;
        float sweep = max(vExtra.w, 0.0001);
        float rel = atan(p.y, p.x) - start;
        rel -= TAU * floor(rel / TAU);
        float halfWidth = vB.y * 0.5;
        float t;
        if (rel <= sweep) {
            t = rel / sweep;
            d = abs(r - vB.x) - halfWidth;
        } else {
            float toEnd = rel - sweep;
            float toStart = TAU - rel;
            if (vB.z > 0.5) {
                vec2 ps = vB.x * vec2(cos(start), sin(start));
                vec2 pe = vB.x * vec2(cos(start + sweep), sin(start + sweep));
                float ds = length(p - ps) - halfWidth;
                float de = length(p - pe) - halfWidth;
                t = de < ds ? 1.0 : 0.0;
                d = min(ds, de);
            } else {
                t = toEnd < toStart ? 1.0 : 0.0;
                vec2 k = vec2(abs(r - vB.x) - halfWidth, min(toEnd, toStart) * r);
                d = length(max(k, 0.0)) + min(max(k.x, k.y), 0.0);
            }
        }
        if (fill > 2.5) {
            color = vec4(palette(mix(vExtra2.y, vExtra2.z, t)), vColor.a);
        } else {
            color = mix(vColor2, vColor, t);
        }
        color.a *= mix(vExtra2.w, 1.0, t);
        fill = 0.0;
    } else if (shape < 7.5) {
        // Ellipse with radii b.xy rotated by a.z (iq's approximation, exact enough near the edge).
        vec2 q = rotate2(p, -vA.z);
        vec2 radii = max(vB.xy, vec2(0.001));
        float k1 = length(q / radii);
        float k2 = length(q / (radii * radii));
        d = k1 * (k1 - 1.0) / max(k2, 0.00001);
    } else if (shape < 8.5) {
        // Needle blade: pivot at the center, tip at b.x along a.z (half width extra.z -> extra.w), counterweight b.y
        // behind (half width extra.z -> b.z).
        vec2 q = rotate2(p, -vA.z);
        float tip = unevenCapsule(vec2(q.y, q.x), vExtra.z, vExtra.w, vB.x);
        float tail = unevenCapsule(vec2(q.y, -q.x), vExtra.z, vB.z, vB.y);
        d = min(tip, tail);
    } else {
        // Flare: two crossed exponential lines.
        vec2 q = abs(rotate2(p, -vA.z));
        float v = exp(-q.x / max(vB.x, 0.0001) - q.y / max(vExtra.z, 0.0001))
                + exp(-q.y / max(vB.y, 0.0001) - q.x / max(vExtra.w, 0.0001));
        float alpha = min(1.0, color.a * v) * keep;
        if (alpha <= 0.0005) discard;
        fragColor = vec4(color.rgb * alpha, alpha);
        return;
    }

    if (fill > 0.5 && fill < 1.5) {
        vec2 direction = vec2(cos(vExtra2.y), sin(vExtra2.y));
        float halfLength = max(vExtra2.z, 0.0001);
        float t = clamp((dot(p, direction) + halfLength) / (2.0 * halfLength), 0.0, 1.0);
        color = mix(vColor, vColor2, t);
    } else if (fill > 1.5 && fill < 2.5) {
        float t = clamp(length(p - vExtra2.yz) / max(vExtra2.w, 0.0001), 0.0, 1.0);
        color = mix(vColor, vColor2, t);
    }

    float coverage = clamp(0.5 - d / aa, 0.0, 1.0);
    float core = coverage * vExtra.x;
    float glow = 0.0;
    if (vB.w > 0.0) {
        float x = max(d, 0.0) / vB.w;
        glow = exp(-x * x) * vExtra.y;
    } else if (vB.w < 0.0) {
        glow = exp(-max(d, 0.0) / -vB.w) * vExtra.y;
    }
    float alpha = (core + glow * (1.0 - core)) * color.a * keep;
    if (alpha <= 0.0005) discard;
    fragColor = vec4(color.rgb * alpha, alpha);
}
)";

// The led fields (DESIGN 2.1): mode 0 the silk core (a 48-triangle fan over the disc), mode 1 the corona with the warp
// streaks (a 48-quad strip over the annulus 0.47...0.74 S). Orb units inside, rim at r = 0.5, y up.
const char* kLedHeader = R"(
uniform vec4 uFrame;   // center x, center y (points), S (points), alpha
uniform vec4 uHeat;    // h, o, act, lvl
uniform vec4 uPhases;  // silk, light, clock, peak age
uniform vec4 uFlags;   // dark, tier flash, ripple amount, silk brightness
uniform vec4 uCorona;  // corona phase, warp phase, corona amount, warp amount
uniform vec4 uText;    // readout offset (S, y up), readout mask on
uniform vec4 uWaves[4];
uniform int uMode;
)";

const char* kLedVertex = R"(
out vec2 vCanvas;
const float SEGMENTS = 48.0;

void main() {
    float s = uFrame.z;
    float pad = 2.0 / uViewport.z;
    float cover = 1.0 / cos(3.14159265 / SEGMENTS);
    float step = TAU / SEGMENTS;
    vec2 point = uFrame.xy;
    int vid = gl_VertexID;
    if (uMode == 0) {
        int segment = vid / 3;
        int corner = vid % 3;
        if (corner > 0) {
            float a = float(segment + corner - 1) * step;
            point += vec2(cos(a), sin(a)) * (0.475 * s * cover + pad);
        }
    } else {
        int segment = vid / 6;
        int corner = vid % 6;
        bool outer = corner == 1 || corner == 3 || corner == 4;
        bool next = corner == 2 || corner == 4 || corner == 5;
        float a = float(segment + (next ? 1 : 0)) * step;
        point += vec2(cos(a), sin(a)) * (outer ? 0.74 * s * cover + pad : 0.47 * s - pad);
    }
    vCanvas = point;
    gl_Position = clipPosition(point);
}
)";

const char* kLedFragment = R"(
uniform sampler2D uPalette;
uniform float uBody[128];
in vec2 vCanvas;
out vec4 fragColor;

const vec3 HOT = vec3(1.0, 0.969, 0.839);

vec3 heat(float h) { return texture(uPalette, vec2(clamp(h, 0.0, 1.0) * (255.0 / 256.0) + 0.5 / 256.0, 0.5)).rgb; }
vec3 ink(vec3 c) { return pow(max(c, 0.0), vec3(1.55)); }
float sstep(float e0, float e1, float x) {
    float t = clamp((x - e0) / (e1 - e0), 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}
float easeOut(float t) {
    float u = 1.0 - clamp(t, 0.0, 1.0);
    return 1.0 - u * u * u;
}

vec3 accRgb = vec3(0.0);
float accA = 0.0;

void glowAdd(vec3 c, float a, bool dark) {
    a = clamp(a, 0.0, 1.0);
    if (dark) {
        accRgb += c * a;
        accA = max(accA, a * 0.35);
    } else {
        accRgb = c * a + accRgb * (1.0 - a);
        accA = a + accA * (1.0 - a);
    }
}

void paint(vec3 c, float a) {
    a = clamp(a, 0.0, 1.0);
    accRgb = c * a + accRgb * (1.0 - a);
    accA = a + accA * (1.0 - a);
}

vec3 mod289(vec3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 mod289(vec4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 permute(vec4 x) { return mod289(((x * 34.0) + 1.0) * x); }
vec4 taylorInvSqrt(vec4 r) { return 1.79284291400159 - 0.85373472095314 * r; }

float snoiseGrad(vec3 v, out vec3 gradient) {
    const vec2 C = vec2(1.0 / 6.0, 1.0 / 3.0);
    const vec4 D = vec4(0.0, 0.5, 1.0, 2.0);
    vec3 i = floor(v + dot(v, C.yyy));
    vec3 x0 = v - i + dot(i, C.xxx);
    vec3 g = step(x0.yzx, x0.xyz);
    vec3 l = 1.0 - g;
    vec3 i1 = min(g.xyz, l.zxy);
    vec3 i2 = max(g.xyz, l.zxy);
    vec3 x1 = x0 - i1 + C.xxx;
    vec3 x2 = x0 - i2 + C.yyy;
    vec3 x3 = x0 - D.yyy;
    i = mod289(i);
    vec4 p = permute(permute(permute(i.z + vec4(0.0, i1.z, i2.z, 1.0)) + i.y + vec4(0.0, i1.y, i2.y, 1.0))
                     + i.x + vec4(0.0, i1.x, i2.x, 1.0));
    float n_ = 0.142857142857;
    vec3 ns = n_ * D.wyz - D.xzx;
    vec4 j = p - 49.0 * floor(p * ns.z * ns.z);
    vec4 x_ = floor(j * ns.z);
    vec4 y_ = floor(j - 7.0 * x_);
    vec4 x = x_ * ns.x + ns.yyyy;
    vec4 y = y_ * ns.x + ns.yyyy;
    vec4 h = 1.0 - abs(x) - abs(y);
    vec4 b0 = vec4(x.xy, y.xy);
    vec4 b1 = vec4(x.zw, y.zw);
    vec4 s0 = floor(b0) * 2.0 + 1.0;
    vec4 s1 = floor(b1) * 2.0 + 1.0;
    vec4 sh = -step(h, vec4(0.0));
    vec4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
    vec4 a1 = b1.xzyw + s1.xzyw * sh.zzww;
    vec3 p0 = vec3(a0.xy, h.x);
    vec3 p1 = vec3(a0.zw, h.y);
    vec3 p2 = vec3(a1.xy, h.z);
    vec3 p3 = vec3(a1.zw, h.w);
    vec4 norm = taylorInvSqrt(vec4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3)));
    p0 *= norm.x;
    p1 *= norm.y;
    p2 *= norm.z;
    p3 *= norm.w;
    vec4 m = max(0.6 - vec4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
    vec4 m2 = m * m;
    vec4 pdotx = vec4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3));
    vec4 temp = m2 * m * pdotx;
    vec4 m4 = m2 * m2;
    gradient = 42.0 * (-8.0 * (temp.x * x0 + temp.y * x1 + temp.z * x2 + temp.w * x3)
                       + m4.x * p0 + m4.y * p1 + m4.z * p2 + m4.w * p3);
    return 42.0 * dot(m4, pdotx);
}

float snoise(vec3 v) {
    vec3 unused;
    return snoiseGrad(v, unused);
}

float silkBox(vec2 p, vec2 b, float rad) {
    vec2 d = abs(p) - b + rad;
    return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - rad;
}
float silkFold(float ph) { return sin(ph + 0.25 * sin(ph)); }
float bell(float x) { return exp(-x * x); }

struct SilkX { float y; float p1; float a2; float p2; float a3; float p3; float rip; };

SilkX silkX(float X, float t, float rip) {
    SilkX x;
    x.y = 0.025 * sin(X * 5.0 + t * 0.3) + 0.03 * sin(X * 1.6 - t * 0.17);
    x.p1 = 0.25 * sin(X * 3.3 + t * 0.2);
    x.a2 = 0.6 * (0.7 + 0.3 * sin(X * 2.8 + 1.0 + t * 0.1));
    x.p2 = 0.35 * sin(X * 4.1 - t * 0.35);
    x.a3 = 0.22 * (0.5 + 0.5 * sin(X * 3.6 - t * 0.25 + 2.0));
    x.p3 = 0.4 * sin(X * 4.7 + t * 0.4);
    x.rip = rip > 0.0 ? 1.3 * sin(X * 2.7) : 0.0;
    return x;
}

float silkHeight(vec2 p, float X, SilkX x, float noise, float t, float rip, float wave, float clock) {
    float Y = dot(p, vec2(-0.60, 0.80)) * (1.0 + 0.45 * X) + 0.03 * noise + x.y;
    float hgt = 0.5 * sin(Y * 6.0 + x.p1 + t * 0.15)
              + x.a2 * silkFold(Y * 16.5 + x.p2 + t * 0.6)
              + x.a3 * silkFold(Y * 27.0 + x.p3 - t * 0.8 + 2.0);
    if (rip > 0.0) {
        float packet = max(sin(Y * 4.0 - clock * 2.6 + x.rip), 0.0);
        hgt += rip * 0.05 * packet * packet * sin(Y * 44.0 + X * 6.0 - clock * 16.0);
    }
    if (wave > 0.0) hgt += wave * 0.11 * sin(length(p) * 36.0 - clock * 8.0);
    return hgt;
}

void silkCore(vec2 p, float r, float waveField) {
    float fade = sstep(0.475, 0.385, r);
    if (fade <= 0.0) return;
    bool dark = uFlags.x > 0.5;
    float h = uHeat.x;
    float o = uHeat.y;
    float act = uHeat.z;
    float t = uPhases.x;
    float phase2 = uPhases.y;
    float clock = uPhases.z;
    float peak = uPhases.w;
    float rip = uFlags.z;
    vec2 sp = p - p / max(r, 1e-3) * 0.03 * waveField;
    float e = 0.008;
    vec2 ac = vec2(-0.6, 0.8);
    vec2 al = vec2(0.8, 0.6);
    vec3 dn;
    float n0 = snoiseGrad(vec3(sp * 1.1, t * 0.07), dn);
    vec2 dn2 = dn.xy * 1.1;
    float X0 = dot(sp, al);
    SilkX x0 = silkX(X0, t, rip);
    SilkX xL = silkX(X0 + e, t, rip);
    float h0 = silkHeight(sp, X0, x0, n0, t, rip, waveField, clock);
    float hA = silkHeight(sp + ac * e, X0, x0, n0 + dot(dn2, ac * e), t, rip, waveField, clock);
    float hB = silkHeight(sp - ac * e, X0, x0, n0 - dot(dn2, ac * e), t, rip, waveField, clock);
    float hL = silkHeight(sp + al * e, X0 + e, xL, n0 + dot(dn2, al * e), t, rip, waveField, clock);
    vec2 g = ac * (hA - hB) / (2.0 * e) + al * (hL - h0) / e;
    vec3 n = normalize(vec3(-g * 0.05, 1.0));
    float crest = sstep(-0.15, 0.75, -(hA - 2.0 * h0 + hB) / (e * e) * 0.006);

    float lift = uText.x;
    float tx = min(silkBox(p - vec2(0.0, 0.03 + lift), vec2(0.27, 0.07), 0.07),
                   silkBox(p - vec2(0.0, -0.17 + lift), vec2(0.13, 0.06), 0.06));
    float textMask = uText.y > 0.5 ? mix(0.3, 1.0, sstep(-0.02, 0.1, tx)) : 1.0;

    float az = 2.2 + 0.3 * sin(phase2 * 0.35);
    vec2 ld = vec2(cos(az), sin(az));
    float sl = dot(n.xy, ld);
    float ao = 0.2 + 0.8 * sstep(-1.6, 1.1, h0);
    float body = pow(sstep(-0.12, 0.5, sl), 1.6) * ao;
    float sheen = bell((sl - 0.3) / 0.11) * ao;
    float glint = bell((sl - 0.33) / 0.045) * (0.35 + 0.65 * crest) * ao;
    float rim = bell((sl + 0.42) / 0.1) * (0.4 + 0.6 * ao);

    float X = dot(p, vec2(0.8, 0.6));
    float pk = peak < 1.4 ? 1.0 - sstep(0.7, 1.4, peak) : 0.0;
    float sweep = pk * bell((X - mix(-0.55, 0.6, easeOut(peak / 1.2))) / 0.13);
    float amp = uFlags.w * (1.0 + 0.3 * uFlags.y + 0.5 * waveField + 2.2 * sweep + 0.4 * pk);
    float k = fade * textMask * amp;

    float glide = 0.05 * sin(X * 3.2 - phase2 * 0.8);
    float spread = mix(0.08, 1.0, act);
    float hotK = clamp(sstep(0.0, 0.3, o) + sweep, 0.0, 1.0);
    vec3 gold = vec3(1.0, 0.84, 0.45);
    vec3 cBody = heat(h + (-0.22 + 0.3 * sstep(-0.1, 0.5, sl) + glide) * spread);
    vec3 cSheen0 = heat(h + (0.06 + glide) * spread);
    vec3 cSheen = mix(cSheen0, mix(gold, HOT, 0.35), hotK);
    vec3 cGlint = mix(cSheen, HOT, 0.55 + 0.3 * hotK);
    vec3 cRim = heat(h + (-0.32 - glide) * spread);

    if (dark) {
        paint(vec3(0.008, 0.01, 0.016), 0.7 * fade);
        glowAdd(cBody, body * 0.32 * k, true);
        glowAdd(cSheen, sheen * 0.75 * k, true);
        glowAdd(cGlint, glint * 0.7 * k, true);
        glowAdd(cRim, rim * 0.35 * k, true);
        glowAdd(mix(gold, HOT, 0.4), sweep * (0.12 + 0.6 * body + 0.8 * sheen) * fade * textMask, true);
    } else {
        float tm = 0.4 + 0.6 * textMask;
        paint(vec3(0.99, 0.985, 0.978), 0.55 * fade);
        paint(vec3(0.66, 0.67, 0.72), (pow(sstep(0.15, -0.45, sl), 1.3) * 0.38 + (1.0 - ao) * 0.12) * fade * tm);
        glowAdd(ink(cRim), rim * 0.22 * k, false);
        glowAdd(mix(ink(cSheen0), vec3(0.96, 0.66, 0.16), hotK), sheen * (0.3 + 0.15 * hotK) * k, false);
        paint(vec3(1.0), (glint * 0.75 + sheen * 0.35) * fade * tm);
        glowAdd(ink(gold), sweep * (0.1 + 0.4 * body) * fade * tm, false);
    }
}

float coronaBodyAt(float ang) {
    float u = ang / 360.0 * 128.0;
    float k = floor(u);
    int i0 = int(k) & 127;
    return mix(uBody[i0], uBody[(i0 + 1) & 127], u - k);
}

void corona(vec2 p, float r, float ang, float px) {
    bool dark = uFlags.x > 0.5;
    float h = uHeat.x;
    float o = uHeat.y;
    vec3 hc = heat(h);
    float outer = sstep(0.74, 0.64, r);
    float disc = sstep(0.5 + px, 0.5 - px, r);
    float cover = 1.0 - disc * (dark ? 0.92 : 0.96);

    float cAmt = uCorona.z;
    if (cAmt > 0.001) {
        float phase = uCorona.x;
        vec2 dir = p / max(r, 1e-4);
        float body = coronaBodyAt(ang);
        float reach = 0.02 + (0.06 * h + 0.12 * o) * pow(body, 1.4) * 1.7;
        float d = (r - 0.499) / reach;
        float fall = exp(-d * 2.4) * sstep(0.49, 0.503, r) * outer;
        float base = exp(-max(r - 0.5, 0.0) / 0.012) * sstep(0.49, 0.503, r) * 0.35;
        float flame = 0.0;
        if (fall * 1.44 * cAmt * cover > 0.002) {
            float rise = (r - 0.5) * 9.0 - phase;
            float warpN = snoise(vec3(dir * 3.0, rise * 0.5));
            float ridge = 1.0 - abs(snoise(vec3(dir * 7.0 + warpN * 0.35, rise)));
            ridge = pow(ridge, 5.0);
            float tongues = sstep(0.45, 0.8, 0.5 + 0.5 * snoise(vec3(dir * 4.5, rise * 0.35)));
            flame = ridge * fall * mix(0.12, 1.25, tongues) * (0.45 + 0.7 * body);
        }
        vec3 fc = mix(HOT, hc, clamp(d * 0.9, 0.0, 1.0));
        glowAdd(dark ? fc : ink(hc), (flame + base) * cAmt * 0.95 * cover, dark);
    }

    float wAmt = uCorona.w;
    if (wAmt > 0.001 && r > 0.5) {
        float bins = 96.0;
        int bin = clamp(int(floor(ang / 360.0 * bins)), 0, 95);
        float seed = WARP_SEEDS[bin];
        if (seed < 0.18 + 0.42 * h) {
            float speed = 0.55 + seed * 0.9;
            float head = fract(seed * 7.3 + uCorona.y * speed);
            float rh = mix(0.74, 0.51, head);
            float len = (0.02 + 0.08 * h + 0.07 * o) * (0.55 + seed);
            float along = (r - rh) / len;
            float acrossDeg = abs(fract(ang / 360.0 * bins) - 0.5) * 360.0 / bins;
            float across = acrossDeg * 0.0174532925 * r;
            float line = sstep(0.0018 + 0.001 * h, 0.0, across) * pow(sstep(1.0, 0.0, along), 1.6) * step(0.0, along);
            float fade = sstep(0.0, 0.3, head) * sstep(1.0, 0.82, head);
            vec3 wc = mix(hc, HOT, 0.45);
            glowAdd(dark ? wc : ink(hc), line * fade * wAmt * (dark ? 0.8 : 0.75) * outer * cover, dark);
        }
    }
}

void main() {
    float s = uFrame.z;
    vec2 p = (vCanvas - uFrame.xy) / s;
    p.y = -p.y;
    float r = length(p);
    if (uMode == 0) {
        if (r >= 0.475) discard;
        float waveField = 0.0;
        for (int i = 0; i < 4; i++) {
            vec4 w = uWaves[i];
            if (w.x < 0.0 || w.x > 1.0) continue;
            float rr = 0.1 + 0.4 * easeOut(w.x);
            float k = (r - rr) / 0.05;
            waveField += w.y * (1.0 - w.x) * exp(-k * k);
        }
        silkCore(p, r, waveField);
    } else {
        if (r < 0.47 || r > 0.74) discard;
        float ang = atan(-p.y, p.x) * 57.2957795131;
        if (ang < 0.0) ang += 360.0;
        corona(p, r, ang, 1.0 / (s * uViewport.z));
    }
    float alpha = uFrame.w;
    accRgb *= alpha;
    accA *= alpha;
    if (accA <= 0.0005 && max(accRgb.r, max(accRgb.g, accRgb.b)) <= 0.0005) discard;
    fragColor = vec4(accRgb, accA);
}
)";

// The liquid surface y(x) of the `liquid` style (DESIGN 2a.3), shared by its volume pass and the readout split.
const char* kLiquidCommon = R"(
uniform vec4 uLFrame;    // center x, center y, S, glass radius (points)
uniform vec4 uLSurface;  // mean level y_L (points, y down), wave amplitude (points), chop amplitude (points), back wave on
uniform vec4 uLPhases;   // phi1..phi4 (radians)
uniform vec4 uLRipples[4];  // impact x (canvas), front 0.42 S tau, amplitude (points), active

float liquidWaveY(float x, bool back) {
    float s = uLFrame.z;
    float xr = x - uLFrame.x;
    float offset = back ? 1.7 : 0.0;
    float a = uLSurface.y * (back ? 0.75 : 1.0);
    float y = uLSurface.x - (back ? 0.008 * s : 0.0);
    if (a > 0.0) {
        y += a * (0.55 * sin(xr / (0.9 * s) * TAU + uLPhases.x + offset)
                  + 0.30 * sin(xr / (0.47 * s) * TAU - uLPhases.y + offset * 1.3)
                  + 0.15 * sin(xr / (0.23 * s) * TAU + uLPhases.z + offset * 0.7));
    }
    if (uLSurface.z > 0.0) {
        y += uLSurface.z * sin(xr / (0.11 * s) * TAU + uLPhases.w + offset) * (0.7 + 0.3 * sin(xr / (0.05 * s) + uLPhases.w * 1.7));
    }
    for (int i = 0; i < 4; i++) {
        vec4 ripple = uLRipples[i];
        if (ripple.w <= 0.0) continue;
        float u = abs(x - ripple.x) - ripple.y;
        float k = u / (0.075 * s);
        y -= ripple.z * exp(-k * k) * cos(TAU * u / (0.1 * s)) * (back ? 0.6 : 1.0);
    }
    return y;
}
)";

// A text texture placed by its rect in points, sheared about a baseline, with a glow sampled around each pixel; clip
// mode 2 keeps it above the liquid surface, 3 below it.
const char* kTextVertex = R"(
uniform vec4 uRect;    // x, y (top-left), width, height (points)
uniform vec4 uParams;  // shear, shear pivot y, glow radius (points), clip mode
out vec2 vUv;
out vec2 vCanvas;
const vec2 corners[6] = vec2[6](vec2(-1, -1), vec2(1, -1), vec2(-1, 1), vec2(1, -1), vec2(1, 1), vec2(-1, 1));

void main() {
    vec2 uv = corners[gl_VertexID] * 0.5 + 0.5;
    vec2 point = uRect.xy + uv * uRect.zw;
    point.x += uParams.x * (uParams.y - point.y);
    vUv = uv;
    vCanvas = point;
    gl_Position = clipPosition(point);
}
)";

const char* kTextFragment = R"(
uniform sampler2D uGlyphs;
uniform vec4 uRect;
uniform vec4 uParams;
uniform vec4 uColor;   // straight alpha
uniform vec4 uGlow;    // rgb, strength
uniform vec4 uClipRect;  // min x, min y, max x, max y (points), clip mode 1
in vec2 vUv;
in vec2 vCanvas;
out vec4 fragColor;

void main() {
    float keep = 1.0;
    if (uParams.w > 0.5 && uParams.w < 1.5) {
        vec2 lo = uClipRect.xy - vCanvas;
        vec2 hi = vCanvas - uClipRect.zw;
        float outside = max(max(lo.x, lo.y), max(hi.x, hi.y));
        keep = clamp(0.5 - outside * uViewport.z, 0.0, 1.0);
        if (keep <= 0.0005) discard;
    }
    if (uParams.w > 1.5) {
        float surface = liquidWaveY(vCanvas.x, false);
        float below = clamp((vCanvas.y - surface) * uViewport.z + 0.5, 0.0, 1.0);
        keep = uParams.w > 2.5 ? below : 1.0 - below;
        if (keep <= 0.0005) discard;
    }
    float coverage = texture(uGlyphs, vUv).a;
    float textAlpha = coverage * uColor.a;
    float glowAlpha = 0.0;
    if (uGlow.a > 0.0) {
        vec2 stepUv = uParams.z / uRect.zw;
        float sum = 0.0;
        float weights = 0.0;
        for (int ring = 1; ring <= 4; ring++) {
            float radius = float(ring) / 4.0;
            float weight = exp(-2.2 * radius * radius);
            for (int k = 0; k < 12; k++) {
                float angle = (float(k) + 0.37 * float(ring)) * 0.5235987756;
                sum += texture(uGlyphs, vUv + vec2(cos(angle), sin(angle)) * stepUv * radius).a * weight;
                weights += weight;
            }
        }
        glowAlpha = clamp(sum / weights * 1.5, 0.0, 1.0) * uGlow.a;
    }
    vec3 rgb = uGlow.rgb * glowAlpha;
    float alpha = glowAlpha;
    rgb = uColor.rgb * textAlpha + rgb * (1.0 - textAlpha);
    alpha = textAlpha + alpha * (1.0 - textAlpha);
    alpha *= keep;
    if (alpha <= 0.0005) discard;
    fragColor = vec4(rgb * keep, alpha);
}
)";

// The `liquid` volume (DESIGN 2a.3): light spill, back wave, body, spherical shade, inner glow, readout scrim, energy
// currents and the new-peak gold band in one pass (mode 0), and the meniscus (mode 1), per pixel from y(x).
const char* kLiquidVertex = R"(
out vec2 vCanvas;
const vec2 corners[6] = vec2[6](vec2(-1, -1), vec2(1, -1), vec2(-1, 1), vec2(1, -1), vec2(1, 1), vec2(-1, 1));

void main() {
    vec2 point = uLFrame.xy + corners[gl_VertexID] * (uLFrame.w + 2.0);
    vCanvas = point;
    gl_Position = clipPosition(point);
}
)";

const char* kLiquidFragment = R"(
uniform vec4 uLLiquid;        // L (rgb), front alpha
uniform vec4 uLDeep;          // darkened L, alpha at the bottom
uniform vec4 uLBack;          // back wave color, alpha
uniform vec4 uLSpill;         // spill color, alpha at the surface (0: none)
uniform vec4 uLGlow;          // inner glow color, alpha at its center (0: none)
uniform vec4 uLGlowGeom;      // center x, center y, radius, additive
uniform vec4 uLShade;         // spherical shade color, alpha at the glass
uniform vec4 uLCurrent;       // filament color, alpha (0: none)
uniform vec4 uLCurrentGeom;   // unused, blur sigma (points), drift phase, additive
uniform vec4 uLMeniscus;      // color, alpha
uniform vec4 uLMeniscusGlow;  // color, alpha (0: none)
uniform vec4 uLMeniscusGeom;  // width (points), glow sigma (points), additive, unused
uniform vec4 uLGold;          // band center y, alpha, half height (points), additive
uniform vec4 uLFlags;         // spill additive, scrim strength (0: none), body gradient top y, bottom y
uniform int uMode;
uniform float uAlpha;
in vec2 vCanvas;
out vec4 fragColor;

void over(inout vec3 color, inout float alpha, vec3 rgb, float a) {
    a = clamp(a, 0.0, 1.0);
    color = rgb * a + color * (1.0 - a);
    alpha = a + alpha * (1.0 - a);
}

// Light adds up on dark glass and blends normally on porcelain.
void light(inout vec3 color, inout float alpha, vec3 rgb, float a, bool additive) {
    if (additive) {
        a = clamp(a, 0.0, 1.0);
        color += rgb * a;
        alpha = a + alpha * (1.0 - a);
    } else {
        over(color, alpha, rgb, a);
    }
}

void main() {
    vec2 p = vCanvas;
    vec2 c = uLFrame.xy;
    float s = uLFrame.z;
    float radius = uLFrame.w;
    float aa = 1.0 / uViewport.z;
    float glass = clamp((radius - length(p - c)) / aa + 0.5, 0.0, 1.0);
    if (glass <= 0.0) discard;
    vec3 color = vec3(0.0);
    float alpha = 0.0;
    float ySurface = liquidWaveY(p.x, false);

    if (uMode == 1) {
        float slope = liquidWaveY(p.x + 0.5, false) - liquidWaveY(p.x - 0.5, false);
        float d = abs(p.y - ySurface) / sqrt(1.0 + slope * slope);
        float halfWidth = uLMeniscusGeom.x * 0.5;
        if (uLMeniscusGlow.a > 0.0) {
            float sigma = max(uLMeniscusGeom.y, 0.001);
            float x = max(d - halfWidth, 0.0) / sigma;
            light(color, alpha, uLMeniscusGlow.rgb, uLMeniscusGlow.a * exp(-0.5 * x * x), uLMeniscusGeom.z > 0.5);
        }
        float core = clamp((halfWidth - d) / aa + 0.5, 0.0, 1.0);
        over(color, alpha, uLMeniscus.rgb, uLMeniscus.a * core);
    } else {
        float yLevel = uLSurface.x;
        if (uLSpill.a > 0.0) {
            float t = (p.y - (yLevel - 0.22 * s)) / (0.22 * s);
            if (t >= 0.0 && t <= 1.0) light(color, alpha, uLSpill.rgb, uLSpill.a * t, uLFlags.x > 0.5);
        }
        if (uLSurface.w > 0.5) {
            float yBack = liquidWaveY(p.x, true);
            over(color, alpha, uLBack.rgb, uLBack.a * clamp((p.y - yBack) / aa + 0.5, 0.0, 1.0));
        }
        float cover = clamp((p.y - ySurface) / aa + 0.5, 0.0, 1.0);
        if (cover > 0.0) {
            float t = clamp((p.y - uLFlags.z) / max(uLFlags.w - uLFlags.z, 1.0), 0.0, 1.0);
            over(color, alpha, mix(uLLiquid.rgb, uLDeep.rgb, t), mix(uLLiquid.a, uLDeep.a, t) * cover);
            vec2 shadeCenter = c + vec2(-0.06, -0.08) * s;
            float inner = 0.35 * radius;
            float shade = clamp((length(p - shadeCenter) - inner) / max(radius - inner, 0.001), 0.0, 1.0);
            over(color, alpha, uLShade.rgb, uLShade.a * shade * cover);
            if (uLGlow.a > 0.0) {
                float d = length(p - uLGlowGeom.xy) / max(uLGlowGeom.z, 0.001);
                light(color, alpha, uLGlow.rgb, uLGlow.a * max(0.0, 1.0 - d) * cover, uLGlowGeom.w > 0.5);
            }
        }
        if (uLFlags.y > 0.0) {
            float k = length((p - (c + vec2(0.0, 0.1 * s))) / vec2(0.36 * s, 0.26 * s));
            if (k < 1.0) {
                float a = k < 0.55 ? mix(0.35, 0.30, k / 0.55) : mix(0.30, 0.0, (k - 0.55) / 0.45);
                over(color, alpha, vec3(0.0), a * uLFlags.y);
            }
        }
        if (uLCurrent.a > 0.0 && cover > 0.0) {
            float bottom = c.y + radius;
            float u = (p.x - (c.x - radius)) / (2.0 * radius);
            float sigma = max(uLCurrentGeom.y, 0.001);
            for (int k = 0; k < 3; k++) {
                float depth = 0.25 + 0.25 * float(k);
                float phase = uLCurrentGeom.z * (0.35 + 0.2 * float(k)) + 2.1 * float(k);
                float y = yLevel + (bottom - yLevel) * depth + 0.03 * s * sin(u * 7.0 + phase) + 0.015 * s * sin(u * 13.0 - phase * 1.7);
                float slope = (0.03 * s * 7.0 * cos(u * 7.0 + phase) + 0.015 * s * 13.0 * cos(u * 13.0 - phase * 1.7)) / (2.0 * radius);
                float d = abs(p.y - y) / sqrt(1.0 + slope * slope);
                float halfWidth = (0.018 - 0.004 * float(k)) * s * 0.5;
                float profile = 1.0 - smoothstep(max(halfWidth - 1.5 * sigma, 0.0), halfWidth + 1.5 * sigma, d);
                light(color, alpha, uLCurrent.rgb, uLCurrent.a * (1.0 - 0.25 * float(k)) * profile * cover, uLCurrentGeom.w > 0.5);
            }
        }
        if (uLGold.y > 0.0) {
            float d = abs(p.y - uLGold.x) / max(uLGold.z, 0.001);
            if (d < 1.0) light(color, alpha, vec3(250.0, 204.0, 21.0) / 255.0, uLGold.y * (1.0 - d), uLGold.w > 0.5);
        }
    }
    float k = glass * uAlpha;
    color *= k;
    alpha *= k;
    if (alpha <= 0.0005 && max(color.r, max(color.g, color.b)) <= 0.0005) discard;
    fragColor = vec4(color, alpha);
}
)";

// A premultiplied image (the body) over a rect in points.
const char* kImageVertex = R"(
uniform vec4 uRect;
out vec2 vUv;
const vec2 corners[6] = vec2[6](vec2(-1, -1), vec2(1, -1), vec2(-1, 1), vec2(1, -1), vec2(1, 1), vec2(-1, 1));

void main() {
    vec2 uv = corners[gl_VertexID] * 0.5 + 0.5;
    vUv = uv;
    gl_Position = clipPosition(uRect.xy + uv * uRect.zw);
}
)";

const char* kImageFragment = R"(
uniform sampler2D uImage;
uniform float uAlpha;
in vec2 vUv;
out vec4 fragColor;

void main() { fragColor = texture(uImage, vUv) * uAlpha; }
)";

QByteArray join(const char* a, const char* b, const char* c = "") { return QByteArray(kCommon) + a + b + c; }

QByteArray warpSeeds() {
    QString out = QStringLiteral("const float WARP_SEEDS[96] = float[96](");
    for (int i = 0; i < led::kWarpBins; ++i) {
        if (i) out += QStringLiteral(", ");
        out += QString::number(led::warpSeed(i), 'f', 6);
    }
    out += QStringLiteral(");\n");
    return out.toUtf8();
}

}  // namespace

QByteArray shapeVertex() { return join(kShapeVertex, ""); }
QByteArray shapeFragment() { return join(kShapeFragment, ""); }
QByteArray ledVertex() { return join(kLedHeader, kLedVertex); }
QByteArray ledFragment() { return QByteArray(kCommon) + kLedHeader + warpSeeds() + kLedFragment; }
QByteArray textVertex() { return join(kTextVertex, ""); }
QByteArray textFragment() { return join(kLiquidCommon, kTextFragment); }
QByteArray liquidVertex() { return join(kLiquidCommon, kLiquidVertex); }
QByteArray liquidFragment() { return join(kLiquidCommon, kLiquidFragment); }
QByteArray imageVertex() { return join(kImageVertex, ""); }
QByteArray imageFragment() { return join(kImageFragment, ""); }

}  // namespace orb::shaders
