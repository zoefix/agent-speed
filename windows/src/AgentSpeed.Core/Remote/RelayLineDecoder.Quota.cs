using System.Globalization;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Network;

namespace AgentSpeed.Core.Remote;

/// <summary>
/// The relay's quota lines (REMOTE 11.1), validated by REMOTE 5.6: at most 16 windows of the QUOTAS kinds, names of at most
/// 64 characters of display text, minutes 1–527 040 or null, percents finite 0–1000, times within 2000–2100. A source a
/// host cannot read is ignored.
/// </summary>
internal sealed partial class RelayLineDecoder
{
    /// <summary>The host a source's refusal names (NETWORK 9); the line itself names none.</summary>
    public static string VendorHost(SourceKind? source) => source switch
    {
        SourceKind.GrokBuild => QuotaHosts.Grok,
        SourceKind.Kimi => QuotaHosts.Kimi,
        SourceKind.Gemini => QuotaHosts.Gemini,
        SourceKind.Droid => QuotaHosts.Factory,
        _ => QuotaHosts.Loopback,
    };

    private static SourceKind? HostSource(string key) =>
        History.SourceNames.Parse(key) is { } source && RemoteQuotaConfig.HostSources.Contains(source) ? source : null;

    private RelayMessage.Quota? DecodeQuota(RemoteFields line)
    {
        var f = line.Object("q");
        var key = f.String("source", 64);
        var source = HostSource(key);
        var observed = f.Time("observedMs");
        var windows = new List<QuotaWindow>();
        var index = 0;
        foreach (var item in f.Array("windows", RemoteQuotaReport.MaxWindows).EnumerateArray())
        {
            var path = $"q.windows.{index++}";
            windows.Add(DecodeQuotaWindow(RemoteFields.Of(item, path), path + "."));
        }

        var running = f.Bool("running");
        var signedIn = f.Bool("signedIn");
        var status = f.String("status", 32) switch
        {
            "ok" => QuotaRouteStatus.Ok(observed),
            "denied" => QuotaRouteStatus.Denied(VendorHost(source)),
            "noCredential" => QuotaRouteStatus.Of(QuotaStatusKind.NoCredential),
            "expired" => QuotaRouteStatus.Of(QuotaStatusKind.Expired),
            "failed" => QuotaRouteStatus.Of(QuotaStatusKind.Failed),
            "networkOff" => QuotaRouteStatus.Of(QuotaStatusKind.NetworkOff),
            "noCertificates" => QuotaRouteStatus.Of(QuotaStatusKind.NoCertificates),
            "notRunning" => QuotaRouteStatus.Of(QuotaStatusKind.NotRunning),
            _ => throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, "q.status"),
        };
        return source is { } known
            ? new RelayMessage.Quota(new RemoteQuotaReport(HostId, new QuotaSnapshot(known, observed, QuotaWindows.Ordered(windows)), running, signedIn, status))
            : null;
    }

    private static QuotaWindow DecodeQuotaWindow(RemoteFields w, string path)
    {
        var kind = w.String("kind", 16) switch
        {
            "fiveHour" => QuotaWindowKind.FiveHour,
            "sevenDay" => QuotaWindowKind.SevenDay,
            "modelWeek" => QuotaWindowKind.ModelWeek,
            "other" => QuotaWindowKind.Other,
            "month" => QuotaWindowKind.Month,
            "credits" => QuotaWindowKind.Credits,
            _ => throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, path + "kind"),
        };
        var name = w.OptionalString("name", RemoteQuotaReport.MaxNameCharacters * 4, RemoteText.Model);
        if (name is not null && new StringInfo(name).LengthInTextElements > RemoteQuotaReport.MaxNameCharacters)
        {
            throw new RemoteLineException(RemoteLineErrorKind.TooLong, path + "name");
        }

        var minutes = w.OptionalInteger("minutes");
        if (minutes is < 1 or > RemoteQuotaReport.MaxMinutes)
        {
            throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, path + "minutes");
        }

        if (kind == QuotaWindowKind.Other && minutes is null)
        {
            throw new RemoteLineException(RemoteLineErrorKind.Missing, path + "minutes");
        }

        var used = w.Number("usedPercent");
        if (used is < 0 or > RemoteQuotaReport.MaxUsedPercent)
        {
            throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, path + "usedPercent");
        }

        var resets = w.OptionalTime("resetsAtMs");
        return QuotaWindow.Of(kind, used, resets, string.IsNullOrEmpty(name) ? null : name, (int?)minutes);
    }

    private RelayMessage.QuotaRemoved? DecodeQuotaRemoved(RemoteFields f) =>
        HostSource(f.String("source", 64)) is { } source ? new RelayMessage.QuotaRemoved(HostId, source) : null;

    private RelayMessage.QuotaBridge DecodeQuotaBridge(RemoteFields f)
    {
        var state = f.String("state", 16) switch
        {
            "off" => ClaudeCliBridgeStatus.Off,
            "waiting" => ClaudeCliBridgeStatus.Waiting,
            "ok" => ClaudeCliBridgeStatus.Ok,
            "replaced" => ClaudeCliBridgeStatus.Replaced,
            "unreadable" => ClaudeCliBridgeStatus.Unreadable,
            "failed" => ClaudeCliBridgeStatus.Failed,
            _ => throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, "state"),
        };
        var path = f.String("settingsPath", RemoteLimits.CwdBytes, RemoteText.Model);
        return new RelayMessage.QuotaBridge(new RemoteQuotaBridge(HostId, state, path));
    }
}
