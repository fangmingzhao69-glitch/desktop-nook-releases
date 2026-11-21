#include "shell/win_key.h"

#include <iostream>

using namespace atrium;

namespace {

bool Check(bool condition, const char* message)
{
    if (!condition)
        std::cerr << "FAIL: " << message << '\n';
    return condition;
}

} // namespace

int main()
{
    bool ok = true;
    WinKeyState s;
    auto a = StepWinKey(s, VK_LWIN, 0x5B, true, true, 1000, false, false, false, true, true);
    ok &= Check(a.swallow && s.held && !s.passed, "Win down held");
    a = StepWinKey(s, 'E', 0x12, false, true, 1050, false, false, false, true, true);
    ok &= Check(a.swallow && a.replayCombo && a.winVk == VK_LWIN && s.passed, "Win+E passes");
    s = WinKeyState{};
    StepWinKey(s, VK_LWIN, 0x5B, true, true, 1000, false, false, false, true, true);
    StepWinKey(s, 'L', 0x26, false, true, 1040, false, false, false, true, true);
    a = StepWinKey(s, VK_LWIN, 0x5B, true, true, 5000, false, false, false, true, true);
    ok &= Check(a.swallow && !s.passed && s.held, "Win after Win+L is fresh");
    s = WinKeyState{};
    StepWinKey(s, VK_LWIN, 0x5B, true, true, 1000, false, false, false, true, true);
    a = StepWinKey(s, '1', 0x02, false, true, 1040, false, false, false, true, true);
    ok &= Check(a.swallow && a.notify && a.notifyParam == 1 && s.digit == '1', "Win+1 digit");
    StepWinKey(s, VK_LWIN, 0x5B, true, false, 1080, false, false, false, true, true);
    a = StepWinKey(s, 'E', 0x12, false, true, 2000, false, false, false, true, true);
    ok &= Check(!a.swallow && s.digit == 0, "stale digit clears");
    s = WinKeyState{};
    StepWinKey(s, VK_LWIN, 0x5B, true, true, 1000, false, false, false, true, true);
    a = StepWinKey(s, VK_LWIN, 0x5B, true, true, 1033, false, false, false, true, true);
    ok &= Check(a.swallow && !s.used && s.lastDownMs == 1033, "33ms repeat not fresh");
    a = StepWinKey(s, VK_LWIN, 0x5B, true, true, 3100, false, false, false, true, true);
    ok &= Check(a.swallow && s.lastDownMs == 3100, ">2000ms resets");
    if (!ok)
        return 1;
    std::cout << "win key checks passed\n";
    return 0;
}
