using System.Globalization;
using System.Reflection;
using System.Text;

namespace AgentSpeed.Presentation.Localization;

/// <summary>Localized strings with live language switching. Not thread-safe; use from the UI thread.</summary>
public sealed class Localizer
{
    public const string SharedResourceName = "AgentSpeed.Strings.Shared.json";
    public const string WindowsResourceName = "AgentSpeed.Strings.Windows.json";

    private readonly StringTable _table;
    private string _languageCode;

    public Localizer(StringTable table, string languageCode = LanguageCodes.En)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _languageCode = Normalize(languageCode);
    }

    public event EventHandler? LanguageChanged;

    public StringTable Table => _table;

    public string LanguageCode
    {
        get => _languageCode;
        set
        {
            var normalized = Normalize(value);
            if (normalized == _languageCode)
            {
                return;
            }

            _languageCode = normalized;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public CultureInfo Culture => LanguageResolver.CultureFor(_languageCode);

    /// <summary>True for languages whose UI labels are uppercased (Latin script).</summary>
    public bool UsesLatinScript => _languageCode == LanguageCodes.En;

    public string this[string key] => Get(key);

    public static StringTable LoadEmbeddedTable()
    {
        var assembly = typeof(Localizer).Assembly;
        return StringTable.Merge(LoadResource(assembly, SharedResourceName), LoadResource(assembly, WindowsResourceName));
    }

    public string Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_table.TryGet(key, _languageCode, out var text) || _table.TryGet(key, LanguageCodes.En, out text))
        {
            return text;
        }

        return key;
    }

    /// <summary>Formats a template that uses %d (integer) and %@ (string) placeholders in order.</summary>
    public string Format(string key, params object?[] args) => FormatTemplate(Get(key), Culture, args);

    public static string FormatTemplate(string template, CultureInfo culture, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(template);
        var builder = new StringBuilder(template.Length + 16);
        var argument = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c != '%' || i + 1 >= template.Length)
            {
                builder.Append(c);
                continue;
            }

            var next = template[i + 1];
            switch (next)
            {
                case '%':
                    builder.Append('%');
                    i++;
                    break;
                case 'd':
                    builder.Append(FormatInteger(argument < args.Length ? args[argument] : null, culture));
                    argument++;
                    i++;
                    break;
                case '@':
                    builder.Append(argument < args.Length ? Convert.ToString(args[argument], culture) : string.Empty);
                    argument++;
                    i++;
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string FormatInteger(object? value, CultureInfo culture) => value switch
    {
        null => "0",
        int i => i.ToString(culture),
        long l => l.ToString(culture),
        double d when double.IsFinite(d) => Math.Round(d).ToString("0", culture),
        float f when float.IsFinite(f) => Math.Round(f).ToString("0", culture),
        IFormattable formattable => formattable.ToString(null, culture),
        _ => Convert.ToString(value, culture) ?? "0",
    };

    private static string Normalize(string? code) =>
        LanguageCodes.All.Contains(code) ? code! : LanguageResolver.Resolve(code);

    private static StringTable LoadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource '{name}'.");
        return StringTable.Parse(stream);
    }
}
