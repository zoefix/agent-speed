// Text quads from the glyph atlas (R: coverage, G: the coverage blurred for the glow), tinted, sheared, clipped to a rect,
// a moving band or one side of the liquid surface.

Texture2D glyphs : register(t1);

struct TextIn
{
    float4 rect : INSTANCE0;     // x, y (top-left), width, height in points
    float4 uv : INSTANCE1;       // atlas u0, v0, u1, v1
    float4 color : INSTANCE2;    // straight alpha
    float4 glow : INSTANCE3;     // rgb, strength
    float4 params : INSTANCE4;   // shear, shear pivot y, unused, clip mode (0 none, 1 rect, 2 above / 3 below the liquid, 4 band)
    float4 clip : INSTANCE5;     // clip rect min x, min y, max x, max y, or band center x, center y, half width, slope dx/dy
    float4 fill2 : INSTANCE6;    // bottom color of a vertical gradient (straight alpha)
    float4 gradient : INSTANCE7; // top y, bottom y, enabled, unused
    float4 rotate : INSTANCE8;   // rotation (radians, clockwise on screen) about the pivot x, y; 0 upright
    uint vid : SV_VertexID;
};

struct TextOut
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float2 canvas : TEXCOORD1;
    nointerpolation float4 color : TEXCOORD2;
    nointerpolation float4 glow : TEXCOORD3;
    nointerpolation float4 params : TEXCOORD4;
    nointerpolation float4 clip : TEXCOORD5;
    nointerpolation float4 fill2 : TEXCOORD6;
    nointerpolation float4 gradient : TEXCOORD7;
};

TextOut VsText(TextIn input)
{
    float2 t = corners[input.vid] * 0.5 + 0.5;
    // Unsheared text lands on whole device pixels, so atlas texels map one to one (crisp at the resting scale).
    float2 origin = input.params.x == 0.0 && input.rotate.x == 0.0 ? round(input.rect.xy * viewport.z) / viewport.z : input.rect.xy;
    float2 p = origin + t * input.rect.zw;
    p.x += input.params.x * (input.params.y - p.y);
    if (input.rotate.x != 0.0)
    {
        float c = cos(input.rotate.x);
        float s = sin(input.rotate.x);
        float2 d = p - input.rotate.yz;
        p = input.rotate.yz + float2(d.x * c - d.y * s, d.x * s + d.y * c);
    }
    TextOut output;
    output.position = orb_clip_position(p);
    output.uv = lerp(input.uv.xy, input.uv.zw, t);
    output.canvas = p;
    output.color = input.color;
    output.glow = input.glow;
    output.params = input.params;
    output.clip = input.clip;
    output.fill2 = input.fill2;
    output.gradient = input.gradient;
    return output;
}

float4 PsText(TextOut input) : SV_Target
{
    float mode = input.params.w;
    float aa = 1.0 / viewport.z;
    float keep = 1.0;
    if (mode > 3.5)
    {
        float dx = input.canvas.x - (input.clip.x + (input.canvas.y - input.clip.y) * input.clip.w);
        keep = max(0.0, 1.0 - abs(dx) / max(input.clip.z, 0.0001));
    }
    else if (mode > 0.5)
    {
        float2 lo = input.clip.xy - input.canvas;
        float2 hi = input.canvas - input.clip.zw;
        float outside = max(max(lo.x, lo.y), max(hi.x, hi.y));
        keep = saturate(0.5 - outside / aa);
    }

    if (mode > 1.5 && mode < 3.5)
    {
        float surface = orb_liquid_surface_y(input.canvas.x);
        float below = saturate((input.canvas.y - surface) / aa + 0.5);
        keep *= mode > 2.5 ? below : 1.0 - below;
    }

    if (keep <= 0.0005)
    {
        discard;
    }

    float2 atlas = glyphs.SampleLevel(linear_clamp, input.uv, 0).rg;
    float4 fill = input.color;
    if (input.gradient.z > 0.5)
    {
        float t = saturate((input.canvas.y - input.gradient.x) / max(input.gradient.y - input.gradient.x, 0.0001));
        fill = lerp(input.color, input.fill2, t);
    }

    float text_alpha = atlas.r * fill.a;
    float glow_alpha = input.glow.a > 0.0 ? saturate(atlas.g * 1.5) * input.glow.a * (1.0 - text_alpha) : 0.0;
    float alpha = (text_alpha + glow_alpha) * keep;
    if (alpha <= 0.0005)
    {
        discard;
    }

    return orb_output((fill.rgb * text_alpha + input.glow.rgb * glow_alpha) * keep, alpha);
}
