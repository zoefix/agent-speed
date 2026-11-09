#pragma once

#include <atomic>
#include <filesystem>
#include <fstream>
#include <map>
#include <optional>
#include <random>
#include <string>
#include <vector>

#include "fixture_support.hpp"

// Small packs on disk built from the fixture rig of spec/fixtures/characters/rig.json, so the tests need no art.
namespace tiny_pack {

struct RigOptions {
    double feetY = 90;
    double faceTop = 10;
    std::string objectSide = "left";
    std::optional<double> torsoLength;
    std::string name = "tiny";
};

inline std::string rigJSON(const RigOptions& o = {}) {
    std::string landmarks = "{\"feet\":[50," + std::to_string(o.feetY) + "],\"face\":[40," + std::to_string(o.faceTop) +
                            ",60," + std::to_string(o.faceTop + 20) + "],\"faceWidth\":20,\"objectSide\":\"" +
                            o.objectSide + "\"";
    if (o.torsoLength) landmarks += ",\"torsoLength\":" + std::to_string(*o.torsoLength);
    landmarks += "}";
    return R"({"format":"agent-speed-character","version":1,"name":")" + o.name +
           R"(","canvas":[100,100],"atlas":{"file":"atlas.png","size":[16,16]},"landmarks":)" + landmarks +
           R"(,"params":[{"id":"Turn","min":-1,"max":1,"default":0},{"id":"Lift","min":0,"max":1,"default":0},)"
           R"({"id":"Grow","min":-1,"max":1,"default":0},{"id":"Fade","min":0,"max":1,"default":0},)"
           R"({"id":"PosX","min":-1,"max":1,"default":0},{"id":"PosY","min":-1,"max":1,"default":0},)"
           R"({"id":"Squash","min":-1,"max":1,"default":0},{"id":"Spin","min":0,"max":1,"default":0},)"
           R"({"id":"Lean","min":-1,"max":1,"default":0}],)"
           R"("bones":[{"name":"root","parent":null,"pivot":[50,90]},{"name":"arm","parent":"root","pivot":[20,30]},)"
           R"({"name":"hand","parent":"arm","pivot":[30,30]}],)"
           R"("drivers":[{"param":"Turn","bone":"arm","channel":"rotate","keys":[[-1,-90],[0,0],[1,45]]},)"
           R"({"param":"Lift","bone":"hand","channel":"y","keys":[[0,0],[1,-5]]},)"
           R"({"param":"Grow","bone":"root","channel":"scaleX","keys":[[-1,-0.5],[1,0.25]]},)"
           R"({"param":"Grow","bone":"root","channel":"scaleY","keys":[[-1,0.1],[1,-0.1]]},)"
           R"({"param":"Turn","bone":"root","channel":"x","keys":[[-1,3],[1,-3]]}],)"
           R"("physics":[],"partDrivers":[{"param":"Fade","part":"b","keys":[[0,1],[1,0]]}],"variants":[],)"
           R"("parts":[{"name":"a","tag":"topwear","order":0,"vertices":[20,20,30,20,30,30,20,30],)"
           R"("uv":[0,0,10,0,10,10,0,10],"triangles":[0,1,2,0,2,3],"influences":2,"bones":[1,2,1,2,1,2,1,2],)"
           R"("weights":[1,0,0.5,0.5,0,1,0.25,0.75]},{"name":"b","tag":"face","order":1,"vertices":[40,40,60,40,60,60],)"
           R"("uv":[0,0,10,0,10,10],"triangles":[0,1,2],"influences":1,"bones":[0,0,0],"weights":[1,1,1]}]})";
}

inline std::filesystem::path tempDir(const std::string& prefix) {
    static std::atomic<int> counter{0};
    std::random_device rd;
    auto dir = std::filesystem::temp_directory_path() /
               (prefix + "-" + std::to_string(rd()) + "-" + std::to_string(counter++));
    std::filesystem::create_directories(dir);
    return dir;
}

inline void write(const std::filesystem::path& path, const std::string& text) {
    std::filesystem::create_directories(path.parent_path());
    std::ofstream out(path, std::ios::binary);
    out << text;
}

struct ManifestOptions {
    std::string id = "tiny";
    std::optional<std::string> preview;  // default "preview.png"
    std::optional<std::string> idleFolder;  // replaces poses.idle
    int version = 1;
    std::optional<std::string> minApp;
    std::string extra;  // raw members appended to the manifest
};

// Writes a pack folder with one rig per band and returns it.
inline std::filesystem::path make(const std::map<std::string, std::string>& bands, const ManifestOptions& o = {}) {
    const auto root = tempDir("pack");
    std::string poses;
    for (const auto& [band, rig] : bands) {
        write(root / "poses" / band / "rig.json", rig);
        write(root / "poses" / band / "atlas.png", std::string(1, '\0'));
        if (!poses.empty()) poses += ",";
        std::string folder = "poses/" + band;
        if (band == "idle" && o.idleFolder) folder = *o.idleFolder;
        poses += "\"" + band + "\":\"" + folder + "\"";
    }
    write(root / "preview.png", std::string(1, '\0'));
    std::string manifest = R"({"format":"agent-speed-character-pack","version":)" + std::to_string(o.version) +
                           R"(,"id":")" + o.id + R"(","packVersion":1,)" +
                           R"("name":{"zh-Hant":"小","zh-Hans":"小","en":"Tiny","ja":"小"},"vendors":["anthropic"],)" +
                           R"("accent":"#D97757","poses":{)" + poses + R"(},"preview":")" + o.preview.value_or("preview.png") +
                           "\"";
    if (o.minApp) manifest += R"(,"minApp":")" + *o.minApp + "\"";
    manifest += o.extra + "}";
    write(root / "manifest.json", manifest);
    return root;
}

}  // namespace tiny_pack
