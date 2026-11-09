#include "agentspeed/character/performer.hpp"

#include <algorithm>
#include <cmath>
#include <set>
#include <unordered_set>

namespace agentspeed::character {

namespace {

std::vector<Param> unionParams(const Pack& pack) {
    std::vector<Param> params;
    std::unordered_set<std::string> seen;
    for (const auto& pose : pack.poses) {
        for (const auto& p : pose.rig.params) {
            if (seen.insert(p.id).second) params.push_back(p);
        }
    }
    return params;
}

Side bookOf(const Rig& rig, Side fallback) {
    if (rig.landmarks.objectSide) {
        if (auto s = sideNamed(*rig.landmarks.objectSide)) return *s;
    }
    return fallback;
}

}  // namespace

Performer::Performer(std::shared_ptr<const Pack> pack, std::uint64_t seed, double orb)
    : pack_(std::move(pack)),
      director_(unionParams(*pack_), seed, other(bookOf(pack_->poses[0].rig, Side::Left)),
                bookOf(pack_->poses[0].rig, Side::Left), orb, false, pack_->manifest.motion.value_or(MotionProfile())),
      slots_(director_.space()) {
    const auto& space = director_.space();
    const auto& idle = pack_->poses[0].rig;
    values_.assign(static_cast<std::size_t>(space.count()), 0);
    std::vector<double> defaults;
    for (const auto& p : space.params()) defaults.push_back(p ? p->def : 0);
    for (const auto& pose : pack_->poses) {
        Physics p(pose.rig.physics, 1, [&space](const std::string& n) -> std::optional<int> {
            return space.isParam(n) ? space.slot(n) : std::nullopt;
        });
        p.reset(defaults);
        physics_.push_back(std::move(p));
        std::vector<int> map;
        for (const auto& param : pose.rig.params) map.push_back(*space.slot(param.id));
        slotMaps_.push_back(std::move(map));
    }
    const auto base = measures(idle);
    for (const auto& pose : pack_->poses) {
        const auto& feet = pose.rig.landmarks.feet;
        const double fx = feet && !feet->empty() ? (*feet)[0] : pose.rig.canvasWidth / 2;
        const double fy = feet && feet->size() > 1 ? (*feet)[1] : pose.rig.canvasHeight;
        placements_.push_back({poseScale(base, measures(pose.rig)), fx, fy});
    }
    double top = std::numeric_limits<double>::infinity();
    for (const auto& part : idle.parts) {
        for (std::size_t i = 1; i < part.vertices.size(); i += 2) top = std::min(top, part.vertices[i]);
    }
    const auto& feet = idle.landmarks.feet;
    const double feetY = feet && !feet->empty() ? feet->back() : idle.canvasHeight;
    idleHeight_ = std::isfinite(top) ? feetY - top : idle.canvasHeight;
    faceWidth_ = idle.landmarks.faceWidth.value_or(idle.canvasWidth / 10);
    events_.reserve(8);
    std::size_t most = 0;
    for (const auto& pose : pack_->poses) most = std::max(most, pose.rig.params.size());
    rigValues_.assign(most, 0);
}

std::array<std::optional<double>, 3> Performer::measures(const Rig& rig) {
    const auto& lm = rig.landmarks;
    if (!lm.face || lm.face->size() != 4) return {std::nullopt, std::nullopt, lm.torsoLength};
    const auto& f = *lm.face;
    std::optional<double> body;
    if (lm.feet && lm.feet->size() == 2) body = (*lm.feet)[1] - (f[1] + f[3]) / 2;
    return {body, std::hypot(f[2] - f[0], f[3] - f[1]), lm.torsoLength};
}

double Performer::poseScale(const std::array<std::optional<double>, 3>& base,
                            const std::array<std::optional<double>, 3>& pose) {
    std::vector<double> ratios;
    for (std::size_t i = 0; i < 3; ++i) {
        if (base[i] && pose[i] && *base[i] > 0 && *pose[i] > 0) ratios.push_back(*base[i] / *pose[i]);
    }
    if (ratios.size() == 3) {
        std::sort(ratios.begin(), ratios.end());
        return ratios[1];
    }
    if (base[0] && pose[0] && *base[0] > 0 && *pose[0] > 0) return *base[0] / *pose[0];
    return 1;
}

int Performer::poseIndex(Band band) const {
    int best = 0;
    for (std::size_t i = 0; i < pack_->poses.size(); ++i) {
        if (pack_->poses[i].band <= band) best = static_cast<int>(i);
    }
    return best;
}

std::pair<double, double> Performer::bodyOffset() const {
    return {values_[static_cast<std::size_t>(slots_.posX)] * kPosX * faceWidth_,
            -values_[static_cast<std::size_t>(slots_.posY)] * kPosY * faceWidth_};
}

void Performer::setCalm(bool calm) {
    calm_ = calm;
    director_.grounded = calm;
}

void Performer::applySides(int pose) {
    const Side book = bookOf(pack_->poses[static_cast<std::size_t>(pose)].rig, director_.book());
    director_.setSides(other(book), book, pack_->poses[static_cast<std::size_t>(pose)].band);
}

void Performer::enterEdgeOn(int level) {
    activePose_ = poseIndex(bandOf(level));
    const Side book = bookOf(activeRig(), director_.book());
    director_.setLevel(level);
    director_.setSides(other(book), book, pack_->poses[static_cast<std::size_t>(activePose_)].band);
    poseSwitch_.enterEdgeOn();
}

void Performer::enterAtRest(int level) {
    activePose_ = poseIndex(bandOf(level));
    const Side book = bookOf(activeRig(), director_.book());
    director_.setLevel(level);
    director_.setSides(other(book), book, pack_->poses[static_cast<std::size_t>(activePose_)].band);
}

HudLayout Performer::hudLayout(double feetX, double feetY, double ppp, double unit) const {
    const double h = idleHeight_ * ppp;
    const auto [dx, dy] = bodyOffset();
    const double fx = feetX + dx * ppp, fy = feetY;
    const double lift = dy * ppp;
    return HudLayout{{fx, fy + 6 * unit, 0.27 * h, 0.27 * h * 0.17}, {fx + 0.01 * h, fy - 0.6 * h + lift, 0.3 * h, 0.05 * h},
                     h, unit};
}

void Performer::damp() {
    const auto& space = director_.space();
    for (std::size_t i = 0; i < values_.size(); ++i) {
        const int slot = static_cast<int>(i);
        if (slot == slots_.breath || slot == slots_.eyeOpen[0] || slot == slots_.eyeOpen[1]) continue;
        const double rest = space.params()[i] ? space.params()[i]->def : 0;
        values_[i] = rest + (values_[i] - rest) * amplitude;
    }
}

void Performer::step(double dt, int level) {
    events_.clear();
    exitReached_ = false;
    director_.setLevel(level);
    director_.update(dt);
    for (auto e : director_.events) {
        if (e == DirectorEvent::Petals) events_.push_back({PerformerEvent::Kind::Petals});
    }
    director_.events.clear();
    std::copy(director_.values().begin(), director_.values().end(), values_.begin());
    if (amplitude < 1) damp();
    if (fadingPose_) {
        fadeProgress_ = std::min(1.0, fadeProgress_ + dt / kCrossfadeDuration);
        if (fadeProgress_ >= 1) fadingPose_.reset();
    }

    const auto band = director_.band();
    if ((calm_ || director_.profile().seated) && !pendingExit_ && band && poseIndex(*band) != activePose_ &&
        !poseSwitch_.isRunning()) {
        fadingPose_ = activePose_;
        fadingWorld_ = world_;
        fadingOpacities_ = opacities_;
        fadeProgress_ = 0;
        activePose_ = poseIndex(*band);
        applySides(activePose_);
        events_.push_back({PerformerEvent::Kind::PoseSwapped, pack_->poses[static_cast<std::size_t>(activePose_)].band});
    } else if (pendingExit_ && !poseSwitch_.isRunning()) {
        poseSwitch_.request(kExitTarget);
        pendingExit_ = false;
    } else if (!pendingExit_ && band) {
        const int wanted = poseIndex(*band);
        if (wanted != activePose_) poseSwitch_.request(wanted);
    }
    const auto sw = poseSwitch_.update(dt);
    if (sw.output) {
        const auto& o = *sw.output;
        values_[static_cast<std::size_t>(slots_.spin)] = o.spin;
        values_[static_cast<std::size_t>(slots_.squash)] += o.squash;
        values_[static_cast<std::size_t>(slots_.posY)] += o.posY;
        auto& flare = values_[static_cast<std::size_t>(slots_.skirtFlare)];
        flare = std::max(flare, o.skirtFlare);
    }
    if (sw.swapped && sw.swap) {
        const int target = *sw.swap;
        if (target == kExitTarget) {
            exitReached_ = true;
        } else {
            activePose_ = target;
            applySides(target);
            events_.push_back({PerformerEvent::Kind::PoseSwapped, pack_->poses[static_cast<std::size_t>(target)].band});
        }
    }
    for (std::size_t i = 0; i < physics_.size(); ++i) {
        physics_[i].step(dt, values_);
        if (static_cast<int>(i) == activePose_) physics_[i].write(values_);
    }
    const auto& map = slotMaps_[static_cast<std::size_t>(activePose_)];
    for (std::size_t i = 0; i < map.size(); ++i) rigValues_[i] = values_[static_cast<std::size_t>(map[i])];
    const Rig& rig = activeRig();
    rig.worldTransforms(rigValues_, world_);
    rig.opacities(rigValues_, opacities_);
}

HudAnchors Performer::trace(HudSilhouette& silhouette, double feetX, double feetY, double ppp,
                            std::vector<double>& scratch) const {
    static const std::set<std::string> headTags = {"face", "front hair", "back hair", "headwear", "ears"};
    silhouette.clear();
    const Rig& rig = activeRig();
    const PosePlacement& placement = activePlacement();
    const double k = ppp * placement.scale;
    const double ox = feetX - placement.feetX * k, oy = feetY - placement.feetY * k;
    double headTop = std::numeric_limits<double>::infinity();
    const double step = silhouette.step();
    for (std::size_t index = 0; index < rig.parts.size(); ++index) {
        if (index < opacities_.size() && !(opacities_[index] > 0.05)) continue;
        const auto& part = rig.parts[index];
        const auto n = static_cast<std::size_t>(part.vertexCount());
        const auto m = static_cast<std::size_t>(part.influences);
        if (scratch.size() < 2 * n) scratch.resize(2 * n, 0);
        const bool head = headTags.count(part.tag) > 0;
        for (std::size_t v = 0; v < n; ++v) {
            const double x = part.vertices[2 * v], y = part.vertices[2 * v + 1];
            double sx = 0, sy = 0;
            for (std::size_t j = 0; j < m; ++j) {
                const double w = part.weights[v * m + j];
                if (w == 0) continue;
                const auto& t = world_[part.bones[v * m + j]];
                sx += w * (t.a * x + t.b * y + t.tx);
                sy += w * (t.c * x + t.d * y + t.ty);
            }
            const double px = ox + sx * k, py = oy + sy * k;
            scratch[2 * v] = px;
            scratch[2 * v + 1] = py;
            silhouette.add(px, py);
            if (head && py < headTop) headTop = py;
        }
        const auto& tris = part.triangles;
        auto edge = [&](std::size_t a, std::size_t b) {
            const double y0 = scratch[2 * a + 1], y1 = scratch[2 * b + 1];
            if (!(std::fabs(y1 - y0) > step)) return;
            silhouette.add(scratch[2 * a], y0, scratch[2 * b], y1);
        };
        for (std::size_t t = 0; t + 2 < tris.size(); t += 3) {
            edge(tris[t], tris[t + 1]);
            edge(tris[t + 1], tris[t + 2]);
            edge(tris[t + 2], tris[t]);
        }
    }
    HudAnchors anchors = restAnchors(feetX, feetY, ppp);
    if (std::isfinite(headTop)) anchors.headTop = headTop;
    return anchors;
}

HudAnchors Performer::restAnchors(double feetX, double feetY, double ppp) const {
    const PosePlacement& placement = activePlacement();
    const auto& lm = activeRig().landmarks;
    const double dx = bodyOffset().first * ppp;
    const double h = idleHeight_ * ppp;
    if (!lm.face || lm.face->size() != 4) {
        return {feetX + dx, feetY - h, feetY - 0.68 * h, feetY - 0.78 * h};
    }
    const auto& face = *lm.face;
    const double chin = lm.chin.value_or(face[3]);
    const double waist = lm.waist && lm.waist->size() == 2 ? (*lm.waist)[1] : chin + 0.32 * idleHeight_ / placement.scale;
    const auto center = placement.screen((face[0] + face[2]) / 2, face[1], feetX, feetY, ppp);
    const double shoulders = placement.screen(0, chin + 0.25 * (waist - chin), feetX, feetY, ppp).second;
    const double chest = placement.screen(0, chin + 0.45 * (waist - chin), feetX, feetY, ppp).second;
    return {center.first + dx, center.second, chest, shoulders};
}

void Stage::switchTo(Performer next) {
    incoming_.emplace(std::move(next));
    current_.requestExit();
}

void Stage::step(double dt, int level) {
    swapped_ = false;
    if (current_.calm() != calm) current_.setCalm(calm);
    current_.amplitude = amplitude;
    if (outgoing_) {
        outgoing_->amplitude = amplitude;
        outgoing_->step(dt, level);
    }
    if (outgoing_) {
        outgoingFade_ = std::min(1.0, outgoingFade_ + dt / Performer::kCrossfadeDuration);
        if (outgoingFade_ >= 1) outgoing_.reset();
    }
    if (calm && incoming_) {
        Performer next = std::move(*incoming_);
        incoming_.reset();
        next.setCalm(true);
        next.enterAtRest(level);
        outgoing_.emplace(std::move(current_));
        outgoingFade_ = 0;
        current_ = std::move(next);
        swapped_ = true;
    }
    current_.step(dt, level);
    if (current_.exitReached() && incoming_) {
        Performer next = std::move(*incoming_);
        incoming_.reset();
        next.enterEdgeOn(level);
        next.step(dt, level);
        current_ = std::move(next);
        swapped_ = true;
    }
}

}  // namespace agentspeed::character
