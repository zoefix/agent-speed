#pragma once

#include <cstdint>
#include <functional>
#include <limits>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// The character orb's models card (DESIGN 2a.6, Models): its names, its rows and how its lines move. The card's place
// and width are HudWings'; text is the renderer's.
namespace agentspeed::character {

// The display name of a model id: the id without a trailing date, `-YYYYMMDD` or `-YYYY-MM-DD`
// (`claude-sonnet-4-5-20250929` → `claude-sonnet-4-5`).
std::string modelDisplayName(std::string_view id);

// `text` at most `width` wide by `measure`: whole when it fits, else cut at its end at a code point with `…`
// (`…` alone when nothing else fits).
std::string cutAtEnd(std::string_view text, double width, const std::function<double(std::string_view)>& measure);

// One row of the card: a running model (its full id), its speed as shown (nullopt: `–`, no timed response yet) and its
// vendor's accent dot (0xRRGGBB; nullopt: the secondary token).
struct ModelRow {
    std::string model;
    std::optional<std::int64_t> speed;
    std::optional<std::uint32_t> accent;
    bool operator==(const ModelRow&) const = default;
};

// At most 4 rows, then a fifth line `character.models.more` for the models not drawn; 2 rows below S = 96 (LOD).
constexpr int kModelRows = 4;
constexpr int kSmallModelRows = 2;

// The card's lines as they move: at most `limit` model rows, then the "+N" line while more models run. A row that
// changes place glides there (0.3 s, smoothstep; still: at once); a model that stops running fades out of its row and a
// new one fades in (0.25 s, also when still). Lines that arrive with the card itself show at once, since the card fades
// in. Times are seconds on one clock.
class ModelsMotion {
public:
    static constexpr double kGlide = 0.3;
    static constexpr double kFade = 0.25;

    struct Line {
        std::optional<ModelRow> row;  // nullopt: the "+N" line
        int more = 0;                 // the models not drawn (the "+N" line's N)
        double from = 0, to = 0;      // places in lines from the first
        double moveAt = -std::numeric_limits<double>::infinity();
        double appearAt = -std::numeric_limits<double>::infinity();
        std::optional<double> leaveAt;

        bool leaving() const { return leaveAt.has_value(); }
        double position(double time) const;
        double alpha(double time) const;
        bool moving(double time) const;
    };

    // Follows the card's rows, at most `limit` of them at a time.
    void sync(const std::vector<ModelRow>& rows, int limit, double time, bool still);
    // Every line drawn now, leaving ones included, in no particular order.
    const std::vector<Line>& lines() const { return lines_; }
    // The lines the card makes room for: the rows that stay and the "+N" line.
    int count() const { return count_; }
    // The rows the card shows, and the models not drawn.
    int shown() const { return shown_; }
    int more() const { return more_; }
    bool moving(double time) const;

private:
    std::vector<Line> lines_;
    std::vector<ModelRow> rows_;
    int limit_ = -1;
    int count_ = 0, shown_ = 0, more_ = 0;
    bool synced_ = false;
};

}  // namespace agentspeed::character
