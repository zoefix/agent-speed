#include "orb/renderer.hpp"

#include <QFont>
#include <QFontMetricsF>
#include <QGuiApplication>
#include <QOpenGLShaderProgram>
#include <QPainter>
#include <QtGlobal>
#include <algorithm>
#include <cmath>

#include "orb/shaders.hpp"

namespace orb {

namespace {

constexpr int kInstanceFloats = 24;
constexpr int kMaxTextTextures = 48;

QOpenGLShaderProgram* build(const QByteArray& vertex, const QByteArray& fragment, const char* name) {
    auto* program = new QOpenGLShaderProgram();
    if (!program->addShaderFromSourceCode(QOpenGLShader::Vertex, vertex) ||
        !program->addShaderFromSourceCode(QOpenGLShader::Fragment, fragment) || !program->link()) {
        qWarning("orb: the %s shader failed to build", name);
        delete program;
        return nullptr;
    }
    return program;
}

QFont textFont(const TextDraw& text) {
    QFont font = QGuiApplication::font();
    // The image keeps the default 96 dpi, so 0.75 pt is one point of the layout.
    font.setPointSizeF(text.pixelSize * 0.75);
    font.setWeight(static_cast<QFont::Weight>(text.weight));
    font.setFeature(QFont::Tag("tnum"), 1);
    if (text.tracking != 0) font.setLetterSpacing(QFont::AbsoluteSpacing, text.tracking);
    font.setHintingPreference(QFont::PreferNoHinting);
    return font;
}

}  // namespace

Renderer::Renderer() = default;

Renderer::~Renderer() = default;

bool Renderer::initialize() {
    initializeOpenGLFunctions();
    const auto* raw = reinterpret_cast<const char*>(glGetString(GL_RENDERER));
    const QByteArray renderer = QByteArray(raw ? raw : "").toLower();
    software_ = renderer.contains("llvmpipe") || renderer.contains("softpipe") || renderer.contains("swrast") ||
                renderer.contains("software");
    shapes_ = build(shaders::shapeVertex(), shaders::shapeFragment(), "shape");
    led_ = build(shaders::ledVertex(), shaders::ledFragment(), "led field");
    text_ = build(shaders::textVertex(), shaders::textFragment(), "text");
    image_ = build(shaders::imageVertex(), shaders::imageFragment(), "image");
    liquid_ = build(shaders::liquidVertex(), shaders::liquidFragment(), "liquid");
    if (!shapes_ || !led_ || !text_ || !image_ || !liquid_) return false;

    glGenVertexArrays(1, &emptyVao_);
    glGenVertexArrays(1, &instanceVao_);
    glGenBuffers(1, &instanceBuffer_);
    glBindVertexArray(instanceVao_);
    glBindBuffer(GL_ARRAY_BUFFER, instanceBuffer_);
    for (GLuint k = 0; k < 6; ++k) {
        glEnableVertexAttribArray(k);
        glVertexAttribDivisor(k, 1);
    }
    glBindVertexArray(0);

    QImage palette(256, 1, QImage::Format_RGBA8888);
    for (int i = 0; i < 256; ++i) {
        const Rgba c = heatColor(i / 255.0);
        palette.setPixelColor(i, 0, QColor::fromRgbF(c.r, c.g, c.b, 1));
    }
    palette_ = upload(palette);
    ready_ = true;
    return true;
}

void Renderer::release() {
    if (!ready_) return;
    for (auto& t : texts_) glDeleteTextures(1, &t.id);
    texts_.clear();
    glDeleteTextures(1, &palette_);
    if (silkTexture_) glDeleteTextures(1, &silkTexture_);
    if (silkFbo_) glDeleteFramebuffers(1, &silkFbo_);
    silkTexture_ = silkFbo_ = 0;
    silkSize_ = 0;
    if (body_) glDeleteTextures(1, &body_);
    if (previousBody_) glDeleteTextures(1, &previousBody_);
    previousBody_ = 0;
    glDeleteBuffers(1, &instanceBuffer_);
    glDeleteVertexArrays(1, &instanceVao_);
    glDeleteVertexArrays(1, &emptyVao_);
    delete shapes_;
    delete led_;
    delete text_;
    delete image_;
    delete liquid_;
    shapes_ = led_ = text_ = image_ = liquid_ = nullptr;
    body_ = 0;
    ready_ = false;
}

unsigned int Renderer::upload(const QImage& source, unsigned int existing) {
    const QImage image = source.format() == QImage::Format_RGBA8888_Premultiplied || source.format() == QImage::Format_RGBA8888
                             ? source
                             : source.convertToFormat(QImage::Format_RGBA8888_Premultiplied);
    GLuint id = existing;
    if (!id) glGenTextures(1, &id);
    glBindTexture(GL_TEXTURE_2D, id);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, image.width(), image.height(), 0, GL_RGBA, GL_UNSIGNED_BYTE,
                 image.constBits());
    return id;
}

