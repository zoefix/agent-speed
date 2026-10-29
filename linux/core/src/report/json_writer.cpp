#include <charconv>
#include <cmath>
#include <cstdio>

#include "agentspeed/report_json.hpp"

namespace agentspeed::report {

Json Json::of(bool value) {
    Json j;
    j.kind_ = Kind::Bool;
    j.bool_ = value;
    return j;
}

Json Json::of(std::int64_t value) {
    Json j;
    j.kind_ = Kind::Int;
    j.int_ = value;
    return j;
}

Json Json::of(std::string value) {
    Json j;
    j.kind_ = Kind::String;
    j.string_ = std::move(value);
    return j;
}

Json Json::decimal(double value, int places) {
    Json j;
    j.kind_ = Kind::Decimal;
    j.decimal_ = value;
    j.places_ = places;
    return j;
}

Json Json::array(std::vector<Json> items) {
    Json j;
    j.kind_ = Kind::Array;
    j.items_ = std::move(items);
    return j;
}

Json Json::object(std::vector<Member> members) {
    Json j;
    j.kind_ = Kind::Object;
    j.members_ = std::move(members);
    return j;
}

const Json* Json::find(std::string_view key) const {
    for (const auto& [name, value] : members_)
        if (name == key) return &value;
    return nullptr;
}

double Json::round(double value, int places) {
    if (!std::isfinite(value)) return 0;
    const double scale = std::pow(10.0, places);
    return std::round(value * scale) / scale;
}

std::string Json::number(double value, int places) {
    const auto rounded = round(value, places);
    if (rounded == 0) return "0";
    char buffer[400];
    // to_chars ignores the C locale, which the Qt app sets from the environment.
    const auto result = std::to_chars(buffer, buffer + sizeof buffer, rounded, std::chars_format::fixed, places);
    std::string text(buffer, result.ptr);
    if (text.find('.') != std::string::npos) {
        while (text.back() == '0') text.pop_back();
        if (text.back() == '.') text.pop_back();
    }
    return text;
}

std::string Json::quoted(std::string_view text) {
    std::string out = "\"";
    for (const char c : text) {
        switch (c) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        case '\b': out += "\\b"; break;
        case '\f': out += "\\f"; break;
        default:
            if (static_cast<unsigned char>(c) < 0x20) {
                char buffer[8];
                std::snprintf(buffer, sizeof buffer, "\\u%04x", static_cast<unsigned>(static_cast<unsigned char>(c)));
                out += buffer;
            } else {
                out += c;
            }
        }
    }
    return out + "\"";
}

void Json::write(std::string& out) const {
    switch (kind_) {
    case Kind::Null: out += "null"; break;
    case Kind::Bool: out += bool_ ? "true" : "false"; break;
    case Kind::Int: out += std::to_string(int_); break;
    case Kind::Decimal: out += number(decimal_, places_); break;
    case Kind::String: out += quoted(string_); break;
    case Kind::Array:
        out += '[';
        for (std::size_t i = 0; i < items_.size(); ++i) {
            if (i) out += ',';
            items_[i].write(out);
        }
        out += ']';
        break;
    case Kind::Object:
        out += '{';
        for (std::size_t i = 0; i < members_.size(); ++i) {
            if (i) out += ',';
            out += quoted(members_[i].first);
            out += ':';
            members_[i].second.write(out);
        }
        out += '}';
        break;
    }
}

void Json::writePretty(std::string& out, int indent) const {
    const std::string pad(static_cast<std::size_t>(indent + 2), ' ');
    if (kind_ == Kind::Array && !items_.empty()) {
        out += "[\n";
        for (std::size_t i = 0; i < items_.size(); ++i) {
            out += pad;
            items_[i].writePretty(out, indent + 2);
            out += i + 1 < items_.size() ? ",\n" : "\n";
        }
        out += std::string(static_cast<std::size_t>(indent), ' ') + "]";
    } else if (kind_ == Kind::Object && !members_.empty()) {
        out += "{\n";
        for (std::size_t i = 0; i < members_.size(); ++i) {
            out += pad + quoted(members_[i].first) + ": ";
            members_[i].second.writePretty(out, indent + 2);
            out += i + 1 < members_.size() ? ",\n" : "\n";
        }
        out += std::string(static_cast<std::size_t>(indent), ' ') + "}";
    } else {
        write(out);
    }
}

std::string Json::pretty() const {
    std::string out;
    writePretty(out, 0);
    return out;
}

std::string Json::compact() const {
    std::string out;
    write(out);
    return out;
}

}  // namespace agentspeed::report
