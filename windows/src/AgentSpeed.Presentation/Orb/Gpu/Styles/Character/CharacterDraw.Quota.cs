using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Quota;

namespace AgentSpeed.Presentation.Orb.Gpu.Styles;

public sealed partial class CharacterDraw
{
    /// <summary>The 額度 card's forms, widest first (DESIGN 2a.6, 額度 Room): resets go first, then bars shorten, then headers.</summary>
    public enum QuotaForm
    {
        Full,
        NoResets,
        ShortBars,
        Compact,
    }

    private const double QuotaBarLong = 5;
    private const double QuotaBarShort = 3;

    private static readonly Rgba AmberDark = Rgba.FromHex("#F5A524");
    private static readonly Rgba AmberLight = Rgba.FromHex("#B86E00");
    private static readonly Rgba RedDark = Rgba.FromHex("#FF99A4");
    private static readonly Rgba RedLight = Rgba.FromHex("#C42B1C");

    /// <summary>
    /// The host's dimmer ink after the name (DESIGN 13 <i>Host quotas</i>), as a share of the secondary token's; high contrast
    /// keeps the secondary token whole.
    /// </summary>
    private const double QuotaHostInk = 0.72;

    private readonly OrbQuotaMotion _quota = new();
    private readonly Dictionary<(string Host, double Room), string> _quotaHosts = [];
    private double _quotaHostsSize;
    private QuotaForm _quotaForm = QuotaForm.Full;
    private IReadOnlyList<OrbQuotaSource>? _quotaSynced;
    private IReadOnlyList<OrbQuotaSource> _quotaFirst = [];
    private bool _quotaSmall;

    /// <summary>The 額度 card's lines this frame (tests).</summary>
    public OrbQuotaMotion QuotaLines => _quota;

    /// <summary>The form the 額度 card takes this frame (tests).</summary>
    public QuotaForm QuotaShape => _quotaForm;

    /// <summary>The card is switched on, or still fading out after it was switched off.</summary>
    private bool QuotaLive => (Content?.Readouts ?? Settings.Orbs.CharacterReadouts.Default).Quota || _wings.Presence[(int)HudCard.Quota] > 0;

    /// <summary>Below S = 96 the card lists its first source without reset cells (DESIGN 2a.6, LOD).</summary>
    private void SyncQuota(OrbGpuContext ctx, in OrbFrame frame)
    {
        // The content keeps its list while it does not change, so a warm frame syncs (and allocates) nothing.
        IReadOnlyList<OrbQuotaSource> sources = QuotaLive ? Content?.Quota.Sources ?? [] : [];
        var small = IsSmall(ctx);
        if (ReferenceEquals(sources, _quotaSynced) && small == _quotaSmall && !_quota.HasLeaving)
        {
            return;
        }

        _quotaSynced = sources;
        _quotaSmall = small;
        if (small && sources.Count > 1)
        {
            if (_quotaFirst.Count != 1 || !ReferenceEquals(_quotaFirst[0], sources[0]))
            {
                _quotaFirst = [sources[0]];
            }

            sources = _quotaFirst;
        }

        _quota.Sync(sources, frame.Time, frame.Effects.ReduceMotion, atOnce: _wings.Presence[(int)HudCard.Quota] == 0);
    }

    private readonly record struct QuotaColumns(double Label, double Percent, double Reset, double Header);

    private static double QuotaSmall(in CardMetrics m) => 0.9 * m.T;

    private QuotaColumns QuotaColumnsOf(in CardMetrics m, double time)
    {
        double label = 0, reset = 0, header = 0;
        var small = QuotaSmall(m);
        var all = _quota.Sources;
        for (var i = 0; i < all.Count; i++)
        {
            var source = all[i];
            if (source.Alpha(time) <= 0.01 && source.Leaving)
            {
                continue;
            }

            header = Math.Max(header, 0.55 * m.T + 0.4 * m.T + Advance(source.Data.Name, OrbFont.Info, small));
            foreach (var window in source.Windows)
            {
                label = Math.Max(label, Advance(window.Data.Label, OrbFont.Info, small));
                if (window.Data.Reset.Length > 0)
                {
                    reset = Math.Max(reset, Advance(window.Data.Reset, OrbFont.Info, small));
                }
            }
        }

        return new QuotaColumns(label, DigitsWidth("100%", OrbFont.TrafficNumber, m.T), reset, header);
    }

