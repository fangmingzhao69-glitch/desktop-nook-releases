#include "settings.h"
#include "shell/displays.h"
#include "shell/task_model.h"
#include "ui/notification_layout.h"
#include "motion.h"

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.h>
#include <algorithm>
#include <iostream>
#include <stdexcept>
#include <string_view>

using namespace atrium;

namespace {
void Require(bool condition, const char* message)
{
    if (!condition)
        throw std::runtime_error(message);
}

void CheckSettings()
{
    Settings settings, parsed;
    settings.dockDisplayMode = L"all";
    settings.dockDisplayDelay = 750;
    settings.dockDisplay = L"\\\\?\\DISPLAY#external";
    settings.desktopDisplay = L"disconnected-screen";
    settings.taskbarApps = L"primaryAndWindow";
    settings.taskbarRightPrimaryOnly = true;
    settings.notifyPlacement = L"topRight";
    settings.hoverRise = false;
    settings.nearEnter = 40;
    settings.nearLeave = 4;
    Require(Settings::FromJson(settings.ToJson(), Settings{}, parsed), "settings round trip parses");
    Require(parsed.nearEnter == 40 && parsed.nearLeave == 4, "a four-DIP leave setting survives the native round trip independently of enter");
    Require(Settings::FromJson(LR"({"nearLeave":4})", Settings{}, parsed) && parsed.nearLeave == 4,
            "a partial four-DIP leave update is not replaced with the default forty-eight");
    Require(!parsed.hoverRise, "disabled hover rise survives saving");
    Require(Settings::FromJson(LR"({})", Settings{}, parsed) && !parsed.hoverRise, "old settings keep the glass flat");
    Require(Settings::FromJson(LR"({})", settings, parsed) && !parsed.hoverRise, "partial updates retain disabled hover rise");
    Settings raised = settings;
    raised.hoverRise = true;
    Require(Settings::FromJson(LR"({})", raised, parsed) && parsed.hoverRise, "partial updates retain enabled hover rise");
    Require(Settings::FromJson(LR"({"hoverRise":"bad"})", settings, parsed) && !parsed.hoverRise, "malformed hover rise retains the current value");
    Require(Settings::FromJson(LR"({"hoverRise":true})", settings, parsed) && parsed.hoverRise && parsed.magnify == settings.magnify,
            "hover rise toggles without changing icon magnification");
    settings.language = L"en";
    Require(Settings::FromJson(settings.ToJson(), Settings{}, parsed) && parsed.language == L"en", "the interface language survives saving");
    settings.stageExcluded = {L"wechat.exe", L"tencent.qq_"};
    Require(Settings::FromJson(settings.ToJson(), Settings{}, parsed) && parsed.stageExcluded == std::vector<std::wstring>{L"wechat.exe", L"tencent.qq_"},
            "the Stage Manager blacklist survives saving");
    Require(Settings::FromJson(LR"({"stageExcluded":[]})", Settings{}, parsed) && parsed.stageExcluded.empty(),
            "an empty blacklist stays empty instead of falling back to the defaults");
    Require(Settings::FromJson(LR"({"stageExcluded":"wechat.exe"})", Settings{}, parsed) && parsed.stageExcluded == DefaultStageExcluded(),
            "a malformed blacklist keeps the current list");
    Require(Settings::FromJson(LR"({})", Settings{}, parsed) && parsed.stageExcluded == DefaultStageExcluded(),
            "old settings leave the screenshot tools out of Stage Manager");
    Require(Settings::FromJson(LR"({})", Settings{}, parsed) && parsed.language == L"auto", "old settings follow the system language");
    Require(Settings::FromJson(LR"({"language":"bad tag"})", Settings{}, parsed) && parsed.language == L"auto", "an unknown language follows the system");
    Require(Settings::FromJson(settings.ToJson(), Settings{}, parsed), "restore settings round trip");
    Require(parsed.dockDisplayMode == L"all" && parsed.dockDisplayDelay == 750 && parsed.dockDisplay == settings.dockDisplay &&
                parsed.desktopDisplay == settings.desktopDisplay && parsed.taskbarApps == settings.taskbarApps && parsed.taskbarRightPrimaryOnly &&
                parsed.notifyPlacement == L"topRight",
            "all display and notification preferences survive saving");
    Require(Settings::FromJson(LR"({"taskbarRightPrimaryOnly":false})", settings, parsed) && !parsed.taskbarRightPrimaryOnly && parsed.showTray &&
                parsed.showStatus && parsed.showClock && parsed.showNotifications,
            "disabling the primary-only group retains individual content switches");
    Require(Settings::FromJson(LR"({"taskbarRightPrimaryOnly":"bad"})", settings, parsed) && parsed.taskbarRightPrimaryOnly,
            "malformed primary-only visibility retains the current value");
    Require(Settings::FromJson(LR"({})", settings, parsed) && parsed.taskbarRightPrimaryOnly, "partial updates retain primary-only visibility");
    for (const wchar_t* mode : {L"follow", L"primary", L"fixed", L"all"}) {
        settings.dockDisplayMode = mode;
        Require(Settings::FromJson(settings.ToJson(), Settings{}, parsed) && parsed.dockDisplayMode == mode, "every Dock mode survives saving");
    }
    settings.showClockSeconds = true;
    Require(Settings::FromJson(settings.ToJson(), Settings{}, parsed) && parsed.showClockSeconds, "clock seconds survive saving");
    Require(Settings::FromJson(LR"({})", Settings{}, parsed) && !parsed.showClockSeconds && parsed.showClock, "old settings keep the clock at minutes");
    Require(Settings::FromJson(LR"({})", Settings{}, parsed) && !parsed.magnifyNear, "icons do not magnify above the bar unless chosen");
    Require(Settings::FromJson(LR"({})", Settings{}, parsed) && !parsed.remoteVisible, "Atrium stays out of the screen's capture unless chosen");
    {
        Settings remote;
        remote.remoteVisible = true;
        Require(Settings::FromJson(remote.ToJson(), Settings{}, parsed) && parsed.remoteVisible, "remote-control mode survives saving");
    }
    Require(Settings::FromJson(LR"({"showClockSeconds":true})", settings, parsed) && parsed.showClockSeconds && parsed.showClock,
            "clock seconds toggle independently of the clock");
    Require(Settings::FromJson(LR"({"showClockSeconds":"bad"})", settings, parsed) && parsed.showClockSeconds,
            "malformed clock seconds retain the current value");
    settings.showClockSeconds = false;
    Require(Settings::FromJson(LR"({"showClockSeconds":true})", settings, parsed) && parsed.showClockSeconds && parsed.dockDisplayMode == L"all",
            "clock seconds preview without changing other keys");
    Settings::FromJson(LR"({})", settings, parsed);
    for (const wchar_t* mode : {L"all", L"primaryAndWindow", L"window"}) {
        settings.taskbarApps = mode;
        Require(Settings::FromJson(settings.ToJson(), Settings{}, parsed) && parsed.taskbarApps == mode, "every task distribution mode survives saving");
    }
    Require(Settings::FromJson(LR"({"height":64})", Settings{}, parsed) && parsed.dockDisplayMode == L"primary" && parsed.taskbarApps == L"all" &&
                parsed.dockDisplayDelay == 450 && !parsed.taskbarRightPrimaryOnly && parsed.notifyPlacement == L"dock",
            "old settings retain right-side elements and default dock notifications");
    Require(Settings::FromJson(LR"({"notifyPlacement":"bad"})", Settings{}, parsed) && parsed.notifyPlacement == L"dock",
            "invalid notification placement uses the default");
    Require(Settings::FromJson(LR"({"notifyPlacement":false})", settings, parsed) && parsed.notifyPlacement == L"topRight",
            "malformed notification placement retains the current value");
    Require(Settings::FromJson(LR"({"notifyPlacement":"dock"})", settings, parsed) && parsed.notifyPlacement == L"dock",
            "notification placement can return to the default");
    for (const wchar_t* corner : {L"topLeft", L"topRight", L"bottomLeft", L"bottomRight"}) {
        settings.notifyPlacement = corner;
        Require(Settings::FromJson(settings.ToJson(), Settings{}, parsed) && parsed.notifyPlacement == corner,
                "every corner of the main screen survives saving");
    }
    settings.notifyPlacement = L"topRight";
    Require(Settings::FromJson(LR"({"dockDisplayMode":"bad","taskbarApps":"bad","dockDisplayDelay":99999})", Settings{}, parsed) &&
                parsed.dockDisplayMode == L"primary" && parsed.taskbarApps == L"all" && parsed.dockDisplayDelay == 2000,
            "invalid values are normalized");
    Require(Settings::FromJson(LR"({"dockDisplayDelay":-1})", Settings{}, parsed) && parsed.dockDisplayDelay == 100, "delay has a lower bound");
    Require(Settings::FromJson(LR"({"dockDisplayDelay":"bad","dockDisplay":false})", settings, parsed) && parsed.dockDisplayDelay == 750 &&
                parsed.dockDisplay == settings.dockDisplay,
            "malformed keys retain current values");
}

void CheckSettingsNormalization()
{
    Settings parsed;
    Require(Settings::FromJson(LR"({"widgetBackdrop":"bogus","widgetMaterial":"bogus"})", Settings{}, parsed) && parsed.widgetBackdrop == L"desktop" &&
                parsed.widgetMaterial == L"glass",
            "invalid widget backdrop and material normalize to defaults");
    Require(Settings::FromJson(LR"({"widgetBackdrop":"wallpaper","widgetMaterial":"dusk"})", Settings{}, parsed) && parsed.widgetBackdrop == L"wallpaper" &&
                parsed.widgetMaterial == L"dusk",
            "valid widget backdrop and dusk material survive");
    Require(Settings::FromJson(LR"({"widgetMaterial":"color"})", Settings{}, parsed) && parsed.widgetMaterial == L"color", "color widget material survives");
    Require(Settings::FromJson(LR"({"stageExcluded":[" \t\r\n\u000B\ffoo.exe\f\u000B\n\r\t "," \t\r\n\u000B\f","\u3000bar.exe\u00A0"]})", Settings{}, parsed) &&
                parsed.stageExcluded == std::vector<std::wstring>{L"foo.exe", L"\u3000bar.exe\u00A0"},
            "stageExcluded trims ASCII whitespace only and preserves U+3000 and U+00A0");
    Require(Settings::FromJson(LR"({"language":"-bad"})", Settings{}, parsed) && parsed.language == L"auto", "leading dash language resets to auto");
    Require(Settings::FromJson(LR"({"language":"en-"})", Settings{}, parsed) && parsed.language == L"auto", "trailing dash language resets to auto");
    Require(Settings::FromJson(LR"({"language":"en--us"})", Settings{}, parsed) && parsed.language == L"auto", "double dash language resets to auto");
    Require(Settings::FromJson(LR"({"language":""})", Settings{}, parsed) && parsed.language == L"auto", "empty language resets to auto");
    Require(Settings::FromJson(LR"({"language":"zh"})", Settings{}, parsed) && parsed.language == L"zh-Hans", "zh normalizes to zh-Hans");
    Require(Settings::FromJson(LR"({"language":"zh-Hant"})", Settings{}, parsed) && parsed.language == L"zh-Hant", "valid subtag survives");
    Settings tinted;
    tinted.tint = 0x112233;
    Require(Settings::FromJson(LR"({"tint":"# 12345"})", tinted, parsed) && parsed.tint == 0x112233, "whitespace-padded tint is rejected and retains fallback");
    Require(Settings::FromJson(LR"({"tint":"#GGGGGG"})", tinted, parsed) && parsed.tint == 0x112233, "non-hex tint is rejected and retains fallback");
    Require(Settings::FromJson(LR"({"tint":"#1a2B3c"})", tinted, parsed) && parsed.tint == 0x1A2B3C, "strict ASCII hex tint parses");
}

void CheckDisplays()
{
    HMONITOR primary = MonitorFromPoint(POINT{}, MONITOR_DEFAULTTOPRIMARY);
    auto* external = reinterpret_cast<HMONITOR>(INT_PTR(2));
    std::vector<DisplayInfo> displays{{primary, L"primary", {0, 0, 1920, 1080}, {0, 0, 1920, 1040}, true},
                                      {external, L"external", {-2560, -200, 0, 1240}, {-2560, -200, 0, 1200}, false}};
    Require(ResolveDisplay(displays, L"external") == external, "select an external display with negative coordinates");
    Require(ResolveDisplay(displays, L"unplugged") == primary, "disconnected display falls back to system primary");
    displays.pop_back();
    Require(ResolveDisplay(displays, L"external") == primary, "unplug falls back without changing the saved ID");
    displays.push_back({external, L"external", {}, {}, false});
    Require(ResolveDisplay(displays, L"external") == external, "reconnection restores the selected display");
    auto actual = ConnectedDisplays();
    Require(!actual.empty(), "enumerate connected physical displays");
    for (const DisplayInfo& display : actual)
        Require(!display.id.empty() && ResolveDisplay(actual, display.id) == display.monitor, "enumerated device IDs resolve to their monitor");
}

void CheckTaskDistribution()
{
    auto primary = reinterpret_cast<HMONITOR>(INT_PTR(1)), external = reinterpret_cast<HMONITOR>(INT_PTR(2));
    HWND first = reinterpret_cast<HWND>(INT_PTR(11)), second = reinterpret_cast<HWND>(INT_PTR(22));
    auto monitorOf = [&](HWND window) { return window == first ? primary : external; };
    TaskItem pinned, shared, other;
    pinned.key = L"pin";
    pinned.pinned = true;
    pinned.windows = {first};
    pinned.active = pinned.attention = true;
    shared.key = L"shared";
    shared.windows = {first, second};
    shared.active = true;
    other.key = L"external";
    other.windows = {second};
    std::vector<TaskItem> items{pinned, shared, other};
    auto all = TaskItemsForDisplay(items, L"all", false, external, first, monitorOf);
    Require(all.size() == 3 && all[1].windows.size() == 2, "all taskbars include all app windows");
    auto main = TaskItemsForDisplay(items, L"primaryAndWindow", true, primary, first, monitorOf);
    Require(main.size() == 3 && main[1].windows.size() == 2, "primary taskbar includes every app");
    auto local = TaskItemsForDisplay(items, L"primaryAndWindow", false, external, first, monitorOf);
    Require(local.size() == 3 && local[0].windows.empty() && !local[0].active && !local[0].attention, "remote pins stay without running or active indicators");
    Require(local[1].windows == std::vector<HWND>{second} && !local[1].active, "window previews only include local windows");
    local = TaskItemsForDisplay(items, L"window", true, primary, first, monitorOf);
    Require(local.size() == 2 && local[1].windows == std::vector<HWND>{first} && local[1].active, "window-only mode also filters the primary taskbar");
    auto moved = TaskItemsForDisplay(items, L"window", false, external, second, [&](HWND) { return external; });
    Require(moved.size() == 3 && moved[0].windows.size() == 1 && moved[1].windows.size() == 2 && moved[1].active,
            "moving windows across screens updates membership and active state");
    Require(items[1].windows.size() == 2 && items[0].active, "per-display filtering leaves the shared model intact");
}

void CheckFullscreenCoverage()
{
    const auto primary = reinterpret_cast<HMONITOR>(INT_PTR(1)), external = reinterpret_cast<HMONITOR>(INT_PTR(2));
    const RECT screen{0, 0, 1920, 1080}, leftScreen{-2560, -200, 0, 1240};
    struct Window {
        HMONITOR monitor;
        RECT frame;
        bool maximized = false;
    };
    const Window fullscreen{primary, screen}, normal{primary, {100, 100, 900, 700}}, other{external, {-2400, 0, -1000, 900}};
    auto covered = [&](std::initializer_list<Window> windows) {
        return std::ranges::any_of(windows, [&](const Window& window) {
            return WindowCoverOnMonitor(window.monitor, window.frame, window.maximized, primary, screen).value_or(false);
        });
    };
    Require(covered({fullscreen}), "focused full-screen app hides its bar");
    Require(covered({other, fullscreen}), "focus on another monitor must not reveal the full-screen monitor's bar");
    Require(covered({{external, leftScreen}, fullscreen}), "each full-screen monitor hides its own bar independently");
    Require(covered({normal, fullscreen}), "a smaller window opened over a full-screen app keeps its bar hidden");
    Require(covered({other, normal, fullscreen}), "an unrelated foreground monitor does not reveal the bar either");
    Require(!covered({normal}), "normal windows alone keep the bar");
    Require(!covered({other}), "minimizing or closing the local full-screen app restores its bar");
    Require(covered({other, {primary, {0, 0, 1920, 1040}, true}}), "an inactive maximized local app keeps auto-hide");
    Require(covered({{primary, {}}, fullscreen}), "empty frames do not override a full-screen app");
    Require(WindowCoverOnMonitor(external, leftScreen, false, external, leftScreen) == true, "full-screen detection supports negative display coordinates");

    const RECT left{0, 0, 960, 1080}, right{960, 0, 1920, 1080}, gapRight{965, 0, 1920, 1080};
    Require(IsTile(left, screen, 8) && IsTile({960, 540, 1920, 1080}, screen, 8), "halves and quarters are tiles");
    Require(!IsTile({100, 100, 900, 700}, screen, 8) && !IsTile({-500, 0, 960, 1080}, screen, 8), "floating or off-screen windows are not tiles");
    Require(TilesFill({left, right}, screen, 8) && TilesFill({left, gapRight}, screen, 8), "two snapped halves fill the screen, a seam allowed");
    Require(!TilesFill({left}, screen, 8) && !TilesFill({left, {1200, 0, 1920, 1080}}, screen, 8), "a half or a gap leaves the screen open");
    Require(TilesFill({left, {960, 0, 1920, 540}, {960, 540, 1920, 1080}}, screen, 8), "a half and two quarters fill the screen");
    Require(TilesFill({{-2560, -200, -1280, 1240}, {-1280, -200, 0, 1240}}, leftScreen, 8), "tiles support negative display coordinates");

    Require(AtDisplayEdge(BarEdge::Bottom, {960, 1079}, screen) && AtDisplayEdge(BarEdge::Bottom, {960, 1078}, screen),
            "the local bottom edge can still reveal the bar");
    Require(!AtDisplayEdge(BarEdge::Bottom, {960, 1080}, screen) && !AtDisplayEdge(BarEdge::Bottom, {960, 1600}, screen),
            "a lower display must not repeatedly reveal the upper display's bar");
    Require(!AtDisplayEdge(BarEdge::Bottom, {1920, 1079}, screen) && !AtDisplayEdge(BarEdge::Bottom, {960, 1077}, screen),
            "another display or a point above the edge does not reveal the bar");
    Require(AtDisplayEdge(BarEdge::Top, {960, 0}, screen) && AtDisplayEdge(BarEdge::Top, {960, 1}, screen) && !AtDisplayEdge(BarEdge::Top, {960, 2}, screen) &&
                !AtDisplayEdge(BarEdge::Top, {960, -1}, screen),
            "top edge reveal covers only its two-pixel strip inside the display");
    Require(AtDisplayEdge(BarEdge::Left, {0, 540}, screen) && AtDisplayEdge(BarEdge::Left, {1, 540}, screen) &&
                !AtDisplayEdge(BarEdge::Left, {2, 540}, screen) && !AtDisplayEdge(BarEdge::Left, {-1, 540}, screen),
            "left edge reveal covers only its two-pixel strip inside the display");
    Require(AtDisplayEdge(BarEdge::Right, {1919, 540}, screen) && AtDisplayEdge(BarEdge::Right, {1918, 540}, screen) &&
                !AtDisplayEdge(BarEdge::Right, {1917, 540}, screen) && !AtDisplayEdge(BarEdge::Right, {1920, 540}, screen),
            "right edge reveal covers only its two-pixel strip inside the display");
    Require(AtDisplayEdge(BarEdge::Bottom, {-1280, 1239}, leftScreen) && AtDisplayEdge(BarEdge::Left, {-2560, 500}, leftScreen) &&
                AtDisplayEdge(BarEdge::Right, {-1, 500}, leftScreen) && !AtDisplayEdge(BarEdge::Right, {0, 500}, leftScreen) &&
                AtDisplayEdge(BarEdge::Top, {-1280, -200}, leftScreen),
            "edge reveal supports negative display coordinates without spilling onto neighbours");
}

void CheckNotificationLayout()
{
    const RECT work{0, 0, 1920, 1040};
    auto single = notification::TopRightLayout(work, 1, 384, 112, 10, 12, 1);
    auto burst = notification::TopRightLayout(work, 1, 384, 112, 10, 12, 5);
    Require(single.rect.right == 1908 && single.rect.top == 12, "popup stays at the primary work area's top right");
    Require(EqualRect(&single.rect, &burst.rect), "new arrivals do not resize or move the popup viewport");
    Require(burst.contentHeight == 5 * 112 + 4 * 10, "all notifications have full-height slots separated by gaps");
    const float oldTop = notification::CardTop(0, 112, 10, 0), newTop = notification::CardTop(1, 112, 10, 0);
    const float moving = motion::Approach(oldTop, newTop, 16, 16);
    Require(oldTop == 0 && newTop == 122 && moving > oldTop && moving < newTop, "a new top notification makes existing cards animate down");
    Require(notification::CardTop(2, 112, 10, 122) == 122, "overflow scrolls older notifications upward into view");
    auto many = notification::TopRightLayout(work, 1, 384, 112, 10, 12, 50);
    Require(many.contentHeight > many.viewHeight && many.rect.bottom <= work.bottom, "notification bursts stay inside the work area and can scroll");
    const RECT negative{-2560, -200, 0, 1200};
    auto scaled = notification::TopRightLayout(negative, 1.5f, 384, 112, 10, 12, 2);
    Require(scaled.rect.right == -18 && scaled.rect.top == -182 && scaled.rect.right - scaled.rect.left == 576,
            "top-right placement supports negative coordinates and monitor DPI");
    const RECT tiny{0, 40, 320, 220};
    auto narrow = notification::TopRightLayout(tiny, 2, 384, 112, 10, 12, 3);
    Require(narrow.rect.left >= tiny.left && narrow.rect.top >= tiny.top && narrow.rect.right <= tiny.right && narrow.rect.bottom <= tiny.bottom,
            "tiny or highly scaled work areas clamp the notification viewport");

    const float controls = 44 + 10; // existing centre capsule and card gap
    auto centre = notification::TopRightLayout(work, 1, 384, 112, 10, 12, 5, controls);
    Require(EqualRect(&centre.rect, &burst.rect), "the bell's notification centre uses the same top-right corner as popup notifications");
    Require(centre.contentTop == controls && centre.viewHeight + centre.contentTop == burst.viewHeight,
            "the centre reserves visible controls above its list without leaving the primary work area");
    Require(centre.contentHeight == burst.contentHeight && burst.contentTop == 0,
            "the notification centre shows every card while popup spacing stays unchanged");
    Require(centre.contentTop + notification::CardTop(0, 112, 10, 0) == controls && centre.contentTop + notification::CardTop(1, 112, 10, 0) == controls + 122,
            "the centre's newest notification starts below its controls and older notifications follow downward");
    auto emptyCentre = notification::TopRightLayout(work, 1, 384, 112, 10, 12, 0, controls);
    Require(EqualRect(&emptyCentre.rect, &centre.rect) && emptyCentre.contentHeight == 0 && emptyCentre.contentTop == controls,
            "an empty notification centre keeps its controls at the top-right corner");
    auto longCentre = notification::TopRightLayout(work, 1, 384, 112, 10, 12, 50, controls);
    Require(longCentre.contentHeight > longCentre.viewHeight && longCentre.rect.bottom <= work.bottom,
            "long centre lists scroll below the fixed top-right controls");
    auto scaledCentre = notification::TopRightLayout(negative, 1.5f, 384, 112, 10, 12, 2, controls);
    Require(EqualRect(&scaledCentre.rect, &scaled.rect) && scaledCentre.contentTop == controls,
            "the notification centre shares the popup's negative-coordinate and DPI placement");

    auto topLeft = notification::CornerLayout(work, 1, 384, 112, 10, 12, 1, 0, true, false);
    Require(topLeft.rect.left == 12 && topLeft.rect.top == 12 && topLeft.rect.right == 396, "the top left corner mirrors the top right one");
    auto bottomRight = notification::CornerLayout(work, 1, 384, 112, 10, 12, 5, controls, false, true);
    Require(EqualRect(&bottomRight.rect, &burst.rect) && bottomRight.contentTop == 0 && bottomRight.viewHeight == centre.viewHeight,
            "a bottom corner's centre keeps its controls below the cards, in the same room");
    auto bottomLeft = notification::CornerLayout(negative, 1.5f, 384, 112, 10, 12, 2, 0, true, true);
    Require(bottomLeft.rect.left == -2542 && bottomLeft.rect.bottom == 1182 && bottomLeft.rect.right - bottomLeft.rect.left == 576,
            "the bottom left corner supports negative coordinates and monitor DPI");
}

std::wstring Utf8ToWide(std::string_view bytes)
{
    if (bytes.empty())
        return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, bytes.data(), int(bytes.size()), nullptr, 0);
    if (n <= 0)
        return {};
    std::wstring wide(size_t(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, bytes.data(), int(bytes.size()), wide.data(), n);
    return wide;
}

std::string WideToUtf8(std::wstring_view wide)
{
    if (wide.empty())
        return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, wide.data(), int(wide.size()), nullptr, 0, nullptr, nullptr);
    if (n <= 0)
        return {};
    std::string bytes(size_t(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.data(), int(wide.size()), bytes.data(), n, nullptr, nullptr);
    return bytes;
}

void RunNormalizer()
{
    namespace json = winrt::Windows::Data::Json;
    std::string line;
    while (std::getline(std::cin, line)) {
        if (!line.empty() && line.back() == '\r')
            line.pop_back();
        if (line.empty())
            continue;
        json::JsonObject obj{nullptr};
        Require(json::JsonObject::TryParse(Utf8ToWide(line), obj), "valid ndjson line");
        Settings base;
        if (obj.HasKey(L"base") && obj.GetNamedValue(L"base").ValueType() == json::JsonValueType::Object)
            Require(Settings::FromJson(std::wstring(obj.GetNamedObject(L"base").Stringify()), Settings{}, base), "valid base");
        std::wstring patchText;
        if (obj.HasKey(L"patchText") && obj.GetNamedValue(L"patchText").ValueType() == json::JsonValueType::String) {
            patchText = std::wstring(obj.GetNamedString(L"patchText"));
        } else {
            Require(obj.HasKey(L"patch") && obj.GetNamedValue(L"patch").ValueType() == json::JsonValueType::Object, "patch object required");
            patchText = std::wstring(obj.GetNamedObject(L"patch").Stringify());
        }
        Settings out;
        if (Settings::FromJson(patchText, base, out))
            std::cout << R"({"ok":true,"result":)" << WideToUtf8(out.ToJson()) << "}\n";
        else
            std::cout << R"({"ok":false})" << '\n';
        std::cout.flush();
    }
}
} // namespace

int main(int argc, char** argv)
{
    try {
        winrt::init_apartment();
        if (std::any_of(argv + 1, argv + argc, [](const char* arg) { return std::string_view(arg) == "--normalize"; })) {
            RunNormalizer();
            return 0;
        }
        CheckSettings();
        CheckSettingsNormalization();
        CheckDisplays();
        CheckTaskDistribution();
        CheckFullscreenCoverage();
        CheckNotificationLayout();
        std::cout << "Display, fullscreen auto-hide, notification placement and animated reflow checks passed.\n";
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
