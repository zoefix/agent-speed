using AgentSpeed.Presentation.Orb.Gpu.Styles;

namespace AgentSpeed.Presentation.Orb.Gpu;

/// <summary>Creates the GPU draw builder (with its model) of a style.</summary>
public static class OrbStyleDraws
{
    public static IOrbStyleDraw Create(OrbStyle style, OrbScene scene, uint seed) => style switch
    {
        OrbStyle.Needle => new NeedleDraw(seed),
        OrbStyle.Minimal => new MinimalDraw(),
        OrbStyle.Liquid => new LiquidDraw(unchecked((int)seed)),
        OrbStyle.Reactor => new ReactorDraw(unchecked((int)seed)),
        _ => new LedDraw(scene, seed),
    };
}
