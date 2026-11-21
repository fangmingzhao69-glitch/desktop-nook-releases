#include "hover_wave.h"
#include "bar_painter.h"
#include "../motion.h"

#include <algorithm>
#include <cmath>
#include <numbers>

namespace atrium {

namespace {
float Approach(float value, float target, double dtMs)
{
    value = motion::Approach(value, target, dtMs, 13);
    return std::fabs(target - value) < 0.002f ? target : value;
}

// Integral of the raised cosine over [-1, 1], from zero to x.
float WaveArea(float x)
{
    constexpr float kPi = std::numbers::pi_v<float>;
    if (x >= 1)
        return 0.5f;
    if (x <= -1)
        return -0.5f;
    return x / 2 + std::sin(kPi * x) / (2 * kPi);
}
} // namespace

bool HoverWave::Update(const BarScene& scene, double dtMs)
{
    const bool hovering = scene.pointerX >= 0 && scene.dragKey.empty() && scene.magnify > 1.001f;
    const bool above = scene.pointerY < scene.bar.top;
    // The expanded input region keeps icons clickable, but must not override a near release.
    const bool hoverMagnify = hovering && !(above && scene.earlyMagnify);
    const bool hoverRise = hovering && !(above && scene.earlyRise);
    mag_ = std::max(1.f, scene.magnify);
    range_ = std::max(1.f, scene.magnifyRange);
    if (hoverMagnify || hoverRise)
        centre_ = scene.pointerX;
    else if (scene.nearX >= 0)
        centre_ = scene.nearX;
    const float amount = Approach(amount_, hoverMagnify || scene.nearMagnify ? 1.f : 0.f, dtMs);
    const float rise = Approach(rise_, hoverRise || scene.nearRise ? 1.f : 0.f, dtMs);
    const bool moving = amount != amount_ || rise != rise_;
    amount_ = amount;
    rise_ = rise;
    return moving;
}

float HoverWave::MapX(float x) const
{
    const float k = (mag_ - 1) * amount_;
    if (k <= 0)
        return x;
    return x + k * range_ * WaveArea((x - centre_) / range_);
}

float HoverWave::Scale(float x, float width) const
{
    width = std::max(1.f, width);
    return (MapX(x + width / 2) - MapX(x - width / 2)) / width;
}

} // namespace atrium
