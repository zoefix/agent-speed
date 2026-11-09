#pragma once

#include <algorithm>
#include <cstdint>
#include <utility>
#include <vector>

namespace agentspeed::character {

// SplitMix64, the random generator every character player shares (spec/CHARACTERS.md, Random numbers), so the same
// seed and the same speed history give the same motion on every platform.
class Rng {
public:
    explicit Rng(std::uint64_t seed = 0) : state_(seed) {}

    std::uint64_t state() const { return state_; }

    std::uint64_t nextUInt64() {
        state_ += 0x9E3779B97F4A7C15ULL;
        std::uint64_t z = state_;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
        return z ^ (z >> 31);
    }

    // Uniform in [0, 1): the top 53 bits over 2^53.
    double random() { return static_cast<double>(nextUInt64() >> 11) / 9007199254740992.0; }

    double uniform(double a, double b) { return a + (b - a) * random(); }

    int index(int n) { return std::min(static_cast<int>(random() * static_cast<double>(n)), n - 1); }

    template <class T>
    const T& choice(const std::vector<T>& items) { return items[static_cast<std::size_t>(index(static_cast<int>(items.size())))]; }

    // Fisher-Yates from the last item down.
    template <class T>
    void shuffle(std::vector<T>& items) {
        for (int i = static_cast<int>(items.size()) - 1; i > 0; --i) {
            const int j = index(i + 1);
            std::swap(items[static_cast<std::size_t>(i)], items[static_cast<std::size_t>(j)]);
        }
    }

    template <class T>
    std::vector<T> sample(const std::vector<T>& items, std::size_t k) {
        std::vector<T> copy = items;
        shuffle(copy);
        if (copy.size() > k) copy.resize(k);
        return copy;
    }

private:
    std::uint64_t state_;
};

}  // namespace agentspeed::character
