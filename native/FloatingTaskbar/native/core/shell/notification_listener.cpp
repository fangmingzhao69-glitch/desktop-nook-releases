#include "shell/notification_listener.h"

#include "log.h"
#include "../posted.h"

#include <winrt/Windows.ApplicationModel.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.Notifications.Management.h>
#include <winrt/Windows.UI.Notifications.h>

#include <algorithm>
#include <cwctype>
#include <map>

namespace atrium {

namespace wun = winrt::Windows::UI::Notifications;
namespace wunm = winrt::Windows::UI::Notifications::Management;

namespace {

const wchar_t* AccessName(wunm::UserNotificationListenerAccessStatus s)
{
    switch (s) {
    case wunm::UserNotificationListenerAccessStatus::Allowed: return L"Allowed";
    case wunm::UserNotificationListenerAccessStatus::Denied: return L"Denied";
    default: return L"Unspecified";
    }
}

wun::NotificationBinding FindBinding(const wun::NotificationVisual& visual)
{
    if (auto generic = visual.GetBinding(wun::KnownNotificationBindings::ToastGeneric()))
        return generic;
    auto bindings = visual.Bindings();
    return bindings.Size() > 0 ? bindings.GetAt(0) : nullptr;
}

// A toast's lines (title, then the body), each poll: senders update a toast in place. Reading its
// app (AppInfo, DisplayInfo: the app's resources) costs far more, so only a toast to post does.
std::wstring TextOf(const wun::UserNotification& un)
{
    try {
        std::wstring all;
        if (auto binding = FindBinding(un.Notification().Visual()))
            for (const auto& text : binding.GetTextElements())
                all += std::wstring(text.Text()) + L"\x1f";
        return all;
    } catch (const winrt::hresult_error&) {
        return {}; // dismissed or malformed while reading
    }
}

Notice ToNotice(const wun::UserNotification& un)
{
    Notice n;
    n.source = L"windows";
    n.externalId = un.Id();
    try {
        auto info = un.AppInfo();
        n.appId = std::wstring(info.AppUserModelId());
        n.app = std::wstring(info.DisplayInfo().DisplayName());
    } catch (const winrt::hresult_error&) {
        n.app.clear(); // unpackaged senders have no AppInfo
    }
    if (!n.appId.empty())
        n.action = L"app:" + n.appId;
    try {
        if (auto binding = FindBinding(un.Notification().Visual())) {
            for (const auto& text : binding.GetTextElements()) {
                std::wstring s(text.Text());
                if (std::ranges::all_of(s, [](wchar_t c) { return std::iswspace(static_cast<wint_t>(c)) != 0; }))
                    continue;
                if (n.title.empty())
                    n.title = std::move(s);
                else
                    n.body += (n.body.empty() ? L"" : L"\n") + s;
            }
        }
    } catch (const winrt::hresult_error&) {
        return n; // dismissed mid-read: keep whatever text and metadata arrived
    }
    return n;
}

void PollOnce(const wunm::UserNotificationListener& listener, HWND notify, UINT msg, std::map<uint32_t, size_t>& seen, bool first, int& posted)
{
    auto list = listener.GetNotificationsAsync(wun::NotificationKinds::Toast).get();
    std::map<uint32_t, size_t> now;
    const auto clock = winrt::clock::now();
    for (const auto& un : list) {
        // Reading a toast's text parses its XML: every second for every toast in the
        // notification centre, that was most of Atrium's idle CPU. A toast is read when
        // it is new, and while it is recent (senders update theirs in place soon after).
        auto old = seen.find(un.Id());
        const bool recent = clock - un.CreationTime() < std::chrono::minutes(2);
        if (old != seen.end() && !recent) {
            now[un.Id()] = old->second;
            continue;
        }
        size_t hash = first && !recent ? 0 : std::hash<std::wstring>{}(TextOf(un));
        now[un.Id()] = hash;
        if (first || (old != seen.end() && old->second == hash))
            continue;
        Notice n = ToNotice(un);
        if (n.title.empty() && n.body.empty())
            continue;
        std::wstring app = n.app; // the UI thread owns the notice once posted
        if (!PostOwned(notify, msg, 0, std::make_unique<Notice>(std::move(n))))
            continue;
        ++posted;
        if (posted <= 3 || posted % 50 == 0)
            Log(L"listener: %s toast from %s (%d so far)", old == seen.end() ? L"new" : L"updated", app.c_str(), posted);
    }
    seen = std::move(now);
}

} // namespace

NotificationListener::NotificationListener(HWND notify, UINT msg) : notify_(notify), msg_(msg), wake_(CreateEventW(nullptr, TRUE, FALSE, nullptr))
{
    thread_ = std::thread([this] { Run(); });
}

NotificationListener::~NotificationListener()
{
    stop_ = true;
    SetEvent(wake_);
    thread_.join();
    CloseHandle(wake_);
}

void NotificationListener::Run()
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    try {
        auto listener = wunm::UserNotificationListener::Current();
        auto access = listener.GetAccessStatus();
        if (access != wunm::UserNotificationListenerAccessStatus::Allowed) {
            // Asking needs a user prompt, which belongs to the settings, not to a
            // background thread; the user grants it in Windows privacy settings.
            Log(L"listener: access %s, not mirroring Windows notifications", AccessName(access));
        }
        // NotificationChanged fails for unpackaged processes:
        // poll instead. The first pass only records what is already there;
        // later a toast is posted when it appears and again when its text
        // changes (senders update a toast in place).
        std::map<uint32_t, size_t> seen; // id -> hash of its text
        bool first = true;
        int posted = 0;
        if (access == wunm::UserNotificationListenerAccessStatus::Allowed)
            Log(L"listener: mirroring Windows notifications (polling every second)");
        while (!stop_) {
            try {
                // The user can withdraw the access in Windows' privacy settings at any time.
                auto nowAccess = listener.GetAccessStatus();
                if (nowAccess != access) {
                    Log(L"listener: access %s", AccessName(nowAccess));
                    access = nowAccess;
                }
                if (access == wunm::UserNotificationListenerAccessStatus::Allowed) {
                    PollOnce(listener, notify_, msg_, seen, first, posted);
                    first = false;
                } else {
                    seen.clear();
                    first = true; // access restored later: existing notifications form a new baseline
                }
            } catch (const winrt::hresult_error& e) {
                Log(L"listener: read failed 0x%08X", unsigned(e.code()));
            }
            WaitForSingleObject(wake_, access == wunm::UserNotificationListenerAccessStatus::Allowed ? 1000 : 10000);
        }
    } catch (const winrt::hresult_error& e) {
        Log(L"listener: unavailable 0x%08X", unsigned(e.code()));
    }
    winrt::uninit_apartment();
}

} // namespace atrium
