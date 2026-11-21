// CPU state and geometry shared by the bar's glass and icon drawing.
#pragma once

namespace atrium {

struct BarScene;

class HoverWave {
public:
    bool Update(const BarScene& scene, double dtMs);
    float MapX(float x) const;
    float Scale(float x, float width) const;
    float Centre() const { return centre_; }
    float Width() const { return range_ * (1 + (mag_ - 1) * rise_ / 2); }
    float Height(float lift) const { return lift * rise_; }

private:
    float amount_ = 0, centre_ = 0, mag_ = 1, range_ = 1, rise_ = 0;
};

} // namespace atrium
