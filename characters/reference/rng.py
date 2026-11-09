"""A small seeded random generator every player implements bit for bit (spec/CHARACTERS.md, Random numbers), so the
same seed and the same speed history give the same motion on every platform.

SplitMix64: state += 0x9E3779B97F4A7C15; z = state; z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
z = (z ^ (z >> 27)) * 0x94D049BB133111EB; return z ^ (z >> 31), all modulo 2^64. random() is the top 53 bits / 2^53.
"""

MASK = (1 << 64) - 1


class Rng:
    def __init__(self, seed):
        self.state = seed & MASK

    def next_u64(self):
        self.state = (self.state + 0x9E3779B97F4A7C15) & MASK
        z = self.state
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK
        return z ^ (z >> 31)

    def random(self):
        """Uniform in [0, 1)."""
        return (self.next_u64() >> 11) / float(1 << 53)

    def uniform(self, a, b):
        return a + (b - a) * self.random()

    def index(self, n):
        return min(int(self.random() * n), n - 1)

    def choice(self, seq):
        return seq[self.index(len(seq))]

    def shuffle(self, items):
        """Fisher-Yates from the end: swap item i with an index in [0, i]."""
        for i in range(len(items) - 1, 0, -1):
            j = self.index(i + 1)
            items[i], items[j] = items[j], items[i]

    def sample(self, seq, k):
        items = list(seq)
        self.shuffle(items)
        return items[:k]
