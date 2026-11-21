// Periodic release checks belong to the resident process, not the settings window.
#pragma once

#include "notice.h"

#include <windows.h>
#include <functional>
#include <optional>
#include <thread>
#include <string>

namespace atrium {

class UpdateChecker {
public:
    UpdateChecker(HWND notify, UINT message);
    ~UpdateChecker();
    UpdateChecker(const UpdateChecker&) = delete;
    UpdateChecker& operator=(const UpdateChecker&) = delete;
    // UI thread; completion or next startup. `show` gets the update card, or nothing to clear it.
    void NotifyPrepared(const std::function<void(std::optional<Notice> card, bool silent)>& show);

private:
    void Run();
    HWND notify_;
    UINT message_;
    HANDLE stop_;
    std::wstring cache_;
    std::thread worker_;
};

} // namespace atrium
