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

#include "strings.hpp"

namespace {

// Heat bands: resting slate, then teal, mint, amber and coral as the speed climbs.
int bandOf(double tokS) {
    if (tokS < 0.5) return 0;
    if (tokS < 60) return 1;
    if (tokS < 150) return 2;
    if (tokS < 300) return 3;
    return 4;
}

QColor bandColor(int band) {
    switch (band) {
        case 1: return QColor(0x2D, 0xB8, 0xA4);
        case 2: return QColor(0x22, 0xA8, 0x6B);
        case 3: return QColor(0xD9, 0x8E, 0x1F);
        case 4: return QColor(0xE5, 0x53, 0x3D);
        default: return QColor(0x5B, 0x63, 0x70);
    }
}

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
    const QColor base = bandColor(band);
    fill.setColorAt(0, base.lighter(118));
    fill.setColorAt(1, base.darker(112));
    p.fillPath(path, fill);
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
    p.setPen(Qt::white);
    p.drawText(box, Qt::AlignCenter, text);
    return pixmap;
}

}  // namespace

TrayIcon::TrayIcon(const Strings& strings, bool demo, QObject* parent)
    : QObject(parent), tray_(new QSystemTrayIcon(this)), menu_(new QMenu()) {
    auto* open = menu_->addAction(strings.t(QStringLiteral("menu.dashboard")));
    connect(open, &QAction::triggered, this, &TrayIcon::toggleDashboard);
    demo_ = menu_->addAction(strings.language().startsWith(QStringLiteral("zh")) ? QStringLiteral("示範模式")
                             : strings.language() == QStringLiteral("ja")      ? QStringLiteral("デモモード")
                                                                                : QStringLiteral("Demo Mode"));
    demo_->setCheckable(true);
    demo_->setChecked(demo);
    connect(demo_, &QAction::toggled, this, &TrayIcon::demoToggled);
    menu_->addSeparator();
    auto* quit = menu_->addAction(strings.t(QStringLiteral("menu.quit")));
    connect(quit, &QAction::triggered, this, &TrayIcon::quitRequested);
    tray_->setContextMenu(menu_);
    connect(tray_, &QSystemTrayIcon::activated, this, [this](QSystemTrayIcon::ActivationReason reason) {
        if (reason == QSystemTrayIcon::Trigger || reason == QSystemTrayIcon::DoubleClick) emit toggleDashboard();
    });
    update(0, QStringLiteral("0"), strings.t(QStringLiteral("app.name")));
    tray_->show();
}

TrayIcon::~TrayIcon() { delete menu_; }

bool TrayIcon::available() const { return QSystemTrayIcon::isSystemTrayAvailable(); }

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
