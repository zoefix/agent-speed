namespace AgentSpeed.Core.Perf;

/// <summary>The seven components of the performance score (METRICS 6.9 <i>Components</i>), in the order the sums run.</summary>
public enum PerfComponentId
{
    /// <summary>輸出吞吐: output throughput, tok/s.</summary>
    T,

    /// <summary>首段延遲: first-content latency P50, ms.</summary>
    L,

    /// <summary>行動節奏: tool calls per active minute.</summary>
    N,

    /// <summary>思考速度: reasoning throughput, tok/s.</summary>
    R,

    /// <summary>快取命中: cache hit rate, 0–1.</summary>
    H,

    /// <summary>工具耗時: geometric mean tool time, ms.</summary>
    U,

    /// <summary>工具成功率: tool success, 0–1.</summary>
    S,
}

/// <summary>Why a component is absent (METRICS 6.9 <i>Absent components</i>).</summary>
public enum PerfAbsence
{
    /// <summary>The component is present.</summary>
    None,

    /// <summary>The source never records what the component measures.</summary>
    NotRecorded,

    /// <summary>Too few samples to measure it.</summary>
    FewSamples,
}

/// <summary>
/// One component of an evaluation: its presence, the reason it is absent, its samples and, when present, its measure,
/// its clamped ratio and its sub-score (<c>1000 × ratio</c>).
/// </summary>
public readonly record struct PerfComponent(
    PerfComponentId Id,
    PerfAbsence Reason,
    int Samples,
    double? Measure,
    double? Ratio)
{
    public bool Present => Reason == PerfAbsence.None;

    /// <summary>The score this component alone would give; null when absent.</summary>
    public double? SubScore => Ratio is { } ratio ? 1000 * ratio : null;
}
