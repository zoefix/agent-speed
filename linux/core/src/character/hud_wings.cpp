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
    for (std::size_t i = 0; i < static_cast<std::size_t>(kHudCardCount); ++i) {
        if ((presence_[i] > 0 && presence_[i] < 1) || hop_[i] < 1) return true;
    }
    return false;
}

void HudWings::update(double dt, double time, const HudFrame& f, const std::array<CardSize, kHudCardCount>& given,
                      const std::array<bool, kHudCardCount>& shownIn) {
    constexpr auto kModels = static_cast<std::size_t>(HudCard::Models);
    auto shown = shownIn;
    shown[kModels] = shown[kModels] && models.h > 0;
    auto wanted = given;
    if (!placed_) form_ = ModelsForm{true, kModelsNames * models.t};
    wanted[kModels] = {form_.width(models), models.h};
    for (std::size_t i = 0; i < static_cast<std::size_t>(kHudCardCount); ++i) {
        const bool on = shown[i] && (i != kModels || modelsPlace_ != ModelsPlace::Hidden);
        const double target = on ? 1.0 : 0.0;
        const double rate = dt / (target > presence_[i] ? kFadeIn : kFadeOut);
        presence_[i] = target > presence_[i] ? std::min(target, presence_[i] + rate) : std::max(target, presence_[i] - rate);
        if (!placed_) presence_[i] = target;
        if (!placed_ || presence_[i] == 0 || still) {
            growth_[i].first.snap(wanted[i].w);
            growth_[i].second.snap(wanted[i].h);
        }
        sizes_[i] = {growth_[i].first.update(dt, wanted[i].w), growth_[i].second.update(dt, wanted[i].h)};
    }
    targets_[0] = speedAnchor(f, sizes_[0]);
    targets_[1] = modelAnchor(f, sizes_[1]);
    const ModelsForm before = form_;
    chooseModels(dt, f, shown[kModels]);
    if (!placed_ && modelsPlace_ == ModelsPlace::Hidden) presence_[kModels] = 0;
    if (form_ != before && (!placed_ || presence_[kModels] == 0 || still)) {
        growth_[kModels].first.snap(form_.width(models));
        sizes_[kModels].w = form_.width(models);
    }
    columnAnchors(dt, f, wanted, shown);
    modelsAnchor(f);

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

// The column: cards stacked from her shoulders down at her left, their right edges on one line clear of her outline;
// cards from `split_` on stack under the speed at her right. The split is the most even one that fits (`HudBalance`),
// chosen again when a card appears or leaves or a side's weight changes, once the new split has held for `kSpillAfter`
// and no sooner than `changeWait()` after the last change. When the split overflows for `kSpillAfter`, the most even
// one that fits is taken, else the spill-over's, no sooner than that either (the cards hug her outline meanwhile); a
// better one comes back once it has fitted with room to spare for `kReturnAfter` (`kSettledReturn` within
// `kSettleWindow` of the last change). A card whose side changed fades out where it was, jumps, and fades back in.
void HudWings::columnAnchors(double dt, const HudFrame& f, const std::array<CardSize, kHudCardCount>& wanted,
                             const std::array<bool, kHudCardCount>& shown) {
    const int before = split_;
    sinceChange_ += dt;
    const double gap = kColumnGap * f.footprint;
    weighed_ = HudBalance::heights(wanted, shown, modelsPlace_ != ModelsPlace::Hidden);
    const bool moving = balance_.moving(weighed_);
    bool waiting = false;
    if (!placed_ || still || balance_.balanced != balances) {
        choose(f, weighed_);
        strain_ = 0;
        ease_ = 0;
    } else if (fit(f, split_, 0, kReach, true).over > 0.5) {
        ease_ = 0;
        balance_.pending.reset();
        strain_ += dt;
        if (strain_ >= kSpillAfter && sinceChange_ >= changeWait()) {
            choose(f, weighed_);
            strain_ = 0;
        } else if (strain_ >= kSpillAfter) {
            // A card changes sides at most once per `changeWait()`; until then the cards hug her outline where they are.
            waiting = true;
        }
    } else if (balances && balance_.isStale(weighed_, split_, gap, HudBalance::kThreshold * f.footprint)) {
        strain_ = 0;
        ease_ = 0;
        rebalance(dt, f, weighed_, gap, moving);
    } else if (auto back = better(f)) {
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
    const bool aligned = !hugging_ && !waiting;
    const Fit chosen = fit(f, split_, 0, kReach, aligned);
    layout(f, split_, chosen.shift, aligned, 0, true);
    for (std::size_t k = 0; k < kHudColumn.size(); ++k) {
        const auto i = static_cast<std::size_t>(index(kHudColumn[k]));
        const bool wantedRight = static_cast<int>(k) >= split_;
        if (wantedRight == right_[i]) {
            hop_[i] = still ? 1 : std::min(1.0, hop_[i] + dt / kHopIn);
        } else if (presence_[i] == 0 || still || !placed_) {
            right_[i] = wantedRight;
            hop_[i] = 1;
            anchors_[i].first.snap(targets_[i].first);
            anchors_[i].second.snap(targets_[i].second);
        } else {
            hop_[i] = std::max(0.0, hop_[i] - dt / kHopOut);
            if (hop_[i] == 0) {
                right_[i] = wantedRight;
                anchors_[i].first.snap(targets_[i].first);
                anchors_[i].second.snap(targets_[i].second);
            }
        }
    }
    if (placed_ && split_ != before) {
        balance_.lastGap = sinceChange_;
        sinceChange_ = 0;
    }
}

// How long after the last change the split may change again, whether balance or spill-over moves the cards.
double HudWings::changeWait() const { return balance_.lastGap < kSettleWindow ? kSettledReturn : kReturnAfter; }

// Chooses the split at once: the splits ranked by weight (or the most cards at her left first, without balance), the
// first that fits near her shoulders, else the spill-over's.
void HudWings::choose(const HudFrame& f, const HudHeights& heights) {
    const double gap = kColumnGap * f.footprint;
    if (balance_.balanced != balances || balance_.order.empty() || balance_.isStale(heights, split_, gap, 0)) {
        balance_.order = balances ? HudBalance::ranking(heights, gap) : HudBalance::byCount();
        balance_.balanced = balances;
        balance_.settle(heights);
    }
    if (const auto first = firstFitting(f, balance_.order.count, 0)) {
        split_ = *first;
        hugging_ = false;
    } else {
        const Best b = best(f);
        split_ = b.split;
        hugging_ = b.hugging;
    }
}

// The cards or their weights changed: the most even split that fits is taken once it has held for `kSpillAfter`, and
// no sooner than `changeWait()` after the last change. When none fits, or it is the current one, the split stays. The
// heights become the new reference only once no card grows or shrinks any more, so a card still easing to its size is
// weighed in full.
void HudWings::rebalance(double dt, const HudFrame& f, const HudHeights& heights, double gap, bool moving) {
    balance_.order = HudBalance::ranking(heights, gap);
    const auto next = firstFitting(f, balance_.order.count, 0);
    if (!next || *next == split_) {
        if (moving) balance_.pending.reset();
        else balance_.settle(heights);
        return;
    }
    if (balance_.pending != next) {
        balance_.pending = next;
        balance_.held = 0;
    }
    balance_.held += dt;
    if (balance_.held < kSpillAfter || sinceChange_ < changeWait()) return;
    split_ = *next;
    hugging_ = false;
    balance_.pending.reset();
    if (!moving) balance_.settle(heights);
}

// The first of the order's first `count` splits whose column fits near her shoulders with its right edges on one line;
// with `room`, with that much to spare.
std::optional<int> HudWings::firstFitting(const HudFrame& f, std::size_t count, double room) {
    for (std::size_t i = 0; i < count; ++i) {
        const int s = balance_.order.splits[i];
        const double over = fit(f, s, room, kNearReach, true).over;
        if (room > 0 ? over == 0 : over <= 0.5) return s;
    }
    return std::nullopt;
}

// A split ranked before the current one that fits near her shoulders with room to spare (the first such); nullopt when
// none does. Without balance: more cards at her left.
std::optional<int> HudWings::better(const HudFrame& f) {
    const std::size_t rank = balance_.order.rank(split_);
    if (rank == 0) return std::nullopt;
    return firstFitting(f, rank, 0.04 * f.footprint);
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
    std::array<double, kHudColumn.size()> edges{};
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
        const double right = aligned && std::isfinite(line) ? line : edges[k];
        over += presence_[i] * std::max(0.0, minX - (right - fitting(i) - room));
        if (!write) continue;
        targets_[i].first = std::max(right - w / 2, minX + w / 2);
        if (kHudColumn[k] == HudCard::Quota) quotaRoom_ = right - minX;
    }

    if (split >= kHudColumn.size()) return over;
    y = speedBottom() + gap;
    // Under the models card while it shows there, and while it fades out from there.
    if ((modelsPlace_ == ModelsPlace::Hidden ? modelsWas_ : modelsPlace_) == ModelsPlace::UnderSpeed) {
        constexpr auto m = static_cast<std::size_t>(HudCard::Models);
        y += presence_[m] * (sizes_[m].h + gap);
    }
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
    // Under the models card, never left of its edge: the right side reads as one column.
    constexpr auto m = static_cast<std::size_t>(HudCard::Models);
    if (modelsPlace_ == ModelsPlace::UnderSpeed && sizes_[m].h > 0 && presence_[m] > 0.01) {
        left = std::max(left, std::min(modelsEdge(f, speedBottom() + gap, sizes_[m].h), maxX - sizes_[m].w));
    }
    for (std::size_t k = split; k < kHudColumn.size(); ++k) {
        const auto i = static_cast<std::size_t>(index(kHudColumn[k]));
        const double w = sizes_[i].w + room;
        over += presence_[i] * std::max(0.0, left + fitting(i) + room - maxX);
        if (!write) continue;
        targets_[i].first = std::min(left + w / 2, maxX - w / 2);
        if (kHudColumn[k] == HudCard::Quota) quotaRoom_ = maxX - left;
    }
    return over;
}

// The width a card needs to fit: the 額度 card narrows itself to its room, so only its narrowest form counts.
double HudWings::fitting(std::size_t card) const {
    const double w = sizes_[card].w;
    return card == static_cast<std::size_t>(HudCard::Quota) && quotaNarrowest > 0 ? std::min(w, quotaNarrowest) : w;
}

double HudWings::speedBottom() const { return targets_[0].second + sizes_[0].h / 2; }

double HudWings::spillBottom(const HudFrame& f) const {
    const double gap = kColumnGap * f.footprint;
    double y = speedBottom() + gap;
    for (std::size_t k = static_cast<std::size_t>(std::max(0, split_)); k < kHudColumn.size(); ++k) {
        const auto i = static_cast<std::size_t>(index(kHudColumn[k]));
        y += presence_[i] * (sizes_[i].h + gap);
    }
    return y;
}

// The widest form that fits `room`: the names 9 t wide, else as wide as fits down to 4 t, else the dots and speeds.
std::optional<ModelsForm> HudWings::formFor(double room) const {
    const auto& s = models;
    constexpr double kSlack = 1e-9;
    if (std::max(s.fixed + std::min(s.names, kModelsNames * s.t), s.floor) <= room + kSlack) return ModelsForm{true, kModelsNames * s.t};
    if (std::max(s.fixed + std::min(s.names, kModelsNamesMin * s.t), s.floor) <= room + kSlack) {
        return ModelsForm{true, std::clamp(room - s.fixed, kModelsNamesMin * s.t, kModelsNames * s.t)};
    }
    if (s.dots <= room + kSlack) return ModelsForm{false, 0};
    return std::nullopt;
}

// Its left edge on the speed's line, or further right where her outline is wider under the speed, `kGap` clear of it.
double HudWings::modelsEdge(const HudFrame& f, double top, double h) const {
    const double line = targets_[0].first - sizes_[0].w / 2;
    const auto e = f.silhouette.extent(top, top + h);
    return e ? std::max(line, e->right + kGap * f.footprint) : line;
}

HudWings::ModelsFit HudWings::modelsFit(const HudFrame& f, double top) const {
    const double d = f.footprint;
    const double h = models.h;
    ModelsFit out{modelsEdge(f, top, h), std::nullopt, std::nullopt};
    if (top + h > f.bounds.maxY - kMargin * d + 0.5) return out;
    const double room = f.bounds.maxX - kMargin * d - out.left;
    out.form = formFor(room);
    out.spare = formFor(room - kModelsSpare * d);
    return out;
}

// The first top from the spill-over cards' room down to 0.3 D below it where the card fits (with room to spare).
std::optional<double> HudWings::lowerTop(const HudFrame& f, bool spare) const {
    const double start = spillBottom(f);
    const double step = std::max(1.0, f.silhouette.step());
    for (double top = start; top <= start + kModelsReach * f.footprint + 1e-9; top += step) {
        const auto fit = modelsFit(f, top);
        if (spare ? fit.spare.has_value() : fit.form.has_value()) return top;
    }
    return std::nullopt;
}

// The models card's place, with the column's patience (DESIGN 2a.6, *Place and width*): it moves down or hides once
// its place has not fit for 0.6 s, comes back after it has fit with 0.04 D to spare for 1.2 s and moves back up after
// 4 s; between its two places it fades out and back in. Its names narrow at once to fit and widen again after 1.2 s
// with room to spare.
void HudWings::chooseModels(double dt, const HudFrame& f, bool shown) {
    constexpr auto i = static_cast<std::size_t>(HudCard::Models);
    // Switched off or quiet and gone: nothing to place until it shows again, and then its best place at once.
    if (!shown && presence_[i] == 0) {
        modelsFresh_ = true;
        modelsHop_.reset();
        hop_[i] = 1;
        modelsStrain_ = 0;
        modelsEase_ = 0;
        return;
    }
    const double under = speedBottom() + kColumnGap * f.footprint;
    const auto atUnder = modelsFit(f, under);
    // The lower place keeps its top while the card fits there, below the spilled cards.
    const bool keepsLower = lowerTop_ >= spillBottom(f) - 0.5 && modelsFit(f, lowerTop_).form.has_value();
    const auto lower = keepsLower ? std::optional<double>(lowerTop_) : lowerTop(f, false);
    const auto go = [&](ModelsPlace place) {
        modelsStrain_ = 0;
        modelsEase_ = 0;
        if (place == ModelsPlace::Lower && lower) lowerTop_ = *lower;
        if (place != ModelsPlace::Hidden && modelsPlace_ != ModelsPlace::Hidden && presence_[i] > 0) {
            modelsHop_ = place;
            return;
        }
        snapModels_ = snapModels_ || presence_[i] == 0;
        modelsPlace_ = place;
    };
    const bool atOnce = !placed_ || still || modelsFresh_;
    modelsFresh_ = false;
    if (modelsHop_) {
        hop_[i] = std::max(0.0, hop_[i] - dt / kHopOut);
        if (hop_[i] == 0 || atOnce) {
            modelsPlace_ = *modelsHop_;
            modelsHop_.reset();
            snapModels_ = true;
        }
    } else {
        hop_[i] = std::min(1.0, hop_[i] + dt / kHopIn);
    }
    if (atOnce) {
        hop_[i] = 1;
        modelsHop_.reset();
        const ModelsPlace best = atUnder.form ? ModelsPlace::UnderSpeed : lower ? ModelsPlace::Lower : ModelsPlace::Hidden;
        snapModels_ = snapModels_ || best != modelsPlace_ || presence_[i] == 0;
        modelsPlace_ = best;
        if (best == ModelsPlace::Lower) lowerTop_ = *lower;
        modelsStrain_ = 0;
        modelsEase_ = 0;
    } else if (!shown || modelsHop_) {
        modelsStrain_ = 0;
        modelsEase_ = 0;
    } else if (modelsPlace_ == ModelsPlace::UnderSpeed) {
        modelsEase_ = 0;
        modelsStrain_ = atUnder.form ? 0 : modelsStrain_ + dt;
        if (modelsStrain_ >= kSpillAfter) go(lower ? ModelsPlace::Lower : ModelsPlace::Hidden);
    } else if (modelsPlace_ == ModelsPlace::Lower) {
        modelsEase_ = atUnder.spare ? modelsEase_ + dt : 0;
        modelsStrain_ = lower ? 0 : modelsStrain_ + dt;
        if (modelsEase_ >= kReturnAfter) go(ModelsPlace::UnderSpeed);
        else if (modelsStrain_ >= kSpillAfter) go(atUnder.form ? ModelsPlace::UnderSpeed : ModelsPlace::Hidden);
    } else {
        modelsStrain_ = 0;
        const bool lowerSpare = !atUnder.spare && lowerTop(f, true).has_value();
        modelsEase_ = atUnder.spare || lowerSpare ? modelsEase_ + dt : 0;
        if (modelsEase_ >= kModelsBack) go(atUnder.spare ? ModelsPlace::UnderSpeed : ModelsPlace::Lower);
    }

    // The form at its place: narrower at once, wider again once the wider one has had room to spare for 1.2 s.
    if (modelsPlace_ == ModelsPlace::Hidden) return;
    modelsWas_ = modelsPlace_;
    if (modelsPlace_ == ModelsPlace::Lower && lower) lowerTop_ = *lower;
    const auto at = modelsPlace_ == ModelsPlace::UnderSpeed ? atUnder : modelsFit(f, lowerTop_);
    const ModelsForm target = at.form.value_or(ModelsForm{false, 0});
    const auto rank = [](const ModelsForm& form) { return form.names ? form.nameWidth : -1.0; };
    if (atOnce || presence_[i] == 0 || snapModels_) {
        form_ = target;
        modelsWiden_ = 0;
    } else if (rank(target) < rank(form_) - 1e-9) {
        form_ = target;
        modelsWiden_ = 0;
    } else if (at.spare && rank(*at.spare) > rank(form_) + 1e-9) {
        modelsWiden_ += dt;
        if (modelsWiden_ >= kModelsBack) {
            form_ = *at.spare;
            modelsWiden_ = 0;
        }
    } else {
        modelsWiden_ = 0;
    }
}

// The models card's target: under the speed or at its lower place; where it was while it hides.
void HudWings::modelsAnchor(const HudFrame& f) {
    constexpr auto i = static_cast<std::size_t>(HudCard::Models);
    const double d = f.footprint;
    const double w = sizes_[i].w, h = sizes_[i].h;
    if (modelsPlace_ != ModelsPlace::Hidden) {
        double top = speedBottom() + kColumnGap * d;
        if (modelsPlace_ == ModelsPlace::Lower) {
            if (lowerTop_ < spillBottom(f) - 0.5 || !modelsFit(f, lowerTop_).form) {
                if (const auto lower = lowerTop(f, false)) lowerTop_ = *lower;
            }
            top = lowerTop_;
        }
        const double left = modelsFit(f, top).left;
        targets_[i] = {std::max(std::min(left + w / 2, f.bounds.maxX - kMargin * d - w / 2), f.bounds.minX + kMargin * d + w / 2),
                       top + h / 2};
    }
    if (snapModels_) {
        anchors_[i].first.snap(targets_[i].first);
        anchors_[i].second.snap(targets_[i].second);
        snapModels_ = false;
    }
}

}  // namespace agentspeed::character
