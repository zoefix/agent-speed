using System.Windows.Threading;
using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Perf;

namespace AgentSpeed.Services;

/// <summary>
/// Developer option <c>--demo-speed 24,96,400</c>: holds the hub (as a preview) at each speed in turn for a few seconds,
/// with a small fixed set of agents in every phase, so speed titles and the zero-speed glyphs can be inspected. The agents
/// also cover every intelligence form (live, resting, none), a sub-agent two levels deep with the widest capsule, and a
/// source without separate reasoning; at 0 tok/s every row rests and the taskbar block is quiet.
/// </summary>
internal sealed class PinnedSpeedDriver : IDisposable
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(3.5);

    /// <summary>The live window (METRICS 6.3) the agents' intelligence sums cover.</summary>
    private const double WindowSeconds = 3;

    private static readonly PinnedAgent[] Agents =
    [
        new("pinned-atlas", SourceKind.Codex, "atlas", "gpt-6.1-sol", AgentPhase.Generating, 0.45, "xhigh", "fast", 0.3),
        new("pinned-quasar", SourceKind.DeepSeek, "quasar", "deepseek-flash", AgentPhase.Generating, 0.25, "high", null, 0),
        new("pinned-nova", SourceKind.ClaudeCode, "nova", "claude-opus-5-5", AgentPhase.Tools, 0, "max", null, 0),
        new("pinned-nova-sub", SourceKind.ClaudeCode, "nova", "claude-sonnet-5-5", AgentPhase.Generating, 0.3, "medium", null, 0.2, "pinned-nova"),
        new("pinned-nova-deep", SourceKind.ClaudeCode, "nova", "claude-haiku-5", AgentPhase.Idle, 0, "ultracode", "ultrafast", 0, "pinned-nova-sub"),
        new("pinned-orbit", SourceKind.ClaudeDesktop, "orbit", null, AgentPhase.Idle, 0, null, null, 0),
    ];

    private readonly SpeedHub _hub;
    private readonly IReadOnlyList<double> _speeds;
    private readonly DispatcherTimer _timer;
    private readonly List<HistoryPoint> _history = [];
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private double _peak;

    /// <summary>The agents report reasoning; off, the thinking rate of every surface reads 0 (DESIGN 10, Thinking hidden at zero).</summary>
    public bool Reasoning { get; set; } = true;

    public PinnedSpeedDriver(SpeedHub hub, Dispatcher dispatcher, IReadOnlyList<double> speeds)
    {
        _hub = hub;
        _speeds = speeds;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Publish(), dispatcher);
        _timer.Stop();
    }

    public void Start()
    {
        if (_speeds.Count == 0)
        {
            return;
        }

        _hub.BeginPreview();
        Publish();
        _timer.Start();
    }

    public void Dispose()
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            _hub.EndPreview();
        }
    }

    private void Publish()
    {
        var now = DateTimeOffset.Now;
        var index = (int)((now - _startedAt).Ticks / Step.Ticks % _speeds.Count);
        var speed = _speeds[index];
        _peak = Math.Max(_peak, speed);

        var second = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, now.Offset);
        if (_history.Count == 0 || _history[^1].Time < second)
        {
            _history.Add(new HistoryPoint(second, new Rate(speed, speed * 24), Rate.Zero));
            if (_history.Count > 300)
            {
                _history.RemoveAt(0);
            }
        }

        var agents = Agents.Select(a =>
        {
            var live = a.Share * speed;
            var window = live * WindowSeconds;
            var score = a.Score;
            var sums = IntelligenceSums.Zero.Add(a.Source, window, Reasoning ? window * a.Thinking : 0) with { Score = score };
            return new AgentStatus(a.Id, a.Source, a.Project, a.Model, a.Phase, a.ParentId is not null, new Rate(live, live * 24),
                live > 0 ? live : null, now, a.ParentId)
            {
                Modes = a.Modes,
                Intelligence = new AgentIntelligence(sums, score),
            };
        }).ToArray();
        var today = new TokenTotals(120, 4_800_000, 4_100_000, 0, 96_000, 0);
        var sources = Agents.Select(a => a.Source).Distinct().Select(kind =>
        {
            var live = agents.Where(a => a.Source == kind).Sum(a => a.Live.Output);
            return new SourceStatus(kind, true, string.Empty, true, 3, new Rate(live, live * 24), today,
                agents.Count(a => a.Source == kind && a.Phase == AgentPhase.Generating))
            {
                Intelligence = agents.Where(a => a.Source == kind).Aggregate(IntelligenceSums.Zero, (sum, a) => sum + a.Intelligence.Sums),
            };
        }).ToArray();

        _hub.PublishPreview(new SpeedSnapshot(
            now,
            MonitorState.Running,
            new Rate(speed, speed * 24),
            0,
            new Rate(_peak, _peak * 24),
            _peak > 0 ? _startedAt : null,
            _peak > 0 ? _peak * 0.6 : null,
            today,
            sources,
            agents,
            agents.Count(a => a.Phase == AgentPhase.Generating),
            agents.Count(a => a.Phase == AgentPhase.Tools),
            _history.ToArray(),
            null,
            []));
    }

    /// <param name="Thinking">The reasoning share of its output while it generates.</param>
    private sealed record PinnedAgent(
        string Id, SourceKind Source, string Project, string? Model, AgentPhase Phase, double Share, string? Effort, string? Speed,
        double Thinking, string? ParentId = null)
    {
        /// <summary>A pinned performance score (METRICS 6.9) from the agent's id, 500 to about 4,700: none without a model.</summary>
        public PerfSetValue Score => Model is null ? PerfSetValue.None
            : new PerfSetValue(Phase == AgentPhase.Idle ? PerfForm.Resting : PerfForm.Live, Math.Round(500 * Math.Pow(2, HashScore(Id) / 30.0), 2), Id);

        // FNV-1a over the id's UTF-16 units: the same score on every run.
        private static double HashScore(string id)
        {
            var hash = 2166136261u;
            foreach (var c in id)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return hash % 9600 / 100.0;
        }

        public ExecutionModes Modes { get; } = new ExecutionModes(new[]
        {
            ExecutionModeNative.Effort(Effort, ExecutionModeEvidence.Recorded, ExecutionModeScope.Request),
            ExecutionModeFact.Create(ExecutionModeKind.Speed, Speed, ExecutionModeEvidence.Recorded, scope: ExecutionModeScope.Request),
        }.OfType<ExecutionModeFact>());
    }
}
