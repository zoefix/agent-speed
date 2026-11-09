"""Pendulum chains for hair and skirt: anchors follow head and body parameters, wind pushes, outputs are angles."""
import math

import numpy as np

GRAVITY = 1400.0
SUBSTEP = 1 / 240


class Chain:
    def __init__(self, spec, wind_sign=-1.0):
        self.spec = spec
        self.lengths = np.array(spec["segments"], np.float64)
        self.inputs = [(i["param"], i.get("x", 0.0), i.get("y", 0.0)) for i in spec["inputs"]]
        self.wind_sign = wind_sign
        self.reset()

    def anchor(self, values):
        ax, ay = self.spec["anchor"]
        return np.array([ax + sum(values.get(p, 0.0) * kx for p, kx, _ in self.inputs),
                         ay + sum(values.get(p, 0.0) * ky for p, _, ky in self.inputs)])

    def reset(self, values=None):
        a = self.anchor(values or {})
        self.pos = [a.copy()]
        for length in self.lengths:
            self.pos.append(self.pos[-1] + np.array([0.0, length]))
        self.pos = np.array(self.pos)
        self.prev = self.pos.copy()
        self.time = 0.0

    def step(self, dt, values, wind):
        steps = max(1, int(round(dt / SUBSTEP)))
        h = dt / steps
        k, damping = self.spec["stiffness"], self.spec["damping"]
        for _ in range(steps):
            self.time += h
            gust = 1 + 0.35 * math.sin(self.time * 7.3) + 0.2 * math.sin(self.time * 13.1 + 1.7)
            force_x = self.wind_sign * wind * self.spec["wind"] * GRAVITY * gust
            self.pos[0] = self.anchor(values)
            rest = self.pos[0].copy()
            for i in range(1, len(self.pos)):
                rest = rest + np.array([0.0, self.lengths[i - 1]])
                vel = (self.pos[i] - self.prev[i]) * max(0.0, 1 - damping * h)
                acc = np.array([force_x, GRAVITY]) + k * (rest - self.pos[i])
                self.prev[i] = self.pos[i].copy()
                self.pos[i] = self.pos[i] + vel + acc * h * h
            for _ in range(2):
                for i in range(1, len(self.pos)):
                    d = self.pos[i] - self.pos[i - 1]
                    dist = max(np.hypot(*d), 1e-6)
                    self.pos[i] = self.pos[i - 1] + d * (self.lengths[i - 1] / dist)

    def outputs(self):
        out = {}
        parent_angle = 0.0
        for o in self.spec["outputs"]:
            i = o["segment"]
            d = self.pos[i + 1] - self.pos[i]
            angle = math.degrees(math.atan2(d[0], d[1]))
            out[o["param"]] = math.tanh((angle - parent_angle) / o["degrees"])
            parent_angle = angle
        return out


class Physics:
    def __init__(self, specs, wind_sign=-1.0):
        self.chains = [Chain(s, wind_sign) for s in specs]

    def reset(self, values):
        for c in self.chains:
            c.reset(values)

    def step(self, dt, values):
        wind = values.get("Wind", 0.0)
        out = {}
        for c in self.chains:
            c.step(dt, values, wind)
            out.update(c.outputs())
        return out
