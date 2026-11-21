// The notification-area callback protocol, as the shell speaks it to an app
// (NOTIFYICONDATA uCallbackMessage, NOTIFYICON_VERSION 0..4). Pure: tested on its
// own (native/tests/tray_protocol.cpp); the tray host sends what this returns.
#pragma once

#include <windows.h>
#include <shellapi.h>

#include <vector>

namespace atrium::tray {

// Open: a left click without the pointer's arrival, for opening an app from the bar (the pointer is
// not on its icon: a mouse move would open a messenger's hover message box there).
enum class HostAction { Left, Right, Middle, Double, DoubleContinuation, HoverIn, PopupOpen, PopupClose, Open };

// The notifications, in order, the shell sends for `action` to an icon of `version`.
// HoverIn is the pointer arriving; PopupOpen/PopupClose are the rich-tooltip
// requests a version 4 app without NIF_SHOWTIP gets after a dwell and on leaving.
inline std::vector<UINT> CallbackSequence(HostAction action, UINT version)
{
    const bool v3 = version >= 3, v4 = version >= 4;
    switch (action) {
    case HostAction::HoverIn: return {WM_MOUSEMOVE};
    case HostAction::PopupOpen:
    case HostAction::PopupClose:
        if (!v4)
            return {};
        return {action == HostAction::PopupOpen ? UINT(NIN_POPUPOPEN) : UINT(NIN_POPUPCLOSE)};
    case HostAction::Right:
        if (v3)
            return {WM_MOUSEMOVE, WM_RBUTTONDOWN, WM_RBUTTONUP, WM_CONTEXTMENU};
        return {WM_MOUSEMOVE, WM_RBUTTONDOWN, WM_RBUTTONUP};
    case HostAction::Middle: return {WM_MOUSEMOVE, WM_MBUTTONDOWN, WM_MBUTTONUP};
    case HostAction::Open:
        if (v3)
            return {WM_LBUTTONDOWN, WM_LBUTTONUP, NIN_SELECT};
        return {WM_LBUTTONDOWN, WM_LBUTTONUP};
    case HostAction::DoubleContinuation: return {WM_LBUTTONDBLCLK, WM_LBUTTONUP};
    case HostAction::Double:
        if (v3)
            return {WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP, NIN_SELECT, WM_LBUTTONDBLCLK, WM_LBUTTONUP};
        return {WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP, WM_LBUTTONDBLCLK, WM_LBUTTONUP};
    case HostAction::Left:
    default:
        if (v3)
            return {WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP, NIN_SELECT};
        return {WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP};
    }
}

// wParam and lParam of one callback: version 4 puts the pointer (screen
// coordinates) in wParam and the icon's id in lParam's high word; older
// versions pass the id as wParam and the notification as lParam.
inline void EncodeCallback(UINT version, UINT uid, UINT notification, POINT at, WPARAM& w, LPARAM& l)
{
    if (version >= 4) {
        w = MAKEWPARAM(WORD(SHORT(at.x)), WORD(SHORT(at.y)));
        l = MAKELPARAM(WORD(notification), WORD(uid));
    } else {
        w = WPARAM(uid);
        l = LPARAM(notification);
    }
}

} // namespace atrium::tray
