using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSpeed.Core.Harnesses;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Hermes;

/// <summary>
/// One Hermes log folder (METRICS 4.15): one parser state shared by <c>agent.log</c> and its rotated files, one agent per
/// session id, and the Codex runtime switch of the folder's profile. Reads only four kinds of record line; no other part
/// of any line is kept. Not thread-safe.
/// </summary>
internal sealed partial class HermesLogSet
{
    public const int TimestampLength = 23;

    private const string TurnLogger = "agent.turn_context";
    private const string LoopLogger = "agent.conversation_loop";
    private const string CallPrefix = "API call #";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly byte[] TurnMarker = Encoding.ASCII.GetBytes(TurnLogger + ": ");
    private static readonly byte[] LoopMarker = Encoding.ASCII.GetBytes(LoopLogger + ": ");
    private static readonly string[] Levels = ["DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"];
    private static readonly byte[][] LevelBytes = [.. Levels.Select(Encoding.ASCII.GetBytes)];

    private const string StateDatabaseName = "state.db";
    private const string StateSql = "SELECT CASE WHEN length(model_config) <= 65536 THEN model_config END FROM sessions WHERE id = ?";

    private readonly IDeduper _deduper;
    private readonly Dictionary<string, HermesAgent> _agents = new(StringComparer.Ordinal);
    private readonly List<HermesAgent> _ordered = [];
    private readonly SessionStore? _sessions;

    /// <param name="folder">The log folder as discovered; agent ids are <c>folder#session</c>.</param>
    /// <param name="zone">The zone whose rules convert the log's wall times (this computer's; fixtures name theirs).</param>
    public HermesLogSet(string folder, IDeduper deduper, TimeZoneInfo zone)
    {
        Folder = folder;
        _deduper = deduper;
        Zone = zone;
        ConfigPath = HermesPaths.ConfigPath(folder);
        _sessions = ConfigPath is null ? null : new SessionStore(Path.Combine(Path.GetDirectoryName(ConfigPath)!, StateDatabaseName), StateSql, StateEffort);
    }

    public string Folder { get; }

    /// <summary>The time-zone rules of this computer at the time of reading.</summary>
    public TimeZoneInfo Zone { get; set; }

    /// <summary>The profile's <c>config.yaml</c>; null for a log folder not named <c>logs</c>.</summary>
    public string? ConfigPath { get; }

    /// <summary>Agents in the order they first appeared.</summary>
    public IReadOnlyList<HermesAgent> Agents => _ordered;

    public long MalformedLines { get; private set; }

    /// <summary>Turns run through Hermes's Codex app-server runtime show no generating phase (METRICS 4.15 Codex runtime).</summary>
    public bool CodexRuntime { get; private set; }

    /// <summary>The effort and tier of the profile's <c>config.yaml</c>, shown as configured (EXECUTION-MODES).</summary>
    public ExecutionModes ConfiguredModes { get; private set; } = ExecutionModes.Empty;

    private (long Ticks, long Size)? _configSignature;

    public static string AgentIdOf(string folder, string sessionId) => folder + "#" + sessionId;

    public HermesAgent? Agent(string sessionId) => _agents.GetValueOrDefault(sessionId);

    /// <summary>Checks the profile's <c>config.yaml</c> and reads it again when its time or size changed; true when a value changed.</summary>
    public bool CheckConfig()
    {
        var (codexBefore, configuredBefore) = (CodexRuntime, ConfiguredModes);
        switch (HermesConfig.Check(ConfigPath, _configSignature, out var signature, out var codex, out var configured))
        {
            case ConfigCheck.Off:
                _configSignature = null;
                SetCodexRuntime(false);
                ConfiguredModes = ExecutionModes.Empty;
                break;
            case ConfigCheck.Read:
                _configSignature = signature;
                SetCodexRuntime(codex);
                ConfiguredModes = configured;
                break;
        }

        foreach (var agent in _ordered)
        {
            agent.SetModes(agent.SessionModes, ConfiguredModes);
        }

        return codexBefore != CodexRuntime || !configuredBefore.Equals(ConfiguredModes);
    }

