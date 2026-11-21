#include "shell/system_chrome.h"
#include "shell/taskbar_visibility.h"

#include "log.h"
#include "shell/shell_taskbar.h"
#include "settings.h"

#include <dwmapi.h>
#include <shellapi.h>
#include <objbase.h>
#include <winreg.h>

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>

#include <chrono>
#include <optional>
#include <thread>
#include <vector>

namespace json = winrt::Windows::Data::Json;

namespace atrium {

namespace {

// The shell's Do Not Disturb service (undocumented; the same one the Windows
// notification centre's button uses). After IUnknown: the selected profile as
// a CoTaskMemAlloc'd string, and setting it.
constexpr CLSID kQuietHoursSettings = {0xF53321FA, 0x34F8, 0x4B7F, {0xB9, 0xA3, 0x36, 0x18, 0x77, 0xCB, 0x94, 0xCF}};
constexpr IID kIQuietHoursSettings = {0x6BFF4732, 0x81EC, 0x4FFB, {0xAE, 0x67, 0xB6, 0xC1, 0xBC, 0x29, 0x63, 0x1F}};
struct IQuietHoursSettings : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE get_UserSelectedProfile(LPWSTR* profile) = 0;
    virtual HRESULT STDMETHODCALLTYPE put_UserSelectedProfile(LPCWSTR profile) = 0;
};

SystemChrome* g_chrome = nullptr;
TaskbarVisibility g_taskbarVisibility;

std::wstring StatePath()
{
    std::wstring p = SettingsPath();
    return p.substr(0, p.find_last_of(L'\\')) + L"\\system.json";
}

std::vector<HWND> Taskbars()
{
    std::vector<HWND> out;
    if (HWND main = ShellTaskbar())
        out.push_back(main);
    for (HWND w = nullptr; (w = FindWindowExW(nullptr, w, L"Shell_SecondaryTrayWnd", nullptr)) != nullptr;)
        out.push_back(w);
    return out;
}

bool IsTaskbar(HWND w)
{
    wchar_t cls[32] = {};
    GetClassNameW(w, cls, 32);
    return wcscmp(cls, L"Shell_TrayWnd") == 0 || wcscmp(cls, L"Shell_SecondaryTrayWnd") == 0;
}

std::optional<bool> QueryAutoHide()
{
    APPBARDATA abd{sizeof(abd)};
    UINT_PTR state = 0;
    if (!TryAppBarMessage(ABM_GETSTATE, abd, state))
        return std::nullopt;
    return (state & ABS_AUTOHIDE) != 0;
}

bool SetAutoHide(bool on, UINT timeoutMs = 1500, UINT sendFlags = SMTO_ABORTIFHUNG)
{
    APPBARDATA abd{sizeof(abd)};
    abd.hWnd = ShellTaskbar();
    abd.lParam = on ? ABS_AUTOHIDE : ABS_ALWAYSONTOP;
    UINT_PTR unused = 0;
    return TryAppBarMessage(ABM_SETSTATE, abd, unused, timeoutMs, sendFlags, on);
}

// Windows maximized while the taskbar still held its strip of the work area keep that gap when the
// area grows: not every app lays itself out again. Each one is stretched to its work area, its
// invisible borders outside it as Windows places a maximized window. A moment later: the shell
// moves the work area after the call returns.
void RefitMaximized()
{
    EnumWindows(
        [](HWND w, LPARAM) -> BOOL {
            MONITORINFO mi{sizeof(mi)};
            RECT r{}, f{};
            if (!IsWindowVisible(w) || !IsZoomed(w) || !GetMonitorInfoW(MonitorFromWindow(w, MONITOR_DEFAULTTONEAREST), &mi) || !GetWindowRect(w, &r) ||
                FAILED(DwmGetWindowAttribute(w, DWMWA_EXTENDED_FRAME_BOUNDS, &f, sizeof(f))))
                return TRUE;
            const RECT& a = mi.rcWork;
            if (f.left <= a.left + 1 && f.top <= a.top + 1 && f.right >= a.right - 1 && f.bottom >= a.bottom - 1)
                return TRUE;
            Log(L"chrome: a maximized window stopped short of the work area (%ld,%ld,%ld,%ld); fitted", f.left, f.top, f.right, f.bottom);
            SetWindowPos(w, nullptr, r.left + a.left - f.left, r.top + a.top - f.top, (r.right + a.right - f.right) - (r.left + a.left - f.left),
                         (r.bottom + a.bottom - f.bottom) - (r.top + a.top - f.top), SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            return TRUE;
        },
        0);
}

void ShowTaskbars()
{
    // Also used by the exception filter: enumerate without allocating.
    if (HWND w = ShellTaskbar())
        ShowWindowAsync(w, SW_SHOWNA);
    for (HWND w = nullptr; (w = FindWindowExW(nullptr, w, L"Shell_SecondaryTrayWnd", nullptr)) != nullptr;)
        ShowWindowAsync(w, SW_SHOWNA);
}

// AutoPlay's "a drive was inserted" toast is one of the system notifications
// Windows still shows in "alarms only" (that is why the USB banner survived Do
// Not Disturb). Its per-app "show banners" switch still applies: with the banner
// off the toast stays in the notification centre, where Atrium's listener reads
// it for a card, but Windows shows no banner.
constexpr wchar_t kAutoPlayKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Notifications\\Settings\\Windows.SystemToast.AutoPlay";
constexpr wchar_t kShowBanner[] = L"ShowBanner";

bool AutoPlayBannerShown()
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kAutoPlayKey, 0, KEY_QUERY_VALUE, &key) != ERROR_SUCCESS)
        return true; // no per-app override: Windows shows the banner
    DWORD value = 1, size = sizeof(value), type = 0;
    const LSTATUS status = RegQueryValueExW(key, kShowBanner, nullptr, &type, reinterpret_cast<BYTE*>(&value), &size);
    RegCloseKey(key);
    return status != ERROR_SUCCESS || type != REG_DWORD || value != 0;
}

