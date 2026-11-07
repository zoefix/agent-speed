/// Metal shading language source for the orb, compiled once at runtime (no resource bundle needed).
///
/// Every element is an instanced quad whose fragment shader evaluates a signed distance field, so edges are
/// anti-aliased analytically and glows are a Gaussian of the distance to the shape — no blur passes. Style-specific
/// functions (the liquid volume) live in their own source files and are compiled into the same library.
enum OrbShaderSource {
    static var source: String { header + OrbLiquidShaderSource.source + OrbLedShaderSource.source + shapes + text }

    static let header = """
    #include <metal_stdlib>
    using namespace metal;

    // a: center.xy (points, y down), rotation (rad), shape
    // b: size.xy, corner radius, glow radius (< 0: an exponential glow exp(-d / |glow|) instead of the Gaussian)
    // color / color2: straight-alpha sRGB colors (color2 is the gradient tail)
    // extra: core alpha, glow alpha, p0, p1
    // extra2: fill mode, q0, q1, q2
    // Polyline (9): a.z first point, b.xy half size of its bounds, b.z point count; color glow stroke (rgb, outer alpha),
    //   color2 core stroke (rgb, alpha), extra outer, mid and core widths and the mid alpha, extra2.y 1 = strokes add up.
    // Flare (10): a.z direction (rad), b.x / b.y tangential / radial length, extra.z / extra.w radial / tangential width.
    // Perimeter (11): the outline of a rounded rectangle (half size b.xy, corner b.z). extra2.x 0, a stroke of width
    //   extra.z: the whole outline when extra.w is 0, else the extra.w points behind the head a.z (distance along the
    //   outline, clockwise from the end of the top-left corner) with round caps, color2 → color, width extra2.y × → 1 ×
    //   and alpha extra2.z × → 1 × along t^extra2.w. extra2.x 1, a bloom outside only: extra.z points of full light,
    //   then a Gaussian edge of σ b.w, times (1 − (d / extra.w)²)² of the distance d from the outline, so it fades out
    //   along the outline's rounded offsets and ends extra.w points out.
    // Circle (1) with b.z > 0: a soft disc, color.a × smoothstep(b.z, b.x, r) (full inside b.x, zero from b.z out).
    struct Instance {
        float4 a;
        float4 b;
        float4 color;
        float4 color2;
        float4 extra;
        float4 extra2;
    };

    struct Uniforms {
        float4 viewport;  // width, height (points), scale (pixels per point), clip radius (points, 0 = none)
        float4 transform; // scale about (y, z) — the hover scale around the disc center — and the clip's soft edge (points)
    };

    constant float2 corners[6] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(1, -1), float2(1, 1), float2(-1, 1) };
    constant float orb_tau = 6.28318530718;

    static float4 orb_clip_position(float2 point, constant Uniforms &uniforms) {
        float2 center = uniforms.transform.yz;
        float2 scaled = center + (point - center) * uniforms.transform.x;
        return float4(scaled.x / uniforms.viewport.x * 2.0 - 1.0, 1.0 - scaled.y / uniforms.viewport.y * 2.0, 0, 1);
    }

    static float orb_clip_coverage(float2 canvas, constant Uniforms &uniforms) {
        if (uniforms.viewport.w <= 0.0) {
            return 1.0;
        }
        float aa = 1.0 / uniforms.viewport.z;
        float d = length(canvas - uniforms.transform.yz) - uniforms.viewport.w;
        if (uniforms.transform.w > 0.0) {
            // Soft edge: clear at the clip radius, solid `transform.w` inside it.
            return clamp(-d / uniforms.transform.w, 0.0, 1.0);
        }
        return clamp(0.5 - d / aa, 0.0, 1.0);
    }

    """

