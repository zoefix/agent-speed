namespace AgentSpeed.Core.Quota.Network;

/// <summary>The hosts of NETWORK 9; nothing else is ever asked, and never Anthropic.</summary>
public static class QuotaHosts
{
    public const string Grok = "cli-chat-proxy.grok.com";
    public const string Kimi = "api.kimi.com";
    public const string Gemini = "cloudcode-pa.googleapis.com";
    public const string Factory = "api.factory.ai";
    public const string Zai = "api.z.ai";
    public const string Bigmodel = "open.bigmodel.cn";
    public const string Qoder = "qoder.com";
    public const string QoderCn = "qoder.com.cn";
    public const string WorkBuddy = "www.workbuddy.cn";
    public const string Loopback = "127.0.0.1";

    public static IReadOnlySet<string> Remote { get; } =
        new HashSet<string>([Grok, Kimi, Gemini, Factory, Zai, Bigmodel, Qoder, QoderCn, WorkBuddy], StringComparer.Ordinal);

    public static bool IsLoopback(Uri url) => url is { IsAbsoluteUri: true } && url.Host == Loopback;

    /// <summary>HTTPS on port 443 to a listed host, or HTTP/HTTPS to 127.0.0.1 with an explicit port; no user information.</summary>
    public static bool Permits(Uri? url)
    {
        if (url is not { IsAbsoluteUri: true } || url.UserInfo.Length > 0)
        {
            return false;
        }

        var host = url.IdnHost.ToLowerInvariant();
        if (host == Loopback)
        {
            return (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && !url.IsDefaultPort;
        }

        return url.Scheme == Uri.UriSchemeHttps && url.Port == 443 && Remote.Contains(host);
    }
}
