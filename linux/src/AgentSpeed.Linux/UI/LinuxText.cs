using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Linux.UI;

internal static class LinuxText
{
    internal static StringTable Table { get; } = Load();
    private static StringTable Load()
    {
        using var stream = typeof(LinuxText).Assembly.GetManifestResourceStream("AgentSpeed.Linux.Strings.json")!;
        return StringTable.Parse(stream);
    }
    public static string Get(string language, string english) => Table.TryGet(english, language, out var text) ? text : english;
}
