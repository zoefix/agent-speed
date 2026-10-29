#include <QClipboard>
#include <QDateTime>
#include <QGuiApplication>
#include <QRegularExpression>

#include "preferences.hpp"
#include "report/report_controller.hpp"
#include "strings.hpp"

// The preview (DESIGN 11, Preview window and consent sheet): the real body of the next report, refreshed every second.
namespace {

QString qs(const std::string& text) { return QString::fromStdString(text); }
std::string sv(const QString& text) { return text.toStdString(); }

}  // namespace

void ReportController::watchPreview(bool on) {
    previewWatchers_ = std::max(0, previewWatchers_ + (on ? 1 : -1));
    if (previewWatchers_ == 0) {
        previewWindow_.reset();
        return;
    }
    if (on && previewWatchers_ == 1) {
        previewWindow_ = std::make_unique<agentspeed::report::Window>(lastSample_ ? *lastSample_ : agentspeed::report::Sample{});
        previewStartedMs_ = steadyMs();
    }
    refreshPreview();
}

void ReportController::copyPreview() {
    if (auto* clipboard = QGuiApplication::clipboard()) clipboard->setText(previewCopy_);
}

QString ReportController::previewHint() const {
    const bool known = consent_ ? consent_->discovery.has_value() : discovery_.has_value();
    return t(known ? "report.preview.hint" : "report.preview.assumed");
}

void ReportController::refreshPreview() {
    if (!previewWindow_) {
        previewWindow_ = std::make_unique<agentspeed::report::Window>(lastSample_ ? *lastSample_ : agentspeed::report::Sample{});
        previewStartedMs_ = steadyMs();
    }
    const auto sample = lastSample_ ? *lastSample_ : agentspeed::report::Sample{};
    const auto& known = consent_ ? consent_->discovery : discovery_;
    const auto service = consent_ ? std::optional<agentspeed::report::ServiceUrl>(consent_->service) : service_;
    if (!service) return;
    const auto document = known ? *known : agentspeed::report::assumedDiscovery(*service, sv(serviceName()));
    const auto content = previewWindow_->content(sample);
    agentspeed::report::Envelope envelope;
    envelope.client = client();
    envelope.deviceId = sv(identity_ ? identity_->deviceId : previewDeviceId_);
    envelope.username = sv(consent_ ? consent_->username : username());
    envelope.runId = sv(run_ ? run_->id : previewRunId_);
    envelope.seq = run_ ? run_->scheduler->seq() + 1 : 1;
    envelope.sentAtMs = correctedMs();
    const auto now = steadyMs();
    const auto windowStart = run_ && run_->scheduler->windowStart() ? *run_->scheduler->windowStart() : previewStartedMs_;
    envelope.windowMs = std::max<qint64>(0, now - windowStart);
    const auto mode = run_ ? run_->scheduler->mode() : (sample.working() ? agentspeed::report::Mode::Active : agentspeed::report::Mode::Idle);
    envelope.activity = mode == agentspeed::report::Mode::Idle ? agentspeed::report::Activity::Idle : agentspeed::report::Activity::Active;
    envelope.titleLevel = run_ ? run_->title.level() : 0;
    auto opts = options(document, consent_ ? consent_->sections : switches());
    if (!known) opts.extensions.taskScore = true;
    const auto built = agentspeed::report::fitted(envelope, content, sample, opts);
    previewBody_ = qs(built.json.pretty());
    previewCopy_ = previewBody_;
    previewRequest_.clear();
    if (known) {
        if (const auto api = agentspeed::report::ServiceUrl::parseApi(known->api, service->loopbackHttp())) {
            QStringList lines{QStringLiteral("POST ") + qs(api->endpoint("/v1/reports")),
                              QStringLiteral("User-Agent: ") + qs(client().userAgent()),
                              QStringLiteral("Accept: application/json"),
                              QStringLiteral("Content-Type: application/json"),
                              QStringLiteral("X-AgentSpeed-Device: ") + qs(envelope.deviceId),
                              QStringLiteral("X-AgentSpeed-Timestamp: ") + QString::number(envelope.sentAtMs / 1000),
                              QStringLiteral("X-AgentSpeed-Nonce: …"),
                              QStringLiteral("X-AgentSpeed-Signature: …")};
            if (!apiKey_.isEmpty()) lines.append(QStringLiteral("Authorization: Bearer ••••"));
            previewRequest_ = lines.join(QLatin1Char('\n'));
        }
    }
    previewFooter_.clear();
    if (run_) {
        if (const auto due = run_->scheduler->nextDueAt()) {
            previewFooter_ = fmt(QStringLiteral("report.preview.next"), {QString::number(std::max<qint64>(0, (*due - now + 999) / 1000))});
        }
    }
    emit previewChanged();
}

QString ReportController::previewRich(const QString& keyColor) const {
    static const QRegularExpression keyLine(QStringLiteral("^(\\s*)(\"(?:[^\"\\\\]|\\\\.)*\")(: )"));
    QStringList lines;
    for (const auto& line : previewBody_.split(QLatin1Char('\n'))) {
        const auto match = keyLine.match(line);
        if (!match.hasMatch()) {
            lines.append(line.toHtmlEscaped());
            continue;
        }
        lines.append(match.captured(1) + QStringLiteral("<span style=\"color:%1\">").arg(keyColor.toHtmlEscaped()) + match.captured(2).toHtmlEscaped() +
                     QStringLiteral("</span>") + match.captured(3) + line.mid(match.capturedEnd()).toHtmlEscaped());
    }
    return QStringLiteral("<pre style=\"margin:0\">") + lines.join(QLatin1Char('\n')) + QStringLiteral("</pre>");
}
