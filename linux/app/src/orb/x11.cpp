#include "orb/x11.hpp"

#include <QGuiApplication>
#include <QWindow>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <vector>
#include <xcb/shape.h>
#include <xcb/xcb.h>

namespace orb::x11 {

namespace {

xcb_connection_t* connection() {
    if (QGuiApplication::platformName() != QLatin1String("xcb")) return nullptr;
    auto* native = qGuiApp->nativeInterface<QNativeInterface::QX11Application>();
    return native ? native->connection() : nullptr;
}

xcb_atom_t atom(xcb_connection_t* c, const char* name, bool onlyIfExists = false) {
    auto cookie = xcb_intern_atom(c, onlyIfExists ? 1 : 0, static_cast<std::uint16_t>(std::strlen(name)), name);
    xcb_intern_atom_reply_t* reply = xcb_intern_atom_reply(c, cookie, nullptr);
    const xcb_atom_t result = reply ? reply->atom : static_cast<xcb_atom_t>(XCB_ATOM_NONE);
    free(reply);
    return result;
}

xcb_window_t root(xcb_connection_t* c) { return xcb_setup_roots_iterator(xcb_get_setup(c)).data->root; }

// Horizontal strips covering a disc, in device pixels.
std::vector<xcb_rectangle_t> discStrips(QWindow* window, QPointF center, double radius) {
    const double dpr = window->devicePixelRatio();
    const double r = radius * dpr;
    const double cx = center.x() * dpr, cy = center.y() * dpr;
    std::vector<xcb_rectangle_t> rects;
    constexpr int kStrip = 2;
    for (double y = -r; y < r; y += kStrip) {
        const double mid = y + kStrip / 2.0;
        const double half = std::sqrt(std::max(0.0, r * r - mid * mid));
        if (half < 0.5) continue;
        rects.push_back({static_cast<std::int16_t>(std::lround(cx - half)), static_cast<std::int16_t>(std::lround(cy + y)),
                         static_cast<std::uint16_t>(std::lround(2 * half)), static_cast<std::uint16_t>(kStrip)});
    }
    return rects;
}

}  // namespace

bool available() { return connection() != nullptr; }

void setInputDisc(QWindow* window, QPointF center, double radius) {
    xcb_connection_t* c = connection();
    if (!c || !window->handle()) return;
    const auto rects = discStrips(window, center, radius);
    xcb_shape_rectangles(c, XCB_SHAPE_SO_SET, XCB_SHAPE_SK_INPUT, XCB_CLIP_ORDERING_UNSORTED,
                         static_cast<xcb_window_t>(window->winId()), 0, 0, static_cast<std::uint32_t>(rects.size()),
                         rects.data());
    xcb_flush(c);
}

void setInputRect(QWindow* window, QRectF rect) {
    xcb_connection_t* c = connection();
    if (!c || !window->handle()) return;
    const double dpr = window->devicePixelRatio();
    xcb_rectangle_t r{static_cast<std::int16_t>(std::lround(rect.x() * dpr)), static_cast<std::int16_t>(std::lround(rect.y() * dpr)),
                      static_cast<std::uint16_t>(std::lround(rect.width() * dpr)), static_cast<std::uint16_t>(std::lround(rect.height() * dpr))};
    xcb_shape_rectangles(c, XCB_SHAPE_SO_SET, XCB_SHAPE_SK_INPUT, XCB_CLIP_ORDERING_UNSORTED, static_cast<xcb_window_t>(window->winId()), 0, 0, 1, &r);
    xcb_flush(c);
}

bool blurSupported() {
    xcb_connection_t* c = connection();
    if (!c) return false;
    const xcb_atom_t blur = atom(c, "_KDE_NET_WM_BLUR_BEHIND_REGION", true);
    if (blur == XCB_ATOM_NONE) return false;
    const xcb_atom_t supported = atom(c, "_NET_SUPPORTED");
    auto cookie = xcb_get_property(c, 0, root(c), supported, XCB_ATOM_ATOM, 0, 4096);
    xcb_get_property_reply_t* reply = xcb_get_property_reply(c, cookie, nullptr);
    bool found = false;
    if (reply) {
        const auto* atoms = static_cast<const xcb_atom_t*>(xcb_get_property_value(reply));
        const int count = xcb_get_property_value_length(reply) / static_cast<int>(sizeof(xcb_atom_t));
        for (int i = 0; i < count && !found; ++i) found = atoms[i] == blur;
        free(reply);
    }
    return found;
}

void setBlurDisc(QWindow* window, QPointF center, double radius) {
    xcb_connection_t* c = connection();
    if (!c || !window->handle()) return;
    const xcb_atom_t blur = atom(c, "_KDE_NET_WM_BLUR_BEHIND_REGION");
    std::vector<std::uint32_t> data;
    for (const auto& r : discStrips(window, center, radius)) {
        data.insert(data.end(), {static_cast<std::uint32_t>(r.x), static_cast<std::uint32_t>(r.y), r.width, r.height});
    }
    xcb_change_property(c, XCB_PROP_MODE_REPLACE, static_cast<xcb_window_t>(window->winId()), blur, XCB_ATOM_CARDINAL, 32,
                        static_cast<std::uint32_t>(data.size()), data.data());
    xcb_flush(c);
}

void skipTaskbar(QWindow* window) {
    xcb_connection_t* c = connection();
    if (!c || !window->handle()) return;
    xcb_client_message_event_t event{};
    event.response_type = XCB_CLIENT_MESSAGE;
    event.format = 32;
    event.window = static_cast<xcb_window_t>(window->winId());
    event.type = atom(c, "_NET_WM_STATE");
    event.data.data32[0] = 1;  // add
    event.data.data32[1] = atom(c, "_NET_WM_STATE_SKIP_TASKBAR");
    event.data.data32[2] = atom(c, "_NET_WM_STATE_SKIP_PAGER");
    event.data.data32[3] = 1;  // a normal application
    xcb_send_event(c, 0, root(c), XCB_EVENT_MASK_SUBSTRUCTURE_REDIRECT | XCB_EVENT_MASK_SUBSTRUCTURE_NOTIFY,
                   reinterpret_cast<const char*>(&event));
    xcb_flush(c);
}

}  // namespace orb::x11
