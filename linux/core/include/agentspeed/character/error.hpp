#pragma once

#include <stdexcept>
#include <string>

namespace agentspeed::character {

// Why a rig or a pack cannot be played (macOS CharacterRigError, PackLoadError).
class CharacterError : public std::runtime_error {
public:
    enum class Kind {
        Unsupported,
        BadBone,
        ParentsNotFirst,
        BadDriver,
        BadPart,
        BadChain,
        BadID,
        UnsafePath,
        MissingIdlePose,
        Unreadable,
    };

    CharacterError(Kind kind, std::string detail)
        : std::runtime_error(detail), kind_(kind), detail_(std::move(detail)) {}

    Kind kind() const { return kind_; }
    const std::string& detail() const { return detail_; }

private:
    Kind kind_;
    std::string detail_;
};

}  // namespace agentspeed::character
