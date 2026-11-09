#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include "agentspeed/json.hpp"

namespace agentspeed::character {

// A rig parameter: its id, range and default.
struct Param {
    std::string id;
    double min = 0;
    double max = 0;
    double def = 0;
    bool operator==(const Param&) const = default;

    static std::optional<Param> decode(const json::Value& v);
    static std::vector<Param> decodeList(const json::Value& v);
};

// `rig.json` of a character package (characters/README.md, format `agent-speed-character` v1). Unknown keys are
// ignored; landmarks the player does not use are skipped.
struct RigDocument {
    static constexpr std::string_view kFormat = "agent-speed-character";
    static constexpr int kVersion = 1;

    struct Bone {
        std::string name;
        std::optional<std::string> parent;
        std::vector<double> pivot;
    };
    struct Driver {
        std::string param, bone, channel;
        std::vector<std::vector<double>> keys;
    };
    struct PartDriver {
        std::string param, part;
        std::vector<std::vector<double>> keys;
    };
    struct Part {
        std::string name, tag;
        double order = 0;
        std::vector<double> vertices, uv;
        std::vector<std::int64_t> triangles;
        std::int64_t influences = 0;
        std::vector<std::int64_t> bones;
        std::vector<double> weights;
    };
    struct PhysicsInput {
        std::string param;
        std::optional<double> x, y;
    };
    struct PhysicsOutput {
        std::string param;
        std::int64_t segment = 0;
        double degrees = 0;
    };
    struct Chain {
        std::string name;
        std::vector<double> anchor, segments;
        std::vector<PhysicsInput> inputs;
        std::vector<PhysicsOutput> outputs;
        double stiffness = 0, damping = 0, wind = 0;

        // Throws CharacterError(Unreadable) when a required key is missing.
        static Chain decode(const json::Value& v);
    };
    struct Hand {
        std::vector<double> wrist;
        double size = 0;
        std::optional<double> open;
    };
    struct Landmarks {
        std::optional<std::vector<double>> face;
        std::optional<double> faceWidth;
        std::optional<std::vector<double>> feet, waist;
        std::optional<double> chin, hem, torsoLength;
        std::optional<std::string> objectSide;
        std::map<std::string, Hand> hands;
    };

    std::string format;
    std::int64_t version = 0;
    std::string name;
    std::vector<double> canvas;
    std::string atlasFile;
    std::vector<std::int64_t> atlasSize;
    Landmarks landmarks;
    std::vector<Bone> bones;
    std::vector<Param> params;
    std::vector<Driver> drivers;
    std::vector<Chain> physics;
    std::vector<PartDriver> partDrivers;
    std::vector<Part> parts;

    // Throws CharacterError(Unreadable) for malformed JSON or a missing required key.
    static RigDocument decode(std::string_view text);
    static RigDocument decode(const json::Value& root);
};

// A 2D affine transform: x' = a x + b y + tx, y' = c x + d y + ty.
struct Affine2 {
    double a = 1, b = 0, c = 0, d = 1, tx = 0, ty = 0;

    // self · other (other applied first).
    Affine2 then(const Affine2& o) const {
        return {a * o.a + b * o.c, a * o.b + b * o.d, c * o.a + d * o.c, c * o.b + d * o.d,
                a * o.tx + b * o.ty + tx, c * o.tx + d * o.ty + ty};
    }
};

// A compiled rig (spec/CHARACTERS.md, Rig): parameters by index, bones parents-first, drivers per bone and channel,
// parts in draw order with their skinning data.
class Rig {
public:
    enum class Channel { Rotate, X, Y, ScaleX, ScaleY };

    struct Driver {
        int param = 0;
        Channel channel = Channel::Rotate;
        std::vector<double> xs, ys;
    };
    struct Opacity {
        int param = 0;
        std::vector<double> xs, ys;
    };
    struct Part {
        std::string name, tag;
        double order = 0;
        std::vector<double> vertices;  // rest positions, x y pairs in canvas pixels
        std::vector<double> uv;  // atlas pixel coordinates, x y pairs
        std::vector<std::uint32_t> triangles;
        int influences = 1;
        std::vector<std::uint16_t> bones;  // `influences` bone indices and weights per vertex
        std::vector<double> weights;
        std::vector<Opacity> opacity;

        int vertexCount() const { return static_cast<int>(vertices.size() / 2); }
    };

    // Throws CharacterError for an unsupported format or an inconsistent rig.
    explicit Rig(const RigDocument& doc);

    std::string name;
    double canvasWidth = 0, canvasHeight = 0;
    std::string atlasFile;
    int atlasWidth = 0, atlasHeight = 0;
    std::vector<Param> params;
    std::unordered_map<std::string, int> paramIndex;
    std::vector<std::string> boneNames;
    std::vector<int> parents;
    std::vector<std::pair<double, double>> pivots;
    std::vector<std::vector<Driver>> drivers;
    std::vector<Part> parts;
    std::vector<RigDocument::Chain> physics;
    RigDocument::Landmarks landmarks;

    int boneCount() const { return static_cast<int>(parents.size()); }
    std::vector<double> defaults() const;

    // World transform of every bone for parameter values in parameter order (clamped to their ranges).
    void worldTransforms(const std::vector<double>& values, std::vector<Affine2>& world) const;
    // Opacity of every part (the product of its drivers, 1 without any), in draw order.
    void opacities(const std::vector<double>& values, std::vector<double>& out) const;
    // Skinned positions of a part's vertices (x y pairs), as the GPU computes them.
    void skin(int part, const std::vector<Affine2>& world, std::vector<double>& out) const;

private:
    double clamped(const std::vector<double>& values, int i) const;
};

}  // namespace agentspeed::character
