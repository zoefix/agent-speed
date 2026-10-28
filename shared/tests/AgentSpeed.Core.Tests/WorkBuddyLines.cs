using System.Globalization;

namespace AgentSpeed.Core.Tests;

/// <summary>Builders for synthetic WorkBuddy session lines (METRICS 4.9), in the CLI's key order.</summary>
internal static class WorkBuddyLines
{
    public const string Cwd = "/Users/me/projects/atlas";

    public static string User(long ms, string id, bool skipRun = false, string cwd = Cwd) =>
        $$"""{"id":"{{id}}","parentId":null,"timestamp":{{N(ms)}},"type":"message","role":"user","content":[{"type":"input_text","text":"(prompt)"}],"providerData":{"agent":"main"{{(skipRun ? ",\"skipRun\":true" : "")}} },"cwd":"{{cwd}}"}""";

    /// <summary>An assistant message of call <paramref name="messageId"/>; <paramref name="usage"/> is the providerData usage fields.</summary>
    public static string Say(
        long ms, string id, string messageId, string? usage = null, string model = "glm-5.3", string extra = "", string cwd = Cwd) =>
        $$"""{"id":"{{id}}","timestamp":{{N(ms)}},"type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"(answer)"}],"providerData":{"messageId":"{{messageId}}","model":"{{model}}","requestModelId":"{{model}}"{{Fields(usage)}}{{extra}} },"cwd":"{{cwd}}"}""";

    public static string ToolCall(long ms, string id, string messageId, string callId, string? usage = null) =>
        $$"""{"id":"{{id}}","timestamp":{{N(ms)}},"type":"function_call","callId":"{{callId}}","name":"Read","arguments":"{}","status":"completed","providerData":{"messageId":"{{messageId}}","model":"glm-5.3"{{Fields(usage)}} },"cwd":"{{Cwd}}"}""";

    public static string ToolResult(long ms, string id, string messageId, string callId, bool skipRun = false) =>
        $$"""{"id":"{{id}}","timestamp":{{N(ms)}},"type":"function_call_result","name":"Read","callId":"{{callId}}","status":"completed","output":{"type":"text","text":"(output)"},"providerData":{"messageId":"{{messageId}}"{{(skipRun ? ",\"skipRun\":true" : "")}} },"cwd":"{{Cwd}}"}""";

    public static string ModelUsage(long ms, string id, string messageId, string usage) =>
        $$"""{"id":"{{id}}","timestamp":{{N(ms)}},"type":"model-usage","providerData":{"messageId":"{{messageId}}","model":"glm-5.3"{{Fields(usage)}} },"cwd":"{{Cwd}}"}""";

    /// <summary>agents-core <c>usage</c> plus the service's <c>rawUsage</c>.</summary>
    public static string Usage(long input, long output, long cached = 0, long reasoning = 0, string? raw = null) =>
        "\"usage\":{\"requests\":1,\"inputTokens\":" + N(input) + ",\"outputTokens\":" + N(output)
        + ",\"totalTokens\":" + N(input + output) + ",\"inputTokensDetails\":[{\"cached_tokens\":" + N(cached)
        + "}],\"outputTokensDetails\":[{\"reasoning_tokens\":" + N(reasoning) + "}]}"
        + (raw is null ? string.Empty : ",\"rawUsage\":" + raw);

    private static string Fields(string? usage) => usage is null ? string.Empty : "," + usage;

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
