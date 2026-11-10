#include "agentspeed/character/hud_models.hpp"

#include <algorithm>
#include <cmath>

namespace agentspeed::character {

namespace {

constexpr std::string_view kEllipsis = "…";

bool digits(std::string_view s) {
    return !s.empty() && std::all_of(s.begin(), s.end(), [](char c) { return c >= '0' && c <= '9'; });
}

int number(std::string_view s) {
    int n = 0;
    for (char c : s) n = n * 10 + (c - '0');
    return n;
}

bool isDate(std::string_view year, std::string_view month, std::string_view day) {
    if (!digits(year) || !digits(month) || !digits(day)) return false;
    const int y = number(year), m = number(month), d = number(day);
    return y >= 1900 && y <= 2999 && m >= 1 && m <= 12 && d >= 1 && d <= 31;
}

// Byte offsets of the code points of a UTF-8 string, and its size last.
std::vector<std::size_t> boundaries(std::string_view s) {
    std::vector<std::size_t> out;
    for (std::size_t i = 0; i < s.size(); ++i) {
        if ((static_cast<unsigned char>(s[i]) & 0xC0) != 0x80) out.push_back(i);
    }
    out.push_back(s.size());
    return out;
}

bool separator(char c) { return c == '-' || c == '_' || c == '.' || c == ' ' || c == '/' || c == ':'; }

double ramp(double time, double start, double duration) { return std::clamp((time - start) / duration, 0.0, 1.0); }

double smoothstep(double x) { return x * x * (3 - 2 * x); }

}  // namespace

std::string modelDisplayName(std::string_view id) {
    if (id.size() > 9 && id[id.size() - 9] == '-') {
        const auto date = id.substr(id.size() - 8);
        if (isDate(date.substr(0, 4), date.substr(4, 2), date.substr(6, 2))) return std::string(id.substr(0, id.size() - 9));
    }
    if (id.size() > 11 && id[id.size() - 11] == '-' && id[id.size() - 6] == '-' && id[id.size() - 3] == '-') {
        const auto date = id.substr(id.size() - 10);
        if (isDate(date.substr(0, 4), date.substr(5, 2), date.substr(8, 2))) return std::string(id.substr(0, id.size() - 11));
    }
    return std::string(id);
}

std::string cutAtEnd(std::string_view text, double width, const std::function<double(std::string_view)>& measure) {
    if (text.empty() || measure(text) <= width) return std::string(text);
    const auto cuts = boundaries(text);
    // The longest prefix of whole code points that fits with the ellipsis (measures grow with the prefix).
    std::size_t lo = 0, hi = cuts.size() - 2;
    std::string probe;
    while (lo < hi) {
        const std::size_t mid = (lo + hi + 1) / 2;
        probe.assign(text.substr(0, cuts[mid]));
        probe += kEllipsis;
        if (measure(probe) <= width) lo = mid;
        else hi = mid - 1;
    }
    std::size_t end = cuts[lo];
    while (end > 0 && separator(text[end - 1]) && end > 1) --end;
    std::string out(text.substr(0, end));
    out += kEllipsis;
    return out;
}

double ModelsMotion::Line::position(double time) const { return from + (to - from) * smoothstep(ramp(time, moveAt, kGlide)); }

double ModelsMotion::Line::alpha(double time) const {
    const double in = ramp(time, appearAt, kFade);
    return leaveAt ? std::min(in, 1 - ramp(time, *leaveAt, kFade)) : in;
}

bool ModelsMotion::Line::moving(double time) const {
    const auto during = [time](double start, double duration) { return time >= start && time < start + duration; };
    return during(moveAt, kGlide) || during(appearAt, kFade) || (leaveAt && during(*leaveAt, kFade));
}

void ModelsMotion::sync(const std::vector<ModelRow>& rows, int limit, double time, bool still) {
    lines_.erase(std::remove_if(lines_.begin(), lines_.end(), [time](const Line& l) { return l.leaveAt && time >= *l.leaveAt + kFade; }),
                 lines_.end());
    if (synced_ && rows == rows_ && limit == limit_) return;
    synced_ = true;
    rows_ = rows;
    limit_ = limit;
    const bool fresh = std::none_of(lines_.begin(), lines_.end(), [](const Line& l) { return !l.leaving(); });
    shown_ = std::min(static_cast<int>(rows.size()), std::max(0, limit));
    more_ = static_cast<int>(rows.size()) - shown_;
    for (auto& line : lines_) {
        if (!line.leaveAt) line.leaveAt = time;
    }
    const int last = more_ > 0 ? shown_ : shown_ - 1;
    for (int k = 0; k <= last; ++k) {
        const bool isRow = k < shown_;
        const auto place = static_cast<double>(k);
        auto it = std::find_if(lines_.begin(), lines_.end(), [&](const Line& l) {
            return isRow ? l.row && l.row->model == rows[static_cast<std::size_t>(k)].model : !l.row;
        });
        if (it == lines_.end()) {
            Line line;
            if (isRow) line.row = rows[static_cast<std::size_t>(k)];
            line.more = more_;
            line.from = line.to = place;
            line.appearAt = fresh ? -std::numeric_limits<double>::infinity() : time;
            lines_.push_back(std::move(line));
            continue;
        }
        if (it->leaveAt) {
            const double a = it->alpha(time);
            it->leaveAt.reset();
            it->appearAt = time - a * kFade;
        }
        if (isRow) it->row = rows[static_cast<std::size_t>(k)];
        it->more = more_;
        if (it->to != place) {
            it->from = still ? place : it->position(time);
            it->to = place;
            it->moveAt = time;
        }
    }
    count_ = shown_ + (more_ > 0 ? 1 : 0);
}

bool ModelsMotion::moving(double time) const {
    return std::any_of(lines_.begin(), lines_.end(), [time](const Line& l) { return l.moving(time); });
}

}  // namespace agentspeed::character
