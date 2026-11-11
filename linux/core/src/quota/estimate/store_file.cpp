#include "agentspeed/quota/estimate/store_file.hpp"

#include <openssl/rand.h>
#include <string.h>

#include <cmath>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/files.hpp"
#include "agentspeed/report_json.hpp"

namespace agentspeed::quota::estimate {

namespace {

constexpr std::size_t kStoreLimit = 32u << 20;

void text(std::string& out, const std::string& s) { out += report::Json::quoted(s); }

void optionalText(std::string& out, const std::optional<std::string>& s) {
    if (s) {
        text(out, *s);
    } else {
        out += "null";
    }
}

void member(std::string& out, std::string_view key, bool first = false) {
    if (!first) out += ", ";
    out += '"';
    out += key;
    out += "\": ";
}

void observationJson(std::string& out, const Observation& o) {
    out += '{';
    member(out, "account", true);
    text(out, o.account);
    member(out, "source");
    text(out, o.source);
    member(out, "kind");
    text(out, o.kind);
    member(out, "name");
    optionalText(out, o.name);
    member(out, "minutes");
    out += std::to_string(o.minutes);
    member(out, "t");
    out += std::to_string(o.t);
    member(out, "tLo");
    out += std::to_string(o.tLo);
    member(out, "p");
    out += floatText(o.p);
    member(out, "R");
    out += std::to_string(o.R);
    member(out, "plan");
    optionalText(out, o.plan);
    member(out, "tier");
    optionalText(out, o.tier);
    if (o.machine != kLocal) {
        member(out, "machine");
        text(out, o.machine);
        member(out, "hostKey");
        optionalText(out, o.hostKey);
    }
    out += '}';
}

void classJson(std::string& out, const CapClass& cls) {
    out += '[';
    if (cls.byPlan) {
        out += "\"plan\", ";
        text(out, cls.id);
        out += ", ";
        optionalText(out, cls.tier);
    } else {
        out += "\"account\", ";
        text(out, cls.id);
    }
    out += ", ";
    text(out, cls.window);
    out += ']';
}

void instanceJson(std::string& out, const StoredInstance& s) {
    out += '{';
    member(out, "class", true);
    classJson(out, s.cls);
    member(out, "account");
    text(out, s.account);
    member(out, "R");
    out += std::to_string(s.R);
    member(out, "lo");
    out += floatText(s.lo);
    member(out, "hi");
    out += s.hi ? floatText(*s.hi) : std::string("null");
    member(out, "pMax");
    out += floatText(s.pMax);
    member(out, "tLast");
    out += std::to_string(s.tLast);
    out += '}';
}

void keyJson(std::string& out, const StoredKey& k) {
    out += '{';
    member(out, "source", true);
    text(out, k.source);
    member(out, "at");
    out += std::to_string(k.entry.at);
    member(out, "key");
    text(out, k.entry.key);
    member(out, "plan");
    optionalText(out, k.entry.plan);
    member(out, "tier");
    optionalText(out, k.entry.tier);
    if (k.orgKey) {
        member(out, "orgKey");
        text(out, *k.orgKey);
    }
    out += '}';
}

template <typename T, typename F>
void list(std::string& out, std::string_view key, const std::vector<T>& items, F write) {
    member(out, key);
    out += '[';
    for (std::size_t i = 0; i < items.size(); ++i) {
        if (i) out += ", ";
        write(out, items[i]);
    }
    out += ']';
}

std::optional<std::string> optionalString(const json::Value& v) {
    if (!v.valid() || v.isNull()) return std::nullopt;
    return v.string();
}

std::optional<std::int64_t> integer(const json::Value& v) {
    const auto n = v.finiteNumber();
    if (!n) return std::nullopt;
    return static_cast<std::int64_t>(std::llround(*n));
}

std::optional<Observation> parseObservation(const json::Value& v) {
    if (!v.isObject()) return std::nullopt;
    Observation o;
    const auto account = v["account"].string(), source = v["source"].string(), kind = v["kind"].string();
    const auto minutes = integer(v["minutes"]), t = integer(v["t"]), tLo = integer(v["tLo"]), R = integer(v["R"]);
    const auto p = v["p"].finiteNumber();
    if (!account || !source || !kind || !minutes || !t || !tLo || !R || !p) return std::nullopt;
    o.account = *account;
    o.source = *source;
    o.kind = *kind;
    o.name = optionalString(v["name"]);
    o.minutes = *minutes;
    o.t = *t;
    o.tLo = *tLo;
    o.p = *p;
    o.R = *R;
    o.plan = cleanPlan(optionalString(v["plan"]));
    o.tier = cleanPlan(optionalString(v["tier"]));
    if (const auto machine = v["machine"].nonEmptyString()) {
        o.machine = *machine;
        o.hostKey = optionalString(v["hostKey"]);
    }
    return o;
}

std::optional<StoredInstance> parseInstance(const json::Value& v) {
    if (!v.isObject()) return std::nullopt;
    const auto cls = v["class"];
    if (!cls.isArray() || cls.size() < 3) return std::nullopt;
    StoredInstance s;
    const auto kind = cls.at(0).string();
    if (kind == "plan" && cls.size() == 4) {
        const auto id = cls.at(1).string(), window = cls.at(3).string();
        if (!id || !window) return std::nullopt;
        s.cls = {true, *id, optionalString(cls.at(2)), *window};
    } else if (kind == "account" && cls.size() == 3) {
        const auto id = cls.at(1).string(), window = cls.at(2).string();
        if (!id || !window) return std::nullopt;
        s.cls = {false, *id, std::nullopt, *window};
    } else {
        return std::nullopt;
    }
    const auto account = v["account"].string();
    const auto R = integer(v["R"]), tLast = integer(v["tLast"]);
    const auto lo = v["lo"].finiteNumber(), pMax = v["pMax"].finiteNumber();
    if (!account || !R || !tLast || !lo || !pMax) return std::nullopt;
    s.account = *account;
    s.R = *R;
    s.lo = *lo;
    s.hi = v["hi"].finiteNumber();
    s.pMax = *pMax;
    s.tLast = *tLast;
    return s;
}

std::optional<StoredKey> parseKey(const json::Value& v) {
    if (!v.isObject()) return std::nullopt;
    const auto source = v["source"].string(), key = v["key"].string();
    const auto at = integer(v["at"]);
    if (!source || !key || !at) return std::nullopt;
    StoredKey k;
    k.source = *source;
    k.entry = {*at, *key, cleanPlan(optionalString(v["plan"])), cleanPlan(optionalString(v["tier"]))};
    k.orgKey = v["orgKey"].nonEmptyString();
    return k;
}

}  // namespace

std::string storeJson(const Store& store) {
    std::string out = "{\"version\": " + std::to_string(store.version);
    list(out, "observations", store.observations, observationJson);
    list(out, "instances", store.instances, instanceJson);
    list(out, "keys", store.keys, keyJson);
    out += "}\n";
    return out;
}

std::optional<Store> parseStore(std::string_view bytes) {
    json::Document doc;
    if (!doc.parse(bytes)) return std::nullopt;
    const auto root = doc.root();
    if (!root.isObject()) return std::nullopt;
    const auto version = integer(root["version"]);
    if (!version) return std::nullopt;
    Store out;
    out.version = static_cast<int>(*version);
    for (const auto& v : root["observations"].elements()) {
        if (auto o = parseObservation(v)) out.observations.push_back(std::move(*o));
    }
    for (const auto& v : root["instances"].elements()) {
        if (auto s = parseInstance(v)) out.instances.push_back(std::move(*s));
    }
    for (const auto& v : root["keys"].elements()) {
        if (auto k = parseKey(v)) out.keys.push_back(std::move(*k));
    }
    return out;
}

std::optional<Salt> loadOrCreateSalt(const std::string& folder) {
    const auto path = folder + "/" + std::string(kSaltFileName);
    auto read = files::readFile(path, 4096, false);
    if (read.status == files::ReadStatus::Ok && read.bytes.size() == kSaltBytes) {
        Salt salt(read.bytes.begin(), read.bytes.end());
        ::explicit_bzero(read.bytes.data(), read.bytes.size());
        return salt;
    }
    if (!read.bytes.empty()) ::explicit_bzero(read.bytes.data(), read.bytes.size());
    if (read.status == files::ReadStatus::Failed) return std::nullopt;
    Salt salt(kSaltBytes);
    if (RAND_bytes(salt.data(), static_cast<int>(salt.size())) != 1) return std::nullopt;
    if (!files::makeFolders(folder, 0700)) return std::nullopt;
    const std::string_view bytes(reinterpret_cast<const char*>(salt.data()), salt.size());
    if (!files::replaceFile(path, bytes, 0600)) return std::nullopt;
    return salt;
}

std::optional<Store> readStore(const std::string& file) {
    const auto read = files::readFile(file, kStoreLimit, false);
    if (read.status != files::ReadStatus::Ok) return std::nullopt;
    return parseStore(read.bytes);
}

bool writeStore(const std::string& file, const Store& store) {
    const auto folder = files::parentOf(file);
    if (!folder.empty() && !files::makeFolders(folder, 0700)) return false;
    return files::replaceFile(file, storeJson(store), 0600);
}

}  // namespace agentspeed::quota::estimate
