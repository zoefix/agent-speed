"""Second-order followers that give every motion weight: parameters chase their targets with a natural frequency,
damping and initial response, so arms overshoot and settle and the body lags a little behind the head.

y + k1 y' + k2 y'' = x + k3 x', with k1 = z / (pi f), k2 = 1 / (2 pi f)^2, k3 = r z / (2 pi f); k2 is raised when
needed to keep the semi-implicit Euler step stable.
"""
import math

# (prefix or exact id, frequency Hz, damping ratio, response); the first match wins, None leaves a parameter raw.
TUNING = [
    ("SkirtFlare", (2.0, 0.45, 0.0)),
    ("TailSwish", (2.4, 0.5, 0.0)), ("Tail", None), ("WingFlap", (3.0, 0.5, 0.0)), ("EarTwitch", (7.0, 0.4, 0.0)),
    ("Var.", None), ("Mouth.", None), ("EyeOpen.", None), ("EyeBall", None), ("Breath", None), ("Wind", None),
    ("Spin", None), ("PosY", None), ("Squash", None), ("Hair", None), ("Skirt", None),
    ("Lift.", (6.0, 0.85, 0.0)), ("Bend.", (6.0, 0.85, 0.0)),
    ("@", (3.2, 0.55, 0.0)),
    ("Finger.", (5.0, 0.55, 0.0)), ("Spread.", (3.5, 0.6, 0.0)), ("Wrist.", (3.2, 0.55, 0.0)),
    ("Arm.", (2.6, 0.55, 0.0)), ("Forearm.", (2.8, 0.5, 0.0)), ("Shoulder.", (3.0, 0.6, 0.0)),
    ("Angle", (2.2, 0.72, 0.0)), ("BodyAngleZ", (1.8, 0.65, 0.0)), ("BodySway", (1.8, 0.65, 0.0)),
    ("Hip", (1.9, 0.62, 0.0)), ("Lean", (2.0, 0.6, 0.0)), ("PosX", (2.4, 0.8, 0.0)), ("Foot", (6.0, 0.7, 0.0)),
]


def tuning_for(pid):
    for key, spec in TUNING:
        if (key == "@" and "@" in pid) or (key != "@" and pid.startswith(key)):
            return spec
    return None


class SecondOrder:
    def __init__(self, f, z, r, x0=0.0):
        self.k1 = z / (math.pi * f)
        self.k2 = 1 / (2 * math.pi * f) ** 2
        self.k3 = r * z / (2 * math.pi * f)
        self.snap(x0)

    def snap(self, x):
        self.xp, self.y, self.yd = x, x, 0.0

    def update(self, dt, x):
        xd = (x - self.xp) / dt
        self.xp = x
        k2 = max(self.k2, dt * dt / 2 + dt * self.k1 / 2, dt * self.k1)
        self.y += dt * self.yd
        self.yd += dt * (x + self.k3 * xd - self.y - self.k1 * self.yd) / k2
        return self.y


class Springs:
    def __init__(self):
        self.state = {}

    def snap(self, pid, value):
        spec = tuning_for(pid)
        if spec is None:
            return
        if pid in self.state:
            self.state[pid].snap(value)
        else:
            self.state[pid] = SecondOrder(*spec, x0=value)

    def apply(self, dt, values):
        for pid, target in values.items():
            spec = tuning_for(pid)
            if spec is None:
                continue
            s = self.state.get(pid)
            if s is None:
                s = self.state[pid] = SecondOrder(*spec, x0=target)
            values[pid] = s.update(dt, target)
        return values
