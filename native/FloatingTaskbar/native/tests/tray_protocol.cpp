// The tray host's callback protocol (shell/tray_protocol.h): the notifications the
// shell sends for each action and how they are packed, per NOTIFYICON_VERSION.
#include "shell/tray_protocol.h"

#include <cstdio>
#include <vector>

using namespace atrium::tray;

namespace {
int g_failures = 0;
void Check(bool ok, const char* what)
{
    if (!ok) {
        std::printf("FAIL: %s\n", what);
        ++g_failures;
    }
}
} // namespace

int main()
{
    using V = std::vector<UINT>;
    Check(CallbackSequence(HostAction::Left, 0) == V{WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP}, "v0 left: no NIN_SELECT");
    Check(CallbackSequence(HostAction::Left, 3) == V{WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP, NIN_SELECT}, "v3 left ends in NIN_SELECT");
    Check(CallbackSequence(HostAction::Left, 4) == V{WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP, NIN_SELECT}, "v4 left ends in NIN_SELECT");
    Check(CallbackSequence(HostAction::Right, 0) == V{WM_MOUSEMOVE, WM_RBUTTONDOWN, WM_RBUTTONUP}, "v0 right: no WM_CONTEXTMENU");
    Check(CallbackSequence(HostAction::Right, 4) == V{WM_MOUSEMOVE, WM_RBUTTONDOWN, WM_RBUTTONUP, WM_CONTEXTMENU}, "v4 right ends in WM_CONTEXTMENU");
    Check(CallbackSequence(HostAction::Middle, 4) == V{WM_MOUSEMOVE, WM_MBUTTONDOWN, WM_MBUTTONUP}, "middle");
    Check(CallbackSequence(HostAction::DoubleContinuation, 4) == V{WM_LBUTTONDBLCLK, WM_LBUTTONUP},
          "the second click of a double click never repeats the first");
    Check(CallbackSequence(HostAction::Double, 4) == V{WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP, NIN_SELECT, WM_LBUTTONDBLCLK, WM_LBUTTONUP},
          "a whole double click is a click and its continuation");
    Check(CallbackSequence(HostAction::PopupOpen, 3).empty() && CallbackSequence(HostAction::PopupClose, 0).empty(), "popup requests are version 4 only");
    Check(CallbackSequence(HostAction::PopupOpen, 4) == V{NIN_POPUPOPEN} && CallbackSequence(HostAction::PopupClose, 4) == V{NIN_POPUPCLOSE},
          "v4 popup requests");
    Check(CallbackSequence(HostAction::HoverIn, 0) == V{WM_MOUSEMOVE}, "hover is a mouse move");
    Check(CallbackSequence(HostAction::Open, 0) == V{WM_LBUTTONDOWN, WM_LBUTTONUP} &&
              CallbackSequence(HostAction::Open, 4) == V{WM_LBUTTONDOWN, WM_LBUTTONUP, NIN_SELECT},
          "opening from the bar is a click without the pointer's arrival");

    WPARAM w = 0;
    LPARAM l = 0;
    EncodeCallback(0, 7, WM_LBUTTONUP, POINT{100, 200}, w, l);
    Check(w == 7 && l == WM_LBUTTONUP, "v0: the id in wParam, the notification in lParam");
    EncodeCallback(4, 7, WM_CONTEXTMENU, POINT{-1200, 2000}, w, l);
    Check(LOWORD(l) == WM_CONTEXTMENU && HIWORD(l) == 7, "v4: the notification and the id in lParam");
    Check(short(LOWORD(w)) == -1200 && short(HIWORD(w)) == 2000, "v4: the pointer in wParam, negative monitors too");

    if (!g_failures)
        std::printf("tray protocol: all checks passed\n");
    return g_failures ? 1 : 0;
}