void Renderer::setBody(const QImage& image, const QRectF& rect, bool keepPrevious) {
    if (!ready_) return;
    if (keepPrevious && body_) {
        std::swap(body_, previousBody_);
        previousBodyRect_ = bodyRect_;
    }
    body_ = upload(image, body_);
    bodyRect_ = rect;
}

QString Renderer::textKey(const TextDraw& text, double dpr) const {
    return QStringLiteral("%1|%2|%3|%4|%5|%6")
        .arg(text.text)
        .arg(text.pixelSize, 0, 'f', 2)
        .arg(text.weight)
        .arg(text.tracking, 0, 'f', 2)
        .arg(dpr, 0, 'f', 2)
        .arg(text.glowRadius, 0, 'f', 1);
}

Renderer::TextTexture& Renderer::textTexture(const TextDraw& text, double dpr) {
    const QString key = textKey(text, dpr);
    auto it = texts_.find(key);
    if (it != texts_.end()) {
        it->lastUse = frame_;
        return *it;
    }
    if (texts_.size() >= kMaxTextTextures) {
        auto oldest = texts_.begin();
        for (auto t = texts_.begin(); t != texts_.end(); ++t) {
            if (t->lastUse < oldest->lastUse) oldest = t;
        }
        glDeleteTextures(1, &oldest->id);
        texts_.erase(oldest);
    }
    const QFont font = textFont(text);
    const QFontMetricsF fm(font);
    TextTexture t;
    t.metrics.width = fm.horizontalAdvance(text.text);
    t.metrics.padding = std::ceil(text.glowRadius + 2);
    const double height = fm.ascent() + fm.descent();
    t.metrics.size = QSizeF(std::ceil(t.metrics.width + 2 * t.metrics.padding), std::ceil(height + 2 * t.metrics.padding));
    t.metrics.ascent = fm.ascent();
    QImage image(qMax(1, qCeil(t.metrics.size.width() * dpr)), qMax(1, qCeil(t.metrics.size.height() * dpr)),
                 QImage::Format_RGBA8888_Premultiplied);
    image.setDevicePixelRatio(dpr);
    image.fill(Qt::transparent);
    QPainter painter(&image);
    painter.setRenderHint(QPainter::TextAntialiasing);
    painter.setFont(font);
    painter.setPen(Qt::white);
    painter.drawText(QPointF(t.metrics.padding, t.metrics.padding + fm.ascent()), text.text);
    painter.end();
    t.id = upload(image);
    t.lastUse = frame_;
    return *texts_.insert(key, t);
}

TextMetrics Renderer::measure(const TextDraw& text, double dpr) {
    if (!ready_) return {};
    return textTexture(text, dpr).metrics;
}

void Renderer::setCommon(QOpenGLShaderProgram* program, QSizeF viewport, double dpr, QPointF center, double scale) {
    program->setUniformValue("uViewport", static_cast<float>(viewport.width()), static_cast<float>(viewport.height()),
                             static_cast<float>(dpr), 0.0f);
    program->setUniformValue("uTransform", static_cast<float>(scale), static_cast<float>(center.x()),
                             static_cast<float>(center.y()), 0.0f);
}

void Renderer::setLedUniforms(const Scene& scene, int mode) {
    const auto& f = scene.ledField;
    led_->setUniformValue("uPalette", 0);
    led_->setUniformValue("uFrame", f.frame[0], f.frame[1], f.frame[2], f.frame[3]);
    led_->setUniformValue("uHeat", f.heat[0], f.heat[1], f.heat[2], f.heat[3]);
    led_->setUniformValue("uPhases", f.phases[0], f.phases[1], f.phases[2], f.phases[3]);
    led_->setUniformValue("uFlags", f.flags[0], f.flags[1], f.flags[2], f.flags[3]);
    led_->setUniformValue("uCorona", f.corona[0], f.corona[1], f.corona[2], f.corona[3]);
    led_->setUniformValue("uText", f.text[0], f.text[1], f.text[2], f.text[3]);
    led_->setUniformValueArray("uWaves", reinterpret_cast<const GLfloat*>(f.waves.data()), 4, 4);
    led_->setUniformValueArray("uBody", scene.coronaBody.data(), led::kCoronaSamples, 1);
    led_->setUniformValue("uMode", mode);
}

