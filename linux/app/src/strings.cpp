#include "strings.hpp"

#include <QFile>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLocale>

namespace {

QString languageFor(const QLocale& locale) {
    if (locale.language() == QLocale::Chinese) {
        const auto script = locale.script();
        if (script == QLocale::TraditionalChineseScript) return QStringLiteral("zh-Hant");
        if (script == QLocale::SimplifiedChineseScript) return QStringLiteral("zh-Hans");
        const auto territory = locale.territory();
        const bool traditional = territory == QLocale::Taiwan || territory == QLocale::HongKong || territory == QLocale::Macao;
        return traditional ? QStringLiteral("zh-Hant") : QStringLiteral("zh-Hans");
    }
    if (locale.language() == QLocale::Japanese) return QStringLiteral("ja");
    return QStringLiteral("en");
}

}  // namespace

Strings::Strings(QObject* parent) : QObject(parent), language_(languageFor(QLocale::system())) {
    const auto forced = qEnvironmentVariable("AGENT_SPEED_LANGUAGE");
    if (!forced.isEmpty()) language_ = forced;
    QFile file(QStringLiteral(":/strings.json"));
    if (!file.open(QIODevice::ReadOnly)) return;
    const auto root = QJsonDocument::fromJson(file.readAll()).object();
    for (auto it = root.begin(); it != root.end(); ++it) {
        const auto entry = it.value().toObject();
        auto value = entry.value(language_).toString();
        if (value.isEmpty()) value = entry.value(QStringLiteral("en")).toString();
        table_.insert(it.key(), value);
    }
}

QString Strings::t(const QString& key) const { return table_.value(key, key); }
