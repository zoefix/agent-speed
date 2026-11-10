#include "orb/character/character_quota.hpp"

#include <QStringList>
#include <QVariantMap>
#include <algorithm>

#include "source_colors.hpp"
#include "strings.hpp"

namespace orb {

namespace ac = agentspeed::character;

CharacterQuota CharacterQuota::of(const QVariantList& items, const Strings& strings) {
    auto sources = std::make_shared<Sources>();
    sources->reserve(static_cast<std::size_t>(items.size()));
    const QString separator = strings.t(QStringLiteral("list.separator"));
    const QString hostSeparator = QStringLiteral(" · ");
    QStringList spoken;
    for (const auto& value : items) {
        const auto item = value.toMap();
        const QString key = item.value(QStringLiteral("key")).toString();
        const QString source = item.value(QStringLiteral("source")).toString();
        const QString name = item.value(QStringLiteral("name")).toString();
        const auto lines = item.value(QStringLiteral("lines")).toList();
        if (lines.isEmpty()) continue;
        ac::QuotaSource s;
        s.key = key.toStdString();
        s.name = name.toStdString();
        s.color = static_cast<std::uint32_t>(sourceColor(source).rgb() & 0xFFFFFFu);
        if (const QString own = strings.t(QStringLiteral("source.") + source);
            key.contains(QLatin1Char('@')) && name.startsWith(own + hostSeparator)) {
            s.name = own.toStdString();
            s.host = name.mid(own.size() + hostSeparator.size()).toStdString();
        }
        QStringList windows;
        for (const auto& lineValue : lines) {
            const auto line = lineValue.toMap();
            ac::QuotaLine l;
            l.label = line.value(QStringLiteral("label")).toString().toStdString();
            l.percent = std::clamp(line.value(QStringLiteral("percent")).toInt(), 0, 100);
            l.bar = std::clamp(line.value(QStringLiteral("fraction")).toDouble(), 0.0, 1.0);
            l.reset = line.value(QStringLiteral("short")).toString().toStdString();
            l.passed = line.value(QStringLiteral("passed")).toBool();
            s.lines.push_back(std::move(l));
            windows.push_back(line.value(QStringLiteral("spoken")).toString());
        }
        spoken.push_back(name + QLatin1Char(' ') + windows.join(separator));
        sources->push_back(std::move(s));
    }
    CharacterQuota out;
    out.sources = std::move(sources);
    if (!spoken.isEmpty()) out.spoken = strings.fmt(QStringLiteral("a11y.orb.quota"), {spoken.join(separator)});
    return out;
}

}  // namespace orb