void Renderer::renderSilk(const Scene& scene, double dpr, unsigned int target) {
    const auto& f = scene.ledField;
    silkBox_ = 0.96 * f.frame[2];
    const int size = std::max(8, qCeil(silkBox_ * dpr * 0.5));
    if (size != silkSize_) {
        if (!silkTexture_) glGenTextures(1, &silkTexture_);
        glBindTexture(GL_TEXTURE_2D, silkTexture_);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, size, size, 0, GL_RGBA, GL_UNSIGNED_BYTE, nullptr);
        if (!silkFbo_) glGenFramebuffers(1, &silkFbo_);
        glBindFramebuffer(GL_FRAMEBUFFER, silkFbo_);
        glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, silkTexture_, 0);
        silkSize_ = size;
    }
    glBindFramebuffer(GL_FRAMEBUFFER, silkFbo_);
    glViewport(0, 0, size, size);
    glClearColor(0, 0, 0, 0);
    glClear(GL_COLOR_BUFFER_BIT);
    setBlend(Blend::Over);
    led_->bind();
    setCommon(led_, QSizeF(silkBox_, silkBox_), static_cast<double>(size) / silkBox_, QPointF(), 1);
    setLedUniforms(scene, 0);
    led_->setUniformValue("uFrame", static_cast<float>(silkBox_ / 2), static_cast<float>(silkBox_ / 2), f.frame[2], f.frame[3]);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, palette_);
    glBindVertexArray(emptyVao_);
    glDrawArrays(GL_TRIANGLES, 0, 3 * 48);
    glBindFramebuffer(GL_FRAMEBUFFER, target);
}

void Renderer::drawImage(unsigned int texture, const QRectF& rect, QSizeF viewport, double dpr, QPointF center,
                         double scale, double alpha) {
    image_->bind();
    setCommon(image_, viewport, dpr, center, scale);
    image_->setUniformValue("uAlpha", static_cast<float>(alpha));
    image_->setUniformValue("uRect", static_cast<float>(rect.x()), static_cast<float>(rect.y()),
                            static_cast<float>(rect.width()), static_cast<float>(rect.height()));
    image_->setUniformValue("uImage", 0);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, texture);
    glBindVertexArray(emptyVao_);
    glDrawArrays(GL_TRIANGLES, 0, 6);
}

void Renderer::drawText(const TextDraw& text, double dpr) {
    TextTexture& t = textTexture(text, dpr);
    const auto& m = t.metrics;
    const double x = text.baselineCenter.x() - m.width / 2 - m.padding;
    const double y = text.baselineCenter.y() - m.ascent - m.padding;
    text_->setUniformValue("uRect", static_cast<float>(x), static_cast<float>(y), static_cast<float>(m.size.width()),
                           static_cast<float>(m.size.height()));
    text_->setUniformValue("uParams", static_cast<float>(text.shear), static_cast<float>(text.baselineCenter.y()),
                           static_cast<float>(text.glowRadius), static_cast<float>(text.clip));
    text_->setUniformValue("uColor", text.color.r, text.color.g, text.color.b, text.color.a);
    text_->setUniformValue("uGlow", text.glowColor.r, text.glowColor.g, text.glowColor.b, static_cast<float>(text.glowStrength));
    if (text.clip == 1) {
        text_->setUniformValue("uClipRect", static_cast<float>(text.clipRect.left()), static_cast<float>(text.clipRect.top()),
                               static_cast<float>(text.clipRect.right()), static_cast<float>(text.clipRect.bottom()));
    }
    glBindTexture(GL_TEXTURE_2D, t.id);
    glDrawArrays(GL_TRIANGLES, 0, 6);
}

void Renderer::setLiquidUniforms(QOpenGLShaderProgram* program, const Scene& scene, bool surfaceOnly) {
    const auto& l = scene.liquidField;
    const auto set = [program](const char* name, const std::array<float, 4>& v) { program->setUniformValue(name, v[0], v[1], v[2], v[3]); };
    set("uLFrame", l.frame);
    set("uLSurface", l.surface);
    set("uLPhases", l.phases);
    program->setUniformValueArray("uLRipples", reinterpret_cast<const GLfloat*>(l.ripples.data()), 4, 4);
    if (surfaceOnly) return;
    set("uLLiquid", l.liquid);
    set("uLDeep", l.deep);
    set("uLBack", l.back);
    set("uLSpill", l.spill);
    set("uLGlow", l.glow);
    set("uLGlowGeom", l.glowGeom);
    set("uLShade", l.shade);
    set("uLCurrent", l.current);
    set("uLCurrentGeom", l.currentGeom);
    set("uLMeniscus", l.meniscus);
    set("uLMeniscusGlow", l.meniscusGlow);
    set("uLMeniscusGeom", l.meniscusGeom);
    set("uLGold", l.gold);
    set("uLFlags", l.flags);
}

