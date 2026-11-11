using System.Globalization;

namespace AgentSpeed.Core.Quota.Cookies;

/// <summary>The user agent the browser the cookies came from sends (QUOTAS 9.9, WorkBuddy).</summary>
internal static class BrowserUserAgent
{
    public const string WindowsToken = "Windows NT 10.0; Win64; x64";
    public const int DefaultChromeMajor = 142;
    public const string DefaultFirefoxVersion = "144.0";

    public static string Chrome(string platform, int major) => string.Create(
        CultureInfo.InvariantCulture,
        $"Mozilla/5.0 ({platform}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36");

    public static string Firefox(string platform, string version) =>
        $"Mozilla/5.0 ({platform}; rv:{version}) Gecko/20100101 Firefox/{version}";
}
