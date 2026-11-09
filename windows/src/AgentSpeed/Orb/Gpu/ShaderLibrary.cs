using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AgentSpeed.App;
using Vortice.D3DCompiler;

namespace AgentSpeed.Orb.Gpu;

/// <summary>Compiled bytecode of the orb shaders.</summary>
internal sealed record OrbShaderCode(byte[] ShapeVertex, byte[] ShapePixel, byte[] TextVertex, byte[] TextPixel, byte[] LiquidVertex,
    byte[] LiquidPixel, byte[] LedVertex, byte[] LedPixel, byte[] LayerVertex, byte[] LayerPixel, byte[] MeshVertex, byte[] MeshPixel,
    byte[] DotVertex, byte[] DotPixel, byte[] SpriteVertex, byte[] SpritePixel);

/// <summary>
/// Compiles the orb's HLSL (embedded resources) with the operating system's <c>d3dcompiler_47.dll</c> and keeps the bytecode
/// in a per-user cache keyed by a hash of the source, so only the first start after an update pays the ≈ 0.5 s compile.
/// Shader model 4.0 (feature level 10.0 and up); no native file ships with the app.
/// </summary>
internal static class ShaderLibrary
{
    private const string Profile = "4_0";
    private static readonly string[] Parts = ["OrbCommon", "OrbLiquid", "OrbShapes", "OrbText", "OrbLed", "BoardLayer", "CharacterMesh"];
    private const int BlobCount = 16;

    private static Task<OrbShaderCode>? s_preload;

    /// <summary>Starts loading (or compiling) the shaders off the UI thread, early in start-up.</summary>
    public static void Preload()
    {
        if (Environment.GetEnvironmentVariable("AGENT_SPEED_ORB_RENDERER") is not "wpf")
        {
            s_preload ??= Task.Run(LoadNow);
        }
    }

    /// <summary>The per-user cache, or <c>AGENT_SPEED_SHADER_CACHE_DIR</c> (developer harnesses next to an installed app).</summary>
    private static string CacheDirectory =>
        Environment.GetEnvironmentVariable("AGENT_SPEED_SHADER_CACHE_DIR") is { Length: > 0 } custom && Path.IsPathFullyQualified(custom)
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.ProductFolder, "cache", "shaders");

    /// <summary>The bytecode (waits for <see cref="Preload"/> when it started).</summary>
    public static OrbShaderCode Load()
    {
        if (s_preload is { } preload)
        {
            try
            {
                return preload.GetAwaiter().GetResult();
            }
            catch (InvalidOperationException)
            {
                s_preload = null;
                throw;
            }
        }

        return LoadNow();
    }

    private static OrbShaderCode LoadNow()
    {
        var source = Source();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "|" + Profile))).ToLowerInvariant()[..32];
        var path = Path.Combine(CacheDirectory, "orb-" + key + ".bin");
        if (TryRead(path) is { } cached)
        {
            return cached;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var code = new OrbShaderCode(
            Compile(source, "VsShape", "vs"),
            Compile(source, "PsShape", "ps"),
            Compile(source, "VsText", "vs"),
            Compile(source, "PsText", "ps"),
            Compile(source, "VsLiquid", "vs"),
            Compile(source, "PsLiquid", "ps"),
            Compile(source, "VsLed", "vs"),
            Compile(source, "PsLed", "ps"),
            Compile(source, "VsLayer", "vs"),
            Compile(source, "PsLayer", "ps"),
            Compile(source, "VsCharacterMesh", "vs"),
            Compile(source, "PsCharacterMesh", "ps"),
            Compile(source, "VsCharacterDot", "vs"),
            Compile(source, "PsCharacterDot", "ps"),
            Compile(source, "VsCharacterSprite", "vs"),
            Compile(source, "PsCharacterSprite", "ps"));
        AppLog.Info($"Orb shaders compiled in {watch.ElapsedMilliseconds} ms.");
        TryWrite(path, code);
        return code;
    }

    private static string Source()
    {
        var builder = new StringBuilder();
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var part in Parts)
        {
            using var stream = assembly.GetManifestResourceStream($"AgentSpeed.Orb.Shaders.{part}.hlsl")
                ?? throw new InvalidOperationException($"The orb shader {part} is missing.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            builder.Append(reader.ReadToEnd()).Append('\n');
        }

        return builder.ToString();
    }

    private static byte[] Compile(string source, string entry, string stage)
    {
        // UTF-8 bytes: the comments carry symbols (φ, σ, →), so a character count is not the source length.
        var bytes = Encoding.UTF8.GetBytes(source);
        var result = Compiler.Compile(bytes, null!, null!, entry, "orb.hlsl", stage + "_" + Profile,
            ShaderFlags.OptimizationLevel3 | ShaderFlags.WarningsAreErrors, out var blob, out var errors);
        using (blob)
        using (errors)
        {
            if (result.Failure || blob is null)
            {
                var message = errors is null ? result.ToString() : Encoding.ASCII.GetString(errors.AsSpan());
                throw new InvalidOperationException($"Orb shader {entry} failed to compile: {message}");
            }

            return blob.AsSpan().ToArray();
        }
    }

    // Cache file: "ORB4", then sixteen length-prefixed blobs, then the SHA-256 of everything before it.
    private static OrbShaderCode? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 8 + 32 || bytes.Length > 4 * 1024 * 1024)
            {
                return null;
            }

            var body = bytes.AsSpan(0, bytes.Length - 32);
            if (!SHA256.HashData(body).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - 32)))
            {
                return null;
            }

            if (Encoding.ASCII.GetString(body[..4]) != "ORB4")
            {
                return null;
            }

            var offset = 4;
            var blobs = new byte[BlobCount][];
            for (var i = 0; i < blobs.Length; i++)
            {
                var length = BitConverter.ToInt32(body.Slice(offset, 4));
                offset += 4;
                if (length <= 0 || offset + length > body.Length)
                {
                    return null;
                }

                blobs[i] = body.Slice(offset, length).ToArray();
                offset += length;
            }

            return new OrbShaderCode(blobs[0], blobs[1], blobs[2], blobs[3], blobs[4], blobs[5], blobs[6], blobs[7], blobs[8], blobs[9], blobs[10],
                blobs[11], blobs[12], blobs[13], blobs[14], blobs[15]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryWrite(string path, OrbShaderCode code)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            foreach (var stale in Directory.EnumerateFiles(CacheDirectory, "orb-*.bin"))
            {
                File.Delete(stale);
            }

            using var memory = new MemoryStream();
            memory.Write("ORB4"u8);
            foreach (var blob in new[]
                     {
                         code.ShapeVertex, code.ShapePixel, code.TextVertex, code.TextPixel, code.LiquidVertex, code.LiquidPixel, code.LedVertex,
                         code.LedPixel, code.LayerVertex, code.LayerPixel, code.MeshVertex, code.MeshPixel, code.DotVertex, code.DotPixel,
                         code.SpriteVertex, code.SpritePixel,
                     })
            {
                memory.Write(BitConverter.GetBytes(blob.Length));
                memory.Write(blob);
            }

            var body = memory.ToArray();
            var temp = path + ".tmp";
            using (var file = File.Create(temp))
            {
                file.Write(body);
                file.Write(SHA256.HashData(body));
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not cache the orb shaders.", ex);
        }
    }
}
