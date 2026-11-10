using System.Text.Json;

namespace AgentSpeed.Core.Quota.Network;

/// <summary>The ZCode region (QUOTAS 9.8): <c>global</c> asks <c>api.z.ai</c>, <c>bigmodel-cn</c> <c>open.bigmodel.cn</c>.</summary>
public enum ZaiRegion
{
    Global,
    BigmodelCn,
}

/// <summary>The language server's two calls (QUOTAS 9.4).</summary>
public enum AntigravityCall
{
    Summary,
    Status,
}

/// <summary>
/// The requests of QUOTAS 9.2–9.9, each with only the headers its section lists plus <c>User-Agent</c>
/// (<c>AgentSpeed/&lt;version&gt; (Windows)</c>; WorkBuddy sends the browser's own).
/// </summary>
public static class QuotaRequests
{
    private const string Json = "application/json";

    public static string Host(this ZaiRegion region) => region == ZaiRegion.Global ? QuotaHosts.Zai : QuotaHosts.Bigmodel;

    /// <summary>The stored value <c>quotaZcodeRegion</c>.</summary>
    public static string Key(this ZaiRegion region) => region == ZaiRegion.Global ? "global" : "bigmodel-cn";

    public static ZaiRegion RegionOf(string? key) => key == "bigmodel-cn" ? ZaiRegion.BigmodelCn : ZaiRegion.Global;

    public static QuotaHttpRequest GrokBilling(string token, string userAgent) =>
        Get($"https://{QuotaHosts.Grok}/v1/billing?format=credits",
            [new("Accept", Json), new("Authorization", "Bearer " + token), new("x-xai-token-auth", "xai-grok-cli"), new("User-Agent", userAgent)]);

    public static QuotaHttpRequest KimiUsages(string token, string userAgent) =>
        Get($"https://{QuotaHosts.Kimi}/coding/v1/usages", [new("Accept", Json), new("Authorization", "Bearer " + token), new("User-Agent", userAgent)]);

    public static QuotaHttpRequest GeminiLoad(string token, string userAgent) =>
        Post($"https://{QuotaHosts.Gemini}/v1internal:loadCodeAssist", GeminiHeaders(token, userAgent),
            """{"metadata":{"ideType":"GEMINI_CLI","pluginType":"GEMINI"}}"""u8.ToArray());

    public static QuotaHttpRequest GeminiQuota(string token, string? project, string userAgent) =>
        Post($"https://{QuotaHosts.Gemini}/v1internal:retrieveUserQuota", GeminiHeaders(token, userAgent),
            project is null ? "{}"u8.ToArray() : ProjectBody(project));

    public static QuotaHttpRequest DroidLimits(string key, string userAgent) =>
        Get($"https://{QuotaHosts.Factory}/api/billing/limits", [new("Accept", Json), new("Authorization", "Bearer " + key), new("User-Agent", userAgent)]);

    public static QuotaHttpRequest ZaiLimits(string key, ZaiRegion region, string userAgent) =>
        Get($"https://{region.Host()}/api/monitor/usage/quota/limit", [new("Authorization", "Bearer " + key), new("User-Agent", userAgent)]);

    /// <summary><paramref name="site"/> is <c>qoder.com</c> or <c>qoder.com.cn</c>, where the cookies came from; null for any other.</summary>
    public static QuotaHttpRequest? QoderCredits(string cookie, string site, string userAgent) =>
        site is QuotaHosts.Qoder or QuotaHosts.QoderCn
            ? Get($"https://{site}/api/v2/me/usages/big_model_credits",
                [new("Cookie", cookie), new("Origin", "https://" + site), new("Referer", $"https://{site}/"), new("Accept", Json), new("User-Agent", userAgent)])
            : null;

    /// <summary>The browser's user agent: the server ties the session to it.</summary>
    public static QuotaHttpRequest WorkBuddySummary(string cookie, string browserUserAgent) =>
        Post($"https://{QuotaHosts.WorkBuddy}/billing/meter/get-user-resource-summary",
            [new("Content-Type", Json), new("Cookie", cookie), new("User-Agent", browserUserAgent)], "{}"u8.ToArray());

    /// <summary>Loopback only; the token is left out when there is none.</summary>
    public static QuotaHttpRequest? Antigravity(AntigravityCall call, string scheme, int port, string? token, string userAgent)
    {
        if (scheme is not ("http" or "https") || port is < 1 or > 65535)
        {
            return null;
        }

        var name = call == AntigravityCall.Summary ? "RetrieveUserQuotaSummary" : "GetUserStatus";
        var headers = new List<KeyValuePair<string, string>> { new("Connect-Protocol-Version", "1"), new("Content-Type", Json) };
        if (!string.IsNullOrEmpty(token))
        {
            headers.Add(new("X-Codeium-Csrf-Token", token));
        }

        headers.Add(new("User-Agent", userAgent));
        var body = call == AntigravityCall.Summary
            ? """{"forceRefresh":true}"""u8.ToArray()
            : """{"metadata":{"ideName":"antigravity","extensionName":"antigravity","ideVersion":"unknown","locale":"en"}}"""u8.ToArray();
        return new QuotaHttpRequest(HttpMethod.Post,
            new Uri($"{scheme}://{QuotaHosts.Loopback}:{port}/exa.language_server_pb.LanguageServerService/{name}"), headers, body);
    }

    private static byte[] ProjectBody(string project)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("project", project);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static KeyValuePair<string, string>[] GeminiHeaders(string token, string userAgent) =>
        [new("Authorization", "Bearer " + token), new("Content-Type", Json), new("User-Agent", userAgent)];

    private static QuotaHttpRequest Get(string url, KeyValuePair<string, string>[] headers) => new(HttpMethod.Get, new Uri(url), headers);

    private static QuotaHttpRequest Post(string url, KeyValuePair<string, string>[] headers, byte[] body) => new(HttpMethod.Post, new Uri(url), headers, body);
}
