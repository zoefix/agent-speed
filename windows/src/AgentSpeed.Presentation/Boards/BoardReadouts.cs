namespace AgentSpeed.Presentation.Boards;

/// <summary>One of a board's live metrics switches (DESIGN 2c, Live metrics: Switches), in menu and editor order.</summary>
public enum BoardReadout
{
    /// <summary>性能 in the sub-gauge, and the rows' 性能 column with its captions line.</summary>
    Intelligence,

    /// <summary>思考 in the sub-gauge.</summary>
    Thinking,

    /// <summary>費用 in the sub-gauge.</summary>
    Cost,
}

/// <summary>
/// A board's three independent live metrics switches (DESIGN 2c data model <c>readouts</c>): every one on for a new board and
/// for a board saved by an earlier version. Stored as the switches that are off, so the default value has every one on.
/// </summary>
public readonly record struct BoardReadouts
{
    private readonly int _off;

    public BoardReadouts(bool intelligence = true, bool thinking = true, bool cost = true) =>
        _off = (intelligence ? 0 : 1) | (thinking ? 0 : 2) | (cost ? 0 : 4);

    public static BoardReadouts All => default;

    public static BoardReadouts None { get; } = new(false, false, false);

    public bool Intelligence { get => (_off & 1) == 0; init => _off = value ? _off & ~1 : _off | 1; }

    public bool Thinking { get => (_off & 2) == 0; init => _off = value ? _off & ~2 : _off | 2; }

    public bool Cost { get => (_off & 4) == 0; init => _off = value ? _off & ~4 : _off | 4; }

    public static IReadOnlyList<BoardReadout> Order { get; } = [BoardReadout.Intelligence, BoardReadout.Thinking, BoardReadout.Cost];

    /// <summary>The hero has a sub-gauge (at least one of 性能, 思考 and 費用); otherwise it is the board without metrics.</summary>
    public bool Hero => Intelligence || Thinking || Cost;

    /// <summary>Every switch off: no readouts (at M and L the group rows still have their second line).</summary>
    public bool IsNone => !Hero;

    /// <summary>The persisted key (<c>intelligence</c>, <c>thinking</c>, <c>cost</c>).</summary>
    public static string Key(BoardReadout readout) => readout switch
    {
        BoardReadout.Thinking => "thinking",
        BoardReadout.Cost => "cost",
        _ => "intelligence",
    };

    /// <summary>The menu and editor item (<c>boards.readouts.*</c>).</summary>
    public static string ItemKey(BoardReadout readout) => readout switch
    {
        BoardReadout.Thinking => "boards.readouts.thinking",
        BoardReadout.Cost => "boards.readouts.cost",
        _ => "boards.readouts.intel",
    };

    public bool this[BoardReadout readout] => readout switch
    {
        BoardReadout.Thinking => Thinking,
        BoardReadout.Cost => Cost,
        _ => Intelligence,
    };

    public BoardReadouts With(BoardReadout readout, bool on) => readout switch
    {
        BoardReadout.Thinking => this with { Thinking = on },
        BoardReadout.Cost => this with { Cost = on },
        _ => this with { Intelligence = on },
    };

    /// <summary>A short identity for layer signatures.</summary>
    public int Bits => (Intelligence ? 1 : 0) | (Thinking ? 2 : 0) | (Cost ? 4 : 0);
}
