using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb.Lines;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Orb;

/// <summary>
/// What a <c>character</c> orb shows besides the speed (DESIGN 2a.6, CHARACTERS 7–8): the character (null while none is
/// installed, or while her pack loads), the cards switched on, the texts of the today, model and models cards, and her
/// accent.
/// </summary>
public sealed record OrbCharacterContent
{
    /// <summary>The loaded pack of the character to show; null with none installed or while it loads.</summary>
    public CharacterPack? Pack { get; init; }

    /// <summary>No character is installed: the style shows the ring, the standard readout and <see cref="EmptyLabel"/>.</summary>
    public bool NoneInstalled { get; init; }

    public string EmptyLabel { get; init; } = string.Empty;

    public CharacterReadouts Readouts { get; init; } = CharacterReadouts.Default;

    /// <summary>The fastest live model's name (price-table name, else the raw id); null keeps the last one.</summary>
    public string? ModelName { get; init; }

    /// <summary>The character's own name in the app language (the model chip before any model was seen).</summary>
    public string? CharacterName { get; init; }

    public Rgba Accent { get; init; } = Rgba.FromHex("#D97757");

    /// <summary><c>cost.glyph</c> (費用).</summary>
    public string CostGlyph { get; init; } = string.Empty;

    /// <summary><c>boards.today</c> with today's tokens; null hides the line.</summary>
    public string? TodayLine { get; init; }

    /// <summary><c>agents.count</c>; null hides the line.</summary>
    public string? AgentsLine { get; init; }

    /// <summary>The network unit cells are <c>Mb/s</c> wide.</summary>
    public bool Bits { get; init; }

    /// <summary>The 性能 and 思考 glyph labels of the language (DESIGN 10).</summary>
    public string IntelligenceGlyph { get; init; } = "性能";

    public string ThinkingGlyph { get; init; } = "思考";

    /// <summary><c>character.models.more</c> as its template (<c>+%@ 個模型</c>): the models card's line for the models it leaves out.</summary>
    public string ModelsMore { get; init; } = "+%@";

    /// <summary><c>unit.tokS</c>: the models card's unit, whatever the orb's own unit setting.</summary>
    public string TokensUnit { get; init; } = "tok/s";

    /// <summary>The 額度 card: the orb's sources whose app runs and is signed in, with their windows (QUOTAS 6).</summary>
    public OrbQuotaCard Quota { get; init; } = OrbQuotaCard.Empty;
}

/// <summary>
/// The quiet rule of the cost card (DESIGN 2a.6, Which cards show): loud while money flows (or a generating set has no
/// price, shown as <c>–</c>); its value follows the live rate until the card has faded, then holds.
/// </summary>
public sealed class OrbCostQuietRule
{
    public LiveMoney? Money { get; private set; }

    public double QuietAt { get; private set; } = double.NegativeInfinity;

    public void Apply(LiveMoney? next, bool generating, double time)
    {
        if (next is null)
        {
            Money = null;
            QuietAt = double.NegativeInfinity;
            return;
        }

        var loud = next.Value > 0 || (next.Unknown && generating);
        if (loud)
        {
            QuietAt = time + OrbLinesQuietRule.QuietAfter;
        }

        if (loud || time < QuietAt + OrbLinesReading.FadeOut)
        {
            Money = next;
        }
    }

    public bool IsLoud(double time) => Money is not null && time < QuietAt;
}

/// <summary>
/// The live values a <c>character</c> orb floats beside her (DESIGN 2a.6, Readouts): network speed, 性能 and 思考 with the
/// 首字 row, the cost rate and the running models, each with the quiet rule of DESIGN 10.
/// </summary>
public sealed class OrbCharacterReadings
{
    public OrbLinesQuietRule Network { get; } = new();

    public OrbLinesQuietRule Performance { get; } = new();

    public OrbCostQuietRule Cost { get; } = new();

    /// <summary>The 性能 card's 首字 row: the orb's first-content latency while it exists, fading in and out.</summary>
    public OrbLatencyFade Latency { get; private set; } = OrbLatencyFade.None;

