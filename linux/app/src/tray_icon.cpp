#include "tray_icon.hpp"

#include <QAction>
#include <QFont>
#include <QFontMetricsF>
#include <QGuiApplication>
#include <QIcon>
#include <QMenu>
#include <QPainter>
#include <QPainterPath>
#include <QPixmap>
#include <QSystemTrayIcon>
#include <algorithm>
#include <cmath>

#include "heat.hpp"

#include "strings.hpp"

namespace {

// The icon color steps through the heat palette in 1/40 increments, so it is redrawn only when it visibly changes.
constexpr int kHeatSteps = 40;

int bandOf(double tokS) { return tokS < 0.5 ? 0 : std::max(1, static_cast<int>(std::lround(heat::of(tokS) * kHeatSteps))); }

QPixmap renderIcon(int size, const QString& text, int band) {
    QPixmap pixmap(size, size);
    pixmap.fill(Qt::transparent);
    QPainter p(&pixmap);
    p.setRenderHint(QPainter::Antialiasing);
    p.setRenderHint(QPainter::TextAntialiasing);
    const QRectF box(size * 0.03, size * 0.03, size * 0.94, size * 0.94);
    QPainterPath path;
    path.addRoundedRect(box, size * 0.22, size * 0.22);
    QLinearGradient fill(box.topLeft(), box.bottomLeft());
    const QColor base = heat::color(static_cast<double>(band) / kHeatSteps);
    fill.setColorAt(0, base.lighter(112));
    fill.setColorAt(1, base.darker(110));
    p.fillPath(path, fill);
    const double luminance = 0.2126 * base.redF() + 0.7152 * base.greenF() + 0.0722 * base.blueF();
    QFont font = QGuiApplication::font();
    font.setWeight(QFont::Bold);
    font.setLetterSpacing(QFont::PercentageSpacing, 94);
    const double maxWidth = box.width() * 0.86;
    double pixel = size * 0.62;
    for (; pixel > size * 0.2; pixel -= size * 0.02) {
        font.setPixelSize(static_cast<int>(pixel));
        if (QFontMetricsF(font).horizontalAdvance(text) <= maxWidth) break;
    }
    p.setFont(font);
    p.setPen(luminance > 0.62 ? QColor(0x1B, 0x1D, 0x22) : QColor(Qt::white));
    p.drawText(box, Qt::AlignCenter, text);
    return pixmap;
}

}  // namespace

