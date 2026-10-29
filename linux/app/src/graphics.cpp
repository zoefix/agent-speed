#include "graphics.hpp"

#include <QByteArray>
#include <QOffscreenSurface>
#include <QOpenGLContext>
#include <QOpenGLFunctions>
#include <QQuickWindow>
#include <QSGRendererInterface>

// Software OpenGL (llvmpipe in virtual machines and on systems without a GPU driver) redraws the whole window on the CPU
// for every animation frame; the software renderer repaints only what changed, at a small fraction of the cost.
bool chooseGraphicsBackend() {
    if (!qEnvironmentVariableIsEmpty("QT_QUICK_BACKEND") || !qEnvironmentVariableIsEmpty("QSG_RHI_BACKEND")) {
        return QQuickWindow::graphicsApi() == QSGRendererInterface::Software;
    }
    QOpenGLContext context;
    QOffscreenSurface surface;
    bool software = true;
    if (context.create()) {
        surface.setFormat(context.format());
        surface.create();
        if (context.makeCurrent(&surface)) {
            const auto* raw = reinterpret_cast<const char*>(context.functions()->glGetString(GL_RENDERER));
            const QByteArray renderer = QByteArray(raw ? raw : "").toLower();
            software = renderer.contains("llvmpipe") || renderer.contains("softpipe") || renderer.contains("swrast") ||
                       renderer.contains("software");
            context.doneCurrent();
        }
    }
    if (software) QQuickWindow::setGraphicsApi(QSGRendererInterface::Software);
    return software;
}
