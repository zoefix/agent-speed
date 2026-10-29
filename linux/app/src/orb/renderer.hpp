#pragma once

#include <QHash>
#include <QImage>
#include <QOpenGLExtraFunctions>
#include <QRectF>
#include <QString>

#include "orb/scene.hpp"

class QOpenGLShaderProgram;

namespace orb {

// Metrics of a cached text texture, in points.
struct TextMetrics {
    double width = 0;    // advance of the text
    double ascent = 0;   // baseline below the top of the ink box
    double padding = 0;  // empty margin around the text inside the texture
    QSizeF size;         // whole texture
};

// Draws a Scene with OpenGL 3.3: the body image, the SDF instances, the led fields and the texts.
class Renderer : protected QOpenGLExtraFunctions {
public:
    Renderer();
    ~Renderer();
    Renderer(const Renderer&) = delete;
    Renderer& operator=(const Renderer&) = delete;

    bool initialize();
    // OpenGL itself runs on the CPU (llvmpipe and friends).
    bool software() const { return software_; }
    void release();
    // The static body (DESIGN 2 layer 2), premultiplied, drawn over `rect` (points); keepPrevious keeps the old one for
    // a crossfade (Scene::bodyMix).
    void setBody(const QImage& image, const QRectF& rect, bool keepPrevious = false);
    // Measures (and caches) a text at this device pixel ratio.
    TextMetrics measure(const TextDraw& text, double dpr);
    void render(const Scene& scene, QSizeF viewport, double dpr, QPointF center);

private:
    struct TextTexture {
        unsigned int id = 0;
        TextMetrics metrics;
        int lastUse = 0;
    };

    QString textKey(const TextDraw& text, double dpr) const;
    TextTexture& textTexture(const TextDraw& text, double dpr);
    unsigned int upload(const QImage& image, unsigned int existing = 0);
    void setCommon(QOpenGLShaderProgram* program, QSizeF viewport, double dpr, QPointF center, double scale);
    void setLedUniforms(const Scene& scene, int mode);
    // Renders the silk field at half the backing resolution into silkTexture_ (macOS does the same).
    void renderSilk(const Scene& scene, double dpr, unsigned int target);
    void drawImage(unsigned int texture, const QRectF& rect, QSizeF viewport, double dpr, QPointF center, double scale,
                   double alpha = 1);
    void drawText(const TextDraw& text, double dpr);
    void setLiquidUniforms(QOpenGLShaderProgram* program, const Scene& scene, bool surfaceOnly);
    void setBlend(Blend blend);

    QOpenGLShaderProgram* shapes_ = nullptr;
    QOpenGLShaderProgram* led_ = nullptr;
    QOpenGLShaderProgram* text_ = nullptr;
    QOpenGLShaderProgram* image_ = nullptr;
    QOpenGLShaderProgram* liquid_ = nullptr;
    unsigned int instanceVao_ = 0, emptyVao_ = 0, instanceBuffer_ = 0, palette_ = 0, body_ = 0;
    unsigned int silkFbo_ = 0, silkTexture_ = 0;
    int silkSize_ = 0;
    double silkBox_ = 0;
    QRectF bodyRect_;
    unsigned int previousBody_ = 0;
    QRectF previousBodyRect_;
    QHash<QString, TextTexture> texts_;
    int frame_ = 0;
    bool ready_ = false;
    bool software_ = false;
};

}  // namespace orb