    private static double QuotaLineWidth(in QuotaColumns c, in CardMetrics m, QuotaForm form)
    {
        var bar = form >= QuotaForm.ShortBars ? QuotaBarShort : QuotaBarLong;
        var width = c.Label + 0.5 * m.T + bar * m.T + 0.5 * m.T + c.Percent;
        if (form == QuotaForm.Full && c.Reset > 0)
        {
            width += 0.4 * m.T + c.Reset;
        }

        return form == QuotaForm.Compact ? width + 0.55 * m.T + 0.35 * m.T : width;
    }

    private static double QuotaWidth(in QuotaColumns c, in CardMetrics m, QuotaForm form) =>
        Math.Max(form == QuotaForm.Compact ? 0 : c.Header, QuotaLineWidth(c, m, form)) + 2 * m.PadX;

    /// <summary>
    /// The widest form that fits the room the column gives the card (with 0.04 D to spare before it widens again); the
    /// layout learns the narrowest one, so only when even that does not fit does the card spill over.
    /// </summary>
    private void FitQuota(in CardMetrics m, double time, bool small)
    {
        var columns = QuotaColumnsOf(m, time);
        _wings.QuotaNarrowest = _quota.Count == 0 ? 0 : QuotaWidth(columns, m, QuotaForm.Compact);
        var room = _wings.QuotaRoom;
        var spare = HudWings.ModelsSpare * (_ctx?.Size ?? 0);
        var form = QuotaForm.Compact;
        for (var f = small ? QuotaForm.NoResets : QuotaForm.Full; f < QuotaForm.Compact; f++)
        {
            var need = QuotaWidth(columns, m, f) + (f < _quotaForm ? spare : 0);
            if (!double.IsFinite(room) || need <= room + 0.5)
            {
                form = f;
                break;
            }
        }

        _quotaForm = form;
    }

    private static double QuotaHeader(in CardMetrics m) => CardMetrics.RowHeight(0.9 * m.T);

    private static double QuotaRow(in CardMetrics m) => CardMetrics.RowHeight(m.T);

    /// <summary>The card's content width in the current form (the plate less its padding).</summary>
    private double QuotaContent(in QuotaColumns c, in CardMetrics m) => QuotaWidth(c, m, _quotaForm) - 2 * m.PadX;

    /// <summary>
    /// A host's source whose <c>· host</c> does not fit after its name in the card's width takes a line of its own under the
    /// name (in the compact form, above the source's lines): the host never widens the card.
    /// </summary>
    private bool QuotaHostOwnLine(OrbQuotaMotion.Source source, in QuotaColumns c, in CardMetrics m)
    {
        if (source.Data.HostTail is not { } tail)
        {
            return false;
        }

        if (_quotaForm == QuotaForm.Compact)
        {
            return true;
        }

        var small = QuotaSmall(m);
        var header = 0.55 * m.T + 0.4 * m.T + Advance(source.Data.Name, OrbFont.Info, small) + Advance(tail, OrbFont.Info, small);
        return header > QuotaContent(c, m) + 0.5;
    }

    /// <summary>A source's block height in the current form.</summary>
    private double QuotaBlock(OrbQuotaMotion.Source source, in QuotaColumns c, in CardMetrics m)
    {
        var n = source.Windows.Count;
        if (n == 0)
        {
            return 0;
        }

        var lines = n * QuotaRow(m) + (n - 1) * 0.25 * m.T;
        var headers = (_quotaForm == QuotaForm.Compact ? 0 : 1) + (QuotaHostOwnLine(source, c, m) ? 1 : 0);
        return headers * (QuotaHeader(m) + 0.25 * m.T) + lines;
    }

    /// <summary>The host's name cut with an ellipsis to <paramref name="room"/> (cached: its glyphs are measured one by one).</summary>
    private string QuotaHostFitted(string host, double room, double size)
    {
        if (Advance(host, OrbFont.Info, size) <= room + 0.5)
        {
            return host;
        }

        if (_quotaHostsSize != size || _quotaHosts.Count > 32)
        {
            _quotaHosts.Clear();
            _quotaHostsSize = size;
        }

        var key = (host, Math.Round(room * 2) / 2);
        if (!_quotaHosts.TryGetValue(key, out var text))
        {
            text = TruncatedHost(host, room, size);
            _quotaHosts[key] = text;
        }

        return text;
    }

    // Apart, so the measuring closure is only made on a cache miss.
    private string TruncatedHost(string host, double room, double size) => OrbModelName.Truncate(host, room, e => Advance(e, OrbFont.Info, size));

