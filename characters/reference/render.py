"""Offscreen OpenGL renderer for previews: textured part meshes, sprites and full-screen images."""
import moderngl
import numpy as np

MESH_VS = """
#version 330
uniform vec2 screen; uniform vec2 scale; uniform vec2 offset; uniform vec2 atlas;
in vec2 pos; in vec2 uv; out vec2 v_uv;
void main() {
    vec2 p = pos * scale + offset;
    gl_Position = vec4(p.x / screen.x * 2.0 - 1.0, 1.0 - p.y / screen.y * 2.0, 0.0, 1.0);
    v_uv = uv / atlas;
}
"""
MESH_FS = """
#version 330
uniform sampler2D tex; uniform float opacity; in vec2 v_uv; out vec4 color;
void main() { vec4 c = texture(tex, v_uv); color = vec4(c.rgb * c.a, c.a) * opacity; }
"""
SPRITE_VS = """
#version 330
uniform vec2 screen;
in vec2 corner; in vec2 center; in vec2 size; in float angle; in float alpha;
out vec2 v_uv; out float v_alpha;
void main() {
    vec2 c = corner * size;
    vec2 r = vec2(c.x * cos(angle) - c.y * sin(angle), c.x * sin(angle) + c.y * cos(angle));
    vec2 p = center + r;
    gl_Position = vec4(p.x / screen.x * 2.0 - 1.0, 1.0 - p.y / screen.y * 2.0, 0.0, 1.0);
    v_uv = corner + 0.5; v_alpha = alpha;
}
"""
DOT_VS = """
#version 330
uniform vec2 screen;
in vec2 corner; in vec2 center; in vec2 size; in vec4 rgba; in float soft;
out vec2 v_p; out vec4 v_rgba; out float v_soft;
void main() {
    vec2 p = center + corner * size * 2.0;
    gl_Position = vec4(p.x / screen.x * 2.0 - 1.0, 1.0 - p.y / screen.y * 2.0, 0.0, 1.0);
    v_p = corner * 2.0; v_rgba = rgba; v_soft = soft;
}
"""
DOT_FS = """
#version 330
in vec2 v_p; in vec4 v_rgba; in float v_soft; out vec4 color;
void main() {
    float r = length(v_p);
    float a = 1.0 - smoothstep(1.0 - v_soft, 1.0, r);
    a = v_soft >= 0.99 ? pow(max(0.0, 1.0 - r), 2.0) : a;
    color = vec4(v_rgba.rgb * v_rgba.a, v_rgba.a) * a;
}
"""
SPRITE_FS = """
#version 330
uniform sampler2D tex; uniform int premultiplied; in vec2 v_uv; in float v_alpha; out vec4 color;
void main() {
    vec4 c = texture(tex, v_uv);
    if (premultiplied == 0) c = vec4(c.rgb * c.a, c.a);
    color = c * v_alpha;
}
"""


