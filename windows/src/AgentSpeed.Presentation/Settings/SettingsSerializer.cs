using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings.Boards;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Settings;

/// <summary>
/// Tolerant JSON (de)serialization: unknown properties are ignored and an invalid property falls back to its
/// default instead of discarding the whole file. A file without a usable <c>orbs</c> value migrates the single-orb
/// keys (<c>orb.*</c>), which are always written back as loaded so an older version still finds them.
/// </summary>
public static class SettingsSerializer
{
    /// <summary>Version 2 moved the redline defaults to the slider maximum (DESIGN 1).</summary>
    public const int CurrentVersion = 2;
    public const int MaxFileBytes = 1024 * 1024;

    public static string Serialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var s = settings.Validated();
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", CurrentVersion);
            w.WriteString("appearance", s.Appearance.ToString());
            w.WriteString("language", s.Language.ToString());
            w.WriteString("metric", s.Metric.ToString());
            w.WriteNumber("redlineOutput", s.RedlineOutput);
            w.WriteNumber("redlineTotal", s.RedlineTotal);

            w.WriteStartObject("taskbar");
            w.WriteBoolean("show", s.ShowOnTaskbar);
            w.WriteString("mode", s.BarMode.ToString());
            w.WriteBoolean("coloredGlyph", s.BarColoredGlyph);
            w.WriteBoolean("showUnit", s.BarShowUnit);
            w.WriteBoolean("secondary", s.BarSecondary);
            w.WriteBoolean("intelligence", s.BarIntelligence);
            w.WriteNumber("offset", s.TaskbarOffset);
            w.WriteEndObject();

            w.WriteStartObject("orb");
            w.WriteBoolean("show", s.ShowOrb);
            w.WriteNumber("size", s.OrbSize);
            w.WriteNumber("opacity", Math.Round(s.OrbOpacity, 3));
            w.WriteBoolean("alwaysOnTop", s.OrbAlwaysOnTop);
            w.WriteBoolean("clickThrough", s.OrbClickThrough);
            w.WriteBoolean("fadeWhenIdle", s.OrbFadeWhenIdle);
            if (s.OrbMonitor is not null)
            {
                w.WriteString("monitor", s.OrbMonitor);
            }

            w.WriteStartObject("anchors");
            foreach (var (key, anchor) in s.OrbAnchors.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                w.WriteStartObject(key);
                w.WriteNumber("x", Math.Round(anchor.X, 5));
                w.WriteNumber("y", Math.Round(anchor.Y, 5));
                w.WriteEndObject();
            }

            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteBoolean("showOrbs", s.ShowOrbs);
            w.WriteString("orbNames", OrbNamesModes.Key(s.OrbNames));
            w.WritePropertyName("orbs");
            OrbsJson.Write(w, s.Orbs);
            w.WritePropertyName("boards");
            BoardsJson.Write(w, s.Boards);
            w.WritePropertyName("titleThemes");
            TitleThemesJson.Write(w, s.TitleThemes);

            w.WriteStartObject("effects");
            w.WriteString("intensity", s.Effects.ToString());
            w.WriteBoolean("celebratePeaks", s.CelebratePeaks);
            w.WriteBoolean("responsePulses", s.ResponsePulses);
            w.WriteEndObject();

