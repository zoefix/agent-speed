#include "appearance.hpp"

#include <QGuiApplication>
#include <QPalette>
#include <QStyleHints>

namespace {

QPalette paletteFor(bool dark) {
    QPalette p;
    if (dark) {
        p.setColor(QPalette::Window, QColor(0x20, 0x22, 0x28));
        p.setColor(QPalette::WindowText, QColor(0xEC, 0xEF, 0xF4));
        p.setColor(QPalette::Base, QColor(0x17, 0x19, 0x1F));
        p.setColor(QPalette::AlternateBase, QColor(0x22, 0x25, 0x2C));
        p.setColor(QPalette::Text, QColor(0xEC, 0xEF, 0xF4));
        p.setColor(QPalette::Button, QColor(0x2A, 0x2D, 0x35));
        p.setColor(QPalette::ButtonText, QColor(0xEC, 0xEF, 0xF4));
        p.setColor(QPalette::Highlight, QColor(0x5B, 0x8C, 0xFF));
        p.setColor(QPalette::HighlightedText, Qt::white);
    } else {
        p.setColor(QPalette::Window, QColor(0xF4, 0xF5, 0xF8));
        p.setColor(QPalette::WindowText, QColor(0x16, 0x18, 0x1D));
        p.setColor(QPalette::Base, Qt::white);
        p.setColor(QPalette::AlternateBase, QColor(0xF2, 0xF3, 0xF7));
        p.setColor(QPalette::Text, QColor(0x16, 0x18, 0x1D));
        p.setColor(QPalette::Button, QColor(0xEE, 0xEF, 0xF3));
        p.setColor(QPalette::ButtonText, QColor(0x16, 0x18, 0x1D));
        p.setColor(QPalette::Highlight, QColor(0x2F, 0x6B, 0xFF));
        p.setColor(QPalette::HighlightedText, Qt::white);
    }
    return p;
}

bool overrides(const QString& choice) { return choice == QStringLiteral("dark") || choice == QStringLiteral("light"); }

}  // namespace

Appearance& Appearance::instance() {
    static Appearance appearance;
    return appearance;
}

Appearance::Appearance() {
    connect(QGuiApplication::styleHints(), &QStyleHints::colorSchemeChanged, this, [this] {
        if (!overrides(choice_)) emit changed();
    });
}

void Appearance::apply(const QString& choice) {
    const bool before = dark();
    choice_ = choice;
    QGuiApplication::setPalette(overrides(choice) ? paletteFor(choice == QStringLiteral("dark")) : QPalette());
    if (dark() != before) emit changed();
}

bool Appearance::dark() const {
    if (overrides(choice_)) return choice_ == QStringLiteral("dark");
    const auto scheme = QGuiApplication::styleHints()->colorScheme();
    if (scheme != Qt::ColorScheme::Unknown) return scheme == Qt::ColorScheme::Dark;
    return QGuiApplication::palette().color(QPalette::Window).lightnessF() < 0.5;
}
