#include "ui/calendar_panel.h"
#include "ui/tray_panel.h"

#include <iostream>

using namespace atrium;

int main()
{
    int fail = 0;
    auto check = [&](bool ok, const char* msg) {
        if (!ok) {
            std::cerr << "FAIL: " << msg << '\n';
            ++fail;
        }
    };
    check(CalendarCapsuleDate(2026, 11, 18, 2, 2026, false) == L"11月18日 星期三", "Chinese same-year date");
    check(CalendarCapsuleDate(2027, 1, 1, 4, 2026, false) == L"2027年1月1日 星期五", "Chinese cross-year date");
    check(CalendarCapsuleDate(2026, 11, 18, 2, 2026, true) == L"Wed, November 18", "English same-year date");
    check(CalendarCapsuleDate(2027, 1, 1, 4, 2026, true) == L"Fri, January 1, 2027", "English cross-year date");

    const POINT a{500, 900};
    const SIZE d96{4, 4}, d200{8, 8};
    check(!TrayFollowCancelled(100, 100, 90, false, a, {800, 800}, d96, false), "matching click input");
    check(!TrayFollowCancelled(110, 100, 110, false, a, {800, 800}, d96, false), "matching synthetic input");
    check(!TrayFollowCancelled(120, 100, 90, false, a, {501, 901}, d96, false), "1-pixel mouse jitter");
    check(!TrayFollowCancelled(120, 100, 90, false, a, {502, 898}, d96, false), "inside double-click rect");
    check(TrayFollowCancelled(120, 100, 90, false, a, {503, 900}, d96, false), "outside double-click rect horizontally");
    check(TrayFollowCancelled(120, 100, 90, false, a, {500, 897}, d96, false), "outside double-click rect vertically");
    check(!TrayFollowCancelled(120, 100, 90, false, a, {504, 904}, d200, false), "DPI-scaled double-click rect allows 4 px");
    check(!TrayFollowCancelled(120, 100, 90, true, a, {1200, 20}, d96, false), "synthetic pointer in flight ignores cursor distance");
    check(!TrayFollowCancelled(120, 100, 90, true, a, {1200, 20}, d96, true), "the fallback's own button press does not cancel it");
    if (fail)
        return 1;
    std::cout << "PASS: panel_fixes\n";
    return 0;
}
