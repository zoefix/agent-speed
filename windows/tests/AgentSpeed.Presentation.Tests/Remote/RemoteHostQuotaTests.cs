using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Remote;

namespace AgentSpeed.Presentation.Tests.Remote;

/// <summary>A host's quotas in the host editor, the removal confirmation and Dashboard → Hosts (DESIGN 13 <i>Host quotas</i>).</summary>
public sealed class RemoteHostQuotaTests
{
    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;
    private const long Now = 1794338773000;
    private const string Id = "A0000000-0000-4000-8000-000000000001";
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);
    private static readonly Localizer ZhHant = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);

    private static RemoteQuotaReport Report(SourceKind source, long observedMs, params QuotaWindow[] windows) =>
        new(Id, new QuotaSnapshot(source, observedMs, windows), true, true, QuotaRouteStatus.Ok(observedMs));

    private static HostQuotaState Bridged(ClaudeCliBridgeStatus state, string path = "~/.claude/settings.json", bool report = true) => new()
    {
        Reports = report
            ? ImmutableDictionary<SourceKind, RemoteQuotaReport>.Empty.Add(SourceKind.ClaudeCode,
                Report(SourceKind.ClaudeCode, Now - 4 * Minute, QuotaWindow.Of(QuotaWindowKind.FiveHour, 6, Now + 4 * Hour)))
            : ImmutableDictionary<SourceKind, RemoteQuotaReport>.Empty,
        Bridge = new RemoteQuotaBridge(Id, state, path),
    };

    [Fact]
    public void TheDraftCarriesTheBridgeSwitchThroughAnEdit()
    {
        var host = new RemoteHost("devbox", Id) { Name = "Dev", RelayFolder = "~/as", QuotaClaudeCli = true, ExtraJson = "{\"later\":1}" };
        var draft = new RemoteHostDraft(host);
        Assert.True(draft.QuotaClaudeCli);

        var edited = (draft with { Name = "Dev box" }).Host();
        Assert.True(edited.QuotaClaudeCli);
        Assert.Equal(Id, edited.Id);
        Assert.Equal("{\"later\":1}", edited.ExtraJson);
        Assert.Equal(host with { Name = "Dev box" }, edited);

        Assert.False((draft with { QuotaClaudeCli = false }).Host().QuotaClaudeCli);
        Assert.False(new RemoteHostDraft().QuotaClaudeCli);
        Assert.True((new RemoteHostDraft() with { Destination = "devbox", QuotaClaudeCli = true }).Host("B0000000-0000-4000-8000-000000000002").QuotaClaudeCli);
    }

    [Fact]
    public void RemovingABridgedHostSaysWhatStaysInItsFolder()
    {
        var folder = RemoteHostQuotaText.Unbroken("~/tools/as");
        var plain = new RemoteHost("devbox", Id) { RelayFolder = "~/tools/as" };
        Assert.Equal(En.Format("remote.remove.files", folder), RemoteHostQuotaText.RemovalBody(plain, En));

        var bridged = plain with { QuotaClaudeCli = true };
        var body = RemoteHostQuotaText.RemovalBody(bridged, En);
        Assert.StartsWith(En.Format("remote.remove.files", folder) + "\n\n", body, StringComparison.Ordinal);
        Assert.EndsWith(En.Format("remote.quota.removeNote", folder), body, StringComparison.Ordinal);
        Assert.Contains("claude-settings.backup.json", body, StringComparison.Ordinal);

        var zh = RemoteHostQuotaText.RemovalBody(new RemoteHost("devbox", Id) { QuotaClaudeCli = true }, ZhHant);
        Assert.Contains(RemoteHostQuotaText.Unbroken(RemoteHost.DefaultRelayFolder), zh, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderReadsAsOnePieceInTheSentence()
    {
        var shown = RemoteHostQuotaText.Unbroken("~/.cache/agent-speed");
        Assert.Equal("~/\u2060.cache/\u2060agent\u2011speed", shown);
        Assert.Equal("~/.cache/agent-speed", shown.Replace("\u2060", string.Empty, StringComparison.Ordinal).Replace('\u2011', '-'));
    }

    [Fact]
    public void TheConfirmationNamesTheSettingsFileTheHostReported()
    {
        Assert.Equal("~/.claude/settings.json", RemoteHostQuotaText.SettingsPath(HostQuotaState.Empty));
        Assert.Equal("~/.config/claude/settings.json", RemoteHostQuotaText.SettingsPath(Bridged(ClaudeCliBridgeStatus.Waiting, "~/.config/claude/settings.json")));
        Assert.Equal("~/.claude/settings.json", RemoteHostQuotaText.SettingsPath(Bridged(ClaudeCliBridgeStatus.Waiting, string.Empty)));
    }

    [Fact]
    public void TheBridgeLineSpeaksAsSettingsDoes()
    {
        RemoteBridgeLine Line(HostQuotaState state, bool shown = true) => RemoteHostQuotaText.Bridge(state, shown, Now, En, TimeZoneInfo.Utc);

        Assert.True(Line(HostQuotaState.Empty).IsEmpty);
        Assert.True(Line(Bridged(ClaudeCliBridgeStatus.Off)).IsEmpty);
        Assert.True(Line(Bridged(ClaudeCliBridgeStatus.Ok), shown: false).IsEmpty);

        var ok = Line(Bridged(ClaudeCliBridgeStatus.Ok));
        Assert.Equal(En.Format("settings.quota.cli.ok", QuotaText.When(Now - 4 * Minute, Now, En, TimeZoneInfo.Utc)), ok.Text);
        Assert.Equal(RemoteBridgeMark.Ok, ok.Mark);
        Assert.False(ok.Reconnect);

        // Connected before the first quota arrives reads as waiting, never as "updated" with no time.
        Assert.Equal(new RemoteBridgeLine(En["settings.quota.cli.waiting"], RemoteBridgeMark.None, false), Line(Bridged(ClaudeCliBridgeStatus.Ok, report: false)));
        Assert.Equal(new RemoteBridgeLine(En["settings.quota.cli.waiting"], RemoteBridgeMark.None, false), Line(Bridged(ClaudeCliBridgeStatus.Waiting)));

        var replaced = Line(Bridged(ClaudeCliBridgeStatus.Replaced));
        Assert.Equal((En["settings.quota.cli.replaced"], RemoteBridgeMark.Warning, true), (replaced.Text, replaced.Mark, replaced.Reconnect));

        var unreadable = Line(Bridged(ClaudeCliBridgeStatus.Unreadable, "~/.config/claude/settings.json"));
        Assert.Equal(En.Format("settings.quota.cli.unreadable", "~/.config/claude/settings.json"), unreadable.Text);
        Assert.Equal(RemoteBridgeMark.Warning, unreadable.Mark);
        Assert.Equal(En.Format("settings.quota.cli.failed", "~/.claude/settings.json"), Line(Bridged(ClaudeCliBridgeStatus.Failed)).Text);
    }

    [Fact]
    public void AHostRowListsEachSourceWithAQuotaOfTheLastDayInSourceOrder()
    {
        var state = new QuotaState
        {
            Hosts = ImmutableDictionary<string, HostQuotaState>.Empty.Add(Id, new HostQuotaState
            {
                Reports = ImmutableDictionary.CreateRange(new Dictionary<SourceKind, RemoteQuotaReport>
                {
                    [SourceKind.Kimi] = Report(SourceKind.Kimi, Now - 3 * Minute, QuotaWindow.Of(QuotaWindowKind.FiveHour, 52, Now + Hour)),
                    [SourceKind.Codex] = Report(SourceKind.Codex, Now - 2 * Hour, QuotaWindow.Of(QuotaWindowKind.SevenDay, 40, Now + 50 * Hour)),
                    [SourceKind.ClaudeCode] = Report(SourceKind.ClaudeCode, Now - 3 * Minute,
                        QuotaWindow.Of(QuotaWindowKind.FiveHour, 6, Now + 4 * Hour), QuotaWindow.Of(QuotaWindowKind.SevenDay, 86, Now + 70 * Hour)),
                    [SourceKind.Gemini] = Report(SourceKind.Gemini, Now - 25 * Hour, QuotaWindow.Of(QuotaWindowKind.Other, 40, Now + 9 * Hour, "Pro", 1440)),
                    [SourceKind.Droid] = Report(SourceKind.Droid, Now - Minute),
                }),
            }),
        };

        var groups = HostQuotaGroups.Of(state, Id, Now, En, TimeZoneInfo.Utc);
        Assert.Equal([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Kimi], groups.Select(g => g.Source));
        Assert.Equal(["Codex", "Claude Code", "Kimi Code CLI"], groups.Select(g => g.Name));
        Assert.Equal(["5h", "7d"], groups[1].Lines.Select(l => l.Label));
        Assert.StartsWith(" · ", groups[0].Lines[0].Age, StringComparison.Ordinal);
        Assert.Equal(string.Empty, groups[2].Lines[0].Age);

        var spoken = HostQuotaGroups.Spoken(groups, En);
        Assert.StartsWith("Codex 7d 40% used, ", spoken, StringComparison.Ordinal);
        Assert.Contains(", Claude Code 5h 6% used, ", spoken, StringComparison.Ordinal);

        Assert.Empty(HostQuotaGroups.Of(state, RemoteHost.LocalId, Now, En));
        Assert.Empty(HostQuotaGroups.Of(state, "B0000000-0000-4000-8000-000000000002", Now, En));
        Assert.Equal(string.Empty, HostQuotaGroups.Spoken([], En));
    }
}
