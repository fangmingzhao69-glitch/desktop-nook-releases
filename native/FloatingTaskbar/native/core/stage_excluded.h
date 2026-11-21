// Entries are executable names (wechat.exe), package prefixes (microsoft.screensketch_) or
// complete app keys. An app is excluded when its key matches any of them; comparison is
// case-insensitive. Excluded apps are left out of Stage Manager entirely.
#pragma once

#include <windows.h>

#include <algorithm>
#include <string>
#include <vector>

namespace atrium {

// Lowered as TaskModel lowers app keys (CharLowerBuffW), so a path with É matches its key.
inline std::wstring LowerAppKey(std::wstring value)
{
    if (!value.empty())
        CharLowerBuffW(value.data(), DWORD(value.size()));
    return value;
}

inline bool StageAppExcluded(const std::vector<std::wstring>& excluded, const std::wstring& key)
{
    // A window key appends '|' and the window handle; match against the app part only, so
    // older saved stage entries and per-window keys are recognized too.
    std::wstring appKey = key;
    if (const size_t bar = appKey.rfind(L'|'); bar != std::wstring::npos)
        appKey.resize(bar);
    const std::wstring app = LowerAppKey(appKey);
    const size_t slash = app.find_last_of(L"\\/");
    const std::wstring file = slash == std::wstring::npos ? app : app.substr(slash + 1);
    return std::ranges::any_of(excluded, [&](const std::wstring& raw) {
        const std::wstring entry = LowerAppKey(raw);
        // Prefix matching is limited to directory ('\' or '/') and package ('_') prefixes.
        return !entry.empty() &&
               (app == entry || file == entry || ((entry.back() == L'\\' || entry.back() == L'/' || entry.back() == L'_') && app.starts_with(entry)));
    });
}

// The Windows screenshot tools that must not be swallowed by the stage while they record or
// capture: their own overlays/control bars are separate windows, and putting them away hides
// pause/stop from the user. Other screenshot apps can be added in Settings.
inline std::vector<std::wstring> DefaultStageExcluded() { return {L"snippingtool.exe", L"screenclippinghost.exe", L"microsoft.screensketch_"}; }

} // namespace atrium
