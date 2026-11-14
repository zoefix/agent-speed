using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Quota;

/// <summary>
/// Claude Code's own usage cache (QUOTAS 5.6): the top-level <c>cachedUsageUtilization</c> of <c>.claude.json</c>, read with
/// the cadence of <see cref="QuotaSignIn"/> (when the file's time or size changed, at most once a minute; nothing over 64 MiB).
/// Only that key and <c>oauthAccount.accountUuid</c> are looked at; the two uuids are compared while the file is read and
/// never kept. A file that does not parse keeps the previous value and is read again at the next check. Not thread-safe.
/// </summary>
public sealed class ClaudeCodeUsageCache
{
    private readonly string _path;
    private readonly Func<long> _nowMs;
    private DateTime? _modified;
    private long _length = -1;
    private long _readAtMs = long.MinValue;

    public ClaudeCodeUsageCache(string claudeStateFile, Func<long>? nowMs = null)
    {
        _path = claudeStateFile;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>The <c>.claude.json</c> of <see cref="QuotaSignIn.Files"/>.</summary>
    public static ClaudeCodeUsageCache ForThisComputer(Func<string, string?>? environment = null) =>
        new(QuotaSignIn.Files(environment).ClaudeState);

    /// <summary>The cache part of the <c>claudeCode</c> snapshot, observed at <c>fetchedAtMs</c>; null without a valid cache.</summary>
    public QuotaSnapshot? Snapshot { get; private set; }

    /// <summary>Reads the file when it is due; true when the snapshot changed.</summary>
    public bool Poll()
    {
        var info = new FileInfo(_path);
        if (!info.Exists)
        {
            Forget();
            return Set(null);
        }

        var now = _nowMs();
        var changed = _modified != info.LastWriteTimeUtc || _length != info.Length;
        if (!changed || (_readAtMs != long.MinValue && now - _readAtMs < QuotaSignIn.RereadMs))
        {
            return false;
        }

        _modified = info.LastWriteTimeUtc;
        _length = info.Length;
        _readAtMs = now;
        if (info.Length > QuotaSignIn.MaxFileBytes)
        {
            return Set(null);
        }

        byte[] bytes;
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > QuotaSignIn.MaxFileBytes)
            {
                return Set(null);
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Claude Code may be rewriting it: like a file that does not parse, the value stays and the next check reads again.
            Forget();
            return false;
        }

        var parsed = TryRead(bytes, out var cache);
        Array.Clear(bytes);
        if (!parsed)
        {
            Forget();
            return false;
        }

        return Set(cache);
    }

    /// <summary>
    /// One read of the file: false when it does not parse as JSON; otherwise <paramref name="cache"/> is the cache part, or
    /// null when the file has no valid cache (QUOTAS 5.6 <i>Valid</i>).
    /// </summary>
    internal static bool TryRead(ReadOnlySpan<byte> json, out QuotaSnapshot? cache)
    {
        cache = null;
        Cache? found = null;
        string? signedIn = null;
        try
        {
            var reader = new Utf8JsonReader(ClaudeCliQuota.WithoutBom(json), new JsonReaderOptions { MaxDepth = 512 });
            if (!reader.Read())
            {
                return false;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("cachedUsageUtilization"u8))
                    {
                        reader.Read();
                        found = ReadCache(ref reader);
                    }
                    else if (reader.ValueTextEquals("oauthAccount"u8))
                    {
                        reader.Read();
                        signedIn = ReadAccount(ref reader);
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }
            }
            else
            {
                reader.Skip();
            }

            if (reader.Read())
            {
                return false;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return false;
        }

        if (found is { } c && (signedIn is null || c.AccountUuid is null || string.Equals(signedIn, c.AccountUuid, StringComparison.Ordinal)))
        {
            cache = new QuotaSnapshot(SourceKind.ClaudeCode, c.FetchedAtMs, c.Windows);
        }

        return true;
    }

    private sealed record Cache(long FetchedAtMs, string? AccountUuid, IReadOnlyList<QuotaWindow> Windows);

    /// <summary>The value of <c>cachedUsageUtilization</c> (the reader on its first token, left on its last); null unless valid.</summary>
    private static Cache? ReadCache(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        double? fetched = null;
        string? uuid = null;
        IReadOnlyList<QuotaWindow>? windows = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("fetchedAtMs"u8))
            {
                reader.Read();
                fetched = reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("accountUuid"u8))
            {
                reader.Read();
                uuid = UuidText(ref reader);
            }
            else if (reader.ValueTextEquals("utilization"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    using var body = JsonDocument.ParseValue(ref reader);
                    windows = DesktopUsageBody.Windows(body.RootElement);
                }
                else
                {
                    windows = null;
                    reader.Skip();
                }
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        // The integer part (the fraction dropped) must be a time within 2000–2100; checked before it becomes a long.
        return fetched is { } f && f >= RemoteFields.EarliestMs && f < RemoteFields.LatestMs + 1.0 && windows is not null
            ? new Cache((long)f, uuid, windows)
            : null;
    }

    /// <summary>The <c>accountUuid</c> of <c>oauthAccount</c>; null when missing.</summary>
    private static string? ReadAccount(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        string? uuid = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var named = reader.ValueTextEquals("accountUuid"u8);
            reader.Read();
            if (named)
            {
                uuid = UuidText(ref reader);
            }
            else
            {
                reader.Skip();
            }
        }

        return uuid;
    }

    /// <summary>A uuid is missing unless it is a non-empty string.</summary>
    private static string? UuidText(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return null;
        }

        string text;
        try
        {
            text = reader.GetString()!;
        }
        catch (InvalidOperationException)
        {
            // An escaped lone surrogate: compared as written.
            text = Encoding.UTF8.GetString(reader.ValueSpan);
        }

        return text.Length > 0 ? text : null;
    }

    private void Forget()
    {
        _modified = null;
        _length = -1;
    }

    private bool Set(QuotaSnapshot? next)
    {
        var changed = !Equals(next, Snapshot);
        Snapshot = next;
        return changed;
    }
}
