using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Settings;

/// <summary>
/// Tolerant JSON (de)serialization: unknown properties are ignored and an invalid property falls back to its
/// default instead of discarding the whole file.
/// </summary>
public static class SettingsSerializer
{
    public const int CurrentVersion = 1;
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
            w.WriteBoolean("antigravity", s.AntigravityEnabled);
            w.WriteBoolean("antigravityCli", s.AntigravityCliEnabled);
            WriteNullableString(w, "codexRoot", s.CodexRoot);
            WriteNullableString(w, "claudeRoot", s.ClaudeRoot);
            WriteNullableString(w, "dshRoot", s.DshRoot);
            WriteNullableString(w, "opencodeRoot", s.OpenCodeRoot);
            WriteNullableString(w, "geminiRoot", s.GeminiRoot);
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

            w.WriteEndObject();

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
            ShowOrb = ReadBool(orb, "show", d.ShowOrb),
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
            AntigravityEnabled = ReadBool(sources, "antigravity", d.AntigravityEnabled),
            AntigravityCliEnabled = ReadBool(sources, "antigravityCli", d.AntigravityCliEnabled),
            CodexRoot = ReadString(sources, "codexRoot"),
            ClaudeRoot = ReadString(sources, "claudeRoot"),
            DshRoot = ReadString(sources, "dshRoot"),
            OpenCodeRoot = ReadString(sources, "opencodeRoot"),
            GeminiRoot = ReadString(sources, "geminiRoot"),
            HistoryWindow = ReadWindowFrame(history is { } h ? Child(h, "window") : null),
            ChartSeconds = (int)Math.Round(ReadDouble(dashboard, "chartSeconds", d.ChartSeconds)),
            AgentsExpanded = ReadBool(dashboard, "agentsExpanded", d.AgentsExpanded),
            HeroLastTitle = ReadString(dashboard, "heroLastTitle"),
            AutoCheckUpdates = ReadBool(updates, "autoCheck", d.AutoCheckUpdates),
            LastUpdateCheck = ReadTimestamp(updates, "lastCheck"),
            SkippedVersion = ReadString(updates, "skippedVersion"),
            NotifiedVersion = ReadString(updates, "notifiedVersion"),
        };

        return settings.Validated();
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