    static let shapes = """
    struct VertexOut {
        float4 position [[position]];
        float2 local;
        float2 canvas;
        uint instance [[flat]];
    };

    static float2 orb_rotate(float2 p, float angle) {
        float c = cos(angle);
        float s = sin(angle);
        return float2(c * p.x - s * p.y, s * p.x + c * p.y);
    }

    // iq: uneven capsule along +y from radius r1 at the origin to r2 at (0, h).
    static float orb_uneven_capsule(float2 p, float r1, float r2, float h) {
        p.x = abs(p.x);
        float b = (r1 - r2) / max(h, 0.0001);
        float a = sqrt(max(1.0 - b * b, 0.0));
        float k = dot(p, float2(-b, a));
        if (k < 0.0) {
            return length(p) - r1;
        }
        if (k > a * h) {
            return length(p - float2(0.0, h)) - r2;
        }
        return dot(p, float2(a, b)) - r1;
    }

    // Bounds (lo.xy, hi.xy relative to the center) of an annular sector, so an arc's quad covers only the arc: a
    // short arc or one coil of a ring otherwise shades a quad as large as its whole circle.
    static float4 orb_arc_bounds(float radius, float half_width, float start, float sweep) {
        float outer = radius + half_width;
        if (sweep >= orb_tau - 0.001) {
            return float4(-outer, -outer, outer, outer);
        }
        float inner = max(radius - half_width, 0.0);
        float2 a = float2(cos(start), sin(start));
        float2 b = float2(cos(start + sweep), sin(start + sweep));
        float2 lo = min(min(a * outer, a * inner), min(b * outer, b * inner));
        float2 hi = max(max(a * outer, a * inner), max(b * outer, b * inner));
        float base = start - orb_tau * floor(start / orb_tau);
        for (int k = 0; k < 8; k++) {
            float axis = float(k) * 1.5707963268;
            float rel = axis - base;
            if (rel >= 0.0 && rel <= sweep) {
                float2 d = float2(cos(axis), sin(axis)) * outer;
                lo = min(lo, d);
                hi = max(hi, d);
            }
        }
        return float4(lo, hi);
    }

    // The outline of a w × h rounded rectangle with corner r (top-left origin, y down), walked clockwise from (r, 0).
    static float orb_perimeter_length(float2 size, float r) {
        return 2.0 * (size.x - 2.0 * r) + 2.0 * (size.y - 2.0 * r) + orb_tau * r;
    }

    static float2 orb_perimeter_point(float s, float2 size, float r) {
        float w = size.x;
        float h = size.y;
        float sw = w - 2.0 * r;
        float sh = h - 2.0 * r;
        float arc = 1.5707963268 * r;
        float total = 2.0 * sw + 2.0 * sh + 4.0 * arc;
        s -= total * floor(s / max(total, 0.0001));
        float k = 1.0 / max(r, 0.0001);
        if (s <= sw) { return float2(r + s, 0.0); }
        s -= sw;
        if (s <= arc) { float a = -1.5707963268 + s * k; return float2(w - r + cos(a) * r, r + sin(a) * r); }
        s -= arc;
        if (s <= sh) { return float2(w, r + s); }
        s -= sh;
        if (s <= arc) { float a = s * k; return float2(w - r + cos(a) * r, h - r + sin(a) * r); }
        s -= arc;
        if (s <= sw) { return float2(w - r - s, h); }
        s -= sw;
        if (s <= arc) { float a = 1.5707963268 + s * k; return float2(r + cos(a) * r, h - r + sin(a) * r); }
        s -= arc;
        if (s <= sh) { return float2(0.0, h - r - s); }
        s -= sh;
        float a = 3.1415926536 + min(s, arc) * k;
        return float2(r + cos(a) * r, r + sin(a) * r);
    }

    // Distance along the outline of the point of the outline nearest to q.
    static float orb_perimeter_along(float2 q, float2 size, float r) {
        float w = size.x;
        float h = size.y;
        float sw = w - 2.0 * r;
        float sh = h - 2.0 * r;
        float arc = 1.5707963268 * r;
        float2 c = clamp(q, float2(r), float2(w - r, h - r));
        float2 delta = q - c;
        if (abs(delta.x) > 1e-5 && abs(delta.y) > 1e-5) {
            float a = atan2(delta.y, delta.x);
            if (delta.x > 0.0 && delta.y < 0.0) { return sw + (a + 1.5707963268) * r; }
            if (delta.x > 0.0) { return sw + arc + sh + a * r; }
            if (delta.y > 0.0) { return 2.0 * sw + 2.0 * arc + sh + (a - 1.5707963268) * r; }
            return 2.0 * sw + 3.0 * arc + 2.0 * sh + (a + 3.1415926536) * r;
        }
        float top = q.y;
        float right = w - q.x;
        float bottom = h - q.y;
        float left = q.x;
        float nearest = min(min(top, right), min(bottom, left));
        if (nearest == top) { return c.x - r; }
        if (nearest == right) { return sw + arc + c.y - r; }
        if (nearest == bottom) { return sw + 2.0 * arc + sh + (w - r - c.x); }
        return 2.0 * sw + 3.0 * arc + sh + (h - r - c.y);
    }

    // Bounds (lo.xy, hi.xy, top-left origin) of the outline from s0 over `length`: its ends and the corners' ends in
    // between (each corner's arc lies inside the box of its ends).
    static float4 orb_perimeter_bounds(float s0, float length, float2 size, float r) {
        float2 a = orb_perimeter_point(s0, size, r);
        float2 b = orb_perimeter_point(s0 + length, size, r);
        float2 lo = min(a, b);
        float2 hi = max(a, b);
        float w = size.x;
        float h = size.y;
        float sw = w - 2.0 * r;
        float sh = h - 2.0 * r;
        float arc = 1.5707963268 * r;
        float total = orb_perimeter_length(size, r);
        float marks[8] = { 0.0, sw, sw + arc, sw + arc + sh, sw + 2.0 * arc + sh, 2.0 * sw + 2.0 * arc + sh,
                           2.0 * sw + 3.0 * arc + sh, 2.0 * sw + 3.0 * arc + 2.0 * sh };
        float2 points[8] = { float2(r, 0.0), float2(w - r, 0.0), float2(w, r), float2(w, h - r), float2(w - r, h),
                             float2(r, h), float2(0.0, h - r), float2(0.0, r) };
        for (int i = 0; i < 8; i++) {
            float rel = marks[i] - s0;
            rel -= total * floor(rel / max(total, 0.0001));
            if (rel <= length) {
                lo = min(lo, points[i]);
                hi = max(hi, points[i]);
            }
        }
        return float4(lo, hi);
    }

    vertex VertexOut orb_vertex(uint vid [[vertex_id]],
                                uint iid [[instance_id]],
                                const device Instance *instances [[buffer(0)]],
                                constant Uniforms &uniforms [[buffer(1)]]) {
        Instance item = instances[iid];
        float shape = item.a.w;
        float glow = item.b.w >= 0.0 ? item.b.w * 3.2 : -item.b.w * 7.0;
        float extent;
        if (shape < 0.5) {
            extent = length(item.b.xy) + glow;
        } else if (shape < 1.5) {
            extent = max(item.b.x, item.b.z) + glow;
        } else if (shape < 2.5) {
            extent = item.b.x + item.b.y + glow;
        } else if (shape < 3.5) {
            extent = item.b.x * 0.5 + item.b.y + glow;
        } else if (shape < 4.5) {
            extent = item.extra.w;
        } else if (shape < 6.5) {
            extent = item.b.x + item.b.y + glow;
        } else if (shape < 7.5) {
            extent = max(item.b.x, item.b.y) + glow;
        } else if (shape < 8.5) {
            extent = max(item.b.x, item.b.y) + max(item.extra.z, item.b.z) + glow;
        } else {
            extent = 0.0;
        }
        extent += 2.0 / uniforms.viewport.z;
        float2 local = corners[vid] * extent;
        if (shape > 8.5 && shape < 9.5) {
            local = corners[vid] * (item.b.xy + 2.0 / uniforms.viewport.z);
        } else if (shape > 9.5 && shape < 10.5) {
            float2 half_size = 5.0 * max(item.b.xy, item.extra.wz) + 2.0 / uniforms.viewport.z;
            local = orb_rotate(corners[vid] * half_size, item.a.z);
        } else if (shape > 10.5) {
            float aa_pad = 2.0 / uniforms.viewport.z;
            if (item.extra2.x > 0.5) {
                local = corners[vid] * (item.b.xy + min(item.extra.z + 3.0 * item.b.w, item.extra.w) + aa_pad);
            } else if (item.extra.w > 0.0) {
                float2 size = item.b.xy * 2.0;
                float r = min(item.b.z, min(item.b.x, item.b.y));
                float length = min(item.extra.w, orb_perimeter_length(size, r));
                float4 box = orb_perimeter_bounds(item.a.z - length, length, size, r);
                float pad = item.extra.z * 0.5 * max(item.extra2.y, 1.0) + glow + aa_pad;
                local = mix(box.xy - pad, box.zw + pad, corners[vid] * 0.5 + 0.5) - item.b.xy;
            } else {
                local = corners[vid] * (item.b.xy + item.extra.z * 0.5 + glow + aa_pad);
            }
        }
        if (shape > 4.5 && shape < 6.5) {
            float pad = item.b.y * 0.5 + glow + 2.0 / uniforms.viewport.z;
            float4 box = orb_arc_bounds(item.b.x, item.b.y * 0.5, item.extra.z, item.extra.w);
            local = mix(box.xy - pad, box.zw + pad, corners[vid] * 0.5 + 0.5);
        }
        float2 point = item.a.xy + local;
        VertexOut out;
        out.position = orb_clip_position(point, uniforms);
        out.local = local;
        out.canvas = point;
        out.instance = iid;
        return out;
    }

    fragment float4 orb_fragment(VertexOut in [[stage_in]],
                                 const device Instance *instances [[buffer(0)]],
                                 constant Uniforms &uniforms [[buffer(1)]],
                                 const device float2 *points [[buffer(2)]],
                                 texture2d<float> palette [[texture(0)]]) {
        constexpr sampler palette_sampler(address::clamp_to_edge, filter::linear);
        Instance item = instances[in.instance];
        float2 p = in.local;
        float shape = item.a.w;
        float aa = 1.0 / uniforms.viewport.z;
        float4 color = item.color;
        float fill = item.extra2.x;
        float d;

        if (shape < 0.5) {
            // Rounded rectangle rotated by a.z.
            float2 q = orb_rotate(p, -item.a.z);
            float r = item.b.z;
            float2 k = abs(q) - item.b.xy + r;
            d = length(max(k, 0.0)) + min(max(k.x, k.y), 0.0) - r;
        } else if (shape < 1.5) {
            d = length(p) - item.b.x;
            if (item.b.z > 0.0) {
                float t = clamp((length(p) - item.b.z) / (item.b.x - item.b.z), 0.0, 1.0);
                float alpha = color.a * t * t * (3.0 - 2.0 * t) * orb_clip_coverage(in.canvas, uniforms);
                if (alpha <= 0.0005) {
                    discard_fragment();
                }
                return float4(color.rgb * alpha, alpha);
            }
        } else if (shape < 2.5) {
            d = abs(length(p) - item.b.x) - item.b.y * 0.5;
        } else if (shape < 3.5) {
            // Capsule from tail to head along a.z; color2 at the tail, color at the head.
            float2 direction = float2(cos(item.a.z), sin(item.a.z));
            float half_length = item.b.x * 0.5;
            float t = clamp(dot(p, direction), -half_length, half_length);
            d = length(p - direction * t) - item.b.y * 0.5;
            color = mix(item.color2, item.color, (t + half_length) / max(item.b.x, 0.0001));
        } else if (shape < 4.5) {
            // Radial aura from extra.z (inner) to extra.w (outer), nothing inside the inner radius.
            float r = length(p);
            float inner = item.extra.z;
            float outer = item.extra.w;
            float t = clamp((r - inner) / max(outer - inner, 0.0001), 0.0, 1.0);
            float falloff = exp(-t * t * 4.2) * (1.0 - t * t);
            float edge = clamp((r - inner) / aa + 0.5, 0.0, 1.0);
            float alpha = color.a * falloff * edge * item.extra.x * orb_clip_coverage(in.canvas, uniforms);
            if (alpha <= 0.0005) {
                discard_fragment();
            }
            return float4(color.rgb * alpha, alpha);
        } else if (shape < 5.5) {
            // Tapered arc (comet): radius b.x, width b.y, from extra.z over extra.w radians, head at the end.
            float r = length(p);
            float angle = atan2(p.y, p.x);
            float rel = angle - item.extra.z;
            rel -= orb_tau * floor(rel / orb_tau);
            if (rel > item.extra.w) {
                discard_fragment();
            }
            float t = rel / item.extra.w;
            d = abs(r - item.b.x) - item.b.y * 0.5 * (0.3 + 0.7 * t);
            color = mix(item.color2, item.color, t);
            color.a *= pow(t, 1.4);
        } else if (shape < 6.5) {
            // Band arc: radius b.x, width b.y, from extra.z over extra.w radians (clockwise on screen), round caps
            // when b.z > 0.5. Color runs color2 → color (or the heat palette from q0 to q1) and alpha from q2 to 1.
            float r = length(p);
            float start = item.extra.z;
            float sweep = max(item.extra.w, 0.0001);
            float rel = atan2(p.y, p.x) - start;
            rel -= orb_tau * floor(rel / orb_tau);
            float half_width = item.b.y * 0.5;
            float t;
            if (rel <= sweep) {
                t = rel / sweep;
                d = abs(r - item.b.x) - half_width;
            } else {
                float to_end = rel - sweep;
                float to_start = orb_tau - rel;
                if (item.b.z > 0.5) {
                    float2 ps = item.b.x * float2(cos(start), sin(start));
                    float2 pe = item.b.x * float2(cos(start + sweep), sin(start + sweep));
                    float ds = length(p - ps) - half_width;
                    float de = length(p - pe) - half_width;
                    t = de < ds ? 1.0 : 0.0;
                    d = min(ds, de);
                } else {
                    t = to_end < to_start ? 1.0 : 0.0;
                    float2 k = float2(abs(r - item.b.x) - half_width, min(to_end, to_start) * r);
                    d = length(max(k, 0.0)) + min(max(k.x, k.y), 0.0);
                }
            }
            if (fill > 2.5) {
                float n = mix(item.extra2.y, item.extra2.z, t);
                color = float4(palette.sample(palette_sampler, float2(clamp(n, 0.0, 1.0) * (255.0 / 256.0) + 0.5 / 256.0, 0.5)).rgb, item.color.a);
            } else {
                color = mix(item.color2, item.color, t);
            }
            color.a *= mix(item.extra2.w, 1.0, t);
            fill = 0.0;
        } else if (shape < 7.5) {
            // Ellipse with radii b.xy rotated by a.z (iq's approximation, exact enough near the edge).
            float2 q = orb_rotate(p, -item.a.z);
            float2 radii = max(item.b.xy, float2(0.001));
            float k1 = length(q / radii);
            float k2 = length(q / (radii * radii));
            d = k1 * (k1 - 1.0) / max(k2, 0.00001);
        } else if (shape < 8.5) {
            // Needle blade: pivot at the center, tip at b.x along a.z (half width extra.z → extra.w), counterweight
            // b.y behind (half width extra.z → b.z).
            float2 q = orb_rotate(p, -item.a.z);
            float tip = orb_uneven_capsule(float2(q.y, q.x), item.extra.z, item.extra.w, item.b.x);
            float tail = orb_uneven_capsule(float2(q.y, -q.x), item.extra.z, item.b.z, item.b.y);
            d = min(tip, tail);
        } else if (shape < 9.5) {
            // Polyline: three round-joined strokes of one path (glow, glow, core).
            int first = int(item.a.z + 0.5);
            int count = int(item.b.z + 0.5);
            float2 q = in.canvas;
            float dist = 1e6;
            for (int i = 0; i + 1 < count; i++) {
                float2 a = points[first + i];
                float2 ab = points[first + i + 1] - a;
                float t = clamp(dot(q - a, ab) / max(dot(ab, ab), 1e-6), 0.0, 1.0);
                dist = min(dist, length(q - a - ab * t));
            }
            float c1 = clamp(0.5 - (dist - item.extra.x * 0.5) / aa, 0.0, 1.0) * item.color.a;
            float c2 = clamp(0.5 - (dist - item.extra.y * 0.5) / aa, 0.0, 1.0) * item.extra.w;
            float c3 = clamp(0.5 - (dist - item.extra.z * 0.5) / aa, 0.0, 1.0) * item.color2.a;
            float3 rgb;
            float alpha;
            if (item.extra2.y > 0.5) {
                rgb = item.color.rgb * (c1 + c2) + item.color2.rgb * c3;
                alpha = 1.0 - (1.0 - c1) * (1.0 - c2) * (1.0 - c3);
            } else {
                rgb = item.color.rgb * c1;
                alpha = c1;
                rgb = item.color.rgb * c2 + rgb * (1.0 - c2);
                alpha = c2 + alpha * (1.0 - c2);
                rgb = item.color2.rgb * c3 + rgb * (1.0 - c3);
                alpha = c3 + alpha * (1.0 - c3);
            }
            float keep = orb_clip_coverage(in.canvas, uniforms);
            if (alpha * keep <= 0.0005) {
                discard_fragment();
            }
            return float4(rgb * keep, alpha * keep);
        } else if (shape < 10.5) {
            // Flare: two crossed exponential lines.
            float2 q = abs(orb_rotate(p, -item.a.z));
            float v = exp(-q.x / max(item.b.x, 0.0001) - q.y / max(item.extra.z, 0.0001))
                    + exp(-q.y / max(item.b.y, 0.0001) - q.x / max(item.extra.w, 0.0001));
            float alpha = min(1.0, color.a * v) * orb_clip_coverage(in.canvas, uniforms);
            if (alpha <= 0.0005) {
                discard_fragment();
            }
            return float4(color.rgb * alpha, alpha);
        } else {
            // Perimeter: a stroke along the rounded rectangle's outline, or the bloom outside it.
            float r = min(item.b.z, min(item.b.x, item.b.y));
            float2 k = abs(p) - item.b.xy + r;
            float sd = length(max(k, 0.0)) + min(max(k.x, k.y), 0.0) - r;
            if (fill > 0.5) {
                float x = sd - item.extra.z;
                float sigma = max(item.b.w, 0.0001);
                float u = clamp(sd / max(item.extra.w, 0.0001), 0.0, 1.0);
                float fade = (1.0 - u * u) * (1.0 - u * u);
                float light = 1.0 / (1.0 + exp(1.702 * x / sigma));
                float alpha = color.a * light * clamp(sd / aa + 0.5, 0.0, 1.0) * fade;
                if (alpha <= 0.0005) {
                    discard_fragment();
                }
                return float4(color.rgb * alpha, alpha);
            }
            float half_width = item.extra.z * 0.5;
            float t = 1.0;
            if (item.extra.w > 0.0) {
                float2 size = item.b.xy * 2.0;
                float total = orb_perimeter_length(size, r);
                float length = min(item.extra.w, total);
                float rel = item.a.z - orb_perimeter_along(p + item.b.xy, size, r);
                rel -= total * floor(rel / max(total, 0.0001));
                if (rel <= length) {
                    t = 1.0 - rel / max(length, 0.0001);
                    d = abs(sd) - half_width * mix(item.extra2.y, 1.0, t);
                } else {
                    float2 head = orb_perimeter_point(item.a.z, size, r) - item.b.xy;
                    float2 tail = orb_perimeter_point(item.a.z - length, size, r) - item.b.xy;
                    float dh = distance(p, head) - half_width;
                    float dt = distance(p, tail) - half_width * item.extra2.y;
                    t = dh < dt ? 1.0 : 0.0;
                    d = min(dh, dt);
                }
            } else {
                d = abs(sd) - half_width;
            }
            color = mix(item.color2, item.color, t);
            color.a *= mix(item.extra2.z, 1.0, pow(t, max(item.extra2.w, 0.0001)));
            fill = 0.0;
        }

        if (fill > 0.5 && fill < 1.5) {
            float2 direction = float2(cos(item.extra2.y), sin(item.extra2.y));
            float half_length = max(item.extra2.z, 0.0001);
            float t = clamp((dot(p, direction) + half_length) / (2.0 * half_length), 0.0, 1.0);
            color = mix(item.color, item.color2, t);
        } else if (fill > 1.5 && fill < 2.5) {
            float t = clamp(length(p - item.extra2.yz) / max(item.extra2.w, 0.0001), 0.0, 1.0);
            color = mix(item.color, item.color2, t);
        }

        float coverage = clamp(0.5 - d / aa, 0.0, 1.0);
        float core = coverage * item.extra.x;
        float glow = 0.0;
        if (item.b.w > 0.0) {
            float x = max(d, 0.0) / item.b.w;
            glow = exp(-x * x) * item.extra.y;
        } else if (item.b.w < 0.0) {
            glow = exp(-max(d, 0.0) / -item.b.w) * item.extra.y;
        }
        float alpha = (core + glow * (1.0 - core)) * color.a * orb_clip_coverage(in.canvas, uniforms);
        if (alpha <= 0.0005) {
            discard_fragment();
        }
        return float4(color.rgb * alpha, alpha);
    }

    """

