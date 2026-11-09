#include "agentspeed/character/motion.hpp"

#include <algorithm>
#include <cmath>

namespace agentspeed::character {

const std::vector<CurveKey>* CurveSet::find(const std::string& name) const {
    for (std::size_t i = 0; i < names_.size(); ++i) {
        if (names_[i] == name) return &keys_[i];
    }
    return nullptr;
}

void CurveSet::assign(const std::string& name, std::vector<CurveKey> keys) {
    for (std::size_t i = 0; i < names_.size(); ++i) {
        if (names_[i] == name) {
            keys_[i] = std::move(keys);
            return;
        }
    }
    names_.push_back(name);
    keys_.push_back(std::move(keys));
}

void CurveSet::set(const std::string& name, const KeyList& values) {
    std::vector<CurveKey> k;
    k.reserve(values.size());
    for (const auto& [t, v] : values) k.push_back({t, v});
    assign(name, std::move(k));
}

MotionSpec::MotionSpec(std::string n, double d, CurveSet c, bool l, double f, std::optional<std::string> e, bool tk,
                       std::vector<MotionEvent> ev, double tr)
    : name(std::move(n)), duration(d), curves(std::move(c)), loop(l), fade(f), expression(std::move(e)), talk(tk),
      events(std::move(ev)), travel(tr) {
    std::stable_sort(events.begin(), events.end(), [](const MotionEvent& a, const MotionEvent& b) {
        return a.time != b.time ? a.time < b.time : a.name < b.name;
    });
}

Motion MotionSpec::bind(const std::function<std::optional<int>(const std::string&)>& slot) const {
    Motion m;
    m.name = name;
    m.duration = duration;
    m.loop = loop;
    m.fade = fade;
    m.expression = expression;
    m.talk = talk;
    m.events = events;
    m.travel = travel;
    const auto& n = curves.names();
    const auto& k = curves.keys();
    m.entries.reserve(n.size());
    for (std::size_t i = 0; i < n.size(); ++i) {
        auto s = slot(n[i]);
        if (!s) continue;
        m.entries.push_back({*s, n[i], Curve(k[i], loop)});
    }
    return m;
}

namespace keys {

KeyList loop(double period, const std::vector<double>& values) {
    const auto n = static_cast<double>(values.size());
    KeyList out;
    for (std::size_t i = 0; i < values.size(); ++i) out.emplace_back(period * static_cast<double>(i) / n, values[i]);
    out.emplace_back(period, values[0]);
    return out;
}

KeyList oscillate(double start, double end, double period, double amplitude, double base, double phase) {
    KeyList out;
    double t = start;
    int k = 0;
    const double step = period / 4;
    while (t < end - 1e-6) {
        out.emplace_back(t, base + amplitude * std::sin(phase + static_cast<double>(k) * math::kPi / 2));
        t += step;
        k += 1;
    }
    out.emplace_back(end, base);
    return out;
}

KeyList ballistic(double start, double air, double height, int samples) {
    const auto n = static_cast<double>(samples);
    KeyList out;
    for (int k = 0; k <= samples; ++k) {
        const auto kd = static_cast<double>(k);
        out.emplace_back(start + air * kd / n, height * 4 * (kd / n) * (1 - kd / n));
    }
    return out;
}

std::vector<CurveKey> shift(const std::vector<CurveKey>& keys, double dt) {
    if (dt <= 0) return keys;
    std::vector<CurveKey> out;
    out.reserve(keys.size() + 1);
    out.push_back({0, keys[0].v});
    for (const auto& k : keys) out.push_back({k.t + dt, k.v});
    return out;
}

KeyList cat(std::initializer_list<KeyList> parts) {
    KeyList out;
    for (const auto& p : parts) out.insert(out.end(), p.begin(), p.end());
    return out;
}

}  // namespace keys
}  // namespace agentspeed::character
