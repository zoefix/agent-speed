#include "tray_icon.hpp"

#include <QAction>
#include <QFont>
#include <QFontMetricsF>
#include <QGuiApplication>
#include <QIcon>
#include <QMenu>
#include <QPainter>
#include <QPainterPath>
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
    open_ = menu_->addAction(QString());
    connect(open_, &QAction::triggered, this, &TrayIcon::toggleDashboard);
    orb_ = menu_->addAction(QString());
    connect(orb_, &QAction::triggered, this, [this] { emit orbToggled(orb_->data().toBool() == false); });
    clickThrough_ = menu_->addAction(QString());
    clickThrough_->setCheckable(true);
    connect(clickThrough_, &QAction::toggled, this, &TrayIcon::clickThroughToggled);
    demo_ = menu_->addAction(QString());
    demo_->setCheckable(true);
    demo_->setChecked(demo);
    connect(demo_, &QAction::toggled, this, &TrayIcon::demoToggled);
    menu_->addSeparator();
    settings_ = menu_->addAction(QString());
    connect(settings_, &QAction::triggered, this, &TrayIcon::settingsRequested);
    quit_ = menu_->addAction(QString());
    connect(quit_, &QAction::triggered, this, &TrayIcon::quitRequested);
    retranslate();
    tray_->setContextMenu(menu_);
    connect(tray_, &QSystemTrayIcon::activated, this, [this](QSystemTrayIcon::ActivationReason reason) {
        if (reason == QSystemTrayIcon::Trigger || reason == QSystemTrayIcon::DoubleClick) emit toggleDashboard();
    });
    update(0, QStringLiteral("0"), strings.t(QStringLiteral("app.name")));
    tray_->show();
}

void TrayIcon::retranslate() {
    open_->setText(strings_.t(QStringLiteral("menu.dashboard")));
    orb_->setText(strings_.t(orb_->data().toBool() ? QStringLiteral("menu.hideOrb") : QStringLiteral("menu.showOrb")));
    clickThrough_->setText(strings_.t(QStringLiteral("menu.clickThrough")));
    const auto language = strings_.language();
    demo_->setText(language == QStringLiteral("zh-Hans") ? QStringLiteral("演示模式")
                   : language.startsWith(QStringLiteral("zh")) ? QStringLiteral("示範模式")
                   : language == QStringLiteral("ja")           ? QStringLiteral("デモモード")
                                                                : QStringLiteral("Demo Mode"));
    settings_->setText(strings_.t(QStringLiteral("menu.settings")));
    quit_->setText(strings_.t(QStringLiteral("menu.quit")));
}

TrayIcon::~TrayIcon() { delete menu_; }

bool TrayIcon::available() const { return QSystemTrayIcon::isSystemTrayAvailable(); }

void TrayIcon::setOrbVisible(bool visible) {
    orb_->setData(visible);
    orb_->setText(strings_.t(visible ? QStringLiteral("menu.hideOrb") : QStringLiteral("menu.showOrb")));
}

void TrayIcon::setClickThrough(bool on) {
    if (clickThrough_->isChecked() != on) clickThrough_->setChecked(on);
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
