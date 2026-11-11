using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Quota;

namespace AgentSpeed.Presentation.Tests.Quota;

/// <summary>The 額度 card's items of an orb that includes hosts (DESIGN 13 <i>Host quotas</i>, REMOTE 11.2).</summary>
public sealed class HostQuotaCardItemsTests
{
    private const long Hour = 3_600_000;
    private const long Now = 1794338773000;
    private const string Devbox = "A0000000-0000-4000-8000-000000000001";
    private const string Gpu = "A0000000-0000-4000-8000-000000000002";
    private static readonly IReadOnlyList<(string Id, string Name)> Order = [(Devbox, "devbox"), (Gpu, "gpu-01.lab")];

    private static QuotaSnapshot Snapshot(SourceKind source, int percent = 40) =>
        new(source, Now - 60_000, [QuotaWindow.Of(QuotaWindowKind.SevenDay, percent, Now + 50 * Hour)]);

    private static RemoteQuotaReport Report(string host, SourceKind source, bool running = true, bool signedIn = true, bool windows = true) =>
        new(host, windows ? Snapshot(source) : new QuotaSnapshot(source, Now, []), running, signedIn, QuotaRouteStatus.Ok(Now));

    private static HostQuotaState Host(params RemoteQuotaReport[] reports) =>
        new() { Reports = reports.ToImmutableDictionary(r => r.Source) };

    private static QuotaState State()
    {
        var local = new[] { Snapshot(SourceKind.Qoder, 37), Snapshot(SourceKind.QoderCli, 37), Snapshot(SourceKind.ClaudeDesktop), Snapshot(SourceKind.Codex) };
        return new QuotaState
        {
            Snapshots = local.ToImmutableDictionary(s => s.Source),
            Active = [.. local.Select(s => s.Source)],
            Hosts = ImmutableDictionary.CreateRange(new Dictionary<string, HostQuotaState>
            {
                // Inserted out of settings order: the card follows the settings, not the dictionary.
                [Gpu] = Host(Report(Gpu, SourceKind.Gemini), Report(Gpu, SourceKind.Codex, running: false)),
                [Devbox] = Host(Report(Devbox, SourceKind.Kimi), Report(Devbox, SourceKind.Codex), Report(Devbox, SourceKind.ClaudeCode, signedIn: false),
                    Report(Devbox, SourceKind.Droid, windows: false)),
            }),
        };
    }

    private static HashSet<string> Connected(params string[] ids) => new(ids, StringComparer.Ordinal);

    [Fact]
    public void ThisComputerComesFirstThenEachHostInSettingsOrderInSourceOrder()
    {
        var items = State().CardItems(_ => true, _ => true, Order, Connected(Devbox, Gpu));
        Assert.Equal(
            [
                (SourceKind.Codex, (string?)null), (SourceKind.ClaudeDesktop, null), (SourceKind.Qoder, null),
                (SourceKind.Codex, "devbox"), (SourceKind.Kimi, "devbox"), (SourceKind.Gemini, "gpu-01.lab"),
            ],
            items.Select(i => (i.Source, i.HostName)));
        Assert.Equal(Devbox, items[3].HostId);
        Assert.Null(items[0].HostId);
    }

    [Fact]
    public void QodersOneAccountShowsOnceOnThisComputer()
    {
        var items = State().CardItems(_ => true, id => id == RemoteHost.LocalId, Order, Connected());
        Assert.Single(items, i => i.Source is SourceKind.Qoder or SourceKind.QoderCli);
        Assert.Equal(SourceKind.QoderCli, Assert.Single(State().CardItems(s => s == SourceKind.QoderCli, _ => true, Order, Connected())).Source);
    }

    [Fact]
    public void OnlyConnectedTrackedHostsAddTheirSources()
    {
        var state = State();
        Assert.DoesNotContain(state.CardItems(_ => true, _ => true, Order, Connected(Devbox)), i => i.HostId == Gpu);
        Assert.All(state.CardItems(_ => true, _ => true, Order, Connected()), i => Assert.Null(i.HostId));

        var devboxOnly = state.CardItems(_ => true, id => id == Devbox, Order, Connected(Devbox, Gpu));
        Assert.Equal([SourceKind.Codex, SourceKind.Kimi], devboxOnly.Select(i => i.Source));
        Assert.All(devboxOnly, i => Assert.Equal("devbox", i.HostName));
    }

    [Fact]
    public void AHostSourceShowsOnlyWhileItsAgentRunsSignedInWithAWindow()
    {
        var items = State().CardItems(_ => true, _ => true, Order, Connected(Devbox, Gpu));
        Assert.DoesNotContain(items, i => i.HostId == Devbox && i.Source is SourceKind.ClaudeCode or SourceKind.Droid);
        Assert.DoesNotContain(items, i => i.HostId == Gpu && i.Source == SourceKind.Codex);
    }

    [Fact]
    public void TheOrbsSourcesLimitItsHostsToo()
    {
        var items = State().CardItems(s => s == SourceKind.Codex, _ => true, Order, Connected(Devbox, Gpu));
        Assert.Equal([(string?)null, "devbox"], items.Select(i => i.HostName));
        Assert.NotEqual(items[0].Key, items[1].Key);
        Assert.Equal("Codex@" + Devbox, items[1].Key);
    }
}
