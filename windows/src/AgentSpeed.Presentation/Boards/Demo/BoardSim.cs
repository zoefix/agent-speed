using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Hero;

namespace AgentSpeed.Presentation.Boards.Demo;

/// <summary>The stories of <c>spec/prototypes/board/sim.js</c>.</summary>
public enum BoardSimScenario
{
    /// <summary>One workflow with 100 sub-agents plus three projects.</summary>
    Swarm,
    Team,

    /// <summary>Two leaders trading places every few seconds.</summary>
    Duel,
    Calm,
}

/// <summary>One simulated agent (sim.js <c>makeAgent</c>).</summary>
public sealed class BoardSimAgent
{
    internal readonly double[] Window = new double[6];
    internal int WindowCount;

    public required string Id { get; init; }

    public string? ParentId { get; init; }

    public required string Project { get; init; }

    public required string Label { get; init; }

    public required SourceKind Source { get; init; }

    public required string Model { get; init; }

    /// <summary>The effort value of sim.js (for the capsule seam of phase 2), null when the source reports none.</summary>
    public string? Effort { get; internal set; }

    /// <summary>The speed mode of sim.js (<c>fast</c>, <c>ultrafast</c>…), null for standard.</summary>
    public string? SpeedMode { get; internal set; }

    internal double Base;
    internal double Burst = 1;
    internal double PhaseLeft;
    internal double Instant;
    internal bool Lazy;

    public AgentPhase Phase { get; internal set; } = AgentPhase.Idle;

    public double Live { get; internal set; }

    public double LastActive { get; internal set; }
}

/// <summary>
/// A C# port of <c>spec/prototypes/board/sim.js</c> (same seeded xorshift, same draws in the same order): roots and
/// sub-agents that switch between generating, tools and idle with a 3 s live window per agent, ticking every 500 ms. The
/// demo and the benchmark feed its agents through the real grouping, ranking and layout plan. <see cref="TargetTotal"/>
/// scales every live value so the sum follows a script (idle, climb, redline); <see cref="Quiet"/> stops every agent.
/// </summary>
public sealed class BoardSim
{
    public const double TickSeconds = 0.5;

    public const int HistoryTicks = 120;

    private static readonly Dictionary<string, (double Lo, double Hi)> Models = new(StringComparer.Ordinal)
    {
        ["claude-opus-5-5"] = (55, 115),
        ["claude-sonnet-5-5"] = (80, 160),
        ["claude-haiku-5-5"] = (150, 260),
        ["gpt-6.1-sol"] = (90, 210),
        ["deepseek-flash"] = (110, 180),
        ["gemini-3.5-pro"] = (100, 190),
    };

    // sim.js: output list prices (USD per million tokens) for the live cost and the model class (METRICS 6.9, 9.5).
    private static readonly Dictionary<string, double> Prices = new(StringComparer.Ordinal)
    {
        ["claude-opus-5-5"] = 20,
        ["claude-sonnet-5-5"] = 10,
        ["claude-haiku-5-5"] = 0.5,
        ["gpt-6.1-sol"] = 20,
        ["deepseek-flash"] = 1.2,
        ["gemini-3.5-pro"] = 12,
    };

    private static readonly Dictionary<string, int> EffortLevels = new(StringComparer.Ordinal)
    {
        ["off"] = 0, ["minimal"] = 1, ["low"] = 1, ["medium"] = 2, ["high"] = 3, ["xhigh"] = 4, ["max"] = 5, ["ultra"] = 5, ["ultracode"] = 5,
    };

    private static readonly string[] Ladder = ["medium", "high", "xhigh", "max"];
    private static readonly string?[] CodexSpeeds = ["fast", "ultrafast", null];

    private readonly List<BoardSimAgent> _agents = [];
    private readonly List<double> _history = new(HistoryTicks);
    private readonly uint _seed;
    private HeroRandom _random = new(1);

    public BoardSim(BoardSimScenario scenario = BoardSimScenario.Swarm, uint seed = 7)
    {
        _seed = seed;
        Reset(scenario);
    }

    public BoardSimScenario Scenario { get; private set; }

    public IReadOnlyList<BoardSimAgent> Agents => _agents;

    /// <summary>Simulated seconds since the story started.</summary>
    public double Time { get; private set; }

    /// <summary>The sum of every agent's live value after the last tick.</summary>
    public double Total { get; private set; }

    public double Today { get; private set; }

    /// <summary>Today's cost in dollars (sim.js: 15 µ$ per token).</summary>
    public double Cost { get; private set; }

