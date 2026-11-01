using System.Text.Json;
using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Harnesses;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Harnesses.HarnessJson;

namespace AgentSpeed.Core.Devin;

internal sealed record DevinAgent(AgentInfo Info, int MalformedLines, string? Title = null);
internal sealed class DevinUpdate
{
    public List<(ParsedResponse Response, bool Completion)> Responses { get; } = [];
    public List<DevinAgent> Agents { get; } = [];
    public List<string> RemovedAgents { get; } = [];
    public List<string> RemovedResponses { get; } = [];
    public bool Changed => Responses.Count > 0 || Agents.Count > 0 || RemovedAgents.Count > 0 || RemovedResponses.Count > 0;
}

internal sealed class DevinSource(DevinLocation location, IDeduper deduper)
{
    private readonly Dictionary<string, DevinSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DevinAgent> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParsedResponse> _emitted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedCliSessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _duplicates = new(StringComparer.Ordinal);
    private long _lastRowId;
    private long _sinceMs;
    private string? _stamp;
    private string? _databaseStamp;
    private FileIdentity? _databaseIdentity;
    private bool _needsDatabase = true;

    public bool Available => location.Exists;
    public bool Unsupported { get; private set; }
    public bool Unreadable { get; private set; }
    public IEnumerable<string> WatchDirectories => location.DesktopFolders.Where(Directory.Exists)
        .Concat(location.DatabasePath is { } path && File.Exists(path) ? [Path.GetDirectoryName(path) ?? string.Empty] : []);
    public int MalformedLines => _agents.Values.Sum(agent => agent.MalformedLines);

    public DevinUpdate Seed(long todayStartMs)
    {
        _sinceMs = Math.Max(0, todayStartMs - MonitorCore.BacklogWindowMs);
        _lastRowId = 0;
        _needsDatabase = true;
        return Refresh(force: true);
    }

    public DevinUpdate RefreshIfChanged() => CurrentStamp() == _stamp && !_needsDatabase ? new DevinUpdate() : Refresh();

