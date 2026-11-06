using AgentSpeed.Core;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Boards.Live;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Orb.Names;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Boards;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Tests;

/// <summary>DESIGN 13 on Windows: status words and sentences, dots, the copied command, the dialog's draft, pickers, menus and the toolbar.</summary>
public class RemoteHostsUiTests
{
    private const string A = "8C0D6B0A-1111-4222-8333-00000000000A";
    private const string B = "8C0D6B0A-1111-4222-8333-00000000000B";
    private const string Local = RemoteHost.LocalId;

    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);
    private static readonly Localizer ZhHant = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);

    private static AppSettings WithHosts(params RemoteHost[] hosts) => AppSettings.Default with { RemoteHosts = new RemoteHostsDocument(hosts) };

    [Fact]
    public void TheConnectedLineCountsTodaysSessions()
    {
        var connected = new RemoteHostStatus(RemoteStatus.Connected);
        Assert.Equal("Connected", RemoteStatusText.Sentence(connected, "devbox", 0, Now, En));
        Assert.Equal("Connected · 1 agent", RemoteStatusText.Sentence(connected, "devbox", 1, Now, En));
        Assert.Equal("Connected · 3 agents", RemoteStatusText.Sentence(connected, "devbox", 3, Now, En));
        Assert.Equal("已連線 · 3 個 agent", RemoteStatusText.Sentence(connected, "devbox", 3, Now, ZhHant));
        Assert.Equal("Reading… 42%", RemoteStatusText.Sentence(new RemoteHostStatus(RemoteStatus.Seeding(0.429)), "devbox", 0, Now, En));
    }

    [Fact]
    public void ARetryingHostCountsDownToItsNextAttempt()
    {
        var waiting = new RemoteHostStatus(RemoteStatus.Unreachable, RetryAt: Now.AddSeconds(9.2));
        Assert.Equal(En.Format("remote.status.retryIn", En["remote.status.unreachable"], 10), RemoteStatusText.Sentence(waiting, "x", 0, Now, En));
        Assert.Equal(En.Format("remote.status.retryingNow", En["remote.status.unreachable"]),
            RemoteStatusText.Sentence(waiting, "x", 0, Now.AddSeconds(10), En));
        // A host that needs the user has no countdown.
        var auth = new RemoteHostStatus(RemoteStatus.AuthFailed, RetryAt: Now.AddMinutes(5));
        Assert.Equal(En["remote.status.authFailed"], RemoteStatusText.Sentence(auth, "x", 0, Now, En));
    }

    [Fact]
    public void SentencesNameWhatToDoOnWindows()
    {
        Assert.Contains("ssh 'ssh://[::1]:22'", RemoteStatusText.Base(RemoteStatus.HostKeyUnknown, "ssh://[::1]:22", En), StringComparison.Ordinal);
        Assert.Contains("ssh devbox", RemoteStatusText.Base(RemoteStatus.HostKeyUnknown, "devbox", En), StringComparison.Ordinal);
        Assert.Equal(En[RemoteStatusText.SshMissingKey], RemoteStatusText.Base(RemoteStatus.SshMissing, "x", En));
        Assert.DoesNotContain("/usr/bin", RemoteStatusText.Base(RemoteStatus.SshMissing, "x", En), StringComparison.Ordinal);
        Assert.Contains("FreeBSD amd64", RemoteStatusText.Base(RemoteStatus.UnsupportedSystem("FreeBSD amd64"), "x", En), StringComparison.Ordinal);
        foreach (var kind in Enum.GetValues<RemoteStatusKind>())
        {
            var status = new RemoteStatus(kind, System: "X");
            Assert.False(string.IsNullOrWhiteSpace(RemoteStatusText.Base(status, "devbox", En)), kind.ToString());
            Assert.False(string.IsNullOrWhiteSpace(RemoteStatusText.Word(status, ZhHant)), kind.ToString());
        }
    }

    [Fact]
    public void NotesFollowTheClockAndTheHistory()
    {
        var skewed = new RemoteHostStatus(RemoteStatus.Connected, ClockOffsetMs: -12_400);
        Assert.Equal(En.Format("remote.clock.skewed", 12), RemoteStatusText.ClockNote(skewed, En));
        Assert.Equal(En.Format("remote.clock.skewedMinutes", 3), RemoteStatusText.ClockNote(skewed with { ClockOffsetMs = 170_000 }, En));
        Assert.Null(RemoteStatusText.ClockNote(skewed with { ClockOffsetMs = 4_000 }, En));
        Assert.Null(RemoteStatusText.ClockNote(skewed with { Status = RemoteStatus.Unreachable }, En));
        Assert.Null(RemoteStatusText.HistoryNote(skewed, En));
        Assert.Equal(En["remote.status.historyTooLarge"], RemoteStatusText.HistoryNote(skewed with { HistoryTooLarge = true }, En));
    }

    [Fact]
    public void TheTooltipNamesTheHelperOrTheLastError()
    {
        var connected = new RemoteHostStatus(RemoteStatus.Connected, RelayVersion: "1.7.0", RelayArch: "aarch64");
        Assert.Equal(En.Format("remote.status.relay", "1.7.0", "aarch64"), RemoteStatusText.Tooltip(connected, Now, En));
        var exited = new RemoteHostStatus(RemoteStatus.Exited(5), LastErrorAt: Now.AddMinutes(-3));
        var tooltip = RemoteStatusText.Tooltip(exited, Now, En)!;
        Assert.Contains(En.Format("remote.status.exitCode", 5), tooltip, StringComparison.Ordinal);
        Assert.StartsWith(En.Format("remote.status.lastError", string.Empty).TrimEnd(), tooltip, StringComparison.Ordinal);
        Assert.Null(RemoteStatusText.Tooltip(new RemoteHostStatus(RemoteStatus.Connecting), Now, En));
    }

    [Fact]
    public void DotsAndWordsSortTheStatuses()
    {
        Assert.Equal(RemoteDotKind.Connected, RemoteDots.Of(RemoteStatus.Connected));
        Assert.Equal(RemoteDotKind.Progress, RemoteDots.Of(RemoteStatus.Installing(0.5)));
        Assert.Equal(RemoteDotKind.Retrying, RemoteDots.Of(RemoteStatus.Exited(1)));
        Assert.Equal(RemoteDotKind.Attention, RemoteDots.Of(RemoteStatus.HostKeyUnknown));
        Assert.Equal(RemoteDotKind.Idle, RemoteDots.Of(RemoteStatus.NetworkOff));
        Assert.Equal(MenuStatusTone.Hollow, RemoteDots.Tone(RemoteDotKind.Idle));
        Assert.True(RemoteDots.CanRetry(RemoteStatus.AuthFailed));
        Assert.True(RemoteDots.CanRetry(RemoteStatus.Unreachable));
        Assert.False(RemoteDots.CanRetry(RemoteStatus.InvalidDestination));
        Assert.False(RemoteDots.CanRetry(RemoteStatus.Connected));
        Assert.Equal(En["remote.word.retrying"], RemoteStatusText.Word(RemoteStatus.ProtocolError, En));
        Assert.Equal(En["remote.word.attention"], RemoteStatusText.Word(RemoteStatus.CannotVerify, En));
        Assert.Equal(En["remote.word.offline"], RemoteStatusText.Word(RemoteStatus.NetworkOff, En));
        Assert.Equal(En["remote.status.disabled"], RemoteStatusText.Word(RemoteStatus.Disabled, En));
    }

    [Theory]
    [InlineData("devbox", "ssh devbox")]
    [InlineData("zoe@gpu-01.lab.example.com", "ssh zoe@gpu-01.lab.example.com")]
    [InlineData("ssh://ci@build:2222", "ssh ssh://ci@build:2222")]
    [InlineData("@splat", "ssh '@splat'")]
    [InlineData("a,b", "ssh 'a,b'")]
    [InlineData("[fe80::1]", "ssh '[fe80::1]'")]
    [InlineData("~host", "ssh '~host'")]
    [InlineData("host%PATH%", "ssh 'host%PATH%'")]
    public void TheCopiedCommandIsOnePowerShellArgument(string destination, string command)
    {
        Assert.Null(RemoteHostValidation.DestinationIssue(destination));
        Assert.Equal(command, SshCommandText.Command(destination));
    }

    [Fact]
    public void TheShownStatusComesFromTheSettingsFirst()
    {
        var host = new RemoteHost("devbox", A);
        var live = new RemoteHostStatus(RemoteStatus.Connected);
        Assert.Equal(RemoteStatusKind.NetworkOff, RemoteHostStates.Effective(host, false, live, false).Status.Kind);
        Assert.Equal(RemoteStatusKind.Disabled, RemoteHostStates.Effective(host with { Enabled = false }, true, live, false).Status.Kind);
        Assert.Equal(RemoteStatusKind.InvalidDestination, RemoteHostStates.Effective(new RemoteHost("-x", A), true, live, false).Status.Kind);
        Assert.Equal(RemoteStatusKind.Connecting, RemoteHostStates.Effective(host, true, null, false).Status.Kind);
        Assert.Equal(RemoteStatusKind.Connecting,
            RemoteHostStates.Effective(host, true, new RemoteHostStatus(RemoteStatus.Disabled), false).Status.Kind);
        Assert.Same(live, RemoteHostStates.Effective(host, true, live, false));
        var tooLarge = RemoteHostStates.Effective(host, true, live, true);
        Assert.True(tooLarge.HistoryTooLarge);
        Assert.Equal(RemoteStatusKind.Connected, tooLarge.Status.Kind);
        Assert.True(RemoteHostStates.Effective(host, false, live, true).HistoryTooLarge);
    }

    [Fact]
    public void ThisComputerIsNamedForWindowsAndListsFirst()
    {
        Assert.Equal("This PC", RemoteHostStates.LocalName(En));
        Assert.Equal("本機", RemoteHostStates.LocalName(ZhHant));
        Assert.Empty(RemoteHostStates.Choices(RemoteHostsDocument.Empty, En));
        var document = new RemoteHostsDocument([new RemoteHost("devbox", A), new RemoteHost("user@devbox", B)]);
        Assert.Equal(["This PC", "devbox", "devbox (2)"], RemoteHostStates.Choices(document, En).Select(c => c.Name));
        Assert.Equal([Local, A, B], RemoteHostStates.Order(document));
    }

    [Fact]
    public void TheDraftChecksItsFieldsAsTheUserTypes()
    {
        var draft = new RemoteHostDraft();
        Assert.False(draft.IsEditing);
        Assert.Equal(RemoteFieldIssue.Empty, draft.DestinationIssue);
        Assert.False(draft.IsValid);
        draft = draft with { Destination = "  zoe@devbox \n" };
        Assert.Null(draft.DestinationIssue);
        Assert.True(draft.IsValid);
        Assert.Equal(RemoteFieldIssue.ForbiddenCharacter, (draft with { Destination = "dev box" }).DestinationIssue);
        Assert.Equal(RemoteFieldIssue.LeadingDash, (draft with { Destination = "-oProxyCommand=x" }).DestinationIssue);
        Assert.Null((draft with { RelayFolder = "  " }).FolderIssue);
        Assert.Equal(RemoteFieldIssue.UnsafeComponent, (draft with { RelayFolder = "~/../x" }).FolderIssue);
        Assert.Equal(RemoteHost.DefaultRelayFolder, draft.EffectiveFolder);
        Assert.True((draft with { Name = new string('n', 33) }).NameIsTooLong);
        Assert.False((draft with { Name = "  " + new string('n', 32) + "  " }).NameIsTooLong);

        var host = (draft with { Name = " ", RelayFolder = " /opt/as " }).Host(A);
        Assert.Equal(A, host.Id);
        Assert.Equal("zoe@devbox", host.Destination);
        Assert.Null(host.Name);
        Assert.Equal("/opt/as", host.RelayFolder);
        Assert.True(host.Enabled);
    }

    [Fact]
    public void EditingKeepsTheHostsIdSwitchAndUnknownMembers()
    {
        var original = new RemoteHost("devbox", A) { Enabled = false, Name = "Dev", ExtraJson = """{"future":1}""" };
        var edited = new RemoteHostDraft(original) with { Destination = "devbox2" };
        Assert.True(edited.IsEditing);
        Assert.Equal("Dev", edited.Name);
        var host = edited.Host();
        Assert.Equal(A, host.Id);
        Assert.False(host.Enabled);
        Assert.Equal(original.ExtraJson, host.ExtraJson);
    }

    [Fact]
    public void ThePlaceholderIsTheDistinctAutomaticName()
    {
        var document = new RemoteHostsDocument([new RemoteHost("zoe@devbox", A)]);
        Assert.Equal("devbox (2)", (new RemoteHostDraft() with { Destination = "devbox" }).AutomaticName(document));
        Assert.Equal("devbox", (new RemoteHostDraft(document.Hosts[0]) with { Destination = "root@devbox" }).AutomaticName(document));
        Assert.Equal(string.Empty, (new RemoteHostDraft() with { Destination = "bad host" }).AutomaticName(document));
        var full = new RemoteHostsDocument(Enumerable.Range(0, RemoteHostsDocument.MaxHosts).Select(i => new RemoteHost("devbox")));
        Assert.Equal("devbox", (new RemoteHostDraft() with { Destination = "devbox" }).AutomaticName(full));
    }

    [Fact]
    public void LimitedOrbsAndBoardsAskTheMonitorForTheirPeaks()
    {
        var settings = AppSettings.Default with
        {
            Orbs = new OrbsDocument(
            [
                new OrbDefinition("all"),
                new OrbDefinition("codex") { Sources = OrbSources.Of(SourceKind.Codex) },
                new OrbDefinition("codex-dev") { Sources = OrbSources.Of(SourceKind.Codex), Hosts = OrbHosts.Of(A) },
                new OrbDefinition("dev") { Hosts = OrbHosts.Of(A) },
                new OrbDefinition("codex-again") { Sources = OrbSources.Of(SourceKind.Codex) },
            ]),
            Boards = new BoardsDocument([new BoardDefinition("b") { Hosts = OrbHosts.Of(Local, B) }, new BoardDefinition("ball")]),
        };
        var keys = SubsetRequests.For(settings);
        Assert.Equal(4, keys.Count);
        Assert.Contains(new SubsetKey(SourceSet.Of(SourceKind.Codex)), keys);
        Assert.Contains(new SubsetKey(SourceSet.Of(SourceKind.Codex), [A]), keys);
        Assert.Contains(new SubsetKey(SourceSet.Everything, [A]), keys);
        Assert.Contains(new SubsetKey(SourceSet.Everything, [B, Local]), keys);
        Assert.Empty(SubsetRequests.For(AppSettings.Default with { Orbs = new OrbsDocument([new OrbDefinition("x")]), Boards = BoardsDocument.Empty }));
    }

    [Fact]
    public void TheOrbMenuGainsHostsOnlyWithRemoteHosts()
    {
        var orb = new OrbDefinition("o0") { Sources = OrbSources.Of(SourceKind.Codex), Hosts = OrbHosts.Of(A) };
        var plain = AppSettings.Default with { Orbs = new OrbsDocument([orb]) };
        var without = OrbMenuModel.Build("o0", plain, SourceAvailability.All, En)!;
        Assert.DoesNotContain(without.Rows, r => r.Label == En["remote.hosts"]);

        var settings = plain with { RemoteHosts = new RemoteHostsDocument([new RemoteHost("devbox", A), new RemoteHost("gpu", B)]) };
        var states = new Dictionary<string, MenuHostState> { [B] = new(RemoteDotKind.Retrying, En["remote.word.retrying"]) };
        var menu = OrbMenuModel.Build("o0", settings, SourceAvailability.All, En, hosts: states)!;
        Assert.Equal(En["source.codex"] + " · devbox", menu.Rows.First().Label);
        var labels = menu.Rows.Select(r => r.Label).ToList();
        Assert.Equal(labels.IndexOf(En["orbs.sources"]) + 1, labels.IndexOf(En["remote.hosts"]));
        var hosts = menu.Rows.Single(r => r.Label == En["remote.hosts"]).Submenu!;
        var rows = hosts.Rows.ToList();
        Assert.Equal([En["remote.hosts.all"], "This PC", "devbox", "gpu"], rows.Select(r => r.Label));
        Assert.False(rows[0].IsChecked);
        Assert.Equal(new MenuAction.SetOrbAllHosts("o0"), rows[0].Action);
        Assert.True(rows[2].IsChecked);
        Assert.False(rows[2].IsEnabled);
        Assert.Equal(En["remote.hosts.lastOne"], rows[2].ToolTip);
        Assert.Equal(new MenuAction.ToggleOrbHost("o0", Local), rows[1].Action);
        Assert.Equal(new MenuIcon.StatusDot(MenuStatusTone.Green), rows[1].Icon);
        Assert.Equal(new MenuIcon.StatusDot(MenuStatusTone.Amber), rows[3].Icon);
        Assert.Equal(En["remote.word.retrying"], rows[3].Detail);
        Assert.Equal(new MenuIcon.StatusDot(MenuStatusTone.Gray), rows[2].Icon);
    }

    [Fact]
    public void BoardsNameTheirHostsInTheMenuAndTheList()
    {
        var board = new BoardDefinition("b") { Hosts = OrbHosts.Of(A, B) };
        var settings = WithHosts(new RemoteHost("devbox", A), new RemoteHost("gpu", B)) with { Boards = new BoardsDocument([board]) };
        var choices = RemoteHostStates.Choices(settings.RemoteHosts, En);
        var menu = BoardMenuModel.Build("b", settings, SourceAvailability.All, En)!;
        Assert.EndsWith(" · " + En.Format("orbs.name.more", "devbox", 1), menu.Rows.First().Label, StringComparison.Ordinal);
        Assert.Contains(menu.Rows, r => r.Label == En["remote.hosts"]);
        Assert.Contains("devbox +1 · M", BoardRowText.Detail(board, En, choices), StringComparison.Ordinal);
        Assert.Equal(BoardRowText.Detail(board with { Hosts = OrbHosts.All }, En), BoardRowText.Detail(board with { Hosts = OrbHosts.All }, En, choices));
    }

    [Fact]
    public void NamePlatesKeepTheHostsOfAnAutomaticName()
    {
        OrbHostChoice[] hosts = [new(Local, "This PC"), new(A, "devbox")];
        var orb = new OrbDefinition("o") { Sources = OrbSources.Of(SourceSet.Create([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Gemini])), Hosts = OrbHosts.Of(A) };
        Assert.EndsWith(" · devbox", NamePlateName.For(orb, En, hosts).Text, StringComparison.Ordinal);
        Assert.Null(NamePlateName.For(orb, En, hosts).Head);
        Assert.NotNull(NamePlateName.For(orb with { Hosts = OrbHosts.All }, En, hosts).Head);
        Assert.Equal("Mine", NamePlateName.For(orb with { Name = "Mine" }, En, hosts).Text);
    }

    [Fact]
    public void TheHistoryToolbarMakesRoomForTheHostPicker()
    {
        double[] labels = [34.9, 55.6, 36.5, 43.5, 43.5, 50.5, 14.6, 53.5];
        const double Minimum = 720 - 16 - 2 * 24;
        const double Roomy = 1200 - 16 - 2 * 24;
        var roomy = HistoryToolbar.FitWithHosts(Roomy, labels, HistoryToolbar.PickerMinWidth, 160, 40, 84.7);
        Assert.Equal(new ToolbarFit(ToolbarDensity.Full, HostPickerMode.Name), roomy);
        var tight = HistoryToolbar.FitWithHosts(Minimum, labels, HistoryToolbar.PickerMinWidth, 210, 40, 84.7, 180, 32);
        Assert.True(HistoryToolbar.WidthWithHosts(tight, labels, HistoryToolbar.PickerMinWidth, 210, 40, 84.7, 180, 32) <= Minimum, tight.ToString());
        Assert.Equal(HostPickerMode.SourceDot, HistoryToolbar.FitWithHosts(100, labels, HistoryToolbar.PickerMinWidth, 210, 40, 84.7).Hosts);

        var stages = new[]
        {
            new ToolbarFit(ToolbarDensity.Full, HostPickerMode.Name), new ToolbarFit(ToolbarDensity.IconExport, HostPickerMode.Name),
            new ToolbarFit(ToolbarDensity.CompactRange, HostPickerMode.Name), new ToolbarFit(ToolbarDensity.CompactPicker, HostPickerMode.Name),
            new ToolbarFit(ToolbarDensity.CompactPicker, HostPickerMode.Symbol), new ToolbarFit(ToolbarDensity.Tight, HostPickerMode.Symbol),
            new ToolbarFit(ToolbarDensity.Tight, HostPickerMode.SourceDot),
        };
        var widths = stages.Select(s => HistoryToolbar.WidthWithHosts(s, labels, HistoryToolbar.PickerMinWidth, 210, 40, 84.7)).ToArray();
        for (var i = 1; i < widths.Length; i++)
        {
            Assert.True(widths[i] < widths[i - 1], $"{stages[i]} is narrower than {stages[i - 1]}");
            Assert.Equal(stages[i], HistoryToolbar.FitWithHosts(widths[i], labels, HistoryToolbar.PickerMinWidth, 210, 40, 84.7));
        }

        Assert.Equal(HistoryToolbar.HostPickerMaxWidth, HistoryToolbar.HostPickerWidth(stages[0], 400));
        Assert.Equal(HistoryToolbar.HostPickerMinWidth, HistoryToolbar.HostPickerWidth(stages[0], 20));
        Assert.Equal(HistoryToolbar.CompactHostPickerWidth, HistoryToolbar.HostPickerWidth(stages[3], 400));
        Assert.Equal(HistoryToolbar.SymbolPickerWidth, HistoryToolbar.HostPickerWidth(stages[4], 400));
        Assert.Equal(HistoryToolbar.SymbolPickerWidth, HistoryToolbar.SourcePickerWidth(stages[6], 200));

        // A long name takes the room the other controls leave, never less than its stage's width nor more than it needs.
        var compact = stages[3];
        var room = HistoryToolbar.WidthWithHosts(compact, labels, HistoryToolbar.PickerMinWidth, 400, 40, 84.7) + 50;
        Assert.Equal(HistoryToolbar.CompactHostPickerWidth + 50,
            HistoryToolbar.FittedHostPickerWidth(room, compact, labels, HistoryToolbar.PickerMinWidth, 400, 40, 84.7));
        Assert.Equal(HistoryToolbar.HostPickerMaxWidth,
            HistoryToolbar.FittedHostPickerWidth(room + 500, compact, labels, HistoryToolbar.PickerMinWidth, 400, 40, 84.7));
        Assert.Equal(150, HistoryToolbar.FittedHostPickerWidth(room + 500, stages[0], labels, HistoryToolbar.PickerMinWidth, 150, 40, 84.7));
        Assert.Equal(HistoryToolbar.SymbolPickerWidth,
            HistoryToolbar.FittedHostPickerWidth(room + 500, stages[4], labels, HistoryToolbar.PickerMinWidth, 400, 40, 84.7));
    }

    [Fact]
    public void ABoardOfOfflineHostsSaysSoInsteadOfItsTitle()
    {
        var snapshot = SpeedSnapshot.Empty with
        {
            Timestamp = Now,
            State = MonitorState.Running,
            Hosts =
            [
                new HostStatus(Local, Local, null, Rate.Zero, Rate.Zero, LiveCost.Zero, TokenTotals.Zero, CostTally.Zero, 0, true),
                new HostStatus(A, "devbox", new RemoteHostStatus(RemoteStatus.Unreachable), Rate.Zero, Rate.Zero, LiveCost.Zero, TokenTotals.Zero,
                    CostTally.Zero, 0, false),
            ],
            Sources = [new SourceStatus(SourceKind.Codex, true, "c", true, 0, Rate.Zero, TokenTotals.Zero, 0)],
        };
        var texts = new BoardTexts(En, TitleTheme.Vehicles);
        var offline = new BoardLiveFeed(new BoardDefinition("b") { Hosts = OrbHosts.Of(A) });
        offline.Configure(offline.Board, SpeedMetric.Output, 2000, 0);
        var tick = offline.Update(snapshot, 0, texts);
        Assert.Equal(En["remote.orb.offline"], tick.Title);
        Assert.Equal("–", tick.ValueText);

        var local = new BoardLiveFeed(new BoardDefinition("b") { Hosts = OrbHosts.Of(Local, A) });
        local.Configure(local.Board, SpeedMetric.Output, 2000, 0);
        Assert.NotEqual(En["remote.orb.offline"], local.Update(snapshot, 0, texts).Title);
    }
}
