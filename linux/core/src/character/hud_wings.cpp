#include "agentspeed/character/hud_wings.hpp"

#include <algorithm>
#include <cmath>

#include "agentspeed/character/numeric.hpp"

namespace agentspeed::character {

namespace {

constexpr double kDodge = 0.06;
constexpr double kNearReach = 0.12;
constexpr double kReach = 0.3;
constexpr std::array<double, 11> kShifts = {0, -0.04, 0.04, -0.08, 0.08, -0.12, 0.12, 0.17, -0.17, 0.23, 0.3};
constexpr double kInf = std::numeric_limits<double>::infinity();

double wave(double t, double period, double phase) { return std::sin(2 * math::kPi * t / period + phase); }

}  // namespace

HudSilhouette::HudSilhouette(double top, double bottom, double step) : top_(top), step_(std::max(step, 1.0)) {
    const auto count = static_cast<std::size_t>(std::max(1.0, std::ceil((bottom - top) / step_)));
    left_.assign(count, kInf);
    right_.assign(count, -kInf);
}

void HudSilhouette::clear() {
    std::fill(left_.begin(), left_.end(), kInf);
    std::fill(right_.begin(), right_.end(), -kInf);
}

void HudSilhouette::add(double x, double y) {
    const double band = std::floor((y - top_) / step_);
    if (!(band >= 0) || band >= static_cast<double>(left_.size())) return;
    const auto i = static_cast<std::size_t>(band);
    if (x < left_[i]) left_[i] = x;
    if (x > right_[i]) right_[i] = x;
}

void HudSilhouette::add(double x0, double y0, double x1, double y1) {
    const int n = std::max(1, static_cast<int>(std::ceil(std::fabs(y1 - y0) / step_)));
    for (int k = 0; k <= n; ++k) {
        const double t = static_cast<double>(k) / n;
        add(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t);
    }
}

std::optional<HudSilhouette::Extent> HudSilhouette::extent(double y0, double y1) const {
    const double fa = std::floor((std::min(y0, y1) - top_) / step_);
    const double fb = std::floor((std::max(y0, y1) - top_) / step_);
    const auto last = static_cast<double>(left_.size()) - 1;
    const double a = std::max(0.0, fa), b = std::min(last, fb);
    if (!(a <= b)) return std::nullopt;
    double l = kInf, r = -kInf;
    for (auto i = static_cast<std::size_t>(a); i <= static_cast<std::size_t>(b); ++i) {
        l = std::min(l, left_[i]);
        r = std::max(r, right_[i]);
    }
    if (!(l <= r)) return std::nullopt;
    return Extent{l, r};
}

HudWings::HudWings() {
    for (auto& a : anchors_) a = {SecondOrder(0.9, 0.78, 0), SecondOrder(0.9, 0.78, 0)};
    for (auto& g : growth_) g = {SecondOrder(3.2, 1, 0), SecondOrder(3.2, 1, 0)};
    hop_.fill(1);
}

bool HudWings::isFading() const {
    for (std::size_t i = 0; i < 6; ++i) {
        if ((presence_[i] > 0 && presence_[i] < 1) || hop_[i] < 1) return true;
    }
    return false;
}

void HudWings::update(double dt, double time, const HudFrame& f, const std::array<CardSize, 6>& given,
                      const std::array<bool, 6>& shown) {
    for (std::size_t i = 0; i < 6; ++i) {
        const double target = shown[i] ? 1.0 : 0.0;
        const double rate = dt / (target > presence_[i] ? kFadeIn : kFadeOut);
        presence_[i] = target > presence_[i] ? std::min(target, presence_[i] + rate) : std::max(target, presence_[i] - rate);
        if (!placed_) presence_[i] = target;
        if (!placed_ || presence_[i] == 0 || still) {
            growth_[i].first.snap(given[i].w);
            growth_[i].second.snap(given[i].h);
        }
        sizes_[i] = {growth_[i].first.update(dt, given[i].w), growth_[i].second.update(dt, given[i].h)};
    }
    targets_[0] = speedAnchor(f, sizes_[0]);
    targets_[1] = modelAnchor(f, sizes_[1]);
    columnAnchors(dt, f);

    const double d = f.footprint;
    for (HudCard card : kHudCards) {
        const auto i = static_cast<std::size_t>(index(card));
        if (!placed_ || (presence_[i] == 0 && !shown[i])) {
            anchors_[i].first.snap(targets_[i].first);
            anchors_[i].second.snap(targets_[i].second);
        }
        double ax = anchors_[i].first.update(dt, targets_[i].first);
        double ay = anchors_[i].second.update(dt, targets_[i].second);
        if (card != HudCard::Model && placed_ && intrudes(card, ax, ay, f)) {
            const double k = 1 - std::exp(-dt / kDodge);
            ax += (targets_[i].first - ax) * k;
            ay += (targets_[i].second - ay) * k;
            anchors_[i].first.snap(ax);
            anchors_[i].second.snap(ay);
        }
        const double shownShare = presence_[i] * presence_[i] * (3 - 2 * presence_[i]);
        const double hopShare = hop_[i] * hop_[i] * (3 - 2 * hop_[i]);
        const double e = shownShare * hopShare;
        Place place{ax, ay + (1 - shownShare) * 0.02 * d, 0.96 + 0.04 * e, 0, e};
        if (!still) {
            const Float fl = floatOf(card, time);
            place.x += d * fl.dx;
            place.y += d * fl.dy;
            place.spin = fl.spin;
            place.scale *= fl.scale;
        }
        places_[i] = place;
    }
    placed_ = true;
}

bool HudWings::intrudes(HudCard card, double x, double y, const HudFrame& f) const {
    const auto [w, h] = sizes_[static_cast<std::size_t>(index(card))];
    if (!(w > 0)) return false;
    const auto e = f.silhouette.extent(y - h / 2, y + h / 2);
    if (!e) return false;
    const double room = 0.5 * kGap * f.footprint;
    return isAtHerRight(card) ? x - w / 2 < e->right + room : x + w / 2 > e->left - room;
}

HudWings::Float HudWings::floatOf(HudCard card, double t) {
    const auto k = static_cast<double>(index(card));
    const double phase = 1.7 * k + 0.4;
    const bool speed = card == HudCard::Speed;
    const double ax = speed ? 0.008 : 0.013, ay = speed ? 0.011 : 0.017;
    const double x1 = 0.65 * wave(t, 7.3 + 0.9 * k, phase);
    const double x2 = 0.35 * wave(t, 3.1 + 0.4 * k, 2.1 * phase);
    const double y1 = 0.7 * wave(t, 5.9 + 0.7 * k, 1.3 * phase);
    const double y2 = 0.3 * wave(t, 2.6 + 0.3 * k, 0.6 * phase);
    const double tilt = speed ? 0.6 : 1.0;
    const double s1 = 0.032 * wave(t, 11.0 + k, phase);
    const double s2 = 0.014 * wave(t, 4.7 + 0.5 * k, 2 * phase);
    const double breath = 0.012 * wave(t, 6.1 + 0.6 * k, 0.8 * phase);
    return {ax * (x1 + x2), ay * (y1 + y2), tilt * (s1 + s2), 1 + breath};
}

std::pair<double, double> HudWings::speedAnchor(const HudFrame& f, CardSize size) const {
    const double d = f.footprint;
    struct At {
        double x, y, over;
    };
    auto at = [&](double shift) {
        const double y = f.chest + shift * d;
        const auto e = f.silhouette.extent(y - size.h / 2, y + size.h / 2);
        const double side = e ? e->right : f.centerX;
        const double x = std::max(side + kGap * d, f.centerX + 0.04 * d) + size.w / 2;
        return At{x, y, std::max(0.0, x + size.w / 2 - (f.bounds.maxX - kMargin * d))};
    };
    At bestAt = at(0);
    for (std::size_t i = 1; i < kShifts.size(); ++i) {
        if (!(bestAt.over > 0.5)) continue;
        const At candidate = at(kShifts[i]);
        if (candidate.over < bestAt.over - 0.5) bestAt = candidate;
    }
    const double x = std::min(bestAt.x, f.bounds.maxX - kMargin * d - size.w / 2);
    const double y = std::min(std::max(bestAt.y, f.bounds.minY + kMargin * d + size.h / 2),
                              f.bounds.maxY - kMargin * d - size.h / 2);
    return {x, y};
}

std::pair<double, double> HudWings::modelAnchor(const HudFrame& f, CardSize size) const {
    const double d = f.footprint;
    double bottom = f.headTop - kHeadGap * d;
    const double x = std::min(std::max(f.centerX, f.bounds.minX + kMargin * d + size.w / 2),
                              f.bounds.maxX - kMargin * d - size.w / 2);
    auto overlaps = [&](double y0, double y1) {
        const auto e = f.silhouette.extent(y0, y1);
        return e && e->right > x - size.w / 2 && e->left < x + size.w / 2;
    };
    if (overlaps(bottom - size.h, bottom)) {
        double y = bottom - size.h;
        while (y > f.bounds.minY && overlaps(y - size.h, y)) y -= f.silhouette.step();
        bottom = y - kGap * d * 0.5;
    }
    const double y = std::max(bottom - size.h / 2, f.bounds.minY + kMargin * d + size.h / 2);
    return {x, y};
}

void HudWings::columnAnchors(double dt, const HudFrame& f) {
    const int before = split_;
    sinceChange_ += dt;
    if (!placed_ || still) {
        const Best b = best(f);
        split_ = b.split;
        hugging_ = b.hugging;
        strain_ = 0;
        ease_ = 0;
    } else if (fit(f, split_, 0, kReach, true).over > 0.5) {
        ease_ = 0;
        strain_ += dt;
        if (strain_ >= kSpillAfter) {
            const Best b = best(f);
            split_ = b.split;
            hugging_ = b.hugging;
            strain_ = 0;
        }
    } else if (auto back = roomier(f)) {
        strain_ = 0;
        ease_ += dt;
        if (ease_ >= (sinceChange_ < kSettleWindow ? kSettledReturn : kReturnAfter)) {
            split_ = *back;
            hugging_ = false;
            ease_ = 0;
        }
    } else {
        strain_ = 0;
        ease_ = 0;
        hugging_ = false;
    }
    const Fit chosen = fit(f, split_, 0, kReach, !hugging_);
    layout(f, split_, chosen.shift, !hugging_, 0, true);
    for (std::size_t k = 0; k < kHudColumn.size(); ++k) {
        const auto i = static_cast<std::size_t>(index(kHudColumn[k]));
        const bool wanted = static_cast<int>(k) >= split_;
        if (wanted == right_[i]) {
            hop_[i] = still ? 1 : std::min(1.0, hop_[i] + dt / kHopIn);
        } else if (presence_[i] == 0 || still || !placed_) {
            right_[i] = wanted;
            hop_[i] = 1;
            anchors_[i].first.snap(targets_[i].first);
            anchors_[i].second.snap(targets_[i].second);
        } else {
            hop_[i] = std::max(0.0, hop_[i] - dt / kHopOut);
            if (hop_[i] == 0) {
                right_[i] = wanted;
                anchors_[i].first.snap(targets_[i].first);
                anchors_[i].second.snap(targets_[i].second);
            }
        }
    }
    if (placed_ && split_ != before) sinceChange_ = 0;
}

std::optional<int> HudWings::roomier(const HudFrame& f) {
    const int count = static_cast<int>(kHudColumn.size());
    if (split_ >= count) return std::nullopt;
    for (int k = count; k > split_; --k) {
        if (fit(f, k, 0.04 * f.footprint, kNearReach, true).over == 0) return k;
    }
    return std::nullopt;
}

HudWings::Best HudWings::best(const HudFrame& f) {
    const int count = static_cast<int>(kHudColumn.size());
    for (double reach : {kNearReach, kReach}) {
        for (int s = count; s >= 0; --s) {
            if (fit(f, s, 0, reach, true).over <= 0.5) return {s, false};
        }
    }
    int bestSplit = count;
    double bestOver = kInf;
    for (int s = count; s >= 0; --s) {
        const double over = fit(f, s, 0, kReach, false).over;
        if (over < bestOver - 0.5) {
            bestSplit = s;
            bestOver = over;
        }
    }
    return {bestSplit, true};
}

HudWings::Fit HudWings::fit(const HudFrame& f, int s, double room, double reach, bool aligned) {
    Fit bestFit{0, kInf};
    for (double shift : kShifts) {
        if (!(std::fabs(shift) <= reach + 1e-9)) continue;
        const double over = layout(f, s, shift, aligned, room, false);
        if (over < bestFit.over - 0.5) bestFit = {shift, over};
        if (bestFit.over <= 0.5) break;
    }
    return bestFit;
}

double HudWings::layout(const HudFrame& f, int s, double shift, bool aligned, double room, bool write) {
    const double d = f.footprint;
    const double gap = kColumnGap * d;
    const double minX = f.bounds.minX + kMargin * d, maxX = f.bounds.maxX - kMargin * d;
    const double maxY = f.bounds.maxY - kMargin * d;
    double over = 0;
    const auto split = static_cast<std::size_t>(s);

    double total = -gap;
    for (std::size_t k = 0; k < split; ++k) {
        const auto i = static_cast<std::size_t>(index(kHudColumn[k]));
        total += presence_[i] * (sizes_[i].h + gap);
    }
    double top = f.shoulders + shift * d;
    top = std::min(std::max(top, f.bounds.minY + kMargin * d), maxY - std::max(0.0, total));
    double line = kInf;
    double y = top;
    std::array<double, 4> edges{};
    for (std::size_t k = 0; k < split; ++k) {
        const auto i = static_cast<std::size_t>(index(kHudColumn[k]));
        const double h = sizes_[i].h;
        const auto e = f.silhouette.extent(y, y + h);
        const double side = e ? e->left : f.centerX;
        const double edge = std::min(side - kGap * d, f.centerX - 0.04 * d);
        edges[k] = edge;
        if (write) targets_[i].second = y + h / 2;
        if (presence_[i] > 0.01) line = std::min(line, edge);
        y += presence_[i] * (h + gap);
    }
    for (std::size_t k = 0; k < split; ++k) {
        const auto i = static_cast<std::size_t>(index(kHudColumn[k]));
        const double w = sizes_[i].w + room;
        const double x = (aligned && std::isfinite(line) ? line : edges[k]) - w / 2;
        over += presence_[i] * std::max(0.0, minX - (x - w / 2));
        if (write) targets_[i].first = std::max(x, minX + w / 2);
    }

    if (split >= kHudColumn.size()) return over;
    y = targets_[0].second + sizes_[0].h / 2 + gap;
    double left = -kInf;
    for (std::size_t k = split; k < kHudColumn.size(); ++k) {
        const auto i = static_cast<std::size_t>(index(kHudColumn[k]));
        const double h = sizes_[i].h;
        const auto e = f.silhouette.extent(y, y + h);
        const double side = e ? e->right : f.centerX;
        if (presence_[i] > 0.01) left = std::max(left, std::max(side + kGap * d, f.centerX + 0.04 * d));
        if (write) targets_[i].second = y + h / 2;
        over += presence_[i] * std::max(0.0, y + h - maxY);
        y += presence_[i] * (h + gap);
    }
    if (!std::isfinite(left)) left = f.centerX + 0.04 * d;
    for (std::size_t k = split; k < kHudColumn.size(); ++k) {
        const auto i = static_cast<std::size_t>(index(kHudColumn[k]));
        const double w = sizes_[i].w + room;
        over += presence_[i] * std::max(0.0, left + w - maxX);
        if (write) targets_[i].first = std::min(left + w / 2, maxX - w / 2);
    }
    return over;
}

}  // namespace agentspeed::character