    /// <summary>Reads the sessions' efforts from <c>state.db</c> when it changed or a session is new; true when one changed.</summary>
    public bool RefreshSessions()
    {
        if (_sessions is null || _ordered.Count == 0)
        {
            return false;
        }

        var changed = _sessions.Refresh([.. _agents.Keys.Where(id => id.Length > 0)]);
        foreach (var agent in _ordered)
        {
            agent.SetModes(_sessions.Of(agent.SessionId), ConfiguredModes);
        }

        return changed;
    }

    /// <summary>Of <c>model_config</c> only <c>reasoning_config</c>: <c>enabled: false</c> is <c>off</c>, else its <c>effort</c>.</summary>
    internal static ExecutionModes StateEffort(string? modelConfig)
    {
        if (modelConfig is null)
        {
            return ExecutionModes.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(modelConfig, new JsonDocumentOptions { MaxDepth = 512 });
            var reasoning = HarnessJson.Get(document.RootElement, "reasoning_config");
            if (reasoning.ValueKind != JsonValueKind.Object)
            {
                return ExecutionModes.Empty;
            }

            var raw = HarnessJson.Get(reasoning, "enabled").ValueKind == JsonValueKind.False ? "disabled" : HarnessJson.String(reasoning, "effort");
            return ExecutionModes.Of(ExecutionModeNative.Effort(raw, ExecutionModeEvidence.Selected, ExecutionModeScope.Session));
        }
        catch (JsonException)
        {
            return ExecutionModes.Empty;
        }
    }

    /// <summary>Turning the switch on idles the generating agents of the set; turning it off changes no phase.</summary>
    public void SetCodexRuntime(bool on)
    {
        if (on && !CodexRuntime)
        {
            foreach (var agent in _ordered)
            {
                if (agent.Phase == AgentPhase.Generating)
                {
                    agent.EndTurn();
                }
            }
        }

        CodexRuntime = on;
    }

    /// <summary>One physical line (UTF-8, trailing <c>\r</c> removed).</summary>
    public void Line(ReadOnlySpan<byte> line, IResponseSink sink)
    {
        if (!IsCandidate(line))
        {
            return;
        }

        var text = Utf8.GetString(line);
        var level = LevelOf(text);
        var rest = text[(TimestampLength + 1 + level.Length)..];
        string? sessionId = null, message = null, logger = null;
        foreach (var candidate in (ReadOnlySpan<string>)[TurnLogger, LoopLogger])
        {
            if (Split(rest, candidate, out sessionId, out message))
            {
                logger = candidate;
                break;
            }
        }

        if (logger is null)
        {
            return;
        }

        var kind = Classify(logger, level, message!, out var turn);
        if (kind == LineKind.None || TimestampOf(text, Zone) is not { } ts)
        {
            return;
        }

        switch (kind)
        {
            case LineKind.Turn:
                var agent = AgentFor(sessionId!, ts);
                agent.StartTurn(ts, turn!.Groups[1].Value, turn.Groups[2].Value == "subagent", CodexRuntime);
                break;
            case LineKind.End:
                AgentFor(sessionId!, ts).EndTurn();
                break;
            default:
                Call(sessionId!, message!, text[..TimestampLength], ts, sink);
                break;
        }
    }

    /// <summary>Unix ms of a record line's wall time; null when it is not a valid timestamp (METRICS 4.15 Time).</summary>
    public static long? TimestampOf(string line, TimeZoneInfo zone)
    {
        if (line.Length < TimestampLength || !HasTimestampShape(line))
        {
            return null;
        }

        return HermesClock.ToUnixMs(
            Digits(line, 0, 4), Digits(line, 5, 2), Digits(line, 8, 2), Digits(line, 11, 2), Digits(line, 14, 2), Digits(line, 17, 2),
            Digits(line, 20, 3), zone);
    }

    /// <summary>The record-line shape: 23-character timestamp, a space, a level, a space (the rest is checked later).</summary>
    public static bool IsRecordLine(string line) =>
        line.Length > TimestampLength + 1 && HasTimestampShape(line) && line[TimestampLength] == ' ' && LevelOf(line).Length > 0;