            w.WriteStartObject("sources");
            w.WriteBoolean("codex", s.CodexEnabled);
            w.WriteBoolean("claudeCode", s.ClaudeCodeEnabled);
            w.WriteBoolean("claudeDesktop", s.ClaudeDesktopEnabled);
            w.WriteBoolean("deepseek", s.DeepSeekEnabled);
            w.WriteBoolean("opencode", s.OpenCodeEnabled);
            w.WriteBoolean("gemini", s.GeminiEnabled);
            w.WriteBoolean("omp", s.OmpEnabled);
            w.WriteBoolean("antigravity", s.AntigravityEnabled);
            w.WriteBoolean("workbuddy", s.WorkBuddyEnabled);
            w.WriteBoolean("workbuddyAi", s.WorkBuddyAiEnabled);
            w.WriteBoolean("zcode", s.ZCodeEnabled);
            w.WriteBoolean("kimi", s.KimiEnabled);
            w.WriteBoolean("grokBuild", s.GrokBuildEnabled);
            w.WriteBoolean("pi", s.PiEnabled);
            w.WriteBoolean("openclaw", s.OpenClawEnabled);
            w.WriteBoolean("hermes", s.HermesEnabled);
            w.WriteBoolean("droid", s.DroidEnabled);
            w.WriteBoolean("trae", s.TraeEnabled);
            WriteNullableString(w, "codexRoot", s.CodexRoot);
            WriteNullableString(w, "claudeRoot", s.ClaudeRoot);
            WriteNullableString(w, "dshRoot", s.DshRoot);
            WriteNullableString(w, "opencodeRoot", s.OpenCodeRoot);
            WriteNullableString(w, "geminiRoot", s.GeminiRoot);
            WriteNullableString(w, "ompRoot", s.OmpRoot);
            WriteNullableString(w, "antigravityRoot", s.AntigravityRoot);
            WriteNullableString(w, "workbuddyRoot", s.WorkBuddyRoot);
            WriteNullableString(w, "workbuddyAiRoot", s.WorkBuddyAiRoot);
            WriteNullableString(w, "zcodeRoot", s.ZCodeRoot);
            WriteNullableString(w, "kimiRoot", s.KimiRoot);
            WriteNullableString(w, "grokBuildRoot", s.GrokBuildRoot);
            WriteNullableString(w, "piRoot", s.PiRoot);
            WriteNullableString(w, "openclawRoot", s.OpenClawRoot);
            WriteNullableString(w, "hermesRoot", s.HermesRoot);
            WriteNullableString(w, "droidRoot", s.DroidRoot);
            WriteNullableString(w, "traeRoot", s.TraeRoot);
            w.WriteEndObject();

            w.WriteStartObject("dashboard");
            w.WriteNumber("chartSeconds", s.ChartSeconds);
            w.WriteBoolean("agentsExpanded", s.AgentsExpanded);
            WriteNullableString(w, "heroLastTitle", s.HeroLastTitle);
            w.WriteEndObject();

            w.WriteStartObject("history");
            if (s.HistoryWindow is { } frame)
            {
                w.WriteStartObject("window");
                w.WriteNumber("left", Math.Round(frame.Left, 2));
                w.WriteNumber("top", Math.Round(frame.Top, 2));
                w.WriteNumber("width", Math.Round(frame.Width, 2));
                w.WriteNumber("height", Math.Round(frame.Height, 2));
                w.WriteBoolean("maximized", frame.Maximized);
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("window");
            }

            w.WriteString("modeKind", s.HistoryModeKind == ExecutionModeKind.Speed ? "speed" : "effort");
            if (s.HistoryTaskPair is { IsValid: true } pair)
            {
                w.WriteStartArray("taskPair");
                w.WriteStringValue(Orbs.OrbSources.KeyOf(pair.A));
                w.WriteStringValue(Orbs.OrbSources.KeyOf(pair.B));
                w.WriteEndArray();
            }

            w.WriteEndObject();

            w.WriteStartObject("cost");
            w.WriteString("currency", s.Cost.Currency);
            w.WriteString("liveUnit", s.Cost.LiveUnit == LiveUnit.Cost ? "cost" : "tokens");
            w.WriteStartArray("subscriptions");
            foreach (var key in s.Cost.SubscriptionKeys)
            {
                w.WriteStringValue(key);
            }

            w.WriteEndArray();
            w.WriteBoolean("includeSubscriptions", s.Cost.IncludeSubscriptions);
            w.WriteBoolean("syncPrices", s.Cost.SyncPrices);
            if (s.Cost.HistoryIncludeSubscriptions is { } historyInclude)
            {
                w.WriteBoolean("historyIncludeSubscriptions", historyInclude);
            }
            else
            {
                w.WriteNull("historyIncludeSubscriptions");
            }

            w.WriteEndObject();

            w.WriteStartObject("traffic");
            w.WriteBoolean("enabled", s.Traffic.Enabled);
            w.WriteBoolean("includeTools", s.Traffic.IncludeTools);
            w.WriteString("unit", s.Traffic.Bits ? "bits" : "bytes");
            w.WriteString("bar", TrafficSettings.Key(s.Traffic.Bar));
            w.WriteEndObject();

            w.WriteStartObject("network");
            w.WriteBoolean("enabled", s.NetworkEnabled);
            w.WriteEndObject();

            ReportSettingsJson.Write(w, s.Report);

