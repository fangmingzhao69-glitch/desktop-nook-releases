// The screen edge the bar sits on and the geometry that follows from it.
#pragma once

#include <windows.h>
#include <d2d1.h>

#include <algorithm>
#include <string_view>

namespace atrium {

// The screen edge the bar sits on (setting barEdge).
enum class BarEdge { Bottom, Top, Left, Right };

constexpr BarEdge ParseBarEdge(std::wstring_view name)
{
    return name == L"top" ? BarEdge::Top : name == L"left" ? BarEdge::Left : name == L"right" ? BarEdge::Right : BarEdge::Bottom;
}

constexpr bool Vertical(BarEdge edge) { return edge == BarEdge::Left || edge == BarEdge::Right; }

// The edge a bar's glass (screen px) sits on, from the rect alone: popups need no other word of it.
inline BarEdge EdgeOf(const RECT& bar)
{
    MONITORINFO mi{sizeof(mi)};
    GetMonitorInfoW(MonitorFromRect(&bar, MONITOR_DEFAULTTONEAREST), &mi);
    const RECT& m = mi.rcMonitor;
    if (bar.bottom - bar.top > bar.right - bar.left)
        return bar.left + bar.right < m.left + m.right ? BarEdge::Left : BarEdge::Right;
    return bar.top + bar.bottom < m.top + m.bottom ? BarEdge::Top : BarEdge::Bottom;
}

// The bar is laid out as at the bottom of a landscape area: u along the bar, v across it, growing
// toward the edge it sits on. An EdgeFrame puts that layout on the real edge of `area` (screen or
// window px): Top mirrors v; Left and Right run u down the edge. Lengths keep their axis, so a slot
// w along and h across is w tall on a side edge; nothing is rotated, only placed.
struct EdgeFrame {
    BarEdge edge = BarEdge::Bottom;
    D2D1_RECT_F area{};

    EdgeFrame() = default;
    EdgeFrame(BarEdge e, const RECT& r) : edge(e), area{float(r.left), float(r.top), float(r.right), float(r.bottom)} {}
    EdgeFrame(BarEdge e, const D2D1_RECT_F& r) : edge(e), area(r) {}

    float Along() const { return Vertical(edge) ? area.bottom - area.top : area.right - area.left; }
    float Across() const { return Vertical(edge) ? area.right - area.left : area.bottom - area.top; }

    // Area px to layout {u, v}: (x, y) -> (x * _11 + y * _21 + _31, x * _12 + y * _22 + _32).
    // Every edge is a reflection or a quarter turn, so the way back is the transpose.
    D2D1_MATRIX_3X2_F FromAreaMatrix() const
    {
        switch (edge) {
        case BarEdge::Top: return {1, 0, 0, -1, -area.left, area.bottom};
        case BarEdge::Left: return {0, -1, 1, 0, -area.top, area.right};
        case BarEdge::Right: return {0, 1, 1, 0, -area.top, -area.left};
        default: return {1, 0, 0, 1, -area.left, -area.top};
        }
    }

    D2D1_POINT_2F FromArea(D2D1_POINT_2F p) const
    {
        const auto m = FromAreaMatrix();
        return {p.x * m._11 + p.y * m._21 + m._31, p.x * m._12 + p.y * m._22 + m._32};
    }
    D2D1_POINT_2F ToArea(D2D1_POINT_2F c) const
    {
        const auto m = FromAreaMatrix();
        const float u = c.x - m._31, v = c.y - m._32;
        return {u * m._11 + v * m._12, u * m._21 + v * m._22};
    }
    D2D1_RECT_F FromArea(const D2D1_RECT_F& r) const { return Span(FromArea(D2D1_POINT_2F{r.left, r.top}), FromArea(D2D1_POINT_2F{r.right, r.bottom})); }
    D2D1_RECT_F ToArea(const D2D1_RECT_F& r) const { return Span(ToArea(D2D1_POINT_2F{r.left, r.top}), ToArea(D2D1_POINT_2F{r.right, r.bottom})); }

    POINT FromArea(POINT p) const { return Round(FromArea(D2D1_POINT_2F{float(p.x), float(p.y)})); }
    POINT ToArea(POINT c) const { return Round(ToArea(D2D1_POINT_2F{float(c.x), float(c.y)})); }
    RECT FromArea(const RECT& r) const { return Round(FromArea(D2D1_RECT_F{float(r.left), float(r.top), float(r.right), float(r.bottom)})); }
    RECT ToArea(const RECT& r) const { return Round(ToArea(D2D1_RECT_F{float(r.left), float(r.top), float(r.right), float(r.bottom)})); }

private:
    static D2D1_RECT_F Span(D2D1_POINT_2F a, D2D1_POINT_2F b) { return {std::min(a.x, b.x), std::min(a.y, b.y), std::max(a.x, b.x), std::max(a.y, b.y)}; }
    static LONG Round(float f) { return LONG(f < 0 ? f - 0.5f : f + 0.5f); }
    static POINT Round(D2D1_POINT_2F p) { return {Round(p.x), Round(p.y)}; }
    static RECT Round(const D2D1_RECT_F& r) { return {Round(r.left), Round(r.top), Round(r.right), Round(r.bottom)}; }
};

} // namespace atrium