    private static bool IsCandidate(ReadOnlySpan<byte> line)
    {
        if (line.Length <= TimestampLength + 1 || line[TimestampLength] != (byte)' ')
        {
            return false;
        }

        for (var i = 0; i < TimestampLength; i++)
        {
            var c = line[i];
            var ok = i switch
            {
                4 or 7 => c == (byte)'-',
                10 => c == (byte)' ',
                13 or 16 => c == (byte)':',
                19 => c == (byte)',',
                _ => c is >= (byte)'0' and <= (byte)'9',
            };
            if (!ok)
            {
                return false;
            }
        }

        var afterTime = line[(TimestampLength + 1)..];
        var levelLength = 0;
        foreach (var level in LevelBytes)
        {
            if (afterTime.Length > level.Length && afterTime.StartsWith(level) && afterTime[level.Length] == (byte)' ')
            {
                levelLength = level.Length;
                break;
            }
        }

        if (levelLength == 0)
        {
            return false;
        }

        var rest = afterTime[levelLength..];
        return rest.IndexOf(TurnMarker) >= 0 || rest.IndexOf(LoopMarker) >= 0;
    }

    private static bool HasTimestampShape(string line)
    {
        for (var i = 0; i < TimestampLength; i++)
        {
            var c = line[i];
            var ok = i switch
            {
                4 or 7 => c == '-',
                10 => c == ' ',
                13 or 16 => c == ':',
                19 => c == ',',
                _ => char.IsAsciiDigit(c),
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static string LevelOf(string line)
    {
        foreach (var level in Levels)
        {
            var at = TimestampLength + 1;
            if (line.Length > at + level.Length && string.CompareOrdinal(line, at, level, 0, level.Length) == 0 && line[at + level.Length] == ' ')
            {
                return level;
            }
        }

        return string.Empty;
    }

    private static int Digits(string text, int start, int count)
    {
        var value = 0;
        for (var i = start; i < start + count; i++)
        {
            value = (value * 10) + (text[i] - '0');
        }

        return value;
    }

    /// <summary>
    /// The text after the level belongs to <paramref name="logger"/>: <c> logger: message</c> (no session; the id is empty)
    /// or <c> [id] logger: message</c> with a non-empty id up to the first <c>] logger: </c>.
    /// </summary>
    private static bool Split(string rest, string logger, out string? sessionId, out string? message)
    {
        var head = " " + logger + ": ";
        if (rest.StartsWith(head, StringComparison.Ordinal))
        {
            (sessionId, message) = (string.Empty, rest[head.Length..]);
            return true;
        }

        if (rest.StartsWith(" [", StringComparison.Ordinal))
        {
            var end = rest.IndexOf("] " + logger + ": ", 2, StringComparison.Ordinal);
            if (end > 2)
            {
                (sessionId, message) = (rest[2..end], rest[(end + logger.Length + 4)..]);
                return true;
            }
        }

        (sessionId, message) = (null, null);
        return false;
    }

    private static LineKind Classify(string logger, string level, string message, out Match? turn)
    {
        turn = null;
        if (logger == TurnLogger)
        {
            if (level != "INFO")
            {
                return LineKind.None;
            }

            turn = TurnPattern().Match(message);
            return turn.Success ? LineKind.Turn : LineKind.None;
        }

        if (level == "INFO" && message.StartsWith(CallPrefix, StringComparison.Ordinal))
        {
            return LineKind.Call;
        }

        if (level is "INFO" or "WARNING"
            && (message.StartsWith("Turn ended: ", StringComparison.Ordinal)
                || message.StartsWith("Turn ended with pending tool result", StringComparison.Ordinal)))
        {
            return LineKind.End;
        }

        if (level == "ERROR")
        {
            var prefix = LogPrefix().Match(message);
            var after = prefix.Success ? message[prefix.Length..] : message;
            if (after.StartsWith("API call failed after ", StringComparison.Ordinal)
                || after.StartsWith("Non-retryable client error: ", StringComparison.Ordinal))
            {
                return LineKind.End;
            }
        }

        return LineKind.None;
    }

    private HermesAgent AgentFor(string sessionId, long timestampMs)
    {
        if (!_agents.TryGetValue(sessionId, out var agent))
        {
            agent = new HermesAgent(AgentIdOf(Folder, sessionId), sessionId);
            agent.SetModes(ExecutionModes.Empty, ConfiguredModes);
            _agents[sessionId] = agent;
            _ordered.Add(agent);
        }

        agent.Touch(timestampMs);
        return agent;
    }

    private void Call(string sessionId, string message, string stamp, long ts, IResponseSink sink)
    {
        var call = CallPattern().Match(message);
        if (!call.Success)
        {
            MalformedLines++;
            return;
        }

        var agent = AgentFor(sessionId, ts);
        var key = "hermes:" + sessionId + ":" + call.Groups[1].Value + ":" + stamp;
        if (!_deduper.TryClaim(key, Folder, out _))
        {
            return;
        }

        var model = call.Groups[2].Value;
        agent.Call(ts, model);
        var input = call.Groups[4].Value;
        var output = call.Groups[5].Value;
        if (input == "?" || output == "?")
        {
            return;
        }

        var tokens = new TokenCounts(
            Input: Count(input),
            CachedInput: call.Groups[8].Success ? Count(call.Groups[8].Value) : 0,
            CacheWriteInput: call.Groups[9].Success ? Count(call.Groups[9].Value) : 0,
            Output: Count(output));
        if (tokens.Input == 0 && tokens.Output == 0)
        {
            return;
        }

        var latency = call.Groups[7].Value;
        long? start = latency.StartsWith('-') ? null : SaturatingSubtract(ts, LatencyMs(latency));
        var provider = call.Groups[3].Value is { Length: > 0 } named && named != "unknown" ? named : null;
        sink.OnResponse(
            new ParsedResponse(key, SourceKind.Hermes, agent.AgentId, model.Length > 0 ? model : null, start, ts, tokens, Final: true, Provider: provider),
            isCompletion: true);
    }

    /// <summary>A token count of the line: an integer literal, negatives 0, capped at 10^12 (METRICS 3.2).</summary>
    internal static long Count(string text)
    {
        if (text.StartsWith('-'))
        {
            return 0;
        }

        long value = 0;
        foreach (var c in text)
        {
            value = (value * 10) + (c - '0');
            if (value > TokenCounts.MaxValue)
            {
                return TokenCounts.MaxValue;
            }
        }

        return value;
    }

    /// <summary>Latency × 1000 computed from its decimal digits, truncated to whole milliseconds, saturated (METRICS 3.6).</summary>
    internal static long LatencyMs(string text)
    {
        var dot = text.IndexOf('.');
        var whole = dot < 0 ? text : text[..dot];
        var fraction = dot < 0 ? string.Empty : text[(dot + 1)..];
        const long limit = long.MaxValue / 10;
        long value = 0;
        foreach (var c in whole)
        {
            if (value > limit)
            {
                return long.MaxValue;
            }

            value = (value * 10) + (c - '0');
            if (value < 0)
            {
                return long.MaxValue;
            }
        }

        var millis = 0;
        for (var i = 0; i < 3; i++)
        {
            millis = (millis * 10) + (i < fraction.Length ? fraction[i] - '0' : 0);
        }

        if (value > (long.MaxValue - millis) / 1000)
        {
            return long.MaxValue;
        }

        return (value * 1000) + millis;
    }

    private static long SaturatingSubtract(long a, long b)
    {
        var result = a - b;
        return ((a ^ b) & (a ^ result)) < 0 ? (a < 0 ? long.MinValue : long.MaxValue) : result;
    }

    [GeneratedRegex("\\Aconversation turn: session=[^ ]* model=(.*?) provider=[^ ]* platform=([^ ]*) history=-?[0-9]+ msg=",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TurnPattern();

    [GeneratedRegex(
        "\\AAPI call #([0-9]+): model=(.*?) provider=([^ ]*) in=(-?[0-9]+|\\?) out=(-?[0-9]+|\\?) total=(-?[0-9]+|\\?) " +
        "latency=(-?[0-9]+(?:\\.[0-9]+)?)s(?: cache=(-?[0-9]+)/-?[0-9]+ \\(-?[0-9]+%\\))?(?: write=(-?[0-9]+))?(?: .*)?\\z",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CallPattern();

    [GeneratedRegex("\\A\\[[^\\]]*\\] *", RegexOptions.CultureInvariant)]
    private static partial Regex LogPrefix();

    private enum LineKind
    {
        None,
        Turn,
        Call,
        End,
    }
}
