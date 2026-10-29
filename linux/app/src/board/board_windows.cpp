#include "board/board_windows.hpp"

#include <QGuiApplication>
#include <QQmlEngine>
#include <QQuickItem>
#include <QScreen>
#include <set>

#include "board/board_store.hpp"

BoardWindows::BoardWindows(BoardStore& store, QQmlEngine& engine, QObject* parent) : QObject(parent), store_(store), engine_(engine) {
    connect(&store_, &BoardStore::changed, this, &BoardWindows::sync);
    sync();
}

BoardWindows::~BoardWindows() = default;

int BoardWindows::side(const QString& size) { return size == QLatin1String("S") ? 280 : size == QLatin1String("L") ? 420 : 340; }

QPoint BoardWindows::defaultPosition(int index, int edge) const {
    const auto* screen = QGuiApplication::primaryScreen();
    const auto area = screen ? screen->availableGeometry() : QRect(0, 0, 1280, 800);
    return {area.right() - 12 - edge - index * (edge + 24), area.top() + 12 + 48};
}

QQuickWindow* BoardWindows::window(const QString& id) const {
    const auto it = boards_.find(id);
    return it == boards_.end() ? nullptr : it->second->view.get();
}

void BoardWindows::sync() {
    std::set<QString> wanted;
    int index = 0;
    for (const auto& entry : store_.boards()) {
        const auto spec = entry.toMap();
        const auto id = spec.value(QStringLiteral("id")).toString();
        if (!spec.value(QStringLiteral("visible")).toBool()) {
            ++index;
            continue;
        }
        wanted.insert(id);
        auto& slot = boards_[id];
        const auto size = spec.value(QStringLiteral("size")).toString();
        const int edge = side(size);
        if (!slot) {
            slot = std::make_unique<Board>();
            slot->view = std::make_unique<QQuickView>(&engine_, nullptr);
            auto* view = slot->view.get();
            view->setColor(Qt::transparent);
            view->setResizeMode(QQuickView::SizeRootObjectToView);
            view->setTitle(QStringLiteral("Agent Speed"));
            view->setInitialProperties({{QStringLiteral("boardId"), id}, {QStringLiteral("spec"), spec}, {QStringLiteral("view"), views_.value(id, QVariantMap())}});
            view->loadFromModule(QStringLiteral("AgentSpeed"), QStringLiteral("Board"));
            if (view->status() == QQuickView::Error) {
                for (const auto& e : view->errors()) qWarning() << e.toString();
            }
            slot->settle.setSingleShot(true);
            slot->settle.setInterval(350);
            connect(&slot->settle, &QTimer::timeout, this, [this, id] {
                auto* w = window(id);
                if (!w) return;
                // Snap: released within 24 pt of a screen edge, the board's square sits 12 pt from it.
                QPoint square = w->position() + QPoint(kMargin, kMargin);
                const int edgeSide = w->width() - 2 * kMargin;
                if (const auto* screen = w->screen()) {
                    const auto area = screen->availableGeometry();
                    if (square.x() - area.left() < 24) square.setX(area.left() + 12);
                    if (area.right() - (square.x() + edgeSide) < 24) square.setX(area.right() - 12 - edgeSide);
                    if (square.y() - area.top() < 24) square.setY(area.top() + 12);
                    if (area.bottom() - (square.y() + edgeSide) < 24) square.setY(area.bottom() - 12 - edgeSide);
                }
                if (square != w->position() + QPoint(kMargin, kMargin)) w->setPosition(square - QPoint(kMargin, kMargin));
                store_.setPosition(id, square);
            });
            const auto moved = [this, id] {
                if (auto it = boards_.find(id); it != boards_.end()) it->second->settle.start();
            };
            connect(view, &QWindow::xChanged, this, moved);
            connect(view, &QWindow::yChanged, this, moved);
            const auto square = store_.position(id).value_or(defaultPosition(index, edge));
            view->setGeometry(square.x() - kMargin, square.y() - kMargin, edge + 2 * kMargin, edge + 2 * kMargin);
            slot->size = size;
        }
        auto* view = slot->view.get();
        const bool onTop = spec.value(QStringLiteral("alwaysOnTop")).toBool();
        const bool through = spec.value(QStringLiteral("clickThrough")).toBool();
        Qt::WindowFlags flags = Qt::FramelessWindowHint | Qt::Tool | Qt::NoDropShadowWindowHint | Qt::WindowDoesNotAcceptFocus;
        if (onTop) flags |= Qt::WindowStaysOnTopHint;
        if (through) flags |= Qt::WindowTransparentForInput;
        if (view->flags() != flags) view->setFlags(flags);
        if (slot->size != size) {
            const auto center = view->geometry().center();
            view->resize(edge + 2 * kMargin, edge + 2 * kMargin);
            view->setPosition(center - QPoint(view->width() / 2, view->height() / 2));
            slot->size = size;
        }
        view->setOpacity(spec.value(QStringLiteral("opacity")).toDouble());
        if (auto* root = view->rootObject()) root->setProperty("spec", spec);
        if (!view->isVisible()) view->show();
        ++index;
    }
    for (auto it = boards_.begin(); it != boards_.end();) it = wanted.contains(it->first) ? std::next(it) : boards_.erase(it);
}

void BoardWindows::apply(const QVariantMap& views) {
    views_ = views;
    for (const auto& [id, board] : boards_) {
        if (auto* root = board->view->rootObject()) root->setProperty("view", views.value(id, QVariantMap()));
    }
}

void BoardWindows::startMove(const QString& id) {
    if (auto* w = window(id)) w->startSystemMove();
}

void BoardWindows::openMenu(const QString& id) { Q_UNUSED(id); }