    static let text = """
    struct TextInstance {
        float4 rect;   // x, y (top-left), width, height in points
        float4 color;  // straight alpha
        float4 glow;   // rgb, strength
        float4 params; // shear, shear pivot y, glow radius (points), clip mode (0 none, 1 rect, 2 above / 3 below the liquid
                       // surface, 4 a moving band: clip = center x, center y, half width, slope dx/dy)
        float4 clip;   // clip rect: min x, min y, max x, max y (points)
        float4 fill2;  // gradient.z 1: a vertical gradient's bottom color (straight alpha); 2 (the `led` value): chromatic
                       // split offset (points), its alpha, unused, dark backing alpha
        float4 gradient; // top y, bottom y (points), mode (1 gradient, 2 `led` value: a black backing blurred over x × the
                         // glow radius and the chromatic split under the glow, 3 outline: x points wide in fill2's
                         // color, over the glow), quad inset (points: the outer band where nothing is drawn)
        float4 rotation; // angle (radians, clockwise on screen) about the pivot x, y (points); clips stay unrotated
    };

    struct TextOut {
        float4 position [[position]];
        float2 uv;
        float2 canvas;
    };

    vertex TextOut orb_text_vertex(uint vid [[vertex_id]],
                                   constant TextInstance &item [[buffer(0)]],
                                   constant Uniforms &uniforms [[buffer(1)]]) {
        float2 inset = min(float2(max(item.gradient.w, 0.0)), item.rect.zw * 0.5 - 0.5);
        float2 uv = (inset + (corners[vid] * 0.5 + 0.5) * (item.rect.zw - 2.0 * inset)) / item.rect.zw;
        float2 point = item.rect.xy + uv * item.rect.zw;
        point.x += item.params.x * (item.params.y - point.y);
        float2 drawn = point;
        if (item.rotation.x != 0.0) {
            float2 d = point - item.rotation.yz;
            float c = cos(item.rotation.x);
            float s = sin(item.rotation.x);
            drawn = item.rotation.yz + float2(d.x * c - d.y * s, d.x * s + d.y * c);
        }
        TextOut out;
        out.position = orb_clip_position(drawn, uniforms);
        out.uv = uv;
        out.canvas = point;
        return out;
    }

    fragment float4 orb_text_fragment(TextOut in [[stage_in]],
                                      constant TextInstance &item [[buffer(0)]],
                                      constant Uniforms &uniforms [[buffer(1)]],
                                      constant LiquidParams &liquid [[buffer(2)]],
                                      texture2d<float> glyphs [[texture(0)]]) {
        constexpr sampler linear_sampler(address::clamp_to_zero, filter::linear);
        float mode = item.params.w;
        float aa = 1.0 / uniforms.viewport.z;
        float keep = 1.0;
        if (mode > 3.5) {
            float dx = in.canvas.x - (item.clip.x + (in.canvas.y - item.clip.y) * item.clip.w);
            keep = max(0.0, 1.0 - abs(dx) / max(item.clip.z, 0.0001));
        } else if (mode > 0.5) {
            float2 lo = item.clip.xy - in.canvas;
            float2 hi = in.canvas - item.clip.zw;
            float outside = max(max(lo.x, lo.y), max(hi.x, hi.y));
            keep = clamp(0.5 - outside / aa, 0.0, 1.0);
        }
        if (mode > 1.5) {
            float surface = orb_liquid_surface_y(in.canvas.x, liquid);
            float below = clamp((in.canvas.y - surface) / aa + 0.5, 0.0, 1.0);
            keep *= mode > 2.5 ? below : 1.0 - below;
        }
        if (keep <= 0.0005) {
            discard_fragment();
        }
        float coverage = glyphs.sample(linear_sampler, in.uv).a;
        float4 fill = item.color;
        bool backing = item.gradient.z > 1.5 && item.gradient.z < 2.5;
        bool outlined = item.gradient.z > 2.5;
        if (item.gradient.z > 0.5 && item.gradient.z < 1.5) {
            float t = clamp((in.canvas.y - item.gradient.x) / max(item.gradient.y - item.gradient.x, 0.0001), 0.0, 1.0);
            fill = mix(item.color, item.fill2, t);
        }
        float text_alpha = coverage * fill.a;
        float glow_alpha = 0.0;
        float back_alpha = 0.0;
        if (item.glow.a > 0.0 || backing) {
            float2 step = item.params.z / item.rect.zw;
            float inner = max(item.gradient.x, 0.05);
            float sum = 0.0;
            float weights = 0.0;
            float sum_back = 0.0;
            float weights_back = 0.0;
            for (int ring = 1; ring <= 4; ring++) {
                float radius = float(ring) / 4.0;
                float weight = exp(-2.2 * radius * radius);
                float rb = radius / inner;
                float weight_back = backing ? exp(-2.2 * rb * rb) : 0.0;
                for (int k = 0; k < 12; k++) {
                    float angle = (float(k) + 0.37 * float(ring)) * 0.5235987756;
                    float a = glyphs.sample(linear_sampler, in.uv + float2(cos(angle), sin(angle)) * step * radius).a;
                    sum += a * weight;
                    weights += weight;
                    sum_back += a * weight_back;
                    weights_back += weight_back;
                }
            }
            glow_alpha = clamp(sum / weights * 1.5, 0.0, 1.0) * item.glow.a;
            if (backing) {
                back_alpha = clamp(sum_back / max(weights_back, 1e-6) * 1.5, 0.0, 1.0) * item.fill2.w;
            }
        }
        // Bottom to top: backing, chromatic split (right, left), glow, glyph.
        float3 rgb = float3(0.0);
        float alpha = back_alpha;
        if (backing && item.fill2.y > 0.0) {
            float2 shift = float2(item.fill2.x / item.rect.z, 0.0);
            float right = glyphs.sample(linear_sampler, in.uv - shift).a * item.fill2.y;
            float left = glyphs.sample(linear_sampler, in.uv + shift).a * item.fill2.y;
            rgb = float3(0.235, 0.824, 1.0) * right + rgb * (1.0 - right);
            alpha = right + alpha * (1.0 - right);
            rgb = float3(1.0, 0.235, 0.549) * left + rgb * (1.0 - left);
            alpha = left + alpha * (1.0 - left);
        }
        rgb = item.glow.rgb * glow_alpha + rgb * (1.0 - glow_alpha);
        alpha = glow_alpha + alpha * (1.0 - glow_alpha);
        if (outlined) {
            // The glyph dilated by the outline width: the largest coverage on two rings of 16 taps.
            float2 reach = item.gradient.x / item.rect.zw;
            float grown = coverage;
            for (int k = 0; k < 16; k++) {
                float angle = float(k) * 0.3926990817;
                float2 d = float2(cos(angle), sin(angle)) * reach;
                grown = max(grown, glyphs.sample(linear_sampler, in.uv + d).a);
                grown = max(grown, glyphs.sample(linear_sampler, in.uv + d * 0.5).a);
            }
            float edge = clamp(grown * 1.25, 0.0, 1.0) * item.fill2.a;
            rgb = item.fill2.rgb * edge + rgb * (1.0 - edge);
            alpha = edge + alpha * (1.0 - edge);
        }
        rgb = fill.rgb * text_alpha + rgb * (1.0 - text_alpha);
        alpha = text_alpha + alpha * (1.0 - text_alpha);
        alpha *= keep;
        if (alpha <= 0.0005) {
            discard_fragment();
        }
        return float4(rgb * keep, alpha);
    }
    """
}
