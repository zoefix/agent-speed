#pragma once

#include <array>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// Which character a `character` orb shows and which cards float beside her (DESIGN 2b `character`,
// `characterReadouts`; CHARACTERS 7). Plain data, shared by the orb model, the menus and the renderer.
namespace agentspeed::character {

// Pack ids (CHARACTERS 1): lowercase letters, digits and dashes, 1–64 characters, not starting or ending with a dash.
bool isValidCharacterID(std::string_view id);
// A trimmed valid pack id, else nullopt (= Auto).
std::optional<std::string> cleanCharacterID(std::string_view id);

// One switch of `CharacterReadouts`, in the menu's and editor's order.
enum class CharacterReadout { Intelligence, Thinking, Latency, Network, Cost, Agents, Models, Quota, Model };

constexpr std::array<CharacterReadout, 9> kCharacterReadouts = {
    CharacterReadout::Intelligence, CharacterReadout::Thinking, CharacterReadout::Latency,
    CharacterReadout::Network,      CharacterReadout::Cost,     CharacterReadout::Agents,
    CharacterReadout::Models,       CharacterReadout::Quota,    CharacterReadout::Model};

// The JSON key (`intelligence`, `thinking`, `latency`, `network`, `cost`, `agents`, `models`, `quota`, `model`).
const char* key(CharacterReadout r);
// `character.readouts.intel`, `.thinking`, `.latency`, `.network`, `.cost`, `.agents`, `.models`, `.quota`, `.model`.
const char* titleKey(CharacterReadout r);

// The cards a `character` orb floats beside her: the speed always shows; each card (and the 性能 card's 首字 row) has
// its own switch. Every one is on for a new orb, and for one saved before it existed, except the fastest model's name.
struct CharacterReadouts {
    bool model = false;
    bool intelligence = true;
    bool thinking = true;
    bool latency = true;
    bool network = true;
    bool cost = true;
    bool agents = true;
    bool models = true;
    bool quota = true;

    bool operator==(const CharacterReadouts&) const = default;
    bool isDefault() const { return *this == CharacterReadouts{}; }
    bool get(CharacterReadout r) const;
    void set(CharacterReadout r, bool on);
};

// Which character a `character` orb shows (CHARACTERS 7). Locked to an installed pack it shows that one; on Auto it
// follows the fastest live model vendor that has an installed pack, holding a change for 3 s so two close vendors
// never flicker, and keeps the last one when no live vendor has a pack.
class CharacterSelection {
public:
    static constexpr double kHold = 3;

    // An installed pack and the vendors it stands for, in catalog order.
    struct Pack {
        std::string id;
        std::vector<std::string> vendors;
        bool operator==(const Pack&) const = default;
    };

    explicit CharacterSelection(std::optional<std::string> shown = std::nullopt) : shown_(std::move(shown)) {}

    // The character to show now. `vendorRates` are the summed live output rates per vendor.
    std::optional<std::string> update(const std::optional<std::string>& locked,
                                      const std::map<std::string, double>& vendorRates, const std::vector<Pack>& installed,
                                      double now);
    const std::optional<std::string>& shown() const { return shown_; }

    // The pack of the fastest vendor that has one; ties go to the vendor name in order, so the choice is stable.
    static std::optional<std::string> fastest(const std::map<std::string, double>& vendorRates,
                                              const std::vector<Pack>& installed);

private:
    std::optional<std::string> shown_;
    std::optional<std::string> candidate_;
    double candidateSince_ = 0;
};

// The footprint of the `character` style (DESIGN 2a.6): a disc D = 2 S, the window 1.5 D, and clicks on an upright
// capsule around her body (0.42 D wide, 0.9 D tall, centered 0.04 D below the disc's center).
namespace footprint {
constexpr double kCharacterWidth = 0.42;
constexpr double kCharacterHeight = 0.9;
constexpr double kCharacterDrop = 0.04;

// The disc a style occupies, × the orb size S.
inline double scale(std::string_view style) { return style == "character" ? 2.0 : 1.0; }
// Whether a point relative to the disc center (screen coordinates, y down) is on her capsule, with `tolerance`.
bool characterContains(double dx, double dy, double diameter, double tolerance = 0);
struct Box {
    double x, y, width, height;
};
// The capsule's bounding box relative to the disc center (y down).
Box characterBounds(double diameter);
}  // namespace footprint

}  // namespace agentspeed::character