    public OrbModelsQuietRule Models { get; } = new();

    public bool NetworkLoud(double time) => Network.Reading is { } r && time < r.QuietAt;

    public bool PerformanceLoud(double time) => Performance.Reading is { } r && time < r.QuietAt;

    /// <summary>
    /// Applies one engine tick (性能 and 思考, the 首字 latency, the cost rate, the running models); true when anything shown
    /// may have changed.
    /// </summary>
    public bool Apply(OrbLinesSample? performance, LiveMoney? money, bool generating, double time, OrbLatency? latency = null,
        IReadOnlyList<OrbModelRow>? models = null)
    {
        var changed = Performance.Apply(performance, time);
        var before = Cost.Money;
        Cost.Apply(money, generating, time);
        var latencyBefore = Latency;
        Latency = Latency.Next(latency, time);
        changed |= Models.Apply(models ?? [], time);
        return changed || !Equals(before, Cost.Money) || Latency != latencyBefore;
    }

    /// <summary>Applies one traffic sample (null while network speed is not measured); true when it changed.</summary>
    public bool ApplyNetwork(OrbLinesSample? network, double time) => Network.Apply(network, time);

    /// <summary>When a card next fades by its quiet rule, or null.</summary>
    public double? NextChange(double time)
    {
        static double? Min(double? a, double? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
        var cards = Min(Min(Network.Reading?.NextChange(time), Performance.Reading?.NextChange(time)), Cost.QuietAt > time ? Cost.QuietAt : null);
        return Min(cards, Models.Rows.Count > 0 && Models.QuietAt > time ? Models.QuietAt : null);
    }
}

/// <summary>The <c>character</c> orb's cards in its accessible text (DESIGN 2a.6, Accessibility).</summary>
public static class OrbCharacterSpeech
{
    /// <summary>
    /// The cards that show, in the column's order: 性能 and 思考 with the 首字 row, network speed, the cost rate, the
    /// running models, then the 額度 card; null when none shows.
    /// </summary>
    public static string? Sentence(OrbCharacterReadings readings, CharacterReadouts on, double time, Localizer localizer, string currencyName,
        OrbQuotaCard? quota = null)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(on);
        ArgumentNullException.ThrowIfNull(localizer);
        var parts = new List<string>(5);
        if (readings.PerformanceLoud(time) && (on.Intelligence || on.Thinking) && readings.Performance.Reading is { Content: { Kind: OrbLinesKind.Intelligence } } performance)
        {
            var hidden = !on.Thinking || (on.Intelligence && performance.Thinking.Hides(time));
            parts.Add(hidden ? performance.Content.SpokenAlone ?? performance.Content.Spoken : performance.Content.Spoken);
        }

        if (readings.PerformanceLoud(time) && on.Latency && readings.Latency is { Latency: { } latency, HiddenAt: null })
        {
            parts.Add(latency.Spoken);
        }

        if (readings.NetworkLoud(time) && on.Network && readings.Network.Reading is { Content: { Kind: OrbLinesKind.Traffic } network })
        {
            parts.Add(network.Spoken);
        }

        if (readings.Cost.IsLoud(time) && on.Cost && readings.Cost.Money is { } money)
        {
            parts.Add(money.Unknown
                ? localizer["a11y.board.costUnknown"]
                : localizer.Format("a11y.board.cost", money.AtLeast ? localizer.Format("a11y.orb.costAtLeast", money.Number) : money.Number, currencyName));
        }

        if (readings.Models.IsLoud(time) && on.Models && OrbModelsText.Spoken(readings.Models.Rows, localizer) is { } models)
        {
            parts.Add(models);
        }

        if (on.Quota && quota?.Spoken is { Length: > 0 } spoken)
        {
            parts.Add(spoken);
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var separator = localizer.LanguageCode == LanguageCodes.Ja ? "、" : localizer.UsesLatinScript ? ", " : "，";
        return string.Join(separator, parts);
    }
}
