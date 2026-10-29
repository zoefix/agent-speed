#pragma once

// Picks Qt Quick's software renderer when OpenGL itself runs on the CPU. Call after the application object exists and
// before the first window. Returns true when the software renderer was chosen.
bool chooseGraphicsBackend();
