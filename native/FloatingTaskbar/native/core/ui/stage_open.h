// Launches from the bar that join the stage once their window arrives, not whichever app
// opens next. Several can wait at once (A, then B, added before either window shows); each
// lasts ten seconds, ends when its window has joined, and all end when the stage changes.
#pragma once

#include <cstdint>
#include <map>
#include <string>

namespace atrium {

class StageOpenIntent {
public:
    void Arm(const std::wstring& app, uint64_t now) { apps_[app] = now + 10000; }
    void Cancel() { apps_.clear(); }
    void Done(const std::wstring& app) { apps_.erase(app); }
    bool Matches(const std::wstring& app, uint64_t now)
    {
        std::erase_if(apps_, [&](const auto& item) { return now >= item.second; });
        return apps_.contains(app);
    }

private:
    std::map<std::wstring, uint64_t> apps_;
};

} // namespace atrium
