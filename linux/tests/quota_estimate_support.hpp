#pragma once

#include <gtest/gtest.h>

#include <cmath>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/codex.hpp"
#include "agentspeed/quota/estimate/engine.hpp"
#include "agentspeed/report_json.hpp"
#include "fixture_support.hpp"

// spec/fixtures/quota-estimate-cases.json: the reference's inputs as the port's types, and a JSON comparison that
// treats numbers as numbers (Python writes `2` and `2.0` for the same value).
namespace estimate_fixture {

namespace est = agentspeed::quota::estimate;
using agentspeed::json::Value;

inline const fixtures::JsonFile& cases() {
    static const fixtures::JsonFile file(fixtures::fixtureDir() / "quota-estimate-cases.json");
    return file;
}

inline std::optional<double> optNumber(const Value& v) {
    if (!v.valid() || v.isNull()) return std::nullopt;
    return v.number();
}

inline double number(const Value& v) { return v.number().value_or(std::nan("")); }
inline std::int64_t integer(const Value& v) { return static_cast<std::int64_t>(std::llround(number(v))); }

inline est::Salt saltFromHex(const std::string& hex) {
    est::Salt out;
    for (std::size_t i = 0; i + 1 < hex.size(); i += 2) out.push_back(static_cast<unsigned char>(std::stoi(hex.substr(i, 2), nullptr, 16)));
    return out;
}

// Differences between two JSON values: numbers within a relative 1e-12.
inline void diff(const Value& got, const Value& want, const std::string& path, std::vector<std::string>& out) {
    using agentspeed::json::Type;
    if (got.isNumber() && want.isNumber()) {
        const double a = *got.number(), b = *want.number();
        if (std::fabs(a - b) > 1e-12 * std::max({1.0, std::fabs(a), std::fabs(b)})) out.push_back(path + ": " + std::string(got.raw()) + " != " + std::string(want.raw()));
        return;
    }
    if (got.type() != want.type()) {
        out.push_back(path + ": " + fixtures::compactJson(got) + " != " + fixtures::compactJson(want));
        return;
    }
    if (got.isArray()) {
        if (got.size() != want.size()) {
            out.push_back(path + ": " + std::to_string(got.size()) + " items != " + std::to_string(want.size()));
            return;
        }
        for (std::uint32_t i = 0; i < got.size(); ++i) diff(got.at(i), want.at(i), path + "[" + std::to_string(i) + "]", out);
        return;
    }
    if (got.isObject()) {
        const auto a = got.members(), b = want.members();
        if (a.size() != b.size()) out.push_back(path + ": " + std::to_string(a.size()) + " members != " + std::to_string(b.size()));
        for (const auto& [key, value] : b) {
            if (!got[key].valid()) {
                out.push_back(path + "." + key + ": missing");
                continue;
            }
            diff(got[key], value, path + "." + key, out);
        }
        return;
    }
    if (got.isString()) {
        if (*got.string() != *want.string()) out.push_back(path + ": \"" + *got.string() + "\" != \"" + *want.string() + "\"");
        return;
    }
}

// Expects `gotText` (JSON) to equal `want`.
inline void expectJson(const std::string& gotText, const Value& want, const std::string& what) {
    agentspeed::json::Document doc;
    ASSERT_TRUE(doc.parse(gotText)) << what << ": " << gotText;
    std::vector<std::string> diffs;
    diff(doc.root(), want, what, diffs);
    std::string all;
    for (std::size_t i = 0; i < diffs.size() && i < 20; ++i) all += diffs[i] + "\n";
    EXPECT_TRUE(diffs.empty()) << all;
}

inline agentspeed::report::Json optionalNumber(const std::optional<double>& v, int places) {
    if (!v || !std::isfinite(*v)) return agentspeed::report::Json::null();
    return agentspeed::report::Json::decimal(*v, places);
}

// The reference's fnum: three decimals.
inline agentspeed::report::Json fnum(const std::optional<double>& v) {
    if (!v || !std::isfinite(*v)) return agentspeed::report::Json::null();
    return agentspeed::report::Json::decimal(est::pyRound(*v, 3), 6);
}

inline agentspeed::report::Json optionalText(const std::optional<std::string>& v) {
    return v ? agentspeed::report::Json::of(*v) : agentspeed::report::Json::null();
}

inline std::optional<std::string> textOf(const Value& v) { return fixtures::optString(v); }

// The reference's obs(): {source, kind, name, minutes, p, t, tLo, R} (and a plan).
inline est::Observation observation(const Value& v, const std::string& account = "a") {
    est::Observation o;
    o.account = account;
    o.source = *v["source"].string();
    o.kind = *v["kind"].string();
    o.name = textOf(v["name"]);
    o.minutes = integer(v["minutes"]);
    o.p = number(v["p"]);
    o.t = integer(v["t"]);
    o.tLo = integer(v["tLo"]);
    o.R = integer(v["R"]);
    o.plan = textOf(v["plan"]);
    return o;
}

// The reference's ledger_from(): account `a`, source codex, model gpt-5.5 unless stated.
inline est::Ledger ledgerFrom(const Value& rows) {
    est::Ledger led;
    for (const auto& r : rows.elements()) {
        const auto cost = optNumber(r["cost"]);
        led.add(textOf(r["account"]).value_or("a"), textOf(r["source"]).value_or("codex"), textOf(r["model"]).value_or("gpt-5.5"), integer(r["at"]),
                cost ? std::optional<std::int64_t>(static_cast<std::int64_t>(*cost)) : std::nullopt, r["requests"].valid() ? integer(r["requests"]) : 1);
    }
    return led;
}

inline agentspeed::quota::Window windowOf(const Value& w) {
    agentspeed::quota::Window out;
    out.kind = agentspeed::quota::kindFromId(*w["kind"].string()).value_or(agentspeed::quota::Kind::Other);
    out.name = textOf(w["name"]);
    out.minutes = fixtures::optInt(w["minutes"]);
    out.usedPercent = number(w["usedPercent"]);
    out.resetsAtMs = fixtures::optInt(w["resetsAtMs"]);
    return out;
}

// One pipeline event (quota_estimate_pipeline.py Engine.add) given to the port's engine.
inline void addEvent(est::Engine& engine, const est::Salt& salt, const Value& e) {
    const auto type = *e["type"].string();
    if (type == "response") {
        est::Response r;
        r.source = *e["source"].string();
        r.file = textOf(e["file"]).value_or("");
        r.startMs = fixtures::optInt(e["startMs"]);
        r.endMs = integer(e["endMs"]);
        r.model = *e["model"].string();
        if (const auto cost = optNumber(e["costMicroUsd"])) r.cost = static_cast<std::int64_t>(*cost);
        r.tokens = e["tokens"].valid() ? integer(e["tokens"]) : 0;
        r.provider = textOf(e["provider"]);
        r.cloudRoute = textOf(e["cloudRoute"]).has_value();
        engine.add(std::move(r));
    } else if (type == "tokenCount") {
        est::CodexLine line;
        line.file = *e["file"].string();
        line.ts = integer(e["ts"]);
        line.windows = agentspeed::quota::codexWindows(e["rateLimits"], line.ts);
        line.plan = est::cleanPlan(e["rateLimits"]["plan_type"]);
        engine.add(std::move(line));
    } else if (type == "codexAuth") {
        engine.addCodexKey(integer(e["at"]), est::accountKey(salt, "codex", *e["accountId"].string()));
    } else if (type == "claudeProfile") {
        est::ClaudeProfile p;
        p.at = integer(e["at"]);
        const auto org = *e["org"].string();
        p.key = est::accountKey(salt, "claude", org + ":" + *e["account"].string());
        p.orgKey = est::accountKey(salt, "claudeDesktop", org);
        p.plan = est::cleanPlan(textOf(e["orgType"]));
        p.tier = est::cleanPlan(textOf(e["tier"]));
        engine.add(std::move(p));
    } else if (type == "snapshot") {
        est::SnapshotEvent s;
        s.source = *e["source"].string();
        s.observedMs = integer(e["observedMs"]);
        for (const auto& w : e["windows"].elements()) s.windows.push_back(windowOf(w));
        if (const auto org = textOf(e["org"])) s.orgKey = est::accountKey(salt, "claudeDesktop", *org);
        engine.add(std::move(s));
    } else {
        ADD_FAILURE() << "unknown event " << type;
    }
}

}  // namespace estimate_fixture