TrayIcon::TrayIcon(const Strings& strings, bool demo, QObject* parent)
    : QObject(parent), tray_(new QSystemTrayIcon(this)), menu_(new QMenu()), strings_(strings) {
    update_ = menu_->addAction(QString());
    update_->setVisible(false);
    connect(update_, &QAction::triggered, this, &TrayIcon::updateRequested);
    updateSeparator_ = menu_->addSeparator();
    updateSeparator_->setVisible(false);
    open_ = menu_->addAction(QString());
    connect(open_, &QAction::triggered, this, &TrayIcon::toggleDashboard);
    orb_ = menu_->addAction(QString());
    connect(orb_, &QAction::triggered, this, [this] { emit orbToggled(orb_->data().toBool() == false); });
    clickThrough_ = menu_->addAction(QString());
    clickThrough_->setCheckable(true);
    connect(clickThrough_, &QAction::triggered, this, [this](bool on) { emit orbEdit(singleOrb_, QStringLiteral("clickThrough"), on); });
    orbsMenu_ = new QMenu(menu_);
    orbsItem_ = menu_->addMenu(orbsMenu_);
    orbsItem_->setVisible(false);
    demo_ = menu_->addAction(QString());
    demo_->setCheckable(true);
    demo_->setChecked(demo);
    connect(demo_, &QAction::toggled, this, &TrayIcon::demoToggled);
    reportMenu_ = new QMenu(menu_);
    reportItem_ = menu_->addMenu(reportMenu_);
    reportItem_->setVisible(false);
    reportHour_ = reportMenu_->addAction(QString());
    connect(reportHour_, &QAction::triggered, this, [this] { emit reportPause(1); });
    reportIndefinite_ = reportMenu_->addAction(QString());
    connect(reportIndefinite_, &QAction::triggered, this, [this] { emit reportPause(0); });
    reportResume_ = reportMenu_->addAction(QString());
    connect(reportResume_, &QAction::triggered, this, &TrayIcon::reportResume);
    reportLeaderboard_ = reportMenu_->addAction(QString());
    connect(reportLeaderboard_, &QAction::triggered, this, &TrayIcon::reportLeaderboard);
    reportSettings_ = reportMenu_->addAction(QString());
    connect(reportSettings_, &QAction::triggered, this, &TrayIcon::reportSettings);
    boardsToggle_ = menu_->addAction(QString());
    boardsToggle_->setVisible(false);
    connect(boardsToggle_, &QAction::triggered, this, &TrayIcon::boardsToggled);
    boardAdd_ = menu_->addAction(QString());
    connect(boardAdd_, &QAction::triggered, this, &TrayIcon::boardAdd);
    menu_->addSeparator();
    history_ = menu_->addAction(QString());
    connect(history_, &QAction::triggered, this, &TrayIcon::historyRequested);
    settings_ = menu_->addAction(QString());
    connect(settings_, &QAction::triggered, this, &TrayIcon::settingsRequested);
    quit_ = menu_->addAction(QString());
    connect(quit_, &QAction::triggered, this, &TrayIcon::quitRequested);
    retranslate();
    tray_->setContextMenu(menu_);
    connect(tray_, &QSystemTrayIcon::messageClicked, this, [this] {
        if (!updateVersion_.isEmpty()) emit updateRequested();
    });
    connect(tray_, &QSystemTrayIcon::activated, this, [this](QSystemTrayIcon::ActivationReason reason) {
        if (reason == QSystemTrayIcon::Trigger || reason == QSystemTrayIcon::DoubleClick) emit toggleDashboard();
    });
    update(0, QStringLiteral("0"), strings.t(QStringLiteral("app.name")));
    tray_->show();
}

void TrayIcon::retranslate() {
    open_->setText(strings_.t(QStringLiteral("menu.dashboard")));
    const bool several = orbCount_ > 1;
    orb_->setText(strings_.t(orb_->data().toBool() ? (several ? QStringLiteral("menu.hideOrbs") : QStringLiteral("menu.hideOrb"))
                                                   : (several ? QStringLiteral("menu.showOrbs") : QStringLiteral("menu.showOrb"))));
    clickThrough_->setText(strings_.t(QStringLiteral("menu.clickThrough")));
    orbsItem_->setText(strings_.t(QStringLiteral("settings.orb")));
    const auto language = strings_.language();
    demo_->setText(language == QStringLiteral("zh-Hans") ? QStringLiteral("演示模式")
                   : language.startsWith(QStringLiteral("zh")) ? QStringLiteral("示範模式")
                   : language == QStringLiteral("ja")           ? QStringLiteral("デモモード")
                                                                : QStringLiteral("Demo Mode"));
    history_->setText(strings_.t(QStringLiteral("history.open")));
    settings_->setText(strings_.t(QStringLiteral("menu.settings")));
    quit_->setText(strings_.t(QStringLiteral("menu.quit")));
    update_->setText(strings_.t(QStringLiteral("update.menu")).replace(QStringLiteral("%@"), updateVersion_));
    reportHour_->setText(strings_.t(QStringLiteral("report.pause.hour")));
    reportIndefinite_->setText(strings_.t(QStringLiteral("report.pause.indefinite")));
    reportResume_->setText(strings_.t(QStringLiteral("report.resume")));
    reportLeaderboard_->setText(strings_.t(QStringLiteral("report.service.leaderboard")));
    reportSettings_->setText(strings_.t(QStringLiteral("report.menu.settings")));
    boardsToggle_->setText(strings_.t(boardsVisible_ ? QStringLiteral("boards.hide") : QStringLiteral("boards.show")));
    boardAdd_->setText(strings_.t(QStringLiteral("boards.add")));
}

