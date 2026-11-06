using System.Text;

namespace AgentSpeed.Core.Remote.Windows;

/// <summary>A Windows command line for an argument vector: each argument quoted so the C runtime reads it back unchanged.</summary>
internal static class WindowsCommandLine
{
    /// <summary>The executable and the arguments, each quoted as <c>CommandLineToArgvW</c> and the C runtime read them back.</summary>
    public static string Of(string executable, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder();
        Quote(line, executable);
        foreach (var argument in arguments)
        {
            line.Append(' ');
            Quote(line, argument);
        }

        return line.ToString();
    }

    private static void Quote(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            line.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            backslashes = 0;
            line.Append(c);
        }

        line.Append('\\', backslashes * 2);
        line.Append('"');
    }
}
