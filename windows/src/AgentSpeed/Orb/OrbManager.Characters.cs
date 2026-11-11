using AgentSpeed.Characters;
using AgentSpeed.Core;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Characters;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Lines;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Orb;

/// <summary>
/// The <c>character</c> orbs (DESIGN 2a.6, CHARACTERS 7–8): which character each shows (Auto follows its fastest live
/// vendor with a 3 s hold, or the locked pack), her content and the cards' live values (network speed by the orb's scope,
/// 性能 and 思考 with the 首字 latency, the cost rate, the running models), the automatic download when an orb switches to
/// the style, and her cards in the orb's accessible text.
/// </summary>
internal sealed partial class OrbManager
{
    private readonly Dictionary<string, CharacterSelection> _selections = new(StringComparer.Ordinal);

    /// <summary>The character packs; without it a <c>character</c> orb shows the empty state.</summary>
    public CharacterService? Characters { get; init; }

    /// <summary>The price table models resolve against (vendors and model names); null resolves by family and source only.</summary>
    public Func<PriceTable?>? Prices { get; init; }

    /// <summary>The quotas the 額度 cards show (QUOTAS 6).</summary>
    private Presentation.Quota.QuotaState _quota = Presentation.Quota.QuotaState.Empty;

    /// <summary>The quotas changed: every character orb shows its 額度 card again.</summary>
    public void UpdateQuota(Presentation.Quota.QuotaState state)
    {
        _quota = state;
        RefreshCharacters();
    }

    /// <summary>
    /// Developer capture only: remote hosts the 額度 cards treat as connected although the sandbox never connects them. Null
    /// in normal use.
    /// </summary>
    internal IReadOnlySet<string>? CaptureConnected
    {
        get => _captureConnected;
        set
        {
            _captureConnected = value;
            RefreshCharacters();
        }
    }

    private IReadOnlySet<string>? _captureConnected;

    /// <summary>
    /// The 額度 card of an orb (DESIGN 2a.6, 13 <i>Host quotas</i>): this computer's sources while the orb tracks it, then each
    /// tracked, connected host's in settings order with its display name.
    /// </summary>
    private OrbQuotaCard QuotaCard(OrbDefinition orb)
    {
        var hosts = Settings.RemoteHosts;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool Tracked(SourceKind s) => orb.TrackedSources?.Contains(s) ?? true;
        if (hosts.Hosts.IsEmpty || _quota.Hosts.IsEmpty)
        {
            return OrbQuotaCard.Of(_quota.CardItems(Tracked, orb.Hosts.Tracks, [], EmptyHosts), now, _loc);
        }

        var names = hosts.DisplayNames();
        var order = hosts.Hosts.Select(h => (h.Id, names.GetValueOrDefault(h.Id) ?? h.AutomaticName)).ToList();
        var connected = new HashSet<string>(_speed.Snapshot.Hosts.Where(h => !h.IsLocal && h.IsConnected).Select(h => h.Id), StringComparer.Ordinal);
        if (_captureConnected is { } captured)
        {
            connected.UnionWith(captured);
        }

        return OrbQuotaCard.Of(_quota.CardItems(Tracked, orb.Hosts.Tracks, order, connected), now, _loc);
    }

    private static readonly IReadOnlySet<string> EmptyHosts = new HashSet<string>();

    /// <summary>The vendors of an orb's live agents, fastest first (the automatic download's order).</summary>
    public IReadOnlyList<string> LiveVendors(string orbId) =>
        _entries.TryGetValue(orbId, out var entry)
            ? CharacterAutoDownload.Vendors(VendorReading.Of(entry.Feed.Reading.Agents, Prices?.Invoke()))
            : [];

    /// <summary>After <see cref="Apply"/>: orbs that switched to the style start the automatic download; launch starts it too.</summary>
    private void NoteCharacterStyles(OrbsDocument? previous, IReadOnlyList<OrbDefinition> visible)
    {
        if (Characters is not { } characters)
        {
            return;
        }

        var any = Settings.Orbs.Orbs.Any(o => o.Style == OrbStyle.Character);
        foreach (var orb in visible)
        {
            if (orb.Style == OrbStyle.Character && previous?.Find(orb.Id) is { Style: not OrbStyle.Character })
            {
                characters.StyleChosen(LiveVendors(orb.Id));
            }
        }

        characters.SetCharacterOrbs(any);
        foreach (var id in _selections.Keys.Where(id => !_entries.ContainsKey(id)).ToArray())
        {
            _selections.Remove(id);
        }
    }

