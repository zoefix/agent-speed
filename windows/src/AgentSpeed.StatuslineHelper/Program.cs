namespace AgentSpeed.StatuslineHelper;

/// <summary>Claude Code runs this after each response with its status line JSON on standard input (QUOTAS 5.5).</summary>
internal static class Program
{
    private static int Main()
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            return new StatuslineRun(StatuslineRun.DataFolderFor(Environment.ProcessPath)).Run(input, output);
        }
        catch (Exception)
        {
            // Its own errors never break Claude Code's status line.
            return 0;
        }
    }
}
