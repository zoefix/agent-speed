// Warm frames of the performer and the readout allocate nothing (DESIGN 2a.6 Techniques), counted through a
// replacement of the global operator new that only counts while a probe runs on this thread.
#include <gtest/gtest.h>

#include <cstdlib>
#include <new>

#include "agentspeed/character/performer.hpp"
#include "character_support.hpp"

#pragma GCC diagnostic ignored "-Wmismatched-new-delete"

namespace {
thread_local bool counting = false;
thread_local long allocations = 0;
}  // namespace

void* operator new(std::size_t size) {
    if (counting) ++allocations;
    if (void* p = std::malloc(size ? size : 1)) return p;
    throw std::bad_alloc();
}
void* operator new[](std::size_t size) {
    if (counting) ++allocations;
    if (void* p = std::malloc(size ? size : 1)) return p;
    throw std::bad_alloc();
}
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, std::size_t) noexcept { std::free(p); }
void operator delete[](void* p, std::size_t) noexcept { std::free(p); }

using namespace agentspeed::character;

TEST(CharacterPerformer, StepsWithoutAllocatingOnceWarm) {
    Performer performer(Pack::load(tiny_pack::make({{"idle", tiny_pack::rigJSON()}})));
    CharacterHud hud;
    const auto layout = performer.hudLayout(300, 600, 2, 1);
    const std::vector<Rgb> agents = {{217, 119, 87}};
    HudSilhouette silhouette(0, 800, 4);
    std::vector<double> scratch;
    for (int n = 0; n < 240; ++n) {
        performer.step(1.0 / 60, 3);
        hud.update(1.0 / 60, n / 60.0, 40, 0.3, 0.2, "x");
        hud.layout(1.0 / 60, layout, agents, 20, 360, 300);
        (void)performer.trace(silhouette, 300, 600, 2, scratch);
    }
    int quiet = 0;
    for (int window = 0; window < 40; ++window) {
        const int starts = performer.director().motionStarts();
        allocations = 0;
        counting = true;
        for (int n = 0; n < 20; ++n) {
            performer.step(1.0 / 60, 3);
            hud.update(1.0 / 60, 2 + n / 60.0, 40, 0.3, 0.2, "x");
            hud.layout(1.0 / 60, layout, agents, 20, 360, 300);
            (void)performer.trace(silhouette, 300, 600, 2, scratch);
        }
        counting = false;
        if (performer.director().motionStarts() == starts) {
            ++quiet;
            EXPECT_EQ(allocations, 0) << "allocations in a window without new actions";
        }
    }
    EXPECT_GT(quiet, 0);
}

TEST(CharacterHudWings, BalancedFramesAllocateNothingOnceWarm) {
    HudSilhouette s(0, 444, 4);
    for (double y = 80; y <= 400; y += 2) {
        s.add(190, y);
        s.add(254, y);
    }
    const HudFrame f({0, 0, 444, 444}, s, 296, 222, 80, 170, 140);
    std::array<CardSize, kHudCardCount> sizes = {{{84, 62}, {90, 18}, {90, 38.5}, {78, 26.4}, {110, 18}, {86, 26.4}, {130, 16.3}, {150, 85}}};
    std::array<bool, kHudCardCount> shown{};
    shown.fill(true);
    HudWings wings;
    wings.models = ModelsShape{8, 130, 0, 0, 130, 16.3};
    for (int n = 0; n < 120; ++n) wings.update(1.0 / 60, n / 60.0, f, sizes, shown);
    allocations = 0;
    counting = true;
    // A source joining 額度 and leaving again: the split is chosen again, in place.
    for (int n = 0; n < 600; ++n) {
        sizes[static_cast<std::size_t>(index(HudCard::Quota))].h = n < 300 ? 55 : 85;
        wings.update(1.0 / 60, 2 + n / 60.0, f, sizes, shown);
    }
    counting = false;
    EXPECT_EQ(allocations, 0);
}