bool SetAutoPlayBanner(bool shown)
{
    HKEY key = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, kAutoPlayKey, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr) != ERROR_SUCCESS)
        return false;
    LSTATUS status = ERROR_SUCCESS;
    if (shown) {
        // Absent is Windows' default; removing the override restores the banner.
        status = RegDeleteValueW(key, kShowBanner);
        if (status == ERROR_FILE_NOT_FOUND)
            status = ERROR_SUCCESS;
    } else {
        const DWORD value = 0;
        status = RegSetValueExW(key, kShowBanner, 0, REG_DWORD, reinterpret_cast<const BYTE*>(&value), sizeof(value));
    }
    RegCloseKey(key);
    return status == ERROR_SUCCESS;
}

} // namespace

std::wstring QuietProfile()
{
    IQuietHoursSettings* qh = nullptr;
    if (FAILED(CoCreateInstance(kQuietHoursSettings, nullptr, CLSCTX_ALL, kIQuietHoursSettings, reinterpret_cast<void**>(&qh))) || !qh)
        return {};
    LPWSTR p = nullptr;
    std::wstring out;
    if (SUCCEEDED(qh->get_UserSelectedProfile(&p)) && p)
        out = p;
    CoTaskMemFree(p);
    qh->Release();
    return out;
}

bool SetQuietProfile(const wchar_t* profile)
{
    IQuietHoursSettings* qh = nullptr;
    if (FAILED(CoCreateInstance(kQuietHoursSettings, nullptr, CLSCTX_ALL, kIQuietHoursSettings, reinterpret_cast<void**>(&qh))) || !qh)
        return false;
    bool ok = SUCCEEDED(qh->put_UserSelectedProfile(profile));
    qh->Release();
    return ok && QuietProfile() == profile;
}

SystemChrome::SystemChrome()
{
    g_chrome = this;
    g_taskbarVisibility.stopping = false;
    // Left over from a run that did not end cleanly: put it back first (the
    // settings then hide again what they ask for).
    UndoLeftover();
}

