// IconImage: what the icon and picture readers hand to the painters.
#pragma once

#include <cstdint>
#include <vector>

namespace atrium {

// Premultiplied BGRA, top-down, width * height * 4 bytes.
struct IconImage {
    int width = 0;
    int height = 0;
    std::vector<uint8_t> bgra;
    bool Empty() const { return bgra.empty(); }
};

} // namespace atrium
