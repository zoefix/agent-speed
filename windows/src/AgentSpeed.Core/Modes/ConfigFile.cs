using System.Text;
using System.Text.Json;

namespace AgentSpeed.Core;

/// <summary>
/// The config files of EXECUTION-MODES (Current values): regular files of at most 1 MiB, never followed through a link,
/// UTF-8 with one leading U+FEFF removed. Anything else gives nothing, and nothing of a file is logged.
/// </summary>
internal static class ConfigFile
{
    public const int MaxBytes = 1 << 20;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>The file's size and modification time; null when it is no regular file.</summary>
    public static (long Length, DateTime WriteUtc)? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.LinkTarget is null && (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                ? (info.Length, info.LastWriteTimeUtc)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static string? Text(string path)
    {
        if (Stamp(path) is not { Length: <= MaxBytes })
        {
            return null;
        }

        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[MaxBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = RandomAccess.Read(handle, bytes.AsSpan(length), length);
                if (read <= 0)
                {
                    break;
                }

                length += read;
            }

            if (length > MaxBytes)
            {
                return null;
            }

            var text = Utf8.GetString(bytes, 0, length);
            return text.StartsWith('﻿') ? text[1..] : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The file as a JSON object; null when it is anything else.</summary>
    public static JsonDocument? Json(string path)
    {
        if (Text(path) is not { } text)
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 512 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
