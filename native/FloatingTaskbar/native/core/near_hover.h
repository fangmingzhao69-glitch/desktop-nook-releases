// The early hover wave, in input-window client pixels. Enter and leave are DIP.
#pragma once

#include <d2d1.h>
#include <algorithm>

namespace atrium {

struct BarScene;

class NearHover {
public:
    // One current cursor sample supplies both the drawable Y and its early-response flags.
    bool Sample(BarScene& scene, D2D1_POINT_2F current, double enterDip, double leaveDip, bool available);

    float Update(const D2D1_RECT_F& bar, D2D1_POINT_2F pointer, float scale, double enterDip, double leaveDip)
    {
        const float side = 16.f * scale; // past the glass's ends, for an approach from the side
        if (!(scale > 0) || pointer.x < bar.left - side || pointer.x > bar.right + side || pointer.y >= bar.bottom) {
            Reset();
            return -1;
        }
        // Ordinary hover arms departure, but keeps its own wave centre on the bar.
        if (pointer.y >= bar.top) {
            if (pointer.x >= bar.left && pointer.x <= bar.right) {
                on_ = true;
                released_ = false;
                nearest_ = 0;
            } else
                Reset();
            return -1;
        }
        const float enter = std::max(2.f, float(enterDip)), leave = std::max(2.f, float(leaveDip));
        const float distance = (bar.top - pointer.y) / scale;
        if (distance > std::max(enter, leave)) {
            Reset();
            return -1;
        }
        // With leave < enter, wait for a fresh approach or the close band after release.
        if (released_) {
            if (distance > leave)
                return -1;
            released_ = false;
        }
        if (!on_) {
            if (distance > enter)
                return -1;
            on_ = true;
            nearest_ = distance;
        }
        // An approaching pointer may be outside leave. Only its retreat releases it.
        if (distance > leave && distance > nearest_) {
            on_ = false;
            released_ = distance <= enter;
            return -1;
        }
        nearest_ = std::min(nearest_, distance);
        return std::clamp(pointer.x, bar.left, bar.right);
    }

    void Reset()
    {
        on_ = released_ = false;
        nearest_ = 0;
    }

private:
    bool on_ = false, released_ = false;
    float nearest_ = 0;
};

} // namespace atrium
