using System.Buffers;
using System.IO.Compression;
using System.Text.Json;
using AgentSpeed.Core.Harnesses;

namespace AgentSpeed.Core.OpenClaw;

/// <summary>What a <c>trace.metadata</c> row keeps (METRICS 4.14): its time and its facts; a trimmed row has none.</summary>
internal readonly record struct OpenClawTrace(long CreatedMs, ExecutionModes Modes);

/// <summary>
/// Decoding of the trajectory rows the database selects (METRICS 4.14, <i>Trajectory rows</i>). Everything but two fields of
/// <c>data.model</c> is dropped at once, and whatever does not decode is ignored: it is not a malformed line.
/// </summary>
internal static class OpenClawTraces
{
    public const int MaxBytes = 262_144;

    private const string BrotliCodec = "br";

    /// <summary>The trace of a selected row, or null when it does not decode or is no <c>trace.metadata</c> event.</summary>
    public static OpenClawTrace? Read(long createdMs, ReadOnlySpan<byte> json)
    {
        if (!System.Text.Unicode.Utf8.IsValid(json))
        {
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(json, Parsing.JsonScan.Options);
            using var document = JsonDocument.ParseValue(ref reader);
            if (reader.Read())
            {
                return null;
            }

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HarnessJson.String(root, "type") != "trace.metadata")
            {
                return null;
            }

            var model = HarnessJson.Get(HarnessJson.Get(root, "data"), "model");
            var modes = ExecutionModes.Of(ExecutionModeNative.Effort(HarnessJson.String(model, "thinkLevel"), ExecutionModeEvidence.Recorded, ExecutionModeScope.Turn));
            return new OpenClawTrace(createdMs, HarnessJson.Get(model, "fastMode").ValueKind == JsonValueKind.True
                ? modes.With(ExecutionModeFact.Create(ExecutionModeKind.ServiceTier, "fast", ExecutionModeEvidence.Selected, scope: ExecutionModeScope.Turn))
                : modes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The trace of a row stored with a codec: only Brotli, of a size within bounds.</summary>
    public static OpenClawTrace? ReadCompressed(long createdMs, string? codec, ReadOnlySpan<byte> payload, long size)
    {
        if (codec != BrotliCodec || size is < 1 or > MaxBytes)
        {
            return null;
        }

        var buffer = ArrayPool<byte>.Shared.Rent((int)size + 1);
        try
        {
            return Decompress(payload, buffer.AsSpan(0, (int)size + 1), (int)size) ? Read(createdMs, buffer.AsSpan(0, (int)size)) : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>
    /// A whole Brotli stream of exactly <paramref name="size"/> bytes. <paramref name="output"/> holds one byte more, so a
    /// stream that expands past it stops there: damaged, truncated, longer and shorter input all give false.
    /// </summary>
    internal static bool Decompress(ReadOnlySpan<byte> payload, Span<byte> output, int size)
    {
        // A struct holding native state: a `using` variable would be read-only and each call would run on a copy.
        var decoder = new BrotliDecoder();
        try
        {
            return decoder.Decompress(payload, output, out var consumed, out var written) == OperationStatus.Done
                && written == size && consumed == payload.Length;
        }
        finally
        {
            decoder.Dispose();
        }
    }
}
