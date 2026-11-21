#include "near_hover.h"
#include "render/bar_painter.h"

#include <cmath>

namespace atrium {

bool NearHover::Sample(BarScene& scene, D2D1_POINT_2F current, double enterDip, double leaveDip, bool available)
{
    float lead = -1;
    if (available && scene.magnify > 1.001f && (scene.earlyRise || scene.earlyMagnify))
        lead = Update(scene.bar, current, scene.scale, enterDip, leaveDip);
    else
        Reset();
    const bool rise = lead >= 0 && scene.earlyRise, magnify = lead >= 0 && scene.earlyMagnify;
    // Of its height only the side of the glass's top edge matters (HoverWave): moves within one
    // side are not published again.
    const bool pointerChanged = scene.pointerX >= 0 && (scene.pointerY < scene.bar.top) != (current.y < scene.bar.top);
    const bool changed = std::fabs(lead - scene.nearX) >= 1.f || rise != scene.nearRise || magnify != scene.nearMagnify || pointerChanged;
    // Publish these together: a new Y with old near flags could briefly lower the wave.
    if (scene.pointerX >= 0)
        scene.pointerY = current.y;
    scene.nearX = lead;
    scene.nearRise = rise;
    scene.nearMagnify = magnify;
    return changed;
}

} // namespace atrium