    private (double W, double H) QuotaSize(in CardMetrics m, double time)
    {
        if (_quota.Count == 0 || !QuotaLive)
        {
            return (0, 0);
        }

        var columns = QuotaColumnsOf(m, time);
        var height = 0.0;
        var any = false;
        var all = _quota.Sources;
        for (var i = 0; i < all.Count; i++)
        {
            var source = all[i];
            var share = Easing.SmoothStep(0, 1, source.Alpha(time));
            if (share <= 0)
            {
                continue;
            }

            height += share * ((any ? 0.45 * m.T : 0) + QuotaBlock(source, columns, m));
            any = true;
        }

        if (!any)
        {
            return (0, 0);
        }

        return (QuotaWidth(columns, m, _quotaForm), height + 2 * m.PadY);
    }

    private (Rgba Track, Rgba Warning, Rgba Critical) QuotaInk(OrbGpuContext ctx, in CardInk ink)
    {
        if (ctx.HighContrast)
        {
            return (ctx.Contrast.GrayText, ctx.Contrast.WindowText, ctx.Contrast.WindowText);
        }

        var dark = ctx.Dark;
        var label = (dark ? Rgba.White : Rgba.FromHex("#0B0D12")).WithAlpha(dark ? 0.16 : 0.12);
        return (label, dark ? AmberDark : AmberLight, dark ? RedDark : RedLight);
    }