void Renderer::setBlend(Blend blend) {
    glBlendFuncSeparate(GL_ONE, blend == Blend::Additive ? GL_ONE : GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
}

void Renderer::render(const Scene& scene, QSizeF viewport, double dpr, QPointF center) {
    ++frame_;
    GLint target = 0;
    glGetIntegerv(GL_FRAMEBUFFER_BINDING, &target);
    glDisable(GL_DEPTH_TEST);
    glEnable(GL_BLEND);
    const bool silk = ready_ && std::any_of(scene.batches().begin(), scene.batches().end(), [](const Scene::Batch& b) {
        return b.kind == Scene::Batch::Kind::LedField && b.mode == 0;
    });
    if (silk) renderSilk(scene, dpr, static_cast<unsigned int>(target));
    glViewport(0, 0, qCeil(viewport.width() * dpr), qCeil(viewport.height() * dpr));
    glClearColor(0, 0, 0, 0);
    glClear(GL_COLOR_BUFFER_BIT);
    if (!ready_) return;
    const double scale = scene.hoverScale;

    setBlend(Blend::Over);
    if (previousBody_ && scene.bodyMix < 0.999) drawImage(previousBody_, previousBodyRect_, viewport, dpr, center, scale);
    if (body_) drawImage(body_, bodyRect_, viewport, dpr, center, scale, std::clamp(scene.bodyMix, 0.0, 1.0));

    const auto& instances = scene.instances();
    if (!instances.empty()) {
        glBindBuffer(GL_ARRAY_BUFFER, instanceBuffer_);
        glBufferData(GL_ARRAY_BUFFER, static_cast<GLsizeiptr>(instances.size() * sizeof(Instance)), instances.data(),
                     GL_STREAM_DRAW);
    }
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, palette_);
    const auto& batches = scene.batches();
    const auto& texts = scene.texts();
    const auto drawTexts = [&](int layer) {
        bool bound = false;
        for (const auto& text : texts) {
            const bool here = layer < 0 ? text.layer < 0 || text.layer >= static_cast<int>(batches.size()) : text.layer == layer;
            if (!here || text.text.isEmpty() || text.color.a <= 0.002f) continue;
            if (!bound) {
                setBlend(Blend::Over);
                text_->bind();
                setCommon(text_, viewport, dpr, center, scale);
                text_->setUniformValue("uGlyphs", 0);
                if (scene.hasLiquid) setLiquidUniforms(text_, scene, true);
                glBindVertexArray(emptyVao_);
                bound = true;
            }
            drawText(text, dpr);
        }
        if (bound) glBindTexture(GL_TEXTURE_2D, palette_);
    };
    for (std::size_t index = 0; index < batches.size(); ++index) {
        const auto& batch = batches[index];
        drawTexts(static_cast<int>(index));
        setBlend(batch.blend);
        if (batch.kind == Scene::Batch::Kind::Shapes) {
            if (batch.count == 0) continue;
            shapes_->bind();
            setCommon(shapes_, viewport, dpr, center, scale);
            shapes_->setUniformValue("uClip", batch.clip, batch.feather, 0.0f, 0.0f);
            shapes_->setUniformValue("uPalette", 0);
            glBindVertexArray(instanceVao_);
            glBindBuffer(GL_ARRAY_BUFFER, instanceBuffer_);
            for (GLuint k = 0; k < 6; ++k) {
                const auto offset = static_cast<std::uintptr_t>(batch.start) * sizeof(Instance) + k * 4 * sizeof(float);
                glVertexAttribPointer(k, 4, GL_FLOAT, GL_FALSE, kInstanceFloats * sizeof(float),
                                      reinterpret_cast<const void*>(offset));
            }
            glDrawArraysInstanced(GL_TRIANGLES, 0, 6, batch.count);
        } else if (batch.kind == Scene::Batch::Kind::Liquid) {
            liquid_->bind();
            setCommon(liquid_, viewport, dpr, center, scale);
            setLiquidUniforms(liquid_, scene, false);
            liquid_->setUniformValue("uMode", batch.mode);
            liquid_->setUniformValue("uAlpha", batch.alpha);
            glBindVertexArray(emptyVao_);
            glDrawArrays(GL_TRIANGLES, 0, 6);
        } else if (batch.mode == 0) {
            const auto& f = scene.ledField;
            const QRectF rect(f.frame[0] - silkBox_ / 2, f.frame[1] - silkBox_ / 2, silkBox_, silkBox_);
            drawImage(silkTexture_, rect, viewport, dpr, center, scale, batch.alpha);
            glBindTexture(GL_TEXTURE_2D, palette_);
        } else {
            led_->bind();
            setCommon(led_, viewport, dpr, center, scale);
            setLedUniforms(scene, batch.mode);
            const auto& f = scene.ledField;
            led_->setUniformValue("uFrame", f.frame[0], f.frame[1], f.frame[2], f.frame[3] * batch.alpha);
            glBindVertexArray(emptyVao_);
            glDrawArrays(GL_TRIANGLES, 0, 6 * 48);
        }
    }
    drawTexts(-1);
    glBindVertexArray(0);
}

}  // namespace orb