    public double Peak { get; private set; }

    /// <summary>The total after each tick, oldest first, at most <see cref="HistoryTicks"/> (sim.js <c>history</c>).</summary>
    public IReadOnlyList<double> History => _history;

    /// <summary>When set, every live value is scaled so the total is this (a scripted climb).</summary>
    public double? TargetTotal { get; set; }

    /// <summary>Every agent idles (the idle part of a script).</summary>
    public bool Quiet { get; set; }

    /// <summary>No source reports reasoning apart from output (board-metrics.html <c>noreasoning=1</c>): 思考 reads 0.</summary>
    public bool NoReasoning { get; set; }

    public void Reset(BoardSimScenario scenario)
    {
        Scenario = scenario;
        var name = scenario.ToString().ToLowerInvariant();
        _random = new HeroRandom((uint)(_seed + name.Length * 101));
        _agents.Clear();
        Time = 0;
        Today = 128.4e6;
        Cost = 42.1;
        Peak = 9120;
        _history.Clear();
        var r = 0;
        foreach (var root in Roots(scenario))
        {
            var rootId = "a" + r++;
            _agents.Add(Make(rootId, null, root, root.Project));
            for (var i = 0; i < root.Subs; i++)
            {
                var label = root.Workflow ? "count-" + (i + 1) : new[] { "explore", "review", "tests", "docs" }[i % 4] + (i >= 4 ? "-" + i : "");
                var sub = Make(rootId + "s" + i, rootId, root, label);
                if (root.Workflow)
                {
                    sub.PhaseLeft = _random.Next() * 3;
                    sub.Base *= 0.75 + _random.Next() * 0.4;
                }

                _agents.Add(sub);
            }
        }

        for (var i = 0; i < 6; i++)
        {
            Advance(silent: true);
        }
    }

    /// <summary>One 500 ms tick.</summary>
    public void Step() => Advance(silent: false);

    /// <summary>The agents as the engine reports them, at <paramref name="epoch"/> + the simulated time.</summary>
    public IReadOnlyList<AgentStatus> Statuses(DateTimeOffset epoch)
    {
        var list = new AgentStatus[_agents.Count];
        for (var i = 0; i < _agents.Count; i++)
        {
            var a = _agents[i];
            var activity = epoch.AddSeconds(a.LastActive);
            // A sub-agent's label stands in its project, which is what tells it apart on a sub row.
            var (intelligence, _) = Measure(a);
            var score = PerfSetValue.Of(a.Id, (long)(a.LastActive * 1000), Score(a));
            list[i] = new AgentStatus(a.Id, a.Source, a.ParentId is null ? a.Project : a.Label, a.Model, a.Phase, a.ParentId is not null,
                new Rate(a.Live, a.Live * 4), null, activity, a.ParentId)
            {
                Modes = Modes(a),
                Intelligence = new AgentIntelligence(intelligence with { Score = score }, score),
            };
        }

        return list;
    }

    /// <summary>Each agent's source, live output (tok/s) and live cost, for the demo hero's 費用 (the engine keeps no cost per agent).</summary>
    public IReadOnlyList<(SourceKind Source, double Live, LiveCost Cost)> Costs()
    {
        var list = new (SourceKind, double, LiveCost)[_agents.Count];
        for (var i = 0; i < _agents.Count; i++)
        {
            var a = _agents[i];
            list[i] = (a.Source, a.Live, Measure(a).Cost);
        }

        return list;
    }

    /// <summary>
    /// A demo performance score (METRICS 6.9) from the agent's id alone: about one agent in six has none yet, the rest a score
    /// from about 500 to 4,600 (log-uniform, around the reference 1000), one in five of them provisional; resting while idle.
    /// </summary>
    private static PerfEvaluation Score(BoardSimAgent a)
    {
        var seed = HashUnit(a.Id + "#perf");
        var empty = PerfEvaluation.Empty(a.Source);
        if (seed < 0.16)
        {
            return empty;
        }

        var score = Math.Round(500 * Math.Pow(2, (seed - 0.16) / 0.84 * 3.2), 2);
        var form = a.Phase == AgentPhase.Idle ? PerfForm.Resting : PerfForm.Live;
        var samples = HashUnit(a.Id + "#n") < 0.2 ? 6 : PerfFormula.SampleSize;
        return empty with { Form = form, Score = score, N = samples };
    }

