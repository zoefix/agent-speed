using System.Text.Json;
using AgentSpeed.Presentation.Characters;

namespace AgentSpeed.Presentation.Settings;

/// <summary>
/// The character packs the user removed or cancelled (settings key <c>charactersSetAside</c>, CHARACTERS 2 <i>Automatic
/// download</i>): valid pack ids, sorted and unique, compared by value.
/// </summary>
public sealed class CharacterSetAside : IEquatable<CharacterSetAside>
{
    private const int MaxIds = 256;

    public CharacterSetAside(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Ids = ids.Where(CharacterId.IsValid).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(MaxIds).ToArray();
    }

    public static CharacterSetAside Empty { get; } = new([]);

    public IReadOnlyList<string> Ids { get; }

    public bool IsEmpty => Ids.Count == 0;

    public static CharacterSetAside Read(JsonElement element) => element.ValueKind != JsonValueKind.Array
        ? Empty
        : new CharacterSetAside(element.EnumerateArray().Take(MaxIds * 2).Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));

    public void Write(Utf8JsonWriter writer, string name)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartArray(name);
        foreach (var id in Ids)
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
    }

    public bool Equals(CharacterSetAside? other) => other is not null && Ids.SequenceEqual(other.Ids);

    public override bool Equals(object? obj) => Equals(obj as CharacterSetAside);

    public override int GetHashCode() => Ids.Count == 0 ? 0 : HashCode.Combine(Ids.Count, Ids[0]);
}
