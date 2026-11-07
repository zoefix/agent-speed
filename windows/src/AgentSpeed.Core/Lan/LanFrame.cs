using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Lan;

/// <summary>One scope's values as a frame writes them (LAN 3.1): rounded as METRICS 11.7, clamped as reports are.</summary>
public readonly record struct LanScope(string Phase, double OutputTokS, double EstimatedShare, int Active, int Generating, int Tools)
{
    public static LanScope Of(LanLiveValues values)
    {
        var generating = Count(values.Generating);
        var tools = Count(values.Tools);
        return new LanScope(PhaseOf(generating, tools), LanFrame.TokS(values.OutputTokS), LanFrame.Share(values.EstimatedShare), Count(values.Active),
            generating, tools);
    }

    public static string PhaseOf(int generating, int tools) => generating > 0 ? "generating" : tools > 0 ? "tools" : "idle";

    private static int Count(long value) => (int)Math.Clamp(value, 0, ReportBody.MaxCount);

    internal IEnumerable<(string, ReportJson)> Members() =>
    [
        ("phase", ReportJson.Of(Phase)),
        ("outputTokS", ReportJson.Decimal(OutputTokS, 1)),
        ("estimatedShare", ReportJson.Decimal(EstimatedShare, 3)),
    ];

    internal ReportJson Sessions => ReportJson.Object(
        ("active", ReportJson.Of(Active)), ("generating", ReportJson.Of(Generating)), ("tools", ReportJson.Of(Tools)));
}

/// <summary>
/// The frame of LAN 3: the tick's live values overall and per source with activity, a minute of history, the computer's
/// name and the two random ids. Written compact with the members in order, numbers as reports write them, so the same
/// values give the same bytes on every platform.
/// </summary>
public sealed class LanFrame
{
    public const int Schema = 1;
    public const int HistoryStepMs = 1000;
    public const int MaxBytes = 16_384;

    private static readonly SourceKind[] SourceOrder = Enum.GetValues<SourceKind>();
    private readonly string _content;

    private LanFrame(string instance, string name, string run, long seq, long atMs, LanScope overall, IReadOnlyList<(SourceKind Kind, LanScope Values)> sources,
        IReadOnlyList<double> history)
    {
        Instance = instance;
        Name = name;
        Run = run;
        Seq = seq;
        AtMs = atMs;
        Overall = overall;
        Sources = sources;
        History = history;
        Bytes = Json().ToCompactBytes();
        _content = Json(seq: false).ToCompactString();
    }

    public string Instance { get; }

    public string Name { get; }

    public string Run { get; }

    public long Seq { get; }

    public long AtMs { get; }

    public LanScope Overall { get; }

    public IReadOnlyList<(SourceKind Kind, LanScope Values)> Sources { get; }

    public IReadOnlyList<double> History { get; }

    /// <summary>The frame's UTF-8 bytes, as HTTP, WebSocket and the line protocol send them.</summary>
    public byte[] Bytes { get; }

    /// <summary>LAN 3.1 <c>estimated</c>: the rounded share is above 0.</summary>
    public bool Estimated => Overall.EstimatedShare > 0;

    /// <param name="name">The raw computer name (trimmed and cut here).</param>
    /// <param name="history">The history samples, already rounded (LAN 3.4).</param>
    public static LanFrame Build(string instance, string name, string run, long seq, long atMs, LanTickValues values, IReadOnlyList<double> history)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(history);
        var sources = new List<(SourceKind, LanScope)>();
        foreach (var kind in SourceOrder)
        {
            if (!values.Sources.TryGetValue(kind, out var raw))
            {
                continue;
            }

            var scope = LanScope.Of(raw);
            if (scope.Active > 0 || scope.OutputTokS > 0)
            {
                sources.Add((kind, scope));
            }
        }

        return new LanFrame(instance, LanText.ComputerName(name), run, seq, atMs, LanScope.Of(values.Live), sources, [.. history]);
    }

    /// <summary>LAN 3.3: any member other than <c>seq</c> and <c>at</c> differs from <paramref name="previous"/>'s.</summary>
    public bool ChangedFrom(LanFrame? previous) => previous is null || previous._content != _content;

    /// <summary>The frame as JSON, optionally without <c>history</c> or <c>sources</c> (the datagram size rule, LAN 3.5).</summary>
    public ReportJson Json(bool history = true, bool sources = true) => Json(seq: true, history, sources);

    public static double TokS(double value) => ReportJson.Round(Math.Clamp(double.IsFinite(value) ? value : 0, 0, ReportBody.MaxTokS), 1);

    public static double Share(double value) => ReportJson.Round(Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1), 3);

    private ReportJson Json(bool seq, bool history = true, bool sources = true)
    {
        var members = new List<(string, ReportJson)>
        {
            ("schema", ReportJson.Of(Schema)),
            ("type", ReportJson.Of("live")),
            ("instance", ReportJson.Of(Instance)),
            ("name", ReportJson.Of(Name)),
            ("run", ReportJson.Of(Run)),
        };
        if (seq)
        {
            members.Add(("seq", ReportJson.Of(Seq)));
            members.Add(("at", ReportJson.Of(ReportInstant.Format(AtMs))));
        }

        members.AddRange(Overall.Members());
        members.Add(("estimated", ReportJson.Of(Estimated)));
        members.Add(("sessions", Overall.Sessions));
        if (sources)
        {
            members.Add(("sources", ReportJson.Array(Sources.Select(s =>
                ReportJson.Object(new (string, ReportJson)[] { ("source", ReportJson.Of(ReportBody.SourceId(s.Kind))) }
                    .Concat(s.Values.Members()).Append(("sessions", s.Values.Sessions)))))));
        }

        if (history)
        {
            members.Add(("history", ReportJson.Object(
                ("stepMs", ReportJson.Of(HistoryStepMs)),
                ("outputTokS", ReportJson.Array(History.Select(v => ReportJson.Decimal(v, 1)))))));
        }

        return ReportJson.Object(members);
    }
}