bool SystemChrome::UndoLeftover()
{
    std::wstring left = ReadTextFile(StatePath());
    if (left.empty())
        return false;
    Log(L"chrome: undoing the last run's changes: %s", left.c_str());
    json::JsonObject o{nullptr};
    if (!json::JsonObject::TryParse(left, o)) {
        ShowTaskbars();
        DeleteFileW(StatePath().c_str());
        return true;
    }
    auto flag = [&](const wchar_t* key) { return o.HasKey(key) && o.GetNamedValue(key).ValueType() == json::JsonValueType::Boolean && o.GetNamedBoolean(key); };
    if (flag(L"autoHide"))
        SetAutoHide(false);
    ShowTaskbars();
    if (flag(L"quiet") && QuietProfile() == kQuietProfile) {
        // The profile Atrium changed it from. Files from before this field have
        // no record and are restored to unrestricted, as they were then.
        std::wstring restore = kQuietProfileOff;
        if (o.HasKey(L"quietRestore") && o.GetNamedValue(L"quietRestore").ValueType() == json::JsonValueType::String)
            restore = std::wstring(o.GetNamedString(L"quietRestore"));
        if (restore.empty() || restore == kQuietProfile)
            restore = kQuietProfileOff;
        SetQuietProfile(restore.c_str());
        Log(L"chrome: Do Not Disturb restored (%s)", restore.c_str());
    }
    if (flag(L"autoPlayBanner")) {
        if (SetAutoPlayBanner(true))
            Log(L"chrome: AutoPlay banners restored");
        else
            Log(L"chrome: could not restore AutoPlay banners");
    }
    DeleteFileW(StatePath().c_str());
    return true;
}

void SystemChrome::ShowTaskbarsNow() { ShowTaskbars(); }

// Both the event hook and the periodic/settings hide paths honour this lease.
// No mutex is held across cross-thread ShowWindowAsync calls. The state outlives
// SystemChrome, so a worker finishing during shutdown cannot use a freed owner.
bool SystemChrome::RevealForClick()
{
    if (g_taskbarVisibility.stopping)
        return false;
    ++g_taskbarVisibility.reveals;
    ShowTaskbars();
    const HWND shell = ShellTaskbar();
    for (int i = 0; i < 20 && shell && !IsWindowVisible(shell) && !g_taskbarVisibility.stopping; ++i)
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    if (g_taskbarVisibility.stopping) {
        ConcealAfterClick();
        return false;
    }
    return true;
}

void SystemChrome::ConcealAfterClick()
{
    if (--g_taskbarVisibility.reveals == 0)
        HideTaskbars();
}

SystemChrome::~SystemChrome()
{
    RestoreForExit();
    g_chrome = nullptr;
}

void SystemChrome::RestoreForExit()
{
    if (exiting_)
        return;
    exiting_ = true;
    g_taskbarVisibility.stopping = true;
    SetTaskbarHidden(false);
    SetBannersHidden(false);
    StopRefit(true);
}

void SystemChrome::SetTaskbarHidden(bool hidden)
{
    if (hidden && exiting_)
        return;
    if (hidden == g_taskbarVisibility.hidden)
        return;
    g_taskbarVisibility.hidden = hidden;
    if (hidden) {
        // Auto-hide first: a hidden taskbar that is not auto-hide would still
        // keep its strip of the work area.
        if (const auto autoHide = QueryAutoHide(); autoHide.has_value() && !*autoHide) {
            SetAutoHide(true);
            autoHideOurs_ = true;
            ScheduleRefit();
        }
        Save();
        HideTaskbars();
        Watch();
    } else {
        StopRefit(false);
        if (hook_)
            UnhookWinEvent(hook_);
        hook_ = nullptr;
        hookedPid_ = 0;
        ShowTaskbars();
        if (autoHideOurs_ && (SetAutoHide(false) || !ShellTaskbar()))
            autoHideOurs_ = false;
        Save();
    }
    Log(L"chrome: system taskbar %s", hidden ? L"hidden" : L"shown");
}

