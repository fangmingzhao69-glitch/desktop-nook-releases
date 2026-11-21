// A pinned bar entry as stored in settings.json ("pins", docs/SETTINGS-PROTOCOL.md).
#pragma once

#include <optional>
#include <string>

namespace atrium {

struct PinnedApp {
    std::wstring name;  // display name
    std::wstring path;  // .lnk, .exe or other launchable file; may be empty for Store apps
    std::wstring args;  // extra arguments when `path` is an .exe
    std::wstring aumid; // AppUserModelID, empty when unknown
    std::optional<bool> multipleWindows; // absent: browsers and Explorer allow new windows; others reuse
    bool operator==(const PinnedApp&) const = default;
};

} // namespace atrium