            w.WriteStartObject("updates");
            w.WriteBoolean("autoCheck", s.AutoCheckUpdates);
            WriteNullableString(w, "lastCheck", s.LastUpdateCheck?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            WriteNullableString(w, "skippedVersion", s.SkippedVersion);
            WriteNullableString(w, "notifiedVersion", s.NotifiedVersion);
            w.WriteEndObject();

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <exception cref="JsonException">The text is not a JSON object.</exception>
    public static AppSettings Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 16,
        });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Settings root must be an object.");
        }

        var d = AppSettings.Default;
        var taskbar = Child(root, "taskbar");
        var orb = Child(root, "orb");
        var effects = Child(root, "effects");
        var sources = Child(root, "sources");
        var dashboard = Child(root, "dashboard");
        var updates = Child(root, "updates");
        var history = Child(root, "history");
        var cost = Child(root, "cost");
        var network = Child(root, "network");
        var traffic = Child(root, "traffic");
        var report = Child(root, "report");

        // DESIGN 10: an earlier build's `traffic.bar = intelligence` becomes the network block off and the 智力 switch on.
        var storedBar = ReadString(traffic, "bar");
        var legacyIntelligence = storedBar == TrafficSettings.LegacyIntelligenceKey;

        var settings = new AppSettings
        {
            Appearance = ReadEnum(root, "appearance", d.Appearance),
            Language = ReadEnum(root, "language", d.Language),
            Metric = ReadEnum<SpeedMetric>(root, "metric", d.Metric),
            RedlineOutput = ReadDouble(root, "redlineOutput", ReadDouble(root, "redline", d.RedlineOutput)),
            RedlineTotal = ReadDouble(root, "redlineTotal", d.RedlineTotal),
            ShowOnTaskbar = ReadBool(taskbar, "show", d.ShowOnTaskbar),
            BarMode = ReadEnum(taskbar, "mode", d.BarMode),
            BarColoredGlyph = ReadBool(taskbar, "coloredGlyph", d.BarColoredGlyph),
            BarShowUnit = ReadBool(taskbar, "showUnit", d.BarShowUnit),
            BarSecondary = ReadBool(taskbar, "secondary", d.BarSecondary),
            BarIntelligence = legacyIntelligence || ReadBool(taskbar, "intelligence", d.BarIntelligence),
            TaskbarOffset = ReadDouble(taskbar, "offset", d.TaskbarOffset),
            ShowOrb = ReadBool(orb, "show", d.ShowOrb),
            ShowOrbs = ReadBool(root, "showOrbs", ReadBool(orb, "show", d.ShowOrb)),
            OrbNames = OrbNamesModes.Parse(ReadString(root, "orbNames")),
            OrbSize = ReadDouble(orb, "size", d.OrbSize),
            OrbOpacity = ReadDouble(orb, "opacity", d.OrbOpacity),
            OrbAlwaysOnTop = ReadBool(orb, "alwaysOnTop", d.OrbAlwaysOnTop),
            OrbClickThrough = ReadBool(orb, "clickThrough", d.OrbClickThrough),
            OrbFadeWhenIdle = ReadBool(orb, "fadeWhenIdle", d.OrbFadeWhenIdle),
            OrbMonitor = ReadString(orb, "monitor"),
            OrbAnchors = ReadAnchors(orb),
            Effects = ReadEnum(effects, "intensity", d.Effects),
            CelebratePeaks = ReadBool(effects, "celebratePeaks", d.CelebratePeaks),
            ResponsePulses = ReadBool(effects, "responsePulses", d.ResponsePulses),
            CodexEnabled = ReadBool(sources, "codex", d.CodexEnabled),
            ClaudeCodeEnabled = ReadBool(sources, "claudeCode", d.ClaudeCodeEnabled),
            ClaudeDesktopEnabled = ReadBool(sources, "claudeDesktop", d.ClaudeDesktopEnabled),
            DeepSeekEnabled = ReadBool(sources, "deepseek", d.DeepSeekEnabled),
            OpenCodeEnabled = ReadBool(sources, "opencode", d.OpenCodeEnabled),
            GeminiEnabled = ReadBool(sources, "gemini", d.GeminiEnabled),
            OmpEnabled = ReadBool(sources, "omp", d.OmpEnabled),
            AntigravityEnabled = ReadBool(sources, "antigravity", d.AntigravityEnabled),
            WorkBuddyEnabled = ReadBool(sources, "workbuddy", d.WorkBuddyEnabled),
            WorkBuddyAiEnabled = ReadBool(sources, "workbuddyAi", d.WorkBuddyAiEnabled),
            ZCodeEnabled = ReadBool(sources, "zcode", d.ZCodeEnabled),
            KimiEnabled = ReadBool(sources, "kimi", d.KimiEnabled),
            GrokBuildEnabled = ReadBool(sources, "grokBuild", d.GrokBuildEnabled),
            PiEnabled = ReadBool(sources, "pi", d.PiEnabled),
            OpenClawEnabled = ReadBool(sources, "openclaw", d.OpenClawEnabled),
            HermesEnabled = ReadBool(sources, "hermes", d.HermesEnabled),
            DroidEnabled = ReadBool(sources, "droid", d.DroidEnabled),
            TraeEnabled = ReadBool(sources, "trae", d.TraeEnabled),
            CodexRoot = ReadString(sources, "codexRoot"),
            ClaudeRoot = ReadString(sources, "claudeRoot"),
            DshRoot = ReadString(sources, "dshRoot"),
            OpenCodeRoot = ReadString(sources, "opencodeRoot"),
            GeminiRoot = ReadString(sources, "geminiRoot"),
            OmpRoot = ReadString(sources, "ompRoot"),
            AntigravityRoot = ReadString(sources, "antigravityRoot"),
            WorkBuddyRoot = ReadString(sources, "workbuddyRoot"),
            WorkBuddyAiRoot = ReadString(sources, "workbuddyAiRoot"),
            ZCodeRoot = ReadString(sources, "zcodeRoot"),
            KimiRoot = ReadString(sources, "kimiRoot"),
            GrokBuildRoot = ReadString(sources, "grokBuildRoot"),
            PiRoot = ReadString(sources, "piRoot"),
            OpenClawRoot = ReadString(sources, "openclawRoot"),
            HermesRoot = ReadString(sources, "hermesRoot"),
            DroidRoot = ReadString(sources, "droidRoot"),
            TraeRoot = ReadString(sources, "traeRoot"),
            HistoryWindow = ReadWindowFrame(history is { } h ? Child(h, "window") : null),
            HistoryModeKind = ReadString(history, "modeKind") == "speed" ? ExecutionModeKind.Speed : ExecutionModeKind.Effort,
            HistoryTaskPair = ReadTaskPair(history is { } th && th.TryGetProperty("taskPair", out var taskPair) ? taskPair : null),
            ChartSeconds = (int)Math.Round(ReadDouble(dashboard, "chartSeconds", d.ChartSeconds)),
            AgentsExpanded = ReadBool(dashboard, "agentsExpanded", d.AgentsExpanded),
            HeroLastTitle = ReadString(dashboard, "heroLastTitle"),
            AutoCheckUpdates = ReadBool(updates, "autoCheck", d.AutoCheckUpdates),
            LastUpdateCheck = ReadTimestamp(updates, "lastCheck"),
            SkippedVersion = ReadString(updates, "skippedVersion"),
            NotifiedVersion = ReadString(updates, "notifiedVersion"),
            NetworkEnabled = ReadBool(network, "enabled", d.NetworkEnabled),
            Cost = ReadCost(cost),
            Traffic = new TrafficSettings
            {
                Enabled = ReadBool(traffic, "enabled", TrafficSettings.Default.Enabled),
                IncludeTools = ReadBool(traffic, "includeTools", TrafficSettings.Default.IncludeTools),
                Unit = ReadString(traffic, "unit") == "bits" ? TrafficUnit.Bits : TrafficUnit.Bytes,
                Bar = TrafficSettings.ParseBar(storedBar),
            },
            Report = ReportSettingsJson.Read(report),
        };

        var fileVersion = ReadDouble(root, "version", 1);
        if (fileVersion < 2)
        {
            settings = MigrateRedlineDefaults(settings);
        }

        var validated = settings.Validated();
        var orbs = root.TryGetProperty("orbs", out var orbsValue) ? OrbsJson.Read(orbsValue) : null;
        // An absent value is a fresh install or an older version: Vehicles, no custom sets (DESIGN 1, Persistence).
        var titles = root.TryGetProperty("titleThemes", out var titlesValue) ? TitleThemesJson.Read(titlesValue) : null;
        var boards = root.TryGetProperty("boards", out var boardsValue) ? BoardsJson.Read(boardsValue) : BoardsDocument.Empty;
        return validated with
        {
            Orbs = orbs ?? OrbsMigration.FromLegacy(validated), Boards = boards, TitleThemes = titles ?? TitleThemesDocument.Default,
        };
    }

    /// <summary>
    /// A redline stored as the old default (400 / 200 000 tok/s) counts as never set and moves to the new default; any
    /// other value is kept. Runs once: files are written as version 2 afterwards.
    /// </summary>
    internal static AppSettings MigrateRedlineDefaults(AppSettings settings) => settings with
    {
        RedlineOutput = settings.RedlineOutput == HeatMath.ReferenceRedline ? HeatMath.DefaultRedline : settings.RedlineOutput,
        RedlineTotal = settings.RedlineTotal == HeatMath.ReferenceRedlineTotal ? HeatMath.DefaultRedlineTotal : settings.RedlineTotal,
    };

    private static CostSettings ReadCost(JsonElement? cost)
    {
        var d = CostSettings.Default;
        if (cost is not { } c)
        {
            return d;
        }

        var subscriptions = c.TryGetProperty("subscriptions", out var list) && list.ValueKind == JsonValueKind.Array
            ? CostSettings.ParseSubscriptions(list.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Take(64)
                .Select(e => e.GetString()))
            : SourceSet.Empty;
        bool? historyInclude = c.TryGetProperty("historyIncludeSubscriptions", out var h) && h.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? h.ValueKind == JsonValueKind.True
            : null;
        return new CostSettings
        {
            Currency = ReadString(c, "currency") is { Length: > 0 and <= 8 } code ? code : d.Currency,
            LiveUnit = ReadString(c, "liveUnit") == "cost" ? LiveUnit.Cost : LiveUnit.Tokens,
            Subscriptions = subscriptions,
            IncludeSubscriptions = ReadBool(c, "includeSubscriptions", d.IncludeSubscriptions),
            SyncPrices = ReadBool(c, "syncPrices", d.SyncPrices),
            HistoryIncludeSubscriptions = historyInclude,
        }.Validated();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static JsonElement? Child(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static bool ReadBool(JsonElement? parent, string name, bool fallback)
    {
        if (parent is not { } p || !p.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static double ReadDouble(JsonElement? parent, string name, double fallback)
    {
        if (parent is not { } p || !p.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }

        return value.TryGetDouble(out var number) && double.IsFinite(number) ? number : fallback;
    }

    private static string? ReadString(JsonElement? parent, string name)
    {
        if (parent is not { } p || !p.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement? parent, string name)
    {
        var text = ReadString(parent, name);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToUniversalTime()
            : null;
    }

    /// <summary>The history's remembered task pair: two different scored source keys, else null.</summary>
    private static History.Tasks.TaskPair? ReadTaskPair(JsonElement? pair)
    {
        if (pair is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() != 2)
        {
            return null;
        }

        var a = array[0];
        var b = array[1];
        if (a.ValueKind != JsonValueKind.String || b.ValueKind != JsonValueKind.String
            || !Orbs.OrbSources.TryParseKey(a.GetString(), out var first) || !Orbs.OrbSources.TryParseKey(b.GetString(), out var second))
        {
            return null;
        }

        var parsed = new History.Tasks.TaskPair(first, second);
        return parsed.IsValid ? parsed : null;
    }

    private static WindowFrame? ReadWindowFrame(JsonElement? window)
    {
        if (window is null)
        {
            return null;
        }

        var frame = new WindowFrame(
            ReadDouble(window, "left", double.NaN),
            ReadDouble(window, "top", double.NaN),
            ReadDouble(window, "width", double.NaN),
            ReadDouble(window, "height", double.NaN),
            ReadBool(window, "maximized", false));
        return WindowFrame.ValidHistory(frame);
    }

    private static T ReadEnum<T>(JsonElement? parent, string name, T fallback)
        where T : struct, Enum
    {
        var text = ReadString(parent, name);
        if (text is null || text.Length is 0 or > 64 || char.IsDigit(text[0]) || text[0] == '-')
        {
            return fallback;
        }

        return Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;
    }

    private static ImmutableDictionary<string, OrbAnchor> ReadAnchors(JsonElement? orb)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, OrbAnchor>(StringComparer.OrdinalIgnoreCase);
        if (orb is not { } o || !o.TryGetProperty("anchors", out var anchors) || anchors.ValueKind != JsonValueKind.Object)
        {
            return builder.ToImmutable();
        }

        foreach (var property in anchors.EnumerateObject())
        {
            if (builder.Count >= AppSettings.MaxStoredMonitors || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var x = ReadDouble(property.Value, "x", double.NaN);
            var y = ReadDouble(property.Value, "y", double.NaN);
            if (double.IsFinite(x) && double.IsFinite(y))
            {
                builder[property.Name] = new OrbAnchor(x, y);
            }
        }

        return builder.ToImmutable();
    }
}
