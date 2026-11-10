using System.Globalization;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Qoder;

/// <summary>One model call as METRICS 4.18 <i>Calls</i> defines it.</summary>
internal static class QoderCalls
{
    /// <summary><c>qoder:</c> and the row's message id, else <c>qoder:&lt;session&gt;:row:&lt;rowid&gt;</c>.</summary>
    public static string Key(string session, string? messageId, long rowId) =>
        messageId is not null ? "qoder:" + messageId : "qoder:" + session + ":row:" + rowId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The response of an assistant row whose <c>token_info</c> is an object, ending at the row's time and starting at the time
    /// of the session's previous row; null for any other row and for a call whose input and output are both 0.
    /// </summary>
    public static ParsedResponse? Response(in QoderRow row, string session, long endMs, long? startMs, string agentId)
    {
        if (row.Role != QoderRole.Assistant || row.Usage is not { } usage || (usage.Prompt == 0 && usage.Completion == 0))
        {
            return null;
        }

        // prompt_tokens already includes the cache read, OpenAI's way.
        var tokens = new TokenCounts(usage.Prompt, Math.Min(usage.Cached, usage.Prompt), 0, usage.Completion);
        return new ParsedResponse(Key(session, row.MessageId, row.RowId), SourceKind.Qoder, agentId, row.Model, startMs, endMs, tokens);
    }
}
