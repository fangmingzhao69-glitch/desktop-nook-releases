#include "shell/tray_match.h"
#include <iostream>
#include <stdexcept>
using namespace atrium::tray;
void Check(bool ok, const char* name)
{
    if (!ok)
        throw std::runtime_error(name);
}
Record R(const std::wstring& name, size_t order, bool promoted = false)
{
    Record r;
    r.id = std::to_wstring(order);
    r.exe = name + L".exe";
    r.tip = name;
    r.identity = L"guid:" + name;
    r.live = true;
    r.promoted = promoted;
    r.order = order;
    r.names = {name};
    r.bounds = {900, 1000, 920, 1020};
    return r;
}
int main()
{
    try {
        auto steam = R(L"Steam", 0, true), qq = R(L"QQ", 1), tailscale = R(L"Tailscale", 2);
        steam.bounds = {20, 20, 40, 40};
        std::vector<Record> rs{steam, qq, tailscale};
        auto result = Match({{L"Steam", true, steam.bounds}}, rs);
        Check(result.items.size() == 3 && result.items[1].button == kNone, "unopened overflow supplements live records");
        Check(result.sharedPlace[1] && result.sharedPlace[2], "different exe sharedPlace regression");
        result = Match({{L"Steam", true, steam.bounds}, {L"QQ", false}, {L"Tailscale", false}}, rs);
        Check(result.items.size() == 3 && result.items[2].record == 2, "opened overflow has no duplicates");
        result = Match({{L"QQ", false}}, rs);
        Check(result.items.size() == 3, "partial overflow still supplements missing identities");
        auto nvidia = R(L"NVIDIA", 0), second = nvidia;
        second.id = L"1";
        second.identity = L"uid:42:100:2";
        second.order = 1;
        result = Match({}, {nvidia, second});
        Check(result.items.size() == 2 && result.sharedPlace[0], "same exe GUID and UID survive shared rectangle and identical names");
        result = Match({{L"NVIDIA", false}, {L"NVIDIA", false}}, {nvidia, second});
        Check(result.items.size() == 2 && result.items[0].method == Method::Order, "same exe distinct icons use consistent full order");
        auto old = qq;
        old.id = L"old";
        old.order = 100;
        result = Match({}, {old, qq});
        Check(result.items.size() == 1 && result.items[0].record == 1 && result.aliases[0] == 1, "re-registration aliases deduplicate by live identity");
        old.identity = L"guid:obsolete";
        old.live = false;
        result = Match({}, {old, qq});
        Check(result.items.size() == 1, "dead registration never resurrects");
        qq.live = false;
        result = Match({{L"QQ", false}}, {qq});
        Check(result.items.size() == 1 && result.items[0].record == kNone, "stale tooltip cannot claim live UIA button");
        result = Match({{L"Steam - 正在同步", false}}, {}, {{L"steam.exe", {L"Steam"}}});
        Check(result.items.size() == 1 && result.items[0].method == Method::Running, "Windows 10 running program fallback and changing tooltip");
        result = Match({{L"", false}, {L"", false}}, {nvidia, second});
        Check(result.items.size() == 2 && result.items[1].record == 1, "empty tooltip complete ordering");
        result = Match({{L"QQ", false}}, {}, {{L"a.exe", {L"QQ"}}, {L"b.exe", {L"QQ"}}});
        Check(result.items[0].program == kNone, "ambiguous program name is not ownership");
        result = Match({{L"Steam", false}}, {}, {{L"browser.exe", {L"Steam - website"}, 0}});
        Check(result.items[0].program == kNone, "window title prefix cannot claim another app");
        for (const auto& tip : {L"SteamVR", L"SteamHelper", L"QQMusic"}) {
            result = Match({{tip, false}}, {}, {{L"steam.exe", {L"Steam"}}, {L"qq.exe", {L"QQ"}}});
            Check(result.items[0].program == kNone, "program prefix needs a real status delimiter");
        }
        result = Match({{L"STEAM：正在同步", false}}, {}, {{L"steam.exe", {L"Steam"}}});
        Check(result.items[0].program == 0, "localized status delimiter and case remain supported");
        auto unnamedOrder = R(L"Steam", kNone);
        result = Match({{L"SteamVR", false}}, {unnamedOrder});
        Check(result.items[0].record == kNone, "registration name prefix also needs delimiter");
        auto a = R(L"Alpha", 0), b = R(L"Beta", 1);
        result = Match({{L"Beta", false}, {L"Alpha", false}}, {a, b});
        Check(result.items.size() == 2 && result.items[0].record == 1, "names override obsolete order");
        result = Match({{L"Alpha", false}, {L"当前线路：香港", false}}, {a, b});
        Check(result.items.size() == 2 && result.items[1].method == Method::Order, "fully changed tooltip uses consistent complete order");
        result = Match({{L"", false, a.bounds}}, {a, b});
        Check(result.items[0].record == kNone, "shared rectangle never becomes unique after consumption");
        Check(Title(L" \r\n Steam \r\n status") == L"Steam", "tooltip first line");
        std::cout << "tray matching: all scenarios passed\n";
    } catch (const std::exception& e) {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
