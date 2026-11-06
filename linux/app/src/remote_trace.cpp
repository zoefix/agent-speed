#include "remote_trace.hpp"

#include <QDateTime>
#include <QObject>
#include <QStringList>
#include <QVariantList>
#include <map>

#include "app_state.hpp"
#include "history_service.hpp"

void installRemoteTrace([[maybe_unused]] AppState& state, [[maybe_unused]] HistoryModel& history) {
#ifndef NDEBUG
    if (!qEnvironmentVariableIsSet("AGENT_SPEED_REMOTE_TRACE")) return;
    QObject::connect(&state, &AppState::snapshotChanged, &state, [&state] {
        static qint64 last = 0;
        const auto now = QDateTime::currentMSecsSinceEpoch();
        if (now - last < 2000) return;
        last = now;
        const auto snap = state.snapshot();
        std::map<QString, int> agents;
        for (const auto& row : snap.value(QStringLiteral("agents")).toList()) ++agents[row.toMap().value(QStringLiteral("host")).toString()];
        for (const auto& entry : snap.value(QStringLiteral("hosts")).toList()) {
            const auto host = entry.toMap();
            const auto id = host.value(QStringLiteral("id")).toString();
            qInfo("remote-trace host=%s status=%s connected=%d agents=%d today=%lld live=%.1f sessions=%d skewed=%d",
                  qPrintable(id), qPrintable(host.value(QStringLiteral("status")).toString()), host.value(QStringLiteral("connected")).toBool(),
                  agents[id], host.value(QStringLiteral("today")).toLongLong(), host.value(QStringLiteral("live")).toDouble(),
                  host.value(QStringLiteral("sessions")).toInt(), host.value(QStringLiteral("clockSkewed")).toBool());
        }
        // Each orb's reading and the hosts of the agents it lists (DESIGN 13 host subsets).
        for (const auto& entry : snap.value(QStringLiteral("orbs")).toList()) {
            const auto orb = entry.toMap();
            std::map<QString, int> orbAgents;
            for (const auto& row : orb.value(QStringLiteral("agents")).toList()) ++orbAgents[row.toMap().value(QStringLiteral("host")).toString()];
            QStringList hosts;
            for (const auto& [host, n] : orbAgents) hosts << host.left(8) + QLatin1Char('=') + QString::number(n);
            qInfo("remote-trace orb=%s live=%.1f today=%s unavailable=%s agents=[%s]", qPrintable(orb.value(QStringLiteral("id")).toString().left(8)),
                  orb.value(QStringLiteral("liveOutput")).toDouble(), qPrintable(orb.value(QStringLiteral("todayOutputText")).toString()),
                  qPrintable(orb.value(QStringLiteral("unavailable")).toString()), qPrintable(hosts.join(QLatin1Char(' '))));
        }
    });
    QObject::connect(&history, &HistoryModel::rowsChanged, &history, [&history] {
        std::map<QString, std::pair<int, qint64>> hosts;
        for (const auto& row : history.rows()) {
            const auto r = row.toMap();
            auto& [days, output] = hosts[r.value(QStringLiteral("host")).toString()];
            ++days;
            output += r.value(QStringLiteral("output")).toLongLong();
        }
        for (const auto& [host, totals] : hosts) {
            qInfo("remote-trace history host=%s rows=%d output=%lld", qPrintable(host), totals.first, static_cast<long long>(totals.second));
        }
    });
#endif
}