void SystemChrome::SetBannersHidden(bool hidden)
{
    if (hidden && exiting_)
        return;
    // The setting is idempotent. A failed attempt (the service was unavailable)
    // or a setting the user changed since is retried on the next apply; turning
    // it off retries a restore that failed.
    if (hidden == bannersHidden_) {
        if (hidden ? (quietReady_ && autoPlayBannerReady_) : (!quietOurs_ && !autoPlayBannerOurs_))
            return;
    }
    bannersHidden_ = hidden;
    const std::wstring now = QuietProfile();
    const QuietPlan plan = PlanQuiet(now, hidden, quietOurs_, quietBefore_);
    if (plan.action == QuietAction::Hide) {
        if (SetQuietProfile(plan.profile.c_str())) {
            quietBefore_ = now;
            quietOurs_ = true;
            quietReady_ = true;
            // "Priority only" and "unrestricted" both let banners through; only
            // alarms-only hides the system's own toasts as well as apps'.
            Log(L"chrome: Do Not Disturb on (alarms only; was %s)", now.c_str());
        } else {
            quietReady_ = false;
            Log(L"chrome: Do Not Disturb could not be turned on (was %s)", now.c_str());
        }
    } else if (plan.action == QuietAction::Restore) {
        quietReady_ = false;
        if (now == kQuietProfile) {
            if (SetQuietProfile(plan.profile.c_str()))
                Log(L"chrome: Do Not Disturb restored (%s)", plan.profile.c_str());
            else
                Log(L"chrome: Do Not Disturb could not be restored (%s)", plan.profile.c_str());
        } else if (!now.empty()) {
            // The user chose another profile while it was Atrium's: leave theirs.
            Log(L"chrome: Do Not Disturb changed by the user (%s); left as it is", now.c_str());
        }
        quietOurs_ = false;
        quietBefore_.clear();
    } else if (hidden) {
        if (now.empty()) {
            quietReady_ = false;
            Log(L"chrome: Do Not Disturb is not available; banners stay");
        } else {
            quietReady_ = true;
            Log(L"chrome: Do Not Disturb already hides banners (alarms only); left as it is");
        }
    } else {
        quietReady_ = false;
        Log(L"chrome: Do Not Disturb off (banners shown)");
    }

    // AutoPlay's toast goes through "alarms only", so its own banner switch is
    // what keeps the USB notification quiet.
    if (hidden) {
        if (!autoPlayBannerReady_) {
            if (!AutoPlayBannerShown()) {
                autoPlayBannerReady_ = true;
                Log(L"chrome: AutoPlay banners already off; left as it is");
            } else if (SetAutoPlayBanner(false)) {
                autoPlayBannerOurs_ = true;
                autoPlayBannerReady_ = true;
                Log(L"chrome: AutoPlay banners off (the drive toast bypasses Do Not Disturb)");
            } else {
                Log(L"chrome: could not turn off AutoPlay banners");
            }
        }
    } else {
        autoPlayBannerReady_ = false;
        if (autoPlayBannerOurs_) {
            if (SetAutoPlayBanner(true)) {
                autoPlayBannerOurs_ = false;
                Log(L"chrome: AutoPlay banners restored");
            } else {
                // Keep the ownership so system.json (and the guard) retries it.
                Log(L"chrome: could not restore AutoPlay banners");
            }
        }
    }
    Save();
}

void SystemChrome::Enforce()
{
    if (exiting_)
        return;
    if (g_taskbarVisibility.ShouldHide()) {
        // Hidden but not auto-hide, the taskbar keeps its strip of the work area and maximized
        // windows stop its height short of the bottom. Auto-hide can be lost after it was set:
        // set before Explorer's taskbar was ready at sign-in, Explorer restarted, another program.
        if (const auto autoHide = QueryAutoHide(); autoHide.has_value() && !*autoHide) {
            SetAutoHide(true);
            autoHideOurs_ = true;
            Save();
            Log(L"chrome: the taskbar was no longer auto-hide; set again");
            ScheduleRefit();
        }
        HideTaskbars();
        Watch(); // Explorer may have been restarted
    }
    if (quietReady_) {
        const std::wstring now = QuietProfile();
        if (!now.empty() && now != kQuietProfile) {
            // The user turned it off (or chose another mode): theirs from now on.
            Log(L"chrome: Do Not Disturb changed by the user (%s); no longer hidden by Atrium", now.c_str());
            quietReady_ = false;
            quietOurs_ = false;
            quietBefore_.clear();
            Save();
        }
    }
    if (autoPlayBannerOurs_ && AutoPlayBannerShown()) {
        // The user turned AutoPlay banners back on: theirs from now on.
        Log(L"chrome: AutoPlay banners changed by the user; no longer hidden by Atrium");
        autoPlayBannerOurs_ = false;
        autoPlayBannerReady_ = false;
        Save();
    }
}

