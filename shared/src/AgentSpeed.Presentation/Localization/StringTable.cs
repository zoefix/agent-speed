using System.Text.Json;

namespace AgentSpeed.Presentation.Localization;

/// <summary>Key → language → text table loaded from the shared JSON format (spec/strings.json).</summary>
public sealed class StringTable
{
    private readonly Dictionary<string, Dictionary<string, string>> _entries;

    private StringTable(Dictionary<string, Dictionary<string, string>> entries)
    {
        _entries = entries;
    }

    public IReadOnlyCollection<string> Keys => _entries.Keys;

    public static StringTable Empty { get; } = new(new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal));

    public static StringTable Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("String table root must be an object.");
        }

        var entries = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith('_') || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var translations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var translation in property.Value.EnumerateObject())
            {
                if (translation.Value.ValueKind == JsonValueKind.String)
                {
                    translations[translation.Name] = translation.Value.GetString() ?? string.Empty;
                }
            }

            entries[property.Name] = translations;
        }

        return new StringTable(entries);
    }

    public static StringTable Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Combines tables; later tables override earlier ones per key.</summary>
    public static StringTable Merge(params StringTable[] tables)
    {
        var entries = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            foreach (var (key, translations) in table._entries)
            {
                entries[key] = new Dictionary<string, string>(translations, StringComparer.Ordinal);
            }
        }

        return new StringTable(entries);
    }

    public bool Contains(string key) => _entries.ContainsKey(key);

    public bool TryGet(string key, string languageCode, out string text)
    {
        if (_entries.TryGetValue(key, out var translations)
            && translations.TryGetValue(languageCode, out var value)
            && !string.IsNullOrEmpty(value))
        {
            text = value;
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <summary>Keys that lack a non-empty translation for any of <paramref name="languages"/>.</summary>
    public IReadOnlyList<string> MissingTranslations(IEnumerable<string> languages)
    {
        var required = languages.ToArray();
        return _entries
            .Where(entry => required.Any(lang => !entry.Value.TryGetValue(lang, out var v) || string.IsNullOrWhiteSpace(v)))
            .Select(entry => entry.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
    }
}
