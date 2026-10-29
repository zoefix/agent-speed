#include "app_state.hpp"

void AppState::setDemo(bool demo) {
    if (demo_ == demo) return;
    demo_ = demo;
    emit demoChanged(demo);
}

void AppState::applySnapshot(const QVariantMap& snapshot) {
    snapshot_ = snapshot;
    emit snapshotChanged();
}
