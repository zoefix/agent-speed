#pragma once

#include <filesystem>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/character/motion_profile.hpp"
#include "agentspeed/character/rhythm.hpp"
#include "agentspeed/character/rig.hpp"

namespace agentspeed::character {

// `manifest.json` of a character pack as the player reads it (spec/CHARACTERS.md, Character packs).
struct PackManifest {
    static constexpr std::string_view kFormat = "agent-speed-character-pack";
    static constexpr int kVersion = 1;

    struct Artist {
        std::string name;
        std::optional<std::string> url;
        bool operator==(const Artist&) const = default;
    };

    std::string format;
    std::int64_t version = 0;
    std::string id;
    std::int64_t packVersion = 0;
    std::map<std::string, std::string> name;  // display names by app language
    std::vector<std::string> vendors;
    std::string accent;
    std::optional<Artist> artist;
    std::optional<std::string> license;
    std::optional<std::string> minApp;
    std::map<std::string, std::string> poses;  // band → folder inside the pack
    std::string preview;
    std::optional<MotionProfile> motion;

    // Throws CharacterError(Unreadable) for malformed JSON or a missing required key.
    static PackManifest decode(std::string_view text);
    // Whether an app of `version` (dotted numbers) can play the pack.
    bool playable(const std::string& version) const;
};

// One band's illustration: its rig and the atlas image the renderer loads.
struct Pose {
    Band band;
    Rig rig;
    std::filesystem::path atlas;
};

// An unpacked character pack: its manifest and one rig per band present (idle is required).
struct Pack {
    std::filesystem::path root;
    PackManifest manifest;
    std::vector<Pose> poses;  // present bands in band order; the first is idle

    const std::string& id() const { return manifest.id; }
    std::filesystem::path preview() const { return root / manifest.preview; }

    // Loads and validates a pack folder: known format, a safe id, relative paths that stay inside the folder (no
    // `..`, no absolute paths, no symbolic links leading out), and rigs the player understands. Throws CharacterError.
    static std::shared_ptr<const Pack> load(const std::filesystem::path& root);

    static bool isSafeID(const std::string& id);
    // `relative` inside `root`, refusing absolute paths, `..`, empty components and links that lead outside.
    static std::filesystem::path resolve(const std::string& relative, const std::filesystem::path& root);
};

}  // namespace agentspeed::character