    /// <summary>Pushes the character and the cards' snapshot values of a <c>character</c> orb (with each data push).</summary>
    private void PushCharacter(Entry entry)
    {
        var orb = entry.Definition;
        var surface = entry.Window.Surface;
        entry.Feed.Character = orb.Style == OrbStyle.Character;
        if (orb.Style != OrbStyle.Character)
        {
            _selections.Remove(orb.Id);
            surface.SetCharacter(null);
            return;
        }

        var reading = entry.Feed.Reading;
        var vendors = VendorReading.Of(reading.Agents, Prices?.Invoke());
        if (!_selections.TryGetValue(orb.Id, out var selection))
        {
            selection = new CharacterSelection();
            _selections[orb.Id] = selection;
        }

        var library = Characters?.Library;
        var packs = library?.Packs ?? [];
        var id = selection.Update(orb.Character, vendors.Rates, packs, _speed.Now);
        var installed = id is null ? null : library?.InstalledPack(id);
        var pack = installed is null ? null : Characters?.Pack(installed);
        var language = _loc.LanguageCode;
        var today = Settings.Metric == SpeedMetric.Output ? reading.Today.Output : reading.Today.Total;
        surface.SetCharacter(new OrbCharacterContent
        {
            Pack = pack,
            NoneInstalled = packs.Count == 0,
            EmptyLabel = _loc["character.empty"],
            Readouts = orb.CharacterReadouts,
            ModelName = vendors.FastestModel,
            CharacterName = installed?.Manifest.Name.For(language),
            Accent = installed is not null && CharacterManifest.Rgb(installed.Manifest.Accent) is { } rgb ? new Rgba(rgb.R, rgb.G, rgb.B) : Rgba.FromHex("#D97757"),
            CostGlyph = _loc["cost.glyph"],
            TodayLine = today > 0 ? _loc.Format("boards.today", SpeedFormat.Tokens(today)) : null,
            AgentsLine = reading.Agents.Count > 0 ? _loc.Format("agents.count", reading.Agents.Count) : null,
            Bits = Settings.Traffic.Bits,
            IntelligenceGlyph = _loc["intel.glyph"],
            ThinkingGlyph = _loc["intel.thinking.glyph"],
            ModelsMore = _loc["character.models.more"],
            TokensUnit = _loc["unit.tokS"],
            Quota = orb.CharacterReadouts.Quota ? QuotaCard(orb) : OrbQuotaCard.Empty,
        });
        var generating = reading.GeneratingSources.Count > 0;
        surface.SetCharacterReadings(IntelligenceSample(reading), entry.Feed.CardMoney, generating,
            OrbLatency.Of(PerfSetReadouts.FirstLatency(reading.Agents).Ms, _loc), ModelRows(reading.Agents));
    }

    /// <summary>
    /// The models card's rows (METRICS 6.9 <i>Readouts of a set</i>): the orb's running models, fastest first, each with the
    /// accent of the character that stands for its vendor.
    /// </summary>
    private IReadOnlyList<OrbModelRow> ModelRows(IReadOnlyList<AgentStatus> agents)
    {
        var running = PerfSetReadouts.RunningModels(agents);
        if (running.Count == 0)
        {
            return [];
        }

        var prices = Prices?.Invoke();
        var installed = Characters?.Library.Installed ?? [];
        var catalog = Characters?.Library.Catalog;
        return OrbModelsText.Rows(running, m => VendorAccents.Of(CharacterVendors.Resolve(m.Model, m.Source, prices).Vendor, installed, catalog));
    }

    /// <summary>
    /// The network card of a <c>character</c> orb: the whole computer when its scope is <c>machine</c>, else its agents'
    /// traffic; nothing while measuring is off or per-process traffic is unavailable (DESIGN 2a.6, Readouts).
    /// </summary>
    private void FeedCharacterNetwork(Entry entry)
    {
        var settings = Settings.Traffic;
        var machine = entry.Definition.Traffic == OrbTraffic.Machine;
        var enabled = settings.Enabled && _traffic.Enabled && (machine || _traffic.ProcessState.UnavailableReason is null);
        OrbLinesSample? sample = !enabled
            ? null
            : machine
                ? OrbLinesText.Traffic(_traffic.MachineLive ?? TrafficRate.Zero, settings.Bits, _loc)
                : OrbLinesText.Traffic(_traffic.Live(entry.Definition.TrackedSources, settings.IncludeTools) ?? TrafficRate.Zero, settings.Bits, _loc);
        entry.Window.Surface.SetCharacterNetwork(sample);
    }

    /// <summary>The installed packs or the catalog changed, or a pack finished loading: every character orb picks again.</summary>
    public void RefreshCharacters()
    {
        var single = Settings.Orbs.Orbs.Length == 1;
        foreach (var entry in _entries.Values)
        {
            if (entry.Definition.Style == OrbStyle.Character)
            {
                Push(entry, single);
            }
        }
    }
}