    /// <summary>
    /// sim.js <c>measure</c>: the agent's reasoning (by effort, breathing ±20 % over ~7 s; none for DeepSeek, which reports
    /// reasoning inside its output) as window sums, and its live cost (Ultrafast has no list price; Fast bills 2×). Derived
    /// from the state only, never from the random sequence.
    /// </summary>
    private (IntelligenceSums Sums, LiveCost Cost) Measure(BoardSimAgent a)
    {
        var level = a.Effort is { } effort && EffortLevels.TryGetValue(effort, out var l) ? l + (effort is "ultra" or "ultracode" ? 1 : 0) : 2;
        var price = Prices.GetValueOrDefault(a.Model);
        var seed = HashUnit(a.Id);
        var records = IntelligenceSums.RecordsReasoning(a.Source) && !NoReasoning;
        var breath = 0.8 + 0.4 * (0.5 + 0.5 * Math.Sin(Time * 0.9 + seed * 6.283));
        var thinking = records ? a.Live * (0.07 + 0.05 * level) * (0.7 + 0.6 * seed) * breath : 0;
        var window = WindowSeconds * a.Live;
        var sums = window > 0 ? new IntelligenceSums(window, thinking * WindowSeconds, IntelligenceSums.RecordsReasoning(a.Source) ? window : 0)
            : IntelligenceSums.Zero;
        var priced = a.SpeedMode != "ultrafast";
        var bolts = a.SpeedMode is "fast" or "highspeed" ? 1 : a.SpeedMode == "ultrafast" ? 2 : 0;
        var rate = priced ? a.Live * price * (1.3 + 0.6 * seed) * (bolts > 0 ? 2 : 1) : 0;
        return (sums, new LiveCost(rate, priced ? a.Live : 0));
    }

    /// <summary>The engine's live window in seconds (the sums are tokens over it).</summary>
    private const double WindowSeconds = 3;

    // sim.js hashUnit: FNV-1a over the id's UTF-16 units, then a murmur finalizer, as a fraction of 2^32.
    internal static double HashUnit(string text)
    {
        var h = 2166136261u;
        foreach (var c in text)
        {
            h ^= c;
            h *= 16777619u;
        }

        h ^= h >> 16;
        h *= 0x85ebca6bu;
        h ^= h >> 13;
        h *= 0xc2b2ae35u;
        h ^= h >> 16;
        return h / 4294967296.0;
    }

    private static ExecutionModes Modes(BoardSimAgent a)
    {
        var effort = ExecutionModeFact.Create(ExecutionModeKind.Effort, a.Effort, ExecutionModeEvidence.Recorded);
        var speed = ExecutionModeFact.Create(ExecutionModeKind.ServiceTier, a.SpeedMode, ExecutionModeEvidence.Recorded);
        return effort is null && speed is null ? ExecutionModes.Empty : new ExecutionModes(new[] { effort, speed }.OfType<ExecutionModeFact>());
    }

    private BoardSimAgent Make(string id, string? parentId, Root root, string label)
    {
        var (lo, hi) = Models[root.Model];
        var agent = new BoardSimAgent
        {
            Id = id,
            ParentId = parentId,
            Project = root.Project,
            Label = label,
            Source = root.Source,
            Model = root.Model,
            Effort = parentId is not null && root.Workflow ? null : root.Effort,
            SpeedMode = root.Speed,
            Lazy = root.Lazy,
        };
        agent.Base = lo + (hi - lo) * _random.Next();
        agent.PhaseLeft = 1 + _random.Next() * 6;
        _ = _random.Next(); // today's output of the agent in sim.js
        return agent;
    }

    private void NextPhase(BoardSimAgent a)
    {
        var busy = a.Lazy ? 0.25 : a.ParentId is not null ? 0.72 : 0.6;
        if (a.Phase == AgentPhase.Generating)
        {
            a.Phase = _random.Next() < 0.8 ? AgentPhase.Tools : AgentPhase.Idle;
            a.PhaseLeft = a.Phase == AgentPhase.Tools ? 0.6 + _random.Next() * 3.5 : 2 + _random.Next() * 8;
        }
        else
        {
            a.Phase = _random.Next() < busy ? AgentPhase.Generating : a.Phase == AgentPhase.Tools ? AgentPhase.Idle : AgentPhase.Tools;
            a.PhaseLeft = a.Phase == AgentPhase.Generating ? 2.5 + _random.Next() * 12 : 0.8 + _random.Next() * 5;
            if (a.Phase == AgentPhase.Generating)
            {
                a.Burst = 0.75 + _random.Next() * 0.55;
            }
        }
    }

