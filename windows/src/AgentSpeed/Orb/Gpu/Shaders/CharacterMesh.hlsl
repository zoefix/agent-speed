// The character style (DESIGN 2a.6): the skinned, textured character mesh, its soft lights and its atlas sprites.
// Points are canvas points (y down) mapped like the orb shaders, including the hover scale about the disc center. The
// atlas is decoded premultiplied with mipmaps, so a sample is already premultiplied.

Texture2D character_atlas : register(t3);

cbuffer CharacterMesh : register(b4)
{
    float4 mesh_placement;      // the idle feet on screen (points), points per pose pixel, opacity
    float4 mesh_feet;           // the pose's feet (pose pixels), unused
    float4 mesh_bones[256];     // two rows per bone: a b tx, c d ty
    float4 mesh_opacity[128];   // part opacities in x
};

struct MeshIn
{
    float2 position : POSITION;  // rest position, pose pixels
    float2 uv : TEXCOORD0;       // atlas 0...1
    float4 weights : BLENDWEIGHT;
    uint4 bones : BLENDINDICES;
    uint part : PART;
};

struct MeshOut
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float opacity : TEXCOORD1;
};

MeshOut VsCharacterMesh(MeshIn input)
{
    float3 rest = float3(input.position, 1.0);
    float2 skinned = float2(0.0, 0.0);
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float w = input.weights[i];
        uint b = min(input.bones[i], 127u);
        skinned += w * float2(dot(mesh_bones[2 * b].xyz, rest), dot(mesh_bones[2 * b + 1].xyz, rest));
    }

    float2 p = mesh_placement.xy + (skinned - mesh_feet.xy) * mesh_placement.z;
    MeshOut output;
    output.position = orb_clip_position(p);
    output.uv = input.uv;
    uint part = min(input.part, 127u);
    output.opacity = mesh_opacity[part].x * mesh_placement.w;
    return output;
}

float4 PsCharacterMesh(MeshOut input) : SV_Target
{
    if (input.opacity <= 0.002)
    {
        discard;
    }

    float4 texel = character_atlas.Sample(linear_clamp, input.uv) * input.opacity;
    return float4(texel.rgb * globals.x, texel.a * globals.x);
}

struct DotIn
{
    float4 geometry : INSTANCE0; // center x, y, radius x, y (points)
    float4 color : INSTANCE1;    // straight rgb, alpha
    float4 extra : INSTANCE2;    // softness (>= 0.99: a glow falling as (1 - r)^2)
    uint vid : SV_VertexID;
};

struct DotOut
{
    float4 position : SV_Position;
    float2 local : TEXCOORD0;
    nointerpolation float4 color : TEXCOORD1;
    nointerpolation float softness : TEXCOORD2;
};

DotOut VsCharacterDot(DotIn input)
{
    float2 corner = corners[input.vid];
    DotOut output;
    output.position = orb_clip_position(input.geometry.xy + corner * input.geometry.zw);
    output.local = corner;
    output.color = input.color;
    output.softness = input.extra.x;
    return output;
}

float4 PsCharacterDot(DotOut input) : SV_Target
{
    float r = length(input.local);
    float glow = max(0.0, 1.0 - r);
    float a = input.softness >= 0.99 ? glow * glow : 1.0 - smoothstep(1.0 - input.softness, 1.0, r);
    float alpha = input.color.a * a;
    if (alpha <= 0.0005)
    {
        discard;
    }

    return orb_output(input.color.rgb * alpha, alpha);
}

struct SpriteIn
{
    float4 geometry : INSTANCE0; // center x, y, half width, half height (points)
    float4 uv : INSTANCE1;       // atlas rect: min u, min v, max u, max v
    float4 extra : INSTANCE2;    // rotation (radians, clockwise on screen), opacity
    uint vid : SV_VertexID;
};

struct SpriteOut
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    nointerpolation float alpha : TEXCOORD1;
};

SpriteOut VsCharacterSprite(SpriteIn input)
{
    float2 corner = corners[input.vid];
    float2 d = corner * input.geometry.zw;
    float c = cos(input.extra.x);
    float s = sin(input.extra.x);
    float2 p = input.geometry.xy + float2(d.x * c - d.y * s, d.x * s + d.y * c);
    SpriteOut output;
    output.position = orb_clip_position(p);
    output.uv = lerp(input.uv.xy, input.uv.zw, corner * 0.5 + 0.5);
    output.alpha = input.extra.y;
    return output;
}

float4 PsCharacterSprite(SpriteOut input) : SV_Target
{
    float4 texel = character_atlas.Sample(linear_clamp, input.uv) * input.alpha;
    return float4(texel.rgb * globals.x, texel.a * globals.x);
}
