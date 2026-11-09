using System.Text.Json.Nodes;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>Builds small packs on disk from the fixture rig, so the tests need no art.</summary>
internal static class TinyPack
{
    public static JsonObject RigJson(double feetY = 90, double faceTop = 10, string objectSide = "left")
    {
        var doc = JsonNode.Parse(File.ReadAllText(RepositoryPaths.FixtureFile("characters/rig.json")))!.AsObject();
        var rig = doc["rig"]!.DeepClone().AsObject();
        rig["landmarks"] = new JsonObject
        {
            ["feet"] = new JsonArray(50, feetY),
            ["face"] = new JsonArray(40, faceTop, 60, faceTop + 20),
            ["faceWidth"] = 20,
            ["objectSide"] = objectSide,
        };
        var parameters = rig["params"]!.AsArray();
        foreach (var (id, min, max) in new[] { ("PosX", -1, 1), ("PosY", -1, 1), ("Squash", -1, 1), ("Spin", 0, 1), ("Lean", -1, 1) })
        {
            parameters.Add(new JsonObject { ["id"] = id, ["min"] = min, ["max"] = max, ["default"] = 0 });
        }

        return rig;
    }

    public static void SetLandmark(JsonObject rig, string key, JsonNode value) => rig["landmarks"]!.AsObject()[key] = value;

    public static string Make(IReadOnlyDictionary<string, JsonObject> bands, string id = "tiny", Action<JsonObject>? tamper = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "pack-" + Guid.NewGuid().ToString("N"));
        foreach (var (band, rig) in bands)
        {
            var dir = Path.Combine(root, "poses", band);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "rig.json"), rig.ToJsonString());
            File.WriteAllBytes(Path.Combine(dir, "atlas.png"), [0]);
        }

        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "preview.png"), [0]);
        var poses = new JsonObject();
        foreach (var band in bands.Keys)
        {
            poses[band] = $"poses/{band}";
        }

        var manifest = new JsonObject
        {
            ["format"] = "agent-speed-character-pack",
            ["version"] = 1,
            ["id"] = id,
            ["packVersion"] = 1,
            ["name"] = new JsonObject { ["zh-Hant"] = "小", ["zh-Hans"] = "小", ["en"] = "Tiny", ["ja"] = "小" },
            ["vendors"] = new JsonArray("anthropic"),
            ["accent"] = "#D97757",
            ["poses"] = poses,
            ["preview"] = "preview.png",
        };
        tamper?.Invoke(manifest);
        File.WriteAllText(Path.Combine(root, "manifest.json"), manifest.ToJsonString());
        return root;
    }

    public static Dictionary<string, JsonObject> Bands(params (string Band, JsonObject Rig)[] bands) =>
        bands.ToDictionary(b => b.Band, b => b.Rig);
}
