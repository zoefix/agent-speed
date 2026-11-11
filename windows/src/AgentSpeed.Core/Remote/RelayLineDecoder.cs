using System.Text.Json;

namespace AgentSpeed.Core.Remote;

/// <summary>What one relay line decoded to.</summary>
internal enum RelayDecodeKind
{
    Message,

    /// <summary>A type this version does not know.</summary>
    Ignored,
    Invalid,
}

internal readonly record struct RelayDecodeOutcome(RelayDecodeKind Kind, RelayMessage? Message = null, RemoteLineException? Error = null);

/// <summary>
/// Decodes one relay line (REMOTE 5.2) with every validation rule of REMOTE 5.6. Unknown types and unknown members are
/// ignored; anything else that does not fit makes the line invalid. Relay strings are only ever returned as values:
/// never used as a path, URL, command or format string.
/// </summary>
internal sealed partial class RelayLineDecoder
{
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 64 };

    public RelayLineDecoder(string hostId)
    {
        HostId = hostId;
        Prefix = $"ssh://{hostId}/";
    }

    public string HostId { get; }

    public string Prefix { get; }

    public RelayDecodeOutcome Decode(ReadOnlyMemory<byte> line)
    {
        if (line.Length > RemoteLineFramer.MaxLineBytes)
        {
            return Invalid(new RemoteLineException(RemoteLineErrorKind.Oversized));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line, ParseOptions);
        }
        catch (JsonException)
        {
            return Invalid(new RemoteLineException(RemoteLineErrorKind.NotJson));
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid(new RemoteLineException(RemoteLineErrorKind.NotObject));
            }

            try
            {
                var fields = new RemoteFields(document.RootElement);
                var message = Decode(fields.String("t", 64), fields);
                return message is null ? new RelayDecodeOutcome(RelayDecodeKind.Ignored) : new RelayDecodeOutcome(RelayDecodeKind.Message, message);
            }
            catch (RemoteLineException error)
            {
                return Invalid(error);
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
            {
                return Invalid(new RemoteLineException(RemoteLineErrorKind.WrongType));
            }
        }
    }

    private static RelayDecodeOutcome Invalid(RemoteLineException error) => new(RelayDecodeKind.Invalid, Error: error);

    private RelayMessage? Decode(string type, RemoteFields f) => type switch
    {
        "hello" => new RelayMessage.Hello(DecodeHello(f)),
        "progress" => new RelayMessage.Progress(f.Fraction("value")),
        "agent" => new RelayMessage.Agent(DecodeAgent(f.Object("a"))),
        "agentRemoved" => new RelayMessage.AgentRemoved(AgentId(f, "id")),
        "parents" => new RelayMessage.Parents(DecodeParents(f)),
        "response" => new RelayMessage.Response(DecodeResponse(f)),
        "tool" => new RelayMessage.Tool(DecodeTool(f)),
        "ready" => new RelayMessage.Ready(),
        "status" => new RelayMessage.Status(DecodeStatus(f)),
        "clock" => new RelayMessage.Clock(f.Integer("id"), f.Time("now")),
        "historyDay" => new RelayMessage.HistoryDay(DecodeHistoryDay(f)),
        "historyPerf" => new RelayMessage.HistoryPerf(DecodeHistoryPerf(f)),
        "historyDone" => new RelayMessage.HistoryDone(f.Count("days"), f.Count("analyzed")),
        "historyProgress" => new RelayMessage.HistoryProgress(f.Fraction("value")),
        "error" => new RelayMessage.Error(DecodeError(f)),
        "quota" => DecodeQuota(f),
        "quotaRemoved" => DecodeQuotaRemoved(f),
        "quotaBridge" => DecodeQuotaBridge(f),
        _ => null,
    };

    /// <summary>A relay of another protocol is still a <c>hello</c>: only its version is read, so the status can say why.</summary>
    private static RelayHello DecodeHello(RemoteFields f)
    {
        var version = f.Integer("protocol");
        return version != RemoteProtocol.Version
            ? new RelayHello(version, string.Empty, string.Empty, 0)
            : new RelayHello(version, f.String("version", 64, RemoteText.Model), f.String("arch", 32, RemoteText.Model), f.Time("now"));
    }

    /// <summary>A non-empty agent id of the host, prefixed (REMOTE 2.2).</summary>
    public string AgentId(RemoteFields f, string key) => Prefix + RelayAgentId(f, key);

    public static string RelayAgentId(RemoteFields f, string key)
    {
        var id = f.String(key, RemoteLimits.AgentIdBytes);
        return id.Length > 0 ? id : throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, key);
    }

    private Dictionary<string, string> DecodeParents(RemoteFields f)
    {
        var links = f.ObjectMembers("links");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in links.EnumerateObject())
        {
            var parent = RemoteFields.Checked(member.Value, "links.", RemoteLimits.AgentIdBytes, RemoteText.Identifier);
            var child = member.Name;
            if (child.Length == 0 || parent.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(child) > RemoteLimits.AgentIdBytes
                || !RemoteFields.AllowsText(child, RemoteText.Identifier))
            {
                throw new RemoteLineException(RemoteLineErrorKind.BadCharacter, "links");
            }

            map[Prefix + child] = Prefix + parent;
            if (map.Count > RemoteLimits.Agents)
            {
                throw new RemoteLineException(RemoteLineErrorKind.TooMany, "links");
            }
        }

        return map;
    }

    private static RemoteSourceReport DecodeStatus(RemoteFields f)
    {
        var sources = new List<RemoteSourceState>();
        var index = 0;
        foreach (var item in f.Array("sources", 64).EnumerateArray())
        {
            var entry = RemoteFields.Of(item, $"sources.{index++}");
            var key = entry.String("source", 64);
            var available = entry.Bool("available");
            var sessions = entry.Count("sessions");
            var unsupported = entry.Bool("unsupported");
            var modes = Modes(entry, "configuredModes");
            if (History.SourceNames.Parse(key) is { } source)
            {
                sources.Add(new RemoteSourceState(source, available, sessions, unsupported, modes));
            }
        }

        return new RemoteSourceReport(sources, f.Count("malformed"), f.Count("unreadable"), f.Bool("partialHistory"));
    }

    private static RelayError DecodeError(RemoteFields f)
    {
        var code = f.String("code", 64, RemoteText.Model);
        var detail = f.String("detail", RemoteLimits.ErrorDetailCharacters * 4, RemoteText.Model);
        if (new System.Globalization.StringInfo(detail).LengthInTextElements > RemoteLimits.ErrorDetailCharacters)
        {
            throw new RemoteLineException(RemoteLineErrorKind.TooLong, "detail");
        }

        var known = code switch
        {
            "badStart" => RelayErrorCode.BadStart,
            "historyFailed" => RelayErrorCode.HistoryFailed,
            "internal" => RelayErrorCode.Internal,
            _ => RelayErrorCode.Other,
        };
        return new RelayError(known, code, detail);
    }
}
