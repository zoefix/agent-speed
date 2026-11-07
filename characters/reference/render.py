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
        self.atlas = self.texture(atlas_rgba, mipmaps=True)
        self.mesh_prog["atlas"].value = (atlas_rgba.shape[1], atlas_rgba.shape[0])
        self.mesh_prog["screen"].value = size
        self.sprite_prog["screen"].value = size
        self.meshes = []
        for part in parts:
            pos = self.ctx.buffer(reserve=part["vertices"].astype("f4").nbytes, dynamic=True)
            uv = self.ctx.buffer(part["uv"].astype("f4").tobytes())
            ibo = self.ctx.buffer(part["triangles"].astype("i4").tobytes())
            vao = self.ctx.vertex_array(self.mesh_prog, [(pos, "2f", "pos"), (uv, "2f", "uv")], index_buffer=ibo)
            self.meshes.append((pos, vao))
        corners = np.array([-0.5, -0.5, 0.5, -0.5, 0.5, 0.5, -0.5, -0.5, 0.5, 0.5, -0.5, 0.5], "f4")
        self.corner_buf = self.ctx.buffer(corners.tobytes())
        self.instance_buf = self.ctx.buffer(reserve=6 * 4 * 4096, dynamic=True)
        self.sprite_vao = self.ctx.vertex_array(self.sprite_prog, [
            (self.corner_buf, "2f", "corner"),
            (self.instance_buf, "2f 2f 1f 1f/i", "center", "size", "angle", "alpha"),
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

    def character(self, posed, scale, offset, opacity=1.0):
        self.atlas.use(0)
        self.mesh_prog["scale"].value = (scale, scale)
        self.mesh_prog["offset"].value = offset
        self.mesh_prog["opacity"].value = opacity
        for (pos, vao), verts in zip(self.meshes, posed):
            pos.write(verts.astype("f4").tobytes())
            vao.render(moderngl.TRIANGLES)

    def finish(self):
        self.ctx.copy_framebuffer(self.resolved, self.msaa)
        w, h = self.size
        data = np.frombuffer(self.resolved.read(components=4), np.uint8).reshape(h, w, 4)
        return data[::-1]
