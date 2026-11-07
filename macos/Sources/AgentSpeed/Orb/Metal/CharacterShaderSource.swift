/// Shaders of the `character` style (DESIGN 2a.6): the skinned character mesh, its soft lights and its atlas sprites.
/// Points are canvas points (y down) mapped like the orb shaders, including the hover scale about the disc center.
enum CharacterShaderSource {
    static let source = """
    #include <metal_stdlib>
    using namespace metal;

    struct Uniforms {
        float4 viewport;  // width, height (points), scale (pixels per point), unused
        float4 transform; // hover scale about (y, z), unused
    };

    constant float2 corners[6] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(1, -1), float2(1, 1), float2(-1, 1) };

    static float4 character_clip_position(float2 point, constant Uniforms &uniforms) {
        float2 center = uniforms.transform.yz;
        float2 scaled = center + (point - center) * uniforms.transform.x;
        return float4(scaled.x / uniforms.viewport.x * 2.0 - 1.0, 1.0 - scaled.y / uniforms.viewport.y * 2.0, 0, 1);
    }

    struct CharacterVertex {
        float2 position;  // rest position, canvas pixels of the pose
        float2 uv;        // atlas, 0...1
        float4 weights;
        ushort4 bones;
        uint part;
        uint pad;
    };

    struct MeshParams {
        float4 placement; // the idle feet on screen (points), points per pose pixel, opacity
        float4 feet;      // the pose's feet (pose pixels), unused
    };

    struct MeshOut {
        float4 position [[position]];
        float2 uv;
        float opacity;
    };

    vertex MeshOut character_mesh_vertex(uint vid [[vertex_id]],
                                         const device CharacterVertex *vertices [[buffer(0)]],
                                         const device float4 *bones [[buffer(1)]],
                                         const device float *opacities [[buffer(2)]],
                                         constant MeshParams &params [[buffer(3)]],
                                         constant Uniforms &uniforms [[buffer(4)]]) {
        CharacterVertex v = vertices[vid];
        float3 rest = float3(v.position, 1.0);
        float2 skinned = float2(0.0);
        for (int i = 0; i < 4; i++) {
            float w = v.weights[i];
            if (w > 0.0) {
                uint b = v.bones[i];
                skinned += w * float2(dot(bones[2 * b].xyz, rest), dot(bones[2 * b + 1].xyz, rest));
            }
        }
        float2 point = params.placement.xy + (skinned - params.feet.xy) * params.placement.z;
        MeshOut out;
        out.position = character_clip_position(point, uniforms);
        out.uv = v.uv;
        out.opacity = opacities[v.part] * params.placement.w;
        return out;
    }

    fragment float4 character_mesh_fragment(MeshOut in [[stage_in]],
                                            texture2d<float> atlas [[texture(0)]],
                                            sampler smooth [[sampler(0)]]) {
        if (in.opacity <= 0.002) {
            discard_fragment();
        }
        return atlas.sample(smooth, in.uv) * in.opacity;
    }

    struct Dot {
        float4 geometry; // center x, y, radius x, y (points)
        float4 color;    // straight rgb, alpha
        float4 extra;    // softness (>= 0.99: a glow falling as (1 - r)^2), unused
    };

    struct DotOut {
        float4 position [[position]];
        float2 local;
        float4 color;
        float softness;
    };

    vertex DotOut character_dot_vertex(uint vid [[vertex_id]], uint iid [[instance_id]],
                                       const device Dot *dots [[buffer(0)]],
                                       constant Uniforms &uniforms [[buffer(1)]]) {
        Dot dot = dots[iid];
        float2 corner = corners[vid];
        DotOut out;
        out.position = character_clip_position(dot.geometry.xy + corner * dot.geometry.zw, uniforms);
        out.local = corner;
        out.color = dot.color;
        out.softness = dot.extra.x;
        return out;
    }

    fragment float4 character_dot_fragment(DotOut in [[stage_in]]) {
        float r = length(in.local);
        float a = in.softness >= 0.99 ? pow(max(0.0, 1.0 - r), 2.0) : 1.0 - smoothstep(1.0 - in.softness, 1.0, r);
        float alpha = in.color.a * a;
        return float4(in.color.rgb * alpha, alpha);
    }

    struct Sprite {
        float4 geometry; // center x, y, half width, half height (points)
        float4 uv;       // atlas rect: min u, min v, max u, max v
        float4 extra;    // rotation (radians, clockwise on screen), opacity
    };

    struct SpriteOut {
        float4 position [[position]];
        float2 uv;
        float alpha;
    };

    vertex SpriteOut character_sprite_vertex(uint vid [[vertex_id]], uint iid [[instance_id]],
                                             const device Sprite *sprites [[buffer(0)]],
                                             constant Uniforms &uniforms [[buffer(1)]]) {
        Sprite s = sprites[iid];
        float2 corner = corners[vid];
        float2 d = corner * s.geometry.zw;
        float c = cos(s.extra.x);
        float n = sin(s.extra.x);
        float2 point = s.geometry.xy + float2(d.x * c - d.y * n, d.x * n + d.y * c);
        SpriteOut out;
        out.position = character_clip_position(point, uniforms);
        out.uv = mix(s.uv.xy, s.uv.zw, corner * 0.5 + 0.5);
        out.alpha = s.extra.y;
        return out;
    }

    fragment float4 character_sprite_fragment(SpriteOut in [[stage_in]],
                                              texture2d<float> atlas [[texture(0)]],
                                              sampler smooth [[sampler(0)]]) {
        return atlas.sample(smooth, in.uv) * in.alpha;
    }
    """
}
