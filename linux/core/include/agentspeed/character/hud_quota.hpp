#pragma once

#include <algorithm>
#include <array>
#include <cstdint>
#include <limits>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/character/springs.hpp"

// The character orb's 額度 card (DESIGN 2a.6, 額度): its lines, its forms and widths, and how its lines move. The
// card's place is HudWings'; text is the renderer's.
namespace agentspeed::character {

// The fill's tone: the source's color, amber from 80 %, red from 95 % (the percent then takes the same red).
enum class QuotaLevel { Normal, Warning, Critical };
QuotaLevel quotaLevel(int percent);

// One window line: its label (`5h`, `7d`, `Fable 7d`), the shown percent, the bar's fill (0…1) and the short reset
// (`42m`, `3h`, `週五`; empty while unknown or passed).
struct QuotaLine {
    std::string label;
    int percent = 0;
    double bar = 0;
    std::string reset;
    bool passed = false;
    bool operator==(const QuotaLine&) const = default;
};

// A source of the card: its key (the source, `<source>@<host id>` for a remote host's), its name (`source.*`), the
// host's name for a remote host's source (DESIGN 13: the header reads `<source> · <host>`), its color (0xRRGGBB) and
// only the windows its account has, in QUOTAS 2 order.
struct QuotaSource {
    std::string key;
    std::string name;
    std::string host;
    std::uint32_t color = 0;
    std::vector<QuotaLine> lines;
    bool operator==(const QuotaSource&) const = default;
};

// The card's forms, widest first (額度 Room): the reset cells go first, then the bars shorten to 3 t, then the header
// lines go and the dot moves before each source's first label.
enum class QuotaForm { Full = 0, NoResets = 1, ShortBars = 2, Compact = 3 };
constexpr int kQuotaForms = 4;
using QuotaWidths = std::array<double, kQuotaForms>;

inline bool hasResets(QuotaForm f) { return f == QuotaForm::Full; }
inline bool hasHeaders(QuotaForm f) { return f != QuotaForm::Compact; }

// The card's sizes for the cards' text size `t`, in points.
struct QuotaMetrics {
    double t = 8;
    double small() const { return 0.9 * t; }  // labels, names, hosts and resets
    double dot() const { return 0.55 * t; }
    double dotGap() const { return 0.35 * t; }
    double barGap() const { return 0.5 * t; }  // label to bar, bar to the percent cell
    double bar(QuotaForm f) const { return (f == QuotaForm::ShortBars || f == QuotaForm::Compact ? 3 : 5) * t; }
    double barHeight() const { return 0.26 * t; }
    double resetGap() const { return 0.4 * t; }
    double lineGap() const { return 0.25 * t; }
    double sourceGap() const { return 0.45 * t; }
    double row() const { return 0.92 * t; }
    double headerRow() const { return 0.92 * small(); }
};

// The card's columns over every source and line it draws, in points: the widest label, the `100%` cell, the widest
// short reset (0 while no line has one) and the widest header (the dot, its gap and the name).
struct QuotaColumns {
    double label = 0, percent = 0, reset = 0, header = 0;
};

// Where a window line's parts are in a form, from the content's left edge: the label's left, the bar's left and length,
// the percent cell's right edge and the reset's left.
struct QuotaLineLayout {
    double label = 0, bar = 0, barLength = 0, percentRight = 0, reset = 0;
};
QuotaLineLayout quotaLineLayout(QuotaForm form, const QuotaColumns& c, const QuotaMetrics& m);
// The content width of a form; the headers count, a host never widens the card.
double quotaContentWidth(QuotaForm form, const QuotaColumns& c, const QuotaMetrics& m);
// The plate width of every form, widest first. A form the card does not use (reset cells below S = 96, or while no
// window has a reset time) is infinite, so it never fits.
QuotaWidths quotaWidths(const QuotaColumns& c, const QuotaMetrics& m, double padX, bool small);
// The host follows the name while `headerWithHost` (dot, gap, name and ` · host`) fits the form's content width, else it
// takes a line of its own (always in the compact form, which has no header).
bool quotaHostOwnLine(QuotaForm form, double headerWithHost, double contentWidth);

// Which form fits the room (額度 Room): the first, in the forms' order, that fits with half a point of slack, else the
// narrowest. It narrows at once and widens only after the wider form has fit with room to spare for 1.2 s, so the card
// does not change form with every sway of her hair.
class FormFit {
public:
    static constexpr double kWidenAfter = 1.2;