    public DevinUpdate Refresh(bool force = false)
    {
        var update = new DevinUpdate();
        ReadDatabase(force, update);
        ReadDesktop(update);
        foreach (var (id, agent) in _agents)
        {
            if (agent.Title is not { } title) continue;
            var matches = _sessions.Values.Where(session => string.Equals(session.Title?.Trim(), title, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) continue;
            var session = matches[0];
            var model = SessionModel(session.Model);
            _agents[id] = agent with
            {
                Info = agent.Info with
                {
                    Project = agent.Info.Project ?? (session.WorkingDirectory is { } cwd ? IO.PathText.Basename(cwd) : null),
                    Model = agent.Info.Model ?? model,
                },
            };
            if (model is null) continue;
            foreach (var (key, response) in _emitted.Where(pair => pair.Value.AgentId == id && pair.Value.Model is null).ToArray())
                _emitted[key] = response with { Model = model };
            for (var index = 0; index < update.Responses.Count; index++)
            {
                if (update.Responses[index].Response.AgentId == id && update.Responses[index].Response.Model is null)
                    update.Responses[index] = (update.Responses[index].Response with { Model = model }, update.Responses[index].Completion);
            }
        }
        foreach (var (id, agent) in _agents)
        {
            if (IsDuplicate(agent))
            {
                _duplicates.Add(id);
                update.RemovedAgents.Add(id);
                RemoveResponses(id, update);
            }
        }
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var latest = new List<(ParsedResponse Response, bool Completion)>();
        foreach (var item in update.Responses)
        {
            if (indexes.TryGetValue(item.Response.Key, out var index))
                latest[index] = (item.Response, latest[index].Completion || item.Completion);
            else
            {
                indexes.Add(item.Response.Key, latest.Count);
                latest.Add(item);
            }
        }
        update.Responses.Clear();
        update.Responses.AddRange(latest);
        update.Agents.AddRange(_agents.Values.Where(agent => !_duplicates.Contains(agent.Info.Id) && agent.Info.LastLineMs is not null));
        _stamp = CurrentStamp();
        return update;
    }

    public int ActiveSessionCount(long todayStartMs)
    {
        var cli = _sessions.Values.Count(session => session.LastActivityMs >= todayStartMs);
        var desktop = _agents.Values.Count(agent => agent.Info.Id.EndsWith(".ndjson", StringComparison.Ordinal)
            && agent.Info.LastLineMs >= todayStartMs);
        var mirrors = _agents.Values.Count(agent => agent.Info.LastLineMs >= todayStartMs
            && MatchingSession(agent.Title) is { } session && session.LastActivityMs >= todayStartMs);
        return Math.Max(0, cli + desktop - mirrors);
    }

    private DevinSession? MatchingSession(string? title)
    {
        if (title is null) return null;
        var matches = _sessions.Values.Where(session => string.Equals(session.Title?.Trim(), title, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public bool IsWatchedPath(string path) => location.DatabasePath is { } database
        && (string.Equals(path, database, PathComparison) || string.Equals(path, database + "-wal", PathComparison))
        || location.DesktopFolders.Any(folder => path.StartsWith(folder + Path.DirectorySeparatorChar, PathComparison));

    private void ReadDatabase(bool force, DevinUpdate update)
    {
        if (location.DatabasePath is not { } path) return;
        if (!File.Exists(path))
        {
            foreach (var id in _agents.Keys.Where(id => id.StartsWith(path + "#", StringComparison.Ordinal)).ToArray())
            {
                _agents.Remove(id);
                update.RemovedAgents.Add(id);
                RemoveResponses(id, update);
            }
            _sessions.Clear();
            _usedCliSessions.Clear();
            _duplicates.Clear();
            _needsDatabase = true;
            return;
        }
        var identity = ReadIdentity(path);
        if (_databaseIdentity is { } previous && previous != identity)
        {
            foreach (var id in _agents.Keys.Where(id => id.StartsWith(path + "#", StringComparison.Ordinal)).ToArray())
            {
                _agents.Remove(id);
                update.RemovedAgents.Add(id);
                RemoveResponses(id, update);
            }
            _sessions.Clear();
            _usedCliSessions.Clear();
            _duplicates.Clear();
            _lastRowId = 0;
            _needsDatabase = true;
        }
        _databaseIdentity = identity;
        var stamp = DatabaseStamp(path);
        if (!force && !_needsDatabase && stamp == _databaseStamp) return;
        try
        {
            using var database = DevinDatabase.Open(path);
            foreach (var session in database.Sessions()) _sessions[session.Id] = session;
            foreach (var row in database.Messages(_lastRowId, _sinceMs))
            {
                _lastRowId = Math.Max(_lastRowId, row.RowId);
                if (!_sessions.TryGetValue(row.SessionId, out var session)) continue;
                var id = path + "#" + row.SessionId;
                var previous = _agents.GetValueOrDefault(id);
                var project = previous.Info.Project ?? (session.WorkingDirectory is { } cwd ? IO.PathText.Basename(cwd) : null);
                var model = previous.Info.Model ?? SessionModel(session.Model);
                var info = new AgentInfo(id, SourceKind.Devin, project, model, false, AgentPhase.Idle, null, row.CreatedMs);
                var malformed = previous.MalformedLines;
                if (row.Role == "invalid")
                {
                    _agents[id] = new DevinAgent(info, malformed + 1);
                    continue;
                }
                JsonDocument? document = null;
                try { document = JsonDocument.Parse(row.Metadata); }
                catch (JsonException) when (row.Role is "assistant" or "user") { malformed++; }
                if (document is null && (row.Role is "assistant" or "user") && row.Metadata.Length == 0)
                {
                    malformed++;
                }
                if (document is not null)
                {
                    var metadata = document.RootElement;
                    if (row.Role == "user")
                    {
                        info = info with { Phase = AgentPhase.Generating, InFlightStartMs = row.CreatedMs };
                    }
                    else if (row.Role == "assistant")
                    {
                        info = info with { Phase = String(metadata, "finish_reason") == "tool_calls" ? AgentPhase.Tools : AgentPhase.Idle };
                        var usage = Get(metadata, "metrics");
                        if (usage.ValueKind == JsonValueKind.Object)
                        {
                            var tokens = CliTokens(usage);
                            if (tokens.Input > 0 || tokens.Output > 0)
                            {
                                model = ResponseModel(metadata, session);
                                info = info with { Model = model };
                                var request = String(metadata, "request_id")?.Trim();
                                var identity = !string.IsNullOrWhiteSpace(request) ? "request:" + request : "row:" + row.RowId;
                                var response = new ParsedResponse("devin:" + row.SessionId + ":" + identity, SourceKind.Devin, id,
                                    model, Duration(usage) is { } duration ? row.CreatedMs - duration : null, row.CreatedMs, tokens);
                                Emit(response, row.SessionId, update);
                            }
                        }
                    }
                }
                _agents[id] = new DevinAgent(info, malformed);
                document?.Dispose();
            }
            Unsupported = Unreadable = false;
            _needsDatabase = false;
            _databaseStamp = stamp;
        }
        catch (InvalidDataException) { Unsupported = true; }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or JsonException)
        {
            Unreadable = true;
        }
    }

    private void ReadDesktop(DevinUpdate update)
    {
        var files = DevinPaths.DesktopFiles(location);
        foreach (var id in _agents.Keys.Where(id => id.EndsWith(".ndjson", StringComparison.Ordinal) && !files.Contains(id, StringComparer.Ordinal)).ToArray())
        {
            _agents.Remove(id);
            _duplicates.Remove(id);
            update.RemovedAgents.Add(id);
            RemoveResponses(id, update);
        }
        foreach (var path in files)
        {
            DevinAgent agent;
            try { agent = ReadDesktopFile(path, update); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            _agents[path] = agent;
        }
    }

    private DevinAgent ReadDesktopFile(string path, DevinUpdate update)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        string? title = null, aggregateModel = null;
        long input = 0, cached = 0, write = 0, output = 0, lastMs = 0;
        var malformed = 0;
        var model = (string?)null;
        var lineNumber = 0;
        foreach (var line in DevinPaths.ReadAllText(path).Split('\n'))
        {
            lineNumber++;
            if (line.Length == 0) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { malformed++; continue; }
            using (document)
            {
                var notification = Get(document.RootElement, "notification");
                if (notification.ValueKind != JsonValueKind.Object) continue;
                if (String(notification, "sessionUpdate") == "session_info_update")
                {
                    if (String(notification, "title")?.Trim() is { Length: > 0 } candidate) title = candidate;
                    continue;
                }
                var stamp = Timestamp(notification);
                if (stamp is { } time) lastMs = Math.Max(lastMs, time);
                var eventModel = Model(notification);
                var meta = Get(notification, "_meta");
                if (String(notification, "sessionUpdate") == "usage_update" && HasCanonicalUsage(meta))
                {
                    if (Get(meta, "cognition.ai/inputTokens").ValueKind != JsonValueKind.Undefined)
                        input = Count(meta, "cognition.ai/inputTokens");
                    if (Get(meta, "cognition.ai/cachedReadTokens").ValueKind != JsonValueKind.Undefined)
                        cached = Count(meta, "cognition.ai/cachedReadTokens");
                    if (Get(meta, "cognition.ai/cachedWriteTokens").ValueKind != JsonValueKind.Undefined)
                        write = Count(meta, "cognition.ai/cachedWriteTokens");
                    output = JsonScan.SaturatingAdd(output, Count(meta, "cognition.ai/outputTokens"));
                    aggregateModel ??= eventModel;
                    continue;
                }
                var usage = LegacyUsage(notification);
                if (usage.ValueKind != JsonValueKind.Object) continue;
                var tokens = CliTokens(usage);
                if ((tokens.Input == 0 && tokens.Output == 0) || stamp is not { } end) continue;
                model = eventModel ?? model;
                var response = new ParsedResponse("devin-desktop:" + stem + ":line:" + lineNumber, SourceKind.Devin, path,
                    eventModel, Duration(usage) is { } duration ? end - duration : null, end, tokens);
                Emit(response, null, update);
            }
        }
        if (input + write > 0 || output > 0)
        {
            model = aggregateModel;
            var response = new ParsedResponse("devin-desktop:" + stem + ":usage", SourceKind.Devin, path, aggregateModel,
                null, lastMs, new TokenCounts(JsonScan.SaturatingAdd(input, write), cached, write, output, 0));
            Emit(response, null, update);
        }
        return new DevinAgent(new AgentInfo(path, SourceKind.Devin, null, model, false, AgentPhase.Idle, null,
            lastMs == 0 ? null : lastMs), malformed, title);
    }

    private void Emit(ParsedResponse response, string? cliSession, DevinUpdate update)
    {
        if (_agents.GetValueOrDefault(response.AgentId) is { } current && IsDuplicate(current)) return;
        if (_emitted.TryGetValue(response.Key, out var previous) && previous == response) return;
        var completion = deduper.TryClaim(response.Key, response.AgentId, out var owner);
        if (!completion && !string.Equals(owner, response.AgentId, StringComparison.Ordinal)) return;
        _emitted[response.Key] = response;
        if (cliSession is not null) _usedCliSessions.Add(cliSession);
        update.Responses.Add((response, completion && previous is null));
    }

    private bool IsDuplicate(DevinAgent agent)
    {
        if (agent.Title is not { } title) return false;
        var matches = _sessions.Values.Where(session => string.Equals(session.Title?.Trim(), title, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 && _usedCliSessions.Contains(matches[0].Id);
    }

    private void RemoveResponses(string agentId, DevinUpdate update)
    {
        foreach (var (key, response) in _emitted.Where(pair => pair.Value.AgentId == agentId).ToArray())
        {
            _emitted.Remove(key);
            update.RemovedResponses.Add(key);
        }
    }

    private string CurrentStamp() => DatabaseStamp(location.DatabasePath) + "|" + string.Join("|",
        DevinPaths.DesktopFiles(location).Select(path => DatabaseStamp(path)));

    private static string DatabaseStamp(string? path) => path is null || AntigravityStamp.Of(path) is not { } stamp ? "-"
        : $"{stamp.DatabaseLength}:{stamp.DatabaseWriteUtc.Ticks}:{stamp.WalLength}:{stamp.WalWriteUtc.Ticks}";

    private static string? SessionModel(string? value) => !string.IsNullOrWhiteSpace(value) && !string.Equals(value.Trim(), "adaptive", StringComparison.OrdinalIgnoreCase)
        ? value.Trim() : null;

    private static string? ResponseModel(JsonElement metadata, DevinSession session) =>
        String(metadata, "generation_model")?.Trim() is { Length: > 0 } model ? model : SessionModel(session.Model);

    private static TokenCounts CliTokens(JsonElement usage)
    {
        var cached = Count(usage, "cache_read_tokens");
        var write = Count(usage, "cache_creation_tokens");
        return new TokenCounts(JsonScan.SaturatingAdd(Count(usage, "input_tokens"), JsonScan.SaturatingAdd(cached, write)), cached, write,
            Count(usage, "output_tokens"), 0);
    }

    private static long? Duration(JsonElement usage) => Get(usage, "total_time_ms").ValueKind == JsonValueKind.Number
        && Get(usage, "total_time_ms").TryGetDouble(out var value) && double.IsFinite(value) && value > 0
        ? value >= long.MaxValue ? long.MaxValue : (long)value : null;

    private static bool HasCanonicalUsage(JsonElement meta) => meta.ValueKind == JsonValueKind.Object
        && (Get(meta, "cognition.ai/inputTokens").ValueKind != JsonValueKind.Undefined
            || Get(meta, "cognition.ai/cachedReadTokens").ValueKind != JsonValueKind.Undefined
            || Get(meta, "cognition.ai/cachedWriteTokens").ValueKind != JsonValueKind.Undefined
            || Get(meta, "cognition.ai/outputTokens").ValueKind != JsonValueKind.Undefined);

    private static JsonElement LegacyUsage(JsonElement notification)
    {
        string[][] paths = [["content", "metadata", "metrics"], ["metadata", "metrics"], ["metrics"], ["content", "metadata"], ["metadata"]];
        foreach (var path in paths)
        {
            var value = notification;
            var found = true;
            foreach (var part in path)
            {
                value = Get(value, part);
                if (value.ValueKind == JsonValueKind.Undefined) { found = false; break; }
            }
            if (found) return value;
        }
        return default;
    }

    private static long? Timestamp(JsonElement notification)
    {
        string[][] paths = [["content", "metadata", "created_at"], ["metadata", "created_at"], ["created_at"], ["timestamp"]];
        foreach (var path in paths)
        {
            var value = notification;
            var found = true;
            foreach (var part in path)
            {
                value = Get(value, part);
                if (value.ValueKind == JsonValueKind.Undefined) { found = false; break; }
            }
            if (found && Milliseconds(value) is { } stamp) return stamp;
        }
        return null;
    }

    private static string? Model(JsonElement notification)
    {
        string[][] paths = [["content", "metadata", "generation_model"], ["metadata", "generation_model"], ["_meta", "cognition.ai/model"]];
        foreach (var path in paths)
        {
            var value = notification;
            var found = true;
            foreach (var part in path)
            {
                value = Get(value, part);
                if (value.ValueKind == JsonValueKind.Undefined) { found = false; break; }
            }
            if (found && Text(value) is { } model && !string.Equals(model, "adaptive", StringComparison.OrdinalIgnoreCase)) return model;
        }
        return null;
    }

    private static FileIdentity? ReadIdentity(string path)
    {
        try
        {
            using var handle = IO.TranscriptTailer.Open(path);
            return FileIdentity.From(handle);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