    private void Advance(bool silent)
    {
        Time += TickSeconds;
        var duel = Scenario == BoardSimScenario.Duel;
        var raw = 0.0;
        foreach (var a in _agents)
        {
            a.PhaseLeft -= TickSeconds;
            if (a.PhaseLeft <= 0)
            {
                NextPhase(a);
            }

            if (Quiet)
            {
                a.Phase = AgentPhase.Idle;
            }

            var wobble = 0.82 + 0.36 * _random.Next();
            var target = a.Phase == AgentPhase.Generating ? a.Base * a.Burst * wobble : 0;
            if (duel && a.ParentId is null)
            {
                target *= 1 + 0.55 * Math.Sin(Time / 3.1 + (a.Id == "a0" ? 0 : Math.PI));
            }

            a.Instant += (target - a.Instant) * (a.Phase == AgentPhase.Generating ? 0.6 : 0.85);
            if (a.Instant < 0.05)
            {
                a.Instant = 0;
            }

            if (a.WindowCount == a.Window.Length)
            {
                Array.Copy(a.Window, 1, a.Window, 0, a.Window.Length - 1);
                a.WindowCount--;
            }

            a.Window[a.WindowCount++] = a.Instant;
            var sum = 0.0;
            for (var i = 0; i < a.WindowCount; i++)
            {
                sum += a.Window[i];
            }

            a.Live = sum / 6 < 0.05 ? 0 : sum / 6;
            raw += a.Live;
        }

        if (!silent && _random.Next() < 0.04)
        {
            ChangeModes();
        }

        var scale = TargetTotal is { } goal && raw > 0 ? goal / raw : 1;
        var total = 0.0;
        foreach (var a in _agents)
        {
            a.Live *= scale;
            if (a.Live > 0)
            {
                a.LastActive = Time;
            }

            total += a.Live;
        }

        Total = total;
        Today += total * TickSeconds;
        Cost += total * TickSeconds * 15e-6;
        Peak = Math.Max(Peak, total);
        if (_history.Count == HistoryTicks)
        {
            _history.RemoveAt(0);
        }

        _history.Add(total);
    }

    private void ChangeModes()
    {
        var roots = _agents.Where(a => a.ParentId is null).ToArray();
        var a = roots[(int)Math.Floor(_random.Next() * roots.Length)];
        if (a.Effort is { } effort && effort is not ("ultra" or "ultracode"))
        {
            a.Effort = Ladder[(int)Math.Floor(_random.Next() * Ladder.Length)];
        }

        if (a.Source == SourceKind.Codex)
        {
            a.SpeedMode = CodexSpeeds[(int)Math.Floor(_random.Next() * CodexSpeeds.Length)];
        }
    }

    private sealed record Root(string Project, SourceKind Source, string Model, string? Effort, string? Speed, int Subs, bool Workflow = false,
        bool Lazy = false);

    private static Root[] Roots(BoardSimScenario scenario) => scenario switch
    {
        BoardSimScenario.Duel =>
        [
            new("opencdn", SourceKind.ClaudeCode, "claude-opus-5-5", "max", null, 2),
            new("claude-manager", SourceKind.Codex, "gpt-6.1-sol", "ultra", "ultrafast", 2),
        ],
        BoardSimScenario.Team =>
        [
            new("opencdn", SourceKind.ClaudeCode, "claude-opus-5-5", "max", null, 3),
            new("claude-manager", SourceKind.Codex, "gpt-6.1-sol", "xhigh", "fast", 2),
            new("agent-speed", SourceKind.ClaudeCode, "claude-opus-5-5", "ultracode", null, 4),
            new("nebula", SourceKind.OpenCode, "claude-sonnet-5-5", "high", null, 0),
            new("rain", SourceKind.DeepSeek, "deepseek-flash", null, null, 0),
        ],
        BoardSimScenario.Calm =>
        [
            new("agent-speed", SourceKind.ClaudeCode, "claude-opus-5-5", "xhigh", null, 0, Lazy: true),
            new("notes", SourceKind.Codex, "gpt-6.1-sol", "medium", null, 0, Lazy: true),
        ],
        _ =>
        [
            new("tihu", SourceKind.ClaudeDesktop, "deepseek-flash", null, null, 100, Workflow: true),
            new("opencdn", SourceKind.ClaudeCode, "claude-opus-5-5", "max", null, 3),
            new("claude-manager", SourceKind.Codex, "gpt-6.1-sol", "ultra", "ultrafast", 2),
            new("agent-speed", SourceKind.ClaudeCode, "claude-opus-5-5", "ultracode", null, 1),
        ],
    };
}
