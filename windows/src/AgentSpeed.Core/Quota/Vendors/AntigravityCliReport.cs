using System.Globalization;
using System.Text.Json;

namespace AgentSpeed.Core.Quota.Vendors;

/// <summary>
/// The <c>agy</c> CLI's own usage report (QUOTAS 9.4 <i>CLI usage report</i>): its <c>--version</c> gate and the
/// summary in snake case that <c>agy -p /usage --output-format json</c> prints.
/// </summary>
public static class AntigravityCliReport
{
    /// <summary>Older print modes sent an unknown slash command to the model as a prompt, spending the user's quota.</summary>
    public static readonly (int Major, int Minor, int Patch) Minimum = (1, 1, 11);

    private static readonly (string Camel, string Snake)[] GroupNames = [("displayName", "name")];

    private static readonly (string Camel, string Snake)[] BucketNames =
        [("bucketId", "id"), ("displayName", "name"), ("remainingFraction", "remaining_fraction"), ("resetTime", "reset_time")];

    /// <summary>The trimmed output as <c>major.minor.patch</c> in digits, else null.</summary>
    public static (int Major, int Minor, int Patch)? Version(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Trim().Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)))
        {
            return null;
        }

        return Part(parts[0], out var major) && Part(parts[1], out var minor) && Part(parts[2], out var patch) ? (major, minor, patch) : null;
    }

    private static bool Part(string text, out int value) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    public static bool Supported(string text) => Version(text) is { } version && version.CompareTo(Minimum) >= 0;

    /// <summary>
    /// <c>status == "SUCCESS"</c>, <c>command.name == "usage"</c> and an object <c>command.data</c>, whose groups and buckets
    /// take their camel-case names where those are absent; then the summary's rules. Null for anything else or no window.
    /// </summary>
    public static IReadOnlyList<QuotaWindow>? Windows(ReadOnlyMemory<byte> stdout)
    {
        using var report = VendorValue.ParseObject(stdout);
        if (report is null)
        {
            return null;
        }

        var root = report.RootElement;
        var command = VendorValue.Object(root, "command");
        if (VendorValue.String(VendorValue.Get(root, "status")) != "SUCCESS" || VendorValue.String(VendorValue.Get(command, "name")) != "usage"
            || VendorValue.Object(command, "data") is not { } data)
        {
            return null;
        }

        using var summary = JsonDocument.Parse(CamelCase(data));
        var windows = AntigravitySummary.SummaryWindows(summary.RootElement);
        return windows.Count == 0 ? null : windows;
    }

    private static byte[] CamelCase(JsonElement data)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var member in data.EnumerateObject())
            {
                if (member.NameEquals("groups") && member.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray("groups");
                    foreach (var group in member.Value.EnumerateArray())
                    {
                        WriteGroup(writer, group);
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    member.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteGroup(Utf8JsonWriter writer, JsonElement group)
    {
        if (group.ValueKind != JsonValueKind.Object)
        {
            group.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var member in group.EnumerateObject())
        {
            if (member.NameEquals("buckets") && member.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray("buckets");
                foreach (var bucket in member.Value.EnumerateArray())
                {
                    if (bucket.ValueKind == JsonValueKind.Object)
                    {
                        writer.WriteStartObject();
                        WriteMembers(writer, bucket, BucketNames);
                        writer.WriteEndObject();
                    }
                    else
                    {
                        bucket.WriteTo(writer);
                    }
                }

                writer.WriteEndArray();
            }
            else
            {
                member.WriteTo(writer);
            }
        }

        WriteAliases(writer, group, GroupNames);
        writer.WriteEndObject();
    }

    private static void WriteMembers(Utf8JsonWriter writer, JsonElement item, (string Camel, string Snake)[] names)
    {
        foreach (var member in item.EnumerateObject())
        {
            member.WriteTo(writer);
        }

        WriteAliases(writer, item, names);
    }

    /// <summary>The camel-case member wins where both appear, even when it is null.</summary>
    private static void WriteAliases(Utf8JsonWriter writer, JsonElement item, (string Camel, string Snake)[] names)
    {
        foreach (var (camel, snake) in names)
        {
            if (!item.TryGetProperty(camel, out _) && item.TryGetProperty(snake, out var value))
            {
                writer.WritePropertyName(camel);
                value.WriteTo(writer);
            }
        }
    }
}