    /// <summary>
    /// The 額度 card: per source a header (its color dot and name, a host's source then <c>· host</c> in a dimmer ink) then a
    /// line per window — the label in the card's label column, the bar, the percent right-aligned in a <c>100%</c> cell and
    /// the short reset — each eased by <see cref="OrbQuotaMotion"/>. The compact form drops the headers and puts the dot
    /// before the source's first label. A host that does not fit after the name takes its own line.
    /// </summary>
    private void DrawQuota(OrbDrawList list, OrbGpuContext ctx, in Pen pen, (double W, double H) box, in CardMetrics m, in CardInk ink, bool right,
        double time)
    {
        DrawPlate(list, pen, box, ink, m, null, right ? -1 : 1);
        var columns = QuotaColumnsOf(m, time);
        var (track, warning, critical) = QuotaInk(ctx, ink);
        var small = QuotaSmall(m);
        var left = -box.W / 2 + m.PadX;
        var y = -box.H / 2 + m.PadY;
        var compact = _quotaForm == QuotaForm.Compact;
        var labelLeft = left + (compact ? 0.55 * m.T + 0.35 * m.T : 0);
        var barLeft = labelLeft + columns.Label + 0.5 * m.T;
        var barLength = (_quotaForm >= QuotaForm.ShortBars ? QuotaBarShort : QuotaBarLong) * m.T;
        var percentRight = barLeft + barLength + 0.5 * m.T + columns.Percent;
        var resetLeft = percentRight + 0.4 * m.T;
        var content = QuotaContent(columns, m);
        var thickness = 0.26 * m.T;
        var any = false;
        var all = _quota.Sources;
        for (var i = 0; i < all.Count; i++)
        {
            var source = all[i];
            var alpha = source.Alpha(time);
            var share = Easing.SmoothStep(0, 1, alpha);
            if (share <= 0 || source.Windows.Count == 0)
            {
                continue;
            }

            if (any)
            {
                y += share * 0.45 * m.T;
            }

            any = true;
            var a = alpha;
            var dot = 0.55 * m.T;
            var color = ctx.HighContrast ? ink.Accent : source.Data.Color;
            var nameLeft = left + dot + 0.4 * m.T;
            var ownLine = QuotaHostOwnLine(source, columns, m);
            var hostInk = ink.Secondary.MultiplyAlpha(a * (ctx.HighContrast ? 1 : QuotaHostInk));
            if (!compact)
            {
                var baseline = y + CardMetrics.Baseline(small);
                var (dx, dy) = pen.Point(left + dot / 2, baseline - 0.33 * small);
                list.Add(OrbGpuInstance.Circle(dx, dy, dot / 2 * pen.Scale, color.MultiplyAlpha(a * pen.Alpha)));
                var name = Put(list, source.Data.Name, OrbFont.Info, small, pen, nameLeft, baseline, ink.Secondary.MultiplyAlpha(a));
                if (source.Data.HostTail is { } tail && !ownLine)
                {
                    Put(list, tail, OrbFont.Info, small, pen, nameLeft + name, baseline, hostInk);
                }

                y += share * (QuotaHeader(m) + 0.25 * m.T);
            }

            if (ownLine && source.Data.Host is { } host)
            {
                var x = compact ? left : nameLeft;
                var text = QuotaHostFitted(host, content - (x - left), small);
                Put(list, text, OrbFont.Info, small, pen, x, y + CardMetrics.Baseline(small), hostInk);
                y += share * (QuotaHeader(m) + 0.25 * m.T);
            }

            for (var k = 0; k < source.Windows.Count; k++)
            {
                var window = source.Windows[k];
                var data = window.Data;
                var rowTop = y;
                var baseline = rowTop + CardMetrics.Baseline(m.T);
                var labelBaseline = rowTop + CardMetrics.Baseline(m.T) - 0.02 * m.T;
                if (compact && k == 0)
                {
                    var (dx, dy) = pen.Point(left + dot / 2, labelBaseline - 0.33 * small);
                    list.Add(OrbGpuInstance.Circle(dx, dy, dot / 2 * pen.Scale, color.MultiplyAlpha(a * pen.Alpha)));
                }

                Put(list, data.Label, OrbFont.Info, small, pen, labelLeft, labelBaseline, ink.Secondary.MultiplyAlpha(a));

                // The bar sits on the text's x-height middle, so it reads level with the label and the digits.
                var barY = baseline - 0.36 * m.T;
                var fill = ctx.HighContrast ? ink.Number : data.Tone switch
                {
                    QuotaTone.Critical => critical,
                    QuotaTone.Warning => warning,
                    _ => source.Data.Color,
                };
                DrawQuotaBar(list, pen, barLeft, barY, barLength, thickness, track.MultiplyAlpha(a), fill.MultiplyAlpha(a),
                    Math.Clamp(window.Bar(time), 0, 1));

                var number = data.Tone == QuotaTone.Critical && !ctx.HighContrast ? critical : ink.Number;
                var fresh = window.PercentAlpha(time);
                if (fresh < 1 && window.OldPercent.Length > 0)
                {
                    var oldWidth = DigitsWidth(window.OldPercent, OrbFont.TrafficNumber, m.T);
                    PutDigits(list, window.OldPercent, OrbFont.TrafficNumber, m.T, pen, percentRight - oldWidth, baseline, number.MultiplyAlpha(a * (1 - fresh)));
                }

                var percentWidth = DigitsWidth(data.PercentText, OrbFont.TrafficNumber, m.T);
                PutDigits(list, data.PercentText, OrbFont.TrafficNumber, m.T, pen, percentRight - percentWidth, baseline, number.MultiplyAlpha(a * fresh));

                if (_quotaForm == QuotaForm.Full)
                {
                    var resetAlpha = window.ResetAlpha(time);
                    if (resetAlpha < 1 && window.OldReset.Length > 0)
                    {
                        Put(list, window.OldReset, OrbFont.Info, small, pen, resetLeft, labelBaseline, ink.Secondary.MultiplyAlpha(a * (1 - resetAlpha)));
                    }

                    if (data.Reset.Length > 0)
                    {
                        Put(list, data.Reset, OrbFont.Info, small, pen, resetLeft, labelBaseline, ink.Secondary.MultiplyAlpha(a * resetAlpha));
                    }
                }

                y += share * (QuotaRow(m) + (k < source.Windows.Count - 1 ? 0.25 * m.T : 0));
            }
        }
    }

    /// <summary>A fully rounded bar: the track, then the fill (never shorter than a dot once it is above 0).</summary>
    private static void DrawQuotaBar(OrbDrawList list, in Pen pen, double x, double y, double length, double thickness, Rgba track, Rgba fill, double share)
    {
        var r = thickness / 2;
        var (t0x, t0y) = pen.Point(x + r, y);
        var (t1x, t1y) = pen.Point(x + length - r, y);
        list.Add(OrbGpuInstance.Capsule(t0x, t0y, t1x, t1y, thickness * pen.Scale, track.MultiplyAlpha(pen.Alpha)));
        if (share <= 0.0005)
        {
            return;
        }

        var filled = Math.Max(thickness, share * length);
        var (f1x, f1y) = pen.Point(x + filled - r, y);
        list.Add(OrbGpuInstance.Capsule(t0x, t0y, f1x, f1y, thickness * pen.Scale, fill.MultiplyAlpha(pen.Alpha)));
    }
}
