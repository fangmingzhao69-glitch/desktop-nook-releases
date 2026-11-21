// Physical right-click fallback for tray buttons with user-input cancellation.
#pragma once

#include <windows.h>

#include <array>
#include <atomic>
#include <cstdint>
#include <memory>

namespace atrium::tray {

inline std::atomic<bool> g_pointerInFlight{false};

inline bool SamePoint(POINT a, POINT b) { return a.x == b.x && a.y == b.y; }

// Use pixel centres in the entire virtual desktop, including negative monitor
// origins. A move and both button events are submitted as one SendInput batch:
// physical mouse events cannot split the positioning from the right click.
inline bool RightClickInputs(POINT point, RECT desktop, std::array<INPUT, 3>& input)
{
    const int64_t width = int64_t(desktop.right) - desktop.left;
    const int64_t height = int64_t(desktop.bottom) - desktop.top;
    if (width <= 0 || height <= 0 || !PtInRect(&desktop, point))
        return false;
    input = {};
    for (auto& event : input)
        event.type = INPUT_MOUSE;
    input[0].mi.dx = LONG(((int64_t(point.x) - desktop.left) * 2 + 1) * 65536 / (width * 2));
    input[0].mi.dy = LONG(((int64_t(point.y) - desktop.top) * 2 + 1) * 65536 / (height * 2));
    input[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
    input[1].mi.dwFlags = MOUSEEVENTF_RIGHTDOWN;
    input[2].mi.dwFlags = MOUSEEVENTF_RIGHTUP;
    return true;
}

enum class PointerClick {
    Sent,
    Unavailable,
    Cancelled,
};

// Desktop supplies Win32/UIA in production and an isolated desktop in tests.
// Returning Cancelled must not fall through to another invocation route.
template<class Desktop> PointerClick RightClick(Desktop& desktop)
{
    g_pointerInFlight = true;
    std::unique_ptr<void, void (*)(void*)> flight(&g_pointerInFlight, [](void* p) { *static_cast<std::atomic<bool>*>(p) = false; });
    POINT original{}, target{};
    if (!desktop.Cursor(original) || desktop.Interacting())
        return PointerClick::Cancelled;
    const bool available = desktop.Target(target);
    POINT beforeMove{};
    if (!desktop.Cursor(beforeMove) || !SamePoint(beforeMove, original) || desktop.Interacting())
        return PointerClick::Cancelled;
    if (!available)
        return PointerClick::Unavailable;
    if (!desktop.Move(target))
        return PointerClick::Unavailable;
    auto stationary = [&] {
        POINT now{};
        return desktop.Cursor(now) && SamePoint(now, target) && !desktop.Interacting();
    };
    auto restore = [&] {
        if (stationary())
            desktop.Move(original);
    };
    desktop.Wait(60);
    if (!stationary())
        return PointerClick::Cancelled; // leave the user's new position alone
    POINT currentTarget{};
    const bool valid = desktop.Target(currentTarget) && SamePoint(currentTarget, target) && desktop.TargetAt(target);
    // UIA hit-testing can take time. Recheck physical input after it returns.
    if (!stationary())
        return PointerClick::Cancelled;
    if (!valid) {
        restore();
        return PointerClick::Unavailable;
    }
    const bool sent = desktop.Send(target);
    if (sent)
        desktop.Wait(180); // let the application establish its menu first
    else if (!stationary())
        return PointerClick::Cancelled;
    restore();
    return sent ? PointerClick::Sent : PointerClick::Unavailable;
}

} // namespace atrium::tray