    static int best(const QuotaWidths& widths, double room);
    // The narrowest form, the later one of equal widths.
    static int narrowest(const QuotaWidths& widths);
    // One frame. `snap` takes the best form at once (the card is not on screen).
    void update(double dt, const QuotaWidths& widths, double room, double spare, bool snap);
    QuotaForm form() const { return static_cast<QuotaForm>(form_); }

private:
    int form_ = 0;
    double ease_ = 0;
    bool primed_ = false;
};

// The parts that change with the form follow the plate as it eases to its new width (3.2 Hz, critically damped, as
// the plates do): the bars' length, 3…5 t, and the reset cells' share, 0…1. `snap` takes the form at once (the card is
// off screen, or effects are Off).
class QuotaFormEase {
public:
    void update(double dt, QuotaForm form, bool snap);
    double bar() const { return bar_.value(); }
    double resets() const { return std::clamp(resets_.value(), 0.0, 1.0); }

private:
    SecondOrder bar_{3.2, 1, 0, 5}, resets_{3.2, 1, 0, 1};
    bool primed_ = false;
};

// The card's lines as they move (額度 Motion): a bar eases to its new length (0.4 s, ease-out) and a reset empties it
// over 0.6 s while its reset cell fades out; a changed percent or reset crossfades (0.2 s); a source or window joining
// or leaving fades (0.25 s) as the card eases to its height. `still` (effects Off) changes lengths at once; the fades
// stay. The sources of a card that appears come with it, their bars growing from empty; sources and lines that leave
// keep their place and text while they fade. Times are seconds on one clock.
class QuotaMotion {
public:
    static constexpr double kFade = 0.25;
    static constexpr double kBarEase = 0.4;
    static constexpr double kResetEmpty = 0.6;
    static constexpr double kCrossfade = 0.2;

    // A share that rises over `in` and falls over `out` seconds, from where it was when it last changed direction.
    struct Presence {
        bool wanted = true;
        double from = 1;
        double at = -std::numeric_limits<double>::infinity();
        double in = kFade, out = kFade;
        double value(double time) const;
        bool settled(double time) const;
        void want(bool on, double time);
    };

    struct Line {
        QuotaLine data;  // the latest data, kept while the line leaves
        Presence presence;
        double barFrom = 0, barTo = 0;
        double barAt = -std::numeric_limits<double>::infinity();
        double barDuration = kBarEase;
        std::optional<int> oldPercent;  // the percent before the latest change, crossfading out
        double percentAt = -std::numeric_limits<double>::infinity();
        std::string reset;     // the reset drawn, kept while its cell fades out
        std::string oldReset;  // the reset it replaced, crossfading out
        double resetAt = -std::numeric_limits<double>::infinity();
        Presence resetPresence{true, 1, -std::numeric_limits<double>::infinity(), kCrossfade, kResetEmpty};

        double bar(double time) const;
        // The new percent's and reset's shares of their crossfades; the old ones show at 1 − this.
        double percentMix(double time) const;
        double resetMix(double time) const;
        bool moving(double time) const;
    };

    struct Source {
        std::string key, name, host;
        std::uint32_t color = 0;
        Presence presence;
        std::vector<Line> lines;
        bool leaving() const { return !presence.wanted; }
    };

    // Follows the card's sources, only the first when `small` (below S = 96).
    void sync(const std::vector<QuotaSource>& sources, bool small, double time, bool still);
    // The card has faded out: its next sources appear with it.
    void clear();
    // Every source drawn now, leaving ones included, in the card's order.
    const std::vector<Source>& sources() const { return sources_; }
    bool empty() const { return sources_.empty(); }
    bool moving(double time) const;

private:
    void merge(const std::vector<QuotaSource>& target, std::size_t count, double time, bool still);
    static void mergeLines(std::vector<Line>& lines, const std::vector<QuotaLine>& next, bool appearing, double time, bool still);
    void prune(double time);

    std::vector<Source> sources_;
    std::vector<QuotaSource> last_;
    bool lastSmall_ = false;
    bool synced_ = false;
};

// A source's height in a form at `time`: its header rows (the name; the host on a line of its own counts one more) and
// its lines, each weighted by its presence.
double quotaBlockHeight(const QuotaMotion::Source& source, QuotaForm form, bool hostOwnLine, const QuotaMetrics& m, double time);

// The card's content height at `time`: every source's block and the gap after it, weighted by the source's presence;
// `hostOwnLine(source)` tells whether a source's host takes a line of its own.
template <class HostOwnLine>
double quotaContentHeight(const QuotaMotion& motion, QuotaForm form, const QuotaMetrics& m, double time, HostOwnLine&& hostOwnLine) {
    double h = 0;
    for (const auto& s : motion.sources()) {
        h += s.presence.value(time) * (quotaBlockHeight(s, form, hostOwnLine(s), m, time) + m.sourceGap());
    }
    return h > m.sourceGap() ? h - m.sourceGap() : 0;
}

}  // namespace agentspeed::character