class Renderer:
    def __init__(self, size, atlas_rgba, parts, samples=4):
        self.size = size
        self.ctx = moderngl.create_standalone_context()
        self.ctx.enable(moderngl.BLEND)
        self.ctx.blend_func = moderngl.ONE, moderngl.ONE_MINUS_SRC_ALPHA
        w, h = size
        self.msaa = self.ctx.framebuffer(color_attachments=[self.ctx.renderbuffer((w, h), 4, samples=samples)])
        self.resolved = self.ctx.framebuffer(color_attachments=[self.ctx.texture((w, h), 4)])
        self.mesh_prog = self.ctx.program(vertex_shader=MESH_VS, fragment_shader=MESH_FS)
        self.sprite_prog = self.ctx.program(vertex_shader=SPRITE_VS, fragment_shader=SPRITE_FS)
        self.mesh_prog["screen"].value = size
        self.sprite_prog["screen"].value = size
        self.characters = []
        self.add_character(atlas_rgba, parts)
        corners = np.array([-0.5, -0.5, 0.5, -0.5, 0.5, 0.5, -0.5, -0.5, 0.5, 0.5, -0.5, 0.5], "f4")
        self.corner_buf = self.ctx.buffer(corners.tobytes())
        self.instance_buf = self.ctx.buffer(reserve=6 * 4 * 4096, dynamic=True)
        self.sprite_vao = self.ctx.vertex_array(self.sprite_prog, [
            (self.corner_buf, "2f", "corner"),
            (self.instance_buf, "2f 2f 1f 1f/i", "center", "size", "angle", "alpha"),
        ])
        self.dot_prog = self.ctx.program(vertex_shader=DOT_VS, fragment_shader=DOT_FS)
        self.dot_prog["screen"].value = size
        self.dot_buf = self.ctx.buffer(reserve=9 * 4 * 8192, dynamic=True)
        self.dot_vao = self.ctx.vertex_array(self.dot_prog, [
            (self.corner_buf, "2f", "corner"),
            (self.dot_buf, "2f 2f 4f 1f/i", "center", "size", "rgba", "soft"),
        ])

    def texture(self, rgba, mipmaps=False):
        tex = self.ctx.texture((rgba.shape[1], rgba.shape[0]), 4, np.ascontiguousarray(rgba).tobytes())
        if mipmaps:
            tex.build_mipmaps()
            tex.filter = moderngl.LINEAR_MIPMAP_LINEAR, moderngl.LINEAR
            tex.anisotropy = 8.0
        else:
            tex.filter = moderngl.LINEAR, moderngl.LINEAR
        return tex

    def begin(self, background=(1, 1, 1, 1)):
        self.msaa.use()
        self.msaa.clear(*background)

    def image(self, tex, x, y, w, h, alpha=1.0, premultiplied=False, additive=False):
        self.sprites(tex, np.array([[x + w / 2, y + h / 2, w, h, 0.0, alpha]], np.float32), premultiplied, additive)

    def sprites(self, tex, instances, premultiplied=False, additive=False):
        if len(instances) == 0:
            return
        self.instance_buf.write(np.ascontiguousarray(instances, "f4").tobytes())
        tex.use(0)
        self.sprite_prog["premultiplied"].value = int(premultiplied)
        if additive:
            self.ctx.blend_func = moderngl.ONE, moderngl.ONE
        self.sprite_vao.render(moderngl.TRIANGLES, vertices=6, instances=len(instances))
        self.ctx.blend_func = moderngl.ONE, moderngl.ONE_MINUS_SRC_ALPHA

    def clip(self, box=None):
        """Limits drawing to box (x0, y0, x1, y1) in screen pixels, or lifts the limit."""
        if box is None:
            self.ctx.scissor = None
            return
        x0, y0, x1, y1 = (int(round(v)) for v in box)
        self.ctx.scissor = (x0, self.size[1] - y1, max(0, x1 - x0), max(0, y1 - y0))

    def dots(self, instances, additive=False):
        """Soft round dots: rows of (x, y, radius x, radius y, r, g, b, a, softness); softness 1 is a glow."""
        if len(instances) == 0:
            return
        self.dot_buf.write(np.ascontiguousarray(instances, "f4").tobytes())
        if additive:
            self.ctx.blend_func = moderngl.ONE, moderngl.ONE
        self.dot_vao.render(moderngl.TRIANGLES, vertices=6, instances=len(instances))
        self.ctx.blend_func = moderngl.ONE, moderngl.ONE_MINUS_SRC_ALPHA

    def add_character(self, atlas_rgba, parts):
        """Uploads another character (atlas and meshes); returns its index for character(which=...)."""
        atlas = self.texture(atlas_rgba, mipmaps=True)
        meshes = []
        for part in parts:
            pos = self.ctx.buffer(reserve=part["vertices"].astype("f4").nbytes, dynamic=True)
            uv = self.ctx.buffer(part["uv"].astype("f4").tobytes())
            ibo = self.ctx.buffer(part["triangles"].astype("i4").tobytes())
            vao = self.ctx.vertex_array(self.mesh_prog, [(pos, "2f", "pos"), (uv, "2f", "uv")], index_buffer=ibo)
            meshes.append((pos, vao))
        self.characters.append((atlas, (atlas_rgba.shape[1], atlas_rgba.shape[0]), meshes))
        return len(self.characters) - 1

    def character(self, posed, scale, offset, opacities=None, which=0):
        atlas, atlas_size, meshes = self.characters[which]
        atlas.use(0)
        self.mesh_prog["atlas"].value = atlas_size
        self.mesh_prog["scale"].value = (scale, scale)
        self.mesh_prog["offset"].value = offset
        opacities = opacities or [1.0] * len(posed)
        for (pos, vao), verts, opacity in zip(meshes, posed, opacities):
            if opacity < 0.002:
                continue
            self.mesh_prog["opacity"].value = opacity
            pos.write(verts.astype("f4").tobytes())
            vao.render(moderngl.TRIANGLES)

    def finish(self):
        self.ctx.copy_framebuffer(self.resolved, self.msaa)
        w, h = self.size
        data = np.frombuffer(self.resolved.read(components=4), np.uint8).reshape(h, w, 4)
        return data[::-1]
