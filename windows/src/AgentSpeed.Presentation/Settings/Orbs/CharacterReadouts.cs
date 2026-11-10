using System.Text.Json;

namespace AgentSpeed.Presentation.Settings.Orbs;

/// <summary>One switch of <see cref="CharacterReadouts"/>, in the menu's and editor's order.</summary>
public enum CharacterReadout
{
    Intelligence,
    Thinking,
    Latency,
    Network,
    Cost,
    Agents,
    Models,
    Quota,
    Model,
}

/// <summary>
/// The cards a <c>character</c> orb floats beside her (DESIGN 2a.6 <i>Readouts</i>, 2b <c>characterReadouts</c>): the
/// speed always shows; each card (and the 性能 card's 首字 row) has its own switch. Every card is on for a new orb except the
/// fastest model's name.
/// Decoding is tolerant: a missing key, or a value that is not a boolean, takes its default.
/// </summary>
public sealed record CharacterReadouts(bool Model = false, bool Intelligence = true, bool Thinking = true, bool Network = true, bool Cost = true,
    bool Agents = true, bool Latency = true, bool Models = true, bool Quota = true)
{
    public static CharacterReadouts Default { get; } = new();

    public static IReadOnlyList<CharacterReadout> All { get; } =
    [
        CharacterReadout.Intelligence, CharacterReadout.Thinking, CharacterReadout.Latency, CharacterReadout.Network, CharacterReadout.Cost,
        CharacterReadout.Agents, CharacterReadout.Models, CharacterReadout.Quota, CharacterReadout.Model,
    ];

    public bool IsDefault => this == Default;

    public bool this[CharacterReadout readout] => readout switch
    {
        CharacterReadout.Model => Model,
        CharacterReadout.Intelligence => Intelligence,
        CharacterReadout.Thinking => Thinking,
        CharacterReadout.Latency => Latency,
        CharacterReadout.Network => Network,
        CharacterReadout.Cost => Cost,
        CharacterReadout.Models => Models,
        CharacterReadout.Quota => Quota,
        _ => Agents,
    };

    public CharacterReadouts With(CharacterReadout readout, bool on) => readout switch
    {
        CharacterReadout.Model => this with { Model = on },
        CharacterReadout.Intelligence => this with { Intelligence = on },
        CharacterReadout.Thinking => this with { Thinking = on },
        CharacterReadout.Latency => this with { Latency = on },
        CharacterReadout.Network => this with { Network = on },
        CharacterReadout.Cost => this with { Cost = on },
        CharacterReadout.Models => this with { Models = on },
        CharacterReadout.Quota => this with { Quota = on },
        _ => this with { Agents = on },
    };

    /// <summary>
    /// <c>character.readouts.intel</c>, <c>.thinking</c>, <c>.latency</c>, <c>.network</c>, <c>.cost</c>, <c>.agents</c>,
    /// <c>.models</c>, <c>.quota</c>, <c>.model</c>.
    /// </summary>
    public static string TitleKey(CharacterReadout readout) => readout switch
    {
        CharacterReadout.Intelligence => "character.readouts.intel",
        CharacterReadout.Thinking => "character.readouts.thinking",
        CharacterReadout.Latency => "character.readouts.latency",
        CharacterReadout.Network => "character.readouts.network",
        CharacterReadout.Cost => "character.readouts.cost",
        CharacterReadout.Agents => "character.readouts.agents",
        CharacterReadout.Models => "character.readouts.models",
        CharacterReadout.Quota => "character.readouts.quota",
        _ => "character.readouts.model",
    };

    /// <summary>The persisted key of a switch.</summary>
    public static string Key(CharacterReadout readout) => readout switch
    {
        CharacterReadout.Intelligence => "intelligence",
        CharacterReadout.Thinking => "thinking",
        CharacterReadout.Latency => "latency",
        CharacterReadout.Network => "network",
        CharacterReadout.Cost => "cost",
        CharacterReadout.Agents => "agents",
        CharacterReadout.Models => "models",
        CharacterReadout.Quota => "quota",
        _ => "model",
    };

    public static CharacterReadouts Read(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Default;
        }

        var result = Default;
        foreach (var readout in All)
        {
            if (element.TryGetProperty(Key(readout), out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                result = result.With(readout, value.GetBoolean());
            }
        }

        return result;
    }

    public void Write(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        writer.WriteBoolean("model", Model);
        writer.WriteBoolean("intelligence", Intelligence);
        writer.WriteBoolean("thinking", Thinking);
        writer.WriteBoolean("network", Network);
        writer.WriteBoolean("cost", Cost);
        writer.WriteBoolean("agents", Agents);
        writer.WriteBoolean("latency", Latency);
        writer.WriteBoolean("models", Models);
        writer.WriteBoolean("quota", Quota);
        writer.WriteEndObject();
    }
}
