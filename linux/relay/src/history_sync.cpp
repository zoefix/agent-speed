#include "history_sync.hpp"

#include <openssl/evp.h>

#include <algorithm>
#include <set>

#include "agentspeed/history_store.hpp"
#include "json_out.hpp"

namespace agentspeed::relay {

std::string SyncDay::key() const { return day + "/" + std::string(sourceId(source)); }

std::string sha256Hex16(std::string_view bytes) {
    unsigned char digest[EVP_MAX_MD_SIZE];
    unsigned int length = 0;
    EVP_Digest(bytes.data(), bytes.size(), digest, &length, EVP_sha256(), nullptr);
    static constexpr char kHex[] = "0123456789abcdef";
    std::string out;
    for (int i = 0; i < 8; ++i) {
        out += kHex[digest[i] >> 4];
        out += kHex[digest[i] & 15];
    }
    return out;
}

std::string encodeRecord(const perf::Record& r) {
    JsonOut j;
    j.beginObject();
    j.key("n").integer(r.n);
    j.key("outputSum").integer(r.outputSum);
    j.key("durationMsSum").integer(r.durationMsSum);
    j.key("latencyBins").beginArray();
    for (const auto count : r.latencyBins) j.integer(count);
    j.endArray();
    j.key("reasonN").integer(r.reasonN);
    j.key("reasonOutput").integer(r.reasonOutput);
    j.key("reasonDurationMsSum").integer(r.reasonDurationMsSum);
    j.key("cacheN").integer(r.cacheN);
    j.key("cacheInput").integer(r.cacheInput);
    j.key("cacheCached").integer(r.cacheCached);
    j.key("toolN").integer(r.toolN);
    j.key("toolLnSum").number(r.toolLnSum);
    j.key("toolFailed").integer(r.toolFailed);
    j.key("activeMs").integer(r.activeMs);
    return j.endObject().take();
}

std::string digestOf(std::string_view aggregate, const std::vector<std::pair<std::string, std::string>>& perf) {
    std::string text(aggregate);
    for (const auto& [model, record] : perf) text += record;
    return sha256Hex16(text);
}

std::vector<SyncDay> syncDays(const std::vector<history::DailyUsage>& days, const std::vector<perf::DayRecord>& records) {
    std::map<std::pair<std::string, SourceKind>, std::map<std::string, perf::Record>> summed;
    // Keyed by the model as sent, so the records go out in its UTF-8 byte order (REMOTE 5.4).
    for (const auto& r : records) summed[{r.day, r.source}][sanitizeUtf8(r.model)].add(r.record);

    std::vector<SyncDay> out;
    const auto add = [&](const std::string& day, SourceKind source, const history::DayAggregate& totals) {
        SyncDay d;
        d.day = day;
        d.source = source;
        d.aggregate = sanitizeUtf8(history::encode(totals));
        if (const auto it = summed.find({day, source}); it != summed.end()) {
            for (const auto& [model, record] : it->second) d.perf.emplace_back(model, encodeRecord(record));
        }
        d.digest = digestOf(d.aggregate, d.perf);
        out.push_back(std::move(d));
    };
    std::set<std::pair<std::string, SourceKind>> covered;
    for (const auto& usage : days) {
        add(usage.key.day, usage.key.source, usage.totals);
        covered.insert({usage.key.day, usage.key.source});
    }
    for (const auto& [key, models] : summed) {
        if (!covered.contains(key)) add(key.first, key.second, history::DayAggregate{});
    }
    std::stable_sort(out.begin(), out.end(), [](const SyncDay& a, const SyncDay& b) {
        if (a.day != b.day) return a.day < b.day;
        return sourceId(a.source) < sourceId(b.source);
    });
    return out;
}

std::vector<const SyncDay*> changedDays(const std::vector<SyncDay>& days, const std::map<std::string, std::string>& held) {
    std::vector<const SyncDay*> out;
    for (const auto& d : days) {
        const auto it = held.find(d.key());
        if (it == held.end() || it->second != d.digest) out.push_back(&d);
    }
    return out;
}

}  // namespace agentspeed::relay