void TrayIcon::setBoards(bool any, bool visible, bool canAdd) {
    boardsVisible_ = visible;
    boardsToggle_->setVisible(any);
    boardAdd_->setEnabled(canAdd);
    retranslate();
}

void TrayIcon::setReport(bool visible, const QString& text, bool paused, bool leaderboard) {
    reportItem_->setVisible(visible);
    reportItem_->setText(text);
    reportHour_->setVisible(!paused);
    reportIndefinite_->setVisible(!paused);
    reportResume_->setVisible(paused);
    reportLeaderboard_->setVisible(leaderboard);
}

void TrayIcon::setUpdate(const QString& version) {
    updateVersion_ = version;
    update_->setVisible(!version.isEmpty());
    updateSeparator_->setVisible(!version.isEmpty());
    retranslate();
}

void TrayIcon::notify(const QString& title, const QString& body) {
    if (QSystemTrayIcon::supportsMessages()) tray_->showMessage(title, body, QSystemTrayIcon::Information, 8000);
}

TrayIcon::~TrayIcon() { delete menu_; }

bool TrayIcon::available() const { return QSystemTrayIcon::isSystemTrayAvailable(); }

void TrayIcon::setOrbs(bool shown, const QVariantList& orbs) {
    orbCount_ = std::max(1, static_cast<int>(orbs.size()));
    orb_->setData(shown);
    const bool several = orbs.size() > 1;
    clickThrough_->setVisible(!several);
    orbsItem_->setVisible(several);
    if (!several && !orbs.isEmpty()) {
        const auto o = orbs.front().toMap();
        singleOrb_ = o.value(QStringLiteral("id")).toString();
        clickThrough_->setChecked(o.value(QStringLiteral("clickThrough")).toBool());
    }
    orbsMenu_->clear();
    for (const auto& item : orbs) {
        const auto o = item.toMap();
        const QString id = o.value(QStringLiteral("id")).toString();
        auto* sub = orbsMenu_->addMenu(o.value(QStringLiteral("name")).toString());
        // The orb's colour dots as the submenu's icon.
        const auto colors = o.value(QStringLiteral("colors")).toList();
        QPixmap dots(qMax(1, static_cast<int>(std::min<qsizetype>(3, colors.size()))) * 7 + 5, 12);
        dots.fill(Qt::transparent);
        {
            QPainter p(&dots);
            p.setRenderHint(QPainter::Antialiasing);
            p.setPen(Qt::NoPen);
            for (int i = static_cast<int>(std::min<qsizetype>(3, colors.size())) - 1; i >= 0; --i) {
                p.setBrush(colors[i].value<QColor>());
                p.drawEllipse(QRectF(1 + i * 7, 2, 8, 8));
            }
        }
        sub->setIcon(QIcon(dots));
        auto* show = sub->addAction(strings_.t(QStringLiteral("orbs.visible")));
        show->setCheckable(true);
        show->setChecked(o.value(QStringLiteral("visible")).toBool());
        connect(show, &QAction::triggered, this, [this, id](bool on) { emit orbEdit(id, QStringLiteral("visible"), on); });
        auto* through = sub->addAction(strings_.t(QStringLiteral("menu.clickThrough")));
        through->setCheckable(true);
        through->setChecked(o.value(QStringLiteral("clickThrough")).toBool());
        connect(through, &QAction::triggered, this, [this, id](bool on) { emit orbEdit(id, QStringLiteral("clickThrough"), on); });
        sub->addAction(strings_.t(QStringLiteral("menu.orb.settings")), this, [this, id] { emit orbSettings(id); });
    }
    retranslate();
}

void TrayIcon::setDemo(bool demo) {
    if (demo_->isChecked() != demo) demo_->setChecked(demo);
}

void TrayIcon::update(double liveOutput, const QString& compactText, const QString& tooltip) {
    const int band = bandOf(liveOutput);
    if (compactText != lastText_ || band != lastBand_) {
        QIcon icon;
        for (int size : {22, 32, 44, 64, 128}) icon.addPixmap(renderIcon(size, compactText, band));
        tray_->setIcon(icon);
        lastText_ = compactText;
        lastBand_ = band;
    }
    tray_->setToolTip(tooltip);
}
