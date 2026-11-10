#include "agentspeed/character/hud_quota.hpp"

#include <algorithm>
#include <cmath>

namespace agentspeed::character {

namespace {

constexpr double kNever = -std::numeric_limits<double>::infinity();
constexpr double kInf = std::numeric_limits<double>::infinity();

double ramp(double time, double start, double duration) { return std::clamp((time - start) / duration, 0.0, 1.0); }

bool during(double time, double start, double duration) { return time >= start && time < start + duration; }

QuotaMotion::Presence fadingIn(double time) { return {true, 0, time, QuotaMotion::kFade, QuotaMotion::kFade}; }

}  // namespace

QuotaLevel quotaLevel(int percent) {
    if (percent >= 95) return QuotaLevel::Critical;
    if (percent >= 80) return QuotaLevel::Warning;
    return QuotaLevel::Normal;
}

QuotaLineLayout quotaLineLayout(QuotaForm form, const QuotaColumns& c, const QuotaMetrics& m) {
    QuotaLineLayout l;
    l.label = hasHeaders(form) ? 0 : m.dot() + m.dotGap();
    l.bar = l.label + c.label + m.barGap();
    l.barLength = m.bar(form);
    l.percentRight = l.bar + l.barLength + m.barGap() + c.percent;
    l.reset = l.percentRight + m.resetGap();
    return l;
}

double quotaContentWidth(QuotaForm form, const QuotaColumns& c, const QuotaMetrics& m) {
    const auto l = quotaLineLayout(form, c, m);
    const double line = hasResets(form) && c.reset > 0 ? l.reset + c.reset : l.percentRight;
    return hasHeaders(form) ? std::max(line, c.header) : line;
}

QuotaWidths quotaWidths(const QuotaColumns& c, const QuotaMetrics& m, double padX, bool small) {
    QuotaWidths out{};
    for (int i = 0; i < kQuotaForms; ++i) {
        const auto form = static_cast<QuotaForm>(i);
        const bool unused = form == QuotaForm::Full && (small || c.reset <= 0);
        out[static_cast<std::size_t>(i)] = unused ? kInf : quotaContentWidth(form, c, m) + 2 * padX;
    }
    return out;
}

bool quotaHostOwnLine(QuotaForm form, double headerWithHost, double contentWidth) {
    return !hasHeaders(form) || headerWithHost > contentWidth + 0.5;
}

int FormFit::best(const QuotaWidths& widths, double room) {
    for (std::size_t i = 0; i < widths.size(); ++i) {
        if (widths[i] <= room + 0.5) return static_cast<int>(i);
    }
    return narrowest(widths);
}

int FormFit::narrowest(const QuotaWidths& widths) {
    std::size_t pick = 0;
    for (std::size_t i = 0; i < widths.size(); ++i) {
        if (widths[i] <= widths[pick]) pick = i;
    }
    return static_cast<int>(pick);
}

void FormFit::update(double dt, const QuotaWidths& widths, double room, double spare, bool snap) {
    const int fitting = best(widths, room);
    const auto at = [&](int i) { return widths[static_cast<std::size_t>(i)]; };
    if (!primed_ || snap) {
        form_ = fitting;
        ease_ = 0;
        primed_ = true;
        return;
    }
    if (at(form_) > room + 0.5) {
        form_ = fitting;
        ease_ = 0;
        return;
    }
    const int roomy = best(widths, room - spare);
    if (roomy < form_ && at(roomy) <= room - spare + 0.5) {
        ease_ += dt;
        if (ease_ >= kWidenAfter) {
            form_ = roomy;
            ease_ = 0;
        }
    } else {
        ease_ = 0;
    }
}

void QuotaFormEase::update(double dt, QuotaForm form, bool snap) {
    const double bar = form == QuotaForm::ShortBars || form == QuotaForm::Compact ? 3 : 5;
    const double resets = hasResets(form) ? 1 : 0;
    if (snap || !primed_) {
        bar_.snap(bar);
        resets_.snap(resets);
        primed_ = true;
        return;
    }
    bar_.update(dt, bar);
    resets_.update(dt, resets);
}

double QuotaMotion::Presence::value(double time) const {
    const double elapsed = time - at;
    return std::clamp(wanted ? from + elapsed / in : from - elapsed / out, 0.0, 1.0);
}

bool QuotaMotion::Presence::settled(double time) const { return wanted ? value(time) >= 1 : value(time) <= 0; }

void QuotaMotion::Presence::want(bool on, double time) {
    if (on == wanted) return;
    from = value(time);
    at = time;
    wanted = on;
}

double QuotaMotion::Line::bar(double time) const {
    const double k = ramp(time, barAt, barDuration);
    return barFrom + (barTo - barFrom) * (1 - std::pow(1 - k, 3));
}

double QuotaMotion::Line::percentMix(double time) const { return ramp(time, percentAt, kCrossfade); }

double QuotaMotion::Line::resetMix(double time) const { return ramp(time, resetAt, kCrossfade); }

bool QuotaMotion::Line::moving(double time) const {
    return !presence.settled(time) || !resetPresence.settled(time) || during(time, barAt, barDuration) ||
           during(time, percentAt, kCrossfade) || during(time, resetAt, kCrossfade);
}

void QuotaMotion::sync(const std::vector<QuotaSource>& sources, bool small, double time, bool still) {
    prune(time);
    if (synced_ && small == lastSmall_ && sources == last_) return;
    synced_ = true;
    lastSmall_ = small;
    last_ = sources;
    merge(sources, small ? std::min<std::size_t>(1, sources.size()) : sources.size(), time, still);
}

void QuotaMotion::clear() {
    sources_.clear();
    last_.clear();
    synced_ = false;
}

void QuotaMotion::merge(const std::vector<QuotaSource>& target, std::size_t count, double time, bool still) {
    const bool appearing = sources_.empty();
    std::vector<Source> merged;
    merged.reserve(count + sources_.size());
    std::vector<bool> kept(sources_.size(), false);
    for (std::size_t k = 0; k < count; ++k) {
        const auto& next = target[k];
        const auto it = std::find_if(sources_.begin(), sources_.end(), [&](const Source& s) { return s.key == next.key; });
        Source source;
        if (it != sources_.end()) {
            kept[static_cast<std::size_t>(it - sources_.begin())] = true;
            source = std::move(*it);
            source.presence.want(true, time);
        } else {
            source.key = next.key;
            if (!appearing) source.presence = fadingIn(time);
        }
        source.name = next.name;
        source.host = next.host;
        source.color = next.color;
        mergeLines(source.lines, next.lines, it == sources_.end(), time, still);
        merged.push_back(std::move(source));
    }
    for (std::size_t j = 0; j < sources_.size(); ++j) {
        if (kept[j]) continue;
        Source leaving = std::move(sources_[j]);
        leaving.presence.want(false, time);
        merged.insert(merged.begin() + static_cast<std::ptrdiff_t>(std::min(j, merged.size())), std::move(leaving));
    }
    sources_ = std::move(merged);
}

// A source's lines toward `next`, in its order: a new line's bar grows from empty (at once when still), and a line of a
// source that joins comes with it.
void QuotaMotion::mergeLines(std::vector<Line>& lines, const std::vector<QuotaLine>& next, bool appearing, double time, bool still) {
    std::vector<Line> merged;
    merged.reserve(next.size() + lines.size());
    std::vector<bool> kept(lines.size(), false);
    for (const auto& target : next) {
        const auto it = std::find_if(lines.begin(), lines.end(), [&](const Line& l) { return l.data.label == target.label; });
        if (it == lines.end()) {
            Line line;
            line.data = target;
            if (!appearing) line.presence = fadingIn(time);
            line.barTo = target.bar;
            line.barFrom = still ? target.bar : 0;
            line.barAt = still ? kNever : time;
            line.reset = target.reset;
            line.resetPresence.wanted = !target.reset.empty();
            line.resetPresence.from = target.reset.empty() ? 0 : 1;
            merged.push_back(std::move(line));
            continue;
        }
        kept[static_cast<std::size_t>(it - lines.begin())] = true;
        Line line = std::move(*it);
        if (std::fabs(target.bar - line.barTo) > 1e-9) {
            line.barDuration = target.passed && !line.data.passed ? kResetEmpty : kBarEase;
            line.barFrom = still ? target.bar : line.bar(time);
            line.barTo = target.bar;
            line.barAt = still ? kNever : time;
        }
        if (target.percent != line.data.percent) {
            line.oldPercent = line.data.percent;
            line.percentAt = time;
        }
        if (!target.reset.empty() && target.reset != line.reset) {
            const bool shown = !line.reset.empty() && line.resetPresence.value(time) > 0;
            line.oldReset = shown ? line.reset : std::string();
            line.reset = target.reset;
            line.resetAt = shown ? time : kNever;
        }
        line.resetPresence.want(!target.reset.empty(), time);
        line.presence.want(true, time);
        line.data = target;
        merged.push_back(std::move(line));
    }
    for (std::size_t j = 0; j < lines.size(); ++j) {
        if (kept[j]) continue;
        Line leaving = std::move(lines[j]);
        leaving.presence.want(false, time);
        merged.insert(merged.begin() + static_cast<std::ptrdiff_t>(std::min(j, merged.size())), std::move(leaving));
    }
    lines = std::move(merged);
}

void QuotaMotion::prune(double time) {
    const auto gone = [time](const Presence& p) { return !p.wanted && p.value(time) <= 0; };
    for (auto& source : sources_) {
        source.lines.erase(std::remove_if(source.lines.begin(), source.lines.end(), [&](const Line& l) { return gone(l.presence); }),
                           source.lines.end());
    }
    sources_.erase(std::remove_if(sources_.begin(), sources_.end(), [&](const Source& s) { return gone(s.presence); }), sources_.end());
}

bool QuotaMotion::moving(double time) const {
    return std::any_of(sources_.begin(), sources_.end(), [time](const Source& s) {
        return !s.presence.settled(time) || std::any_of(s.lines.begin(), s.lines.end(), [time](const Line& l) { return l.moving(time); });
    });
}

double quotaBlockHeight(const QuotaMotion::Source& source, QuotaForm form, bool hostOwnLine, const QuotaMetrics& m, double time) {
    double lines = 0;
    for (const auto& line : source.lines) lines += line.presence.value(time) * (m.row() + m.lineGap());
    const int rows = (hasHeaders(form) ? 1 : 0) + (hostOwnLine ? 1 : 0);
    return std::max(0.0, rows * (m.headerRow() + m.lineGap()) + lines - m.lineGap());
}

}  // namespace agentspeed::character
