using AgentSpeed.Core.Quota;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb.Names;

namespace AgentSpeed.Presentation.Quota;

public enum CodexQuotaStatus { Loading, Ready, Unavailable, Paused }
public enum CodexQuotaSeverity { Neutral, Normal, Caution, Critical }

public sealed record CodexQuotaDisplay(string Prefix, string Value, string Tooltip, CodexQuotaSeverity Severity)
{
    private static readonly Lazy<StringTable> Strings = new(Localizer.LoadEmbeddedTable);

    public static CodexQuotaSeverity SeverityOf(int remaining) => remaining switch
    {
        < 10 => CodexQuotaSeverity.Critical,
        < 30 => CodexQuotaSeverity.Caution,
        _ => CodexQuotaSeverity.Normal,
    };

    public static Rgba Color(CodexQuotaSeverity severity, bool dark) => severity switch
    {
        CodexQuotaSeverity.Normal => Rgba.FromHex(dark ? "#34D399" : "#047857"),
        CodexQuotaSeverity.Caution => Rgba.FromHex(dark ? "#FFB020" : "#9A5700"),
        CodexQuotaSeverity.Critical => Rgba.FromHex(dark ? "#FB7185" : "#BE123C"),
        _ => NamePlateMaterial.For(dark).Secondary,
    };

    public static CodexQuotaDisplay Create(CodexQuotaSnapshot? snapshot, CodexQuotaStatus status, DateTimeOffset now,
        string language, TimeZoneInfo? timeZone = null) => Create(snapshot, status, now, new Localizer(Strings.Value, language), timeZone);

    public static CodexQuotaDisplay Create(CodexQuotaSnapshot? snapshot, CodexQuotaStatus status, DateTimeOffset now,
        Localizer loc, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        string Text(string key) => loc["win.quota." + key];
        string Format(string key, params object?[] args) => loc.Format("win.quota." + key, args);
        string Time(DateTimeOffset at, string format) => TimeZoneInfo.ConvertTime(at, timeZone).ToString(format, loc.Culture);
        if (status == CodexQuotaStatus.Paused)
        {
            return new("Codex", Text("paused"), Text("pausedHint"), CodexQuotaSeverity.Neutral);
        }

        if (snapshot is null)
        {
            return new("Codex", Text(status == CodexQuotaStatus.Loading ? "loading" : "unavailable"),
                Text("signInHint"), CodexQuotaSeverity.Neutral);
        }

        var stale = status != CodexQuotaStatus.Ready
            || snapshot.Windows.Any(w => w.ResetsAt is { } reset && reset <= now);
        var remaining = snapshot.LimitingWindow.RemainingPercent;
        var value = $"{remaining}%" + (stale ? " · " + Text("stale") : string.Empty);
        var lines = new List<string> { Text("title") };
        foreach (var window in snapshot.Windows)
        {
            var period = window.DurationMinutes switch
            {
                10080 => Text("weekly"),
                1440 => Text("daily"),
                > 0 when window.DurationMinutes % 60 == 0 => Format("hours", window.DurationMinutes / 60),
                > 0 => Format("minutes", window.DurationMinutes),
                _ => Text("window"),
            };
            lines.Add($"{period}: {window.RemainingPercent}%");
            if (window.ResetsAt is { } reset) { lines.Add(Format("resets", Time(reset, "MM-dd HH:mm"))); }
        }

        lines.Add(Format("updated", Time(snapshot.FetchedAt, "HH:mm:ss")));
        lines.Add(snapshot.AvailableResetCreditCount is { } count ? Format("cards", count) : Text("cardsUnavailable"));
        if (snapshot.AvailableResetCreditCount != 0)
        {
            var cards = snapshot.ResetCredits.OrderBy(c => c.ExpiresAt ?? DateTimeOffset.MaxValue).ToArray();
            var shown = snapshot.AvailableResetCreditCount is { } total ? Math.Min(total, cards.Length) : cards.Length;
            for (var i = 0; i < shown; i++)
            {
                var expiry = cards[i].ExpiresAt is { } expires ? Time(expires, "yyyy-MM-dd HH:mm:ss zzz") : Text("expiryUnknown");
                lines.Add(snapshot.AvailableResetCreditCount == 1 ? Format("expires", expiry) : Format("cardExpires", i + 1, expiry));
            }

            if (snapshot.AvailableResetCreditCount is > 0 && shown == 0) { lines.Add(Format("expires", Text("expiryUnknown"))); }
            else if (snapshot.AvailableResetCreditCount is { } known && known > shown) { lines.Add(Format("moreCards", known - shown)); }
        }

        return new(Text("prefix"), value, string.Join(Environment.NewLine, lines), stale ? CodexQuotaSeverity.Neutral : SeverityOf(remaining));
    }
}