void SystemChrome::EmergencyRestore()
{
    g_taskbarVisibility.stopping = true;
    if (!g_chrome || !g_taskbarVisibility.hidden.exchange(false))
        return;
    // Stop OnShow/Enforce hiding Explorer again before making it visible.
    // The exception can be on a worker, so these flags are atomic. Do not
    // log or save here; system.json still restores Do Not Disturb next start.
    ShowTaskbars();
    if (g_chrome->autoHideOurs_.exchange(false))
        SetAutoHide(false, 250, SMTO_ABORTIFHUNG | SMTO_BLOCK);
}

void SystemChrome::HideTaskbars()
{
    if (!g_taskbarVisibility.ShouldHide())
        return;
    for (HWND w : Taskbars())
        if (g_taskbarVisibility.ShouldHide() && IsWindowVisible(w))
            ShowWindowAsync(w, SW_HIDE);
    // A concurrent reveal or exit can have restored the taskbar while a
    // cross-thread ShowWindowAsync was in flight. Restoration must win that race.
    if (!g_taskbarVisibility.ShouldHide())
        ShowTaskbars();
}

void SystemChrome::Watch()
{
    // Explorer shows its taskbar again by itself now and then: hide it the
    // moment it does.
    DWORD pid = 0;
    GetWindowThreadProcessId(ShellTaskbar(), &pid);
    if (!pid || pid == hookedPid_)
        return;
    if (hook_)
        UnhookWinEvent(hook_);
    hook_ = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, nullptr, OnShow, pid, 0, WINEVENT_OUTOFCONTEXT);
    hookedPid_ = hook_ ? pid : 0;
}

void CALLBACK SystemChrome::OnShow(HWINEVENTHOOK, DWORD, HWND w, LONG obj, LONG, DWORD, DWORD)
{
    if (obj == OBJID_WINDOW && g_taskbarVisibility.ShouldHide() && w && IsTaskbar(w)) {
        HideTaskbars();
        Log(L"chrome: Explorer showed its taskbar; hidden again");
    }
}

void SystemChrome::ScheduleRefit()
{
    {
        std::lock_guard lock(refitMutex_);
        if (refitStop_)
            return;
        refitPending_ = true;
        refitAt_ = std::chrono::steady_clock::now() + std::chrono::milliseconds(500);
        if (!refitThread_.joinable())
            refitThread_ = std::thread([this] { RefitWorker(); });
    }
    refitCv_.notify_one();
}

void SystemChrome::StopRefit(bool stop)
{
    {
        std::lock_guard lock(refitMutex_);
        refitStop_ = refitStop_ || stop;
        refitPending_ = false;
    }
    refitCv_.notify_all();
    if (stop && refitThread_.joinable())
        refitThread_.join();
}

void SystemChrome::RefitWorker()
{
    for (;;) {
        std::unique_lock lock(refitMutex_);
        refitCv_.wait(lock, [this] { return refitStop_ || refitPending_; });
        if (refitStop_)
            break;
        const auto deadline = refitAt_;
        if (refitCv_.wait_until(lock, deadline, [this, deadline] { return refitStop_ || !refitPending_ || refitAt_ != deadline; }))
            continue;
        refitPending_ = false;
        lock.unlock();
        if (g_taskbarVisibility.hidden && !g_taskbarVisibility.stopping)
            RefitMaximized();
    }
}

void SystemChrome::Save() const
{
    if (!g_taskbarVisibility.hidden && !autoHideOurs_ && !quietOurs_ && !autoPlayBannerOurs_) {
        DeleteFileW(StatePath().c_str());
        return;
    }
    json::JsonObject o;
    o.SetNamedValue(L"taskbarHidden", json::JsonValue::CreateBooleanValue(g_taskbarVisibility.hidden.load()));
    o.SetNamedValue(L"autoHide", json::JsonValue::CreateBooleanValue(autoHideOurs_.load()));
    o.SetNamedValue(L"quiet", json::JsonValue::CreateBooleanValue(quietOurs_));
    if (quietOurs_ && !quietBefore_.empty())
        o.SetNamedValue(L"quietRestore", json::JsonValue::CreateStringValue(quietBefore_));
    if (autoPlayBannerOurs_)
        o.SetNamedValue(L"autoPlayBanner", json::JsonValue::CreateBooleanValue(true));
    WriteTextFile(StatePath(), std::wstring(o.Stringify()));
}

} // namespace atrium
