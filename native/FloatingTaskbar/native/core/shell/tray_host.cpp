#include "shell/tray_host.h"

#include "log.h"
#include "shell/shell_icon.h"
#include "shell/shell_taskbar.h"
#include "win32_path.h"

#include <objbase.h>
#include <shellapi.h>
#include <shlwapi.h>

#include <algorithm>
#include <iterator>

namespace atrium {

namespace {

std::atomic<TrayHost*> g_host{nullptr}; // the running host, for its event hook

constexpr UINT_PTR kKeepTimer = 1;      // keep the host first among Shell_TrayWnd windows; post changes
constexpr UINT kKeepMs = 100;
constexpr UINT_PTR kPruneTimer = 2;     // icons of windows that are gone without a NIM_DELETE
constexpr UINT kPruneMs = 2000;
constexpr UINT_PTR kPopupTimer = 3;     // the dwell before a popup request (NIN_POPUPOPEN)
constexpr UINT kPopupMs = 500;
constexpr UINT_PTR kRecollectTimer = 4; // after Explorer's own TaskbarCreated, the host's
constexpr UINT kRecollectMs = 1500;
constexpr DWORD kForwardMs = 4000;      // as long as an app waits for Explorer anyway
constexpr DWORD kSignature = 0x34753423;
constexpr uint64_t kImageBase = 1ull << 62; // apart from TrayModel's own picture versions
constexpr wchar_t kTaskband[] = L"TaskbandHWND"; // Explorer's MSTaskSwWClass, on its Shell_TrayWnd

// The shell's wire format (WM_COPYDATA dwData 1): handles as 32-bit values, the
// same for 32- and 64-bit apps.
struct NotifyIconData32 {
    DWORD cbSize;
    DWORD hWnd;
    UINT uID;
    UINT uFlags;
    UINT uCallbackMessage;
    DWORD hIcon;
    WCHAR szTip[128];
    DWORD dwState;
    DWORD dwStateMask;
    WCHAR szInfo[256];
    UINT uVersion; // also uTimeout
    WCHAR szInfoTitle[64];
    DWORD dwInfoFlags;
    GUID guidItem;
    DWORD hBalloonIcon;
};
struct TrayNotifyData {
    DWORD signature;
    DWORD message;
    NotifyIconData32 nid;
};
// Shell_NotifyIconGetRect (WM_COPYDATA dwData 3).
struct NotifyIconIdentifier32 {
    DWORD signature;
    DWORD message; // 1: the top left corner, 2: the bottom right
    DWORD cbSize;
    DWORD padding;
    DWORD hWnd;
    UINT uID;
    GUID guidItem;
};

HWND Handle(DWORD value) { return reinterpret_cast<HWND>(static_cast<ULONG_PTR>(value)); }

std::wstring KeyOf(HWND hwnd, UINT uid, const GUID* guid)
{
    wchar_t key[64];
    if (guid && *guid != GUID{}) {
        StringFromGUID2(*guid, key, 64);
        return L"guid:" + std::wstring(key);
    }
    swprintf_s(key, L"hwnd:%p:%u", static_cast<void*>(hwnd), uid);
    return key;
}

std::wstring ProcessPath(DWORD pid)
{
    std::wstring out;
    if (HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)) {
        wchar_t path[MAX_PATH * 2];
        DWORD size = DWORD(std::size(path));
        if (QueryFullProcessImageNameW(p, 0, path, &size))
            out.assign(path, size);
        CloseHandle(p);
    }
    return out;
}

DWORD PidOf(HWND h)
{
    DWORD pid = 0;
    if (h)
        GetWindowThreadProcessId(h, &pid);
    return pid;
}

// The shell's process: the desktop's (Explorer, or whatever shell Windows runs).
DWORD ShellPid() { return PidOf(GetShellWindow()); }

const wchar_t* Name(DWORD message)
{
    switch (message) {
    case NIM_ADD: return L"add";
    case NIM_MODIFY: return L"modify";
    case NIM_DELETE: return L"delete";
    case NIM_SETFOCUS: return L"focus";
    case NIM_SETVERSION: return L"version";
    default: return L"?";
    }
}

const wchar_t* Name(tray::HostAction action)
{
    switch (action) {
    case tray::HostAction::Left: return L"click";
    case tray::HostAction::Right: return L"right click";
    case tray::HostAction::Middle: return L"middle click";
    case tray::HostAction::Double: return L"double click";
    case tray::HostAction::DoubleContinuation: return L"second click";
    case tray::HostAction::Open: return L"open";
    default: return L"hover";
    }
}

} // namespace

TrayHost::TrayHost(HWND notify, UINT msg, DWORD readyWaitMs, DWORD startDelayMs)
    : notify_(notify),
      msg_(msg),
      startDelayMs_(startDelayMs),
      taskbarCreated_(RegisterWindowMessageW(L"TaskbarCreated")),
      quit_(RegisterWindowMessageW(L"Atrium.TrayHost.Quit")),
      hover_(RegisterWindowMessageW(L"Atrium.TrayHost.Hover")),
      ready_(CreateEventW(nullptr, TRUE, FALSE, nullptr)),
      stop_(CreateEventW(nullptr, TRUE, FALSE, nullptr))
{
    thread_ = std::thread([this] { Run(); });
    if (ready_)
        WaitForSingleObject(ready_, readyWaitMs);
}

TrayHost::~TrayHost()
{
    stopping_ = true;
    if (HWND w = window_.load()) {
        // Behind Explorer's taskbar first, so that calls from now on go to Explorer; the
        // ones already on their way are answered before the window goes.
        SetWindowPos(w, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Sleep(100);
        PostMessageW(w, quit_, 0, 0);
    }
    if (stop_)
        SetEvent(stop_);
    if (thread_.joinable())
        thread_.join();
    if (ready_)
        CloseHandle(ready_);
    if (stop_)
        CloseHandle(stop_);
}

void TrayHost::Run()
{
    if (!stop_ || WaitForSingleObject(stop_, startDelayMs_) == WAIT_OBJECT_0) {
        if (ready_)
            SetEvent(ready_);
        return;
    }
    HINSTANCE instance = GetModuleHandleW(nullptr);
    WNDCLASSEXW wc{sizeof(wc)};
    wc.lpfnWndProc = Proc;
    wc.hInstance = instance;
    wc.lpszClassName = L"Shell_TrayWnd";
    WNDCLASSEXW child{sizeof(child)};
    child.lpfnWndProc = DefWindowProcW;
    child.hInstance = instance;
    child.lpszClassName = L"TrayNotifyWnd";
    const bool registered = RegisterClassExW(&wc) != 0;
    DWORD error = registered ? 0 : GetLastError();
    RegisterClassExW(&child);
    RECT r{};
    if (HWND shell = ShellTaskbar())
        GetWindowRect(shell, &r);
    HWND w = nullptr;
    if (registered && WaitForSingleObject(stop_, 0) != WAIT_OBJECT_0) {
        w = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, L"Shell_TrayWnd", L"", WS_POPUP, r.left, r.top, r.right - r.left,
                            r.bottom - r.top, nullptr, nullptr, instance, this);
        if (!w)
            error = GetLastError();
    }
    if (!w) {
        if (WaitForSingleObject(stop_, 0) != WAIT_OBJECT_0)
            Log(L"tray host: no window (%lu)", error);
        UnregisterClassW(L"Shell_TrayWnd", instance);
        UnregisterClassW(L"TrayNotifyWnd", instance);
        if (ready_)
            SetEvent(ready_);
        return;
    }
    // Some apps place things by the notification area inside the taskbar.
    CreateWindowExW(0, L"TrayNotifyWnd", L"", WS_CHILD, 0, 0, r.right - r.left, r.bottom - r.top, w, nullptr, instance, nullptr);
    // Apps at a lower integrity send their icons too, as they do to Explorer.
    ChangeWindowMessageFilterEx(w, WM_COPYDATA, MSGFLT_ALLOW, nullptr);
    ChangeWindowMessageFilterEx(w, taskbarCreated_, MSGFLT_ALLOW, nullptr);
    window_ = w;
    // Explorer's taskbar comes before the host when it is shown or activated: back at
    // once on those events (the timer below is the fallback), so that no app's call
    // goes past the host meanwhile.
    g_host = this;
    HWINEVENTHOOK shown = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, nullptr, ShellEvent, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    HWINEVENTHOOK foreground =
        SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nullptr, ShellEvent, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    KeepFirst();
    SetTimer(w, kKeepTimer, kKeepMs, nullptr);
    SetTimer(w, kPruneTimer, kPruneMs, nullptr);
    if (ready_)
        SetEvent(ready_);
    if (!yielded_ && !stopping_)
        Broadcast();

    for (;;) {
        if (WaitForSingleObject(stop_, 0) == WAIT_OBJECT_0)
            break;
        MSG m;
        bool quit = false;
        while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) {
            if (m.message == WM_QUIT) {
                quit = true;
                break;
            }
            TranslateMessage(&m);
            DispatchMessageW(&m);
            if (WaitForSingleObject(stop_, 0) == WAIT_OBJECT_0) {
                quit = true;
                break;
            }
        }
        if (quit)
            break;
        const DWORD wait = MsgWaitForMultipleObjectsEx(1, &stop_, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        if (wait == WAIT_OBJECT_0 || wait == WAIT_FAILED)
            break;
    }
    if (IsWindow(w)) {
        RemovePropW(w, kTaskband);
        DestroyWindow(w);
    }
    if (shown)
        UnhookWinEvent(shown);
    if (foreground)
        UnhookWinEvent(foreground);
    g_host = nullptr;
    window_ = nullptr;
    UnregisterClassW(L"Shell_TrayWnd", instance);
    UnregisterClassW(L"TrayNotifyWnd", instance);
    Log(L"tray host: off");
}

// The apps already running add their icons again, now through the host.
void TrayHost::Broadcast()
{
    broadcastAt_ = GetTickCount64();
    SendNotifyMessageW(HWND_BROADCAST, taskbarCreated_, 0, 0);
    Log(L"tray host: on, apps asked to add their icons again");
}

void CALLBACK TrayHost::ShellEvent(HWINEVENTHOOK, DWORD, HWND hwnd, LONG object, LONG, DWORD, DWORD)
{
    if (object != OBJID_WINDOW || !hwnd)
        return;
    wchar_t cls[32] = {};
    if (!GetClassNameW(hwnd, cls, 32) || wcscmp(cls, L"Shell_TrayWnd") != 0)
        return;
    if (TrayHost* host = g_host.load())
        host->KeepFirst(); // the host's own thread: the hook is out of context
}

LRESULT CALLBACK TrayHost::Proc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    if (m == WM_NCCREATE)
        SetWindowLongPtrW(h, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams));
    auto* self = reinterpret_cast<TrayHost*>(GetWindowLongPtrW(h, GWLP_USERDATA));
    if (!self)
        return DefWindowProcW(h, m, w, l);
    try {
        return self->Message(h, m, w, l);
    } catch (...) {
        // Whatever went wrong here, the app's call still reaches Explorer.
        Log(L"tray host: message 0x%04X failed; passed on", m);
        if (m == WM_COPYDATA || m == WM_COMMAND || m >= WM_USER)
            return self->Forward(m, w, l);
        return DefWindowProcW(h, m, w, l);
    }
}

LRESULT TrayHost::Message(HWND h, UINT m, WPARAM w, LPARAM l)
{
    switch (m) {
    case WM_COPYDATA: {
        const auto* cds = reinterpret_cast<const COPYDATASTRUCT*>(l);
        if (cds && cds->dwData == 1)
            return OnNotifyIcon(w, cds);
        if (cds && cds->dwData == 3)
            return OnIconRect(w, cds);
        return Forward(m, w, l); // app bars (0) and the rest: Explorer's
    }
    case WM_TIMER:
        if (w == kKeepTimer) {
            KeepFirst();
            {
                std::lock_guard g(mutex_);
                const ULONGLONG now = GetTickCount64();
                const bool flashing = std::ranges::any_of(icons_, [&](const auto& i) { return i.second.flashUntil > now; });
                if (flashing != flashing_) {
                    flashing_ = flashing;
                    dirty_ = true; // a flash began or ended: the bar lights or dims the app
                }
            }
            if (dirty_.exchange(false)) {
                PostMessageW(notify_, msg_, 0, 0);
                LogList();
            }
        } else if (w == kPruneTimer) {
            Prune();
        } else if (w == kPopupTimer) {
            KillTimer(h, kPopupTimer);
            Popup(true);
        } else if (w == kRecollectTimer) {
            KillTimer(h, kRecollectTimer);
            if (!yielded_)
                Broadcast();
        }
        return 0;
    case WM_WINDOWPOSCHANGING:
        // Never shown, whoever asks: it only stands in for Explorer's taskbar.
        reinterpret_cast<WINDOWPOS*>(l)->flags &= ~SWP_SHOWWINDOW;
        return 0;
    case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    default: break;
    }
    if (m == quit_ && InSendMessageEx(nullptr) == ISMEX_NOSEND) { // posted by ~TrayHost
        RemovePropW(h, kTaskband);
        DestroyWindow(h);
        return 0;
    }
    if (m == hover_) {
        std::wstring key;
        POINT at{};
        {
            std::lock_guard g(mutex_);
            key = hoverKey_;
            at = hoverAt_;
        }
        if (key != popupKey_)
            Popup(false);
        KillTimer(h, kPopupTimer);
        if (!key.empty()) {
            Send(key, tray::HostAction::HoverIn, at);
            SetTimer(h, kPopupTimer, kPopupMs, nullptr);
        }
        return 0;
    }
    if (m == taskbarCreated_) {
        // The host's own broadcast comes back here; any other is Explorer restarting. Its
        // new taskbar may have come first for a moment, and the apps' new icons gone to it
        // only: once in front again the host asks for them once more.
        if (GetTickCount64() - broadcastAt_ > 3000) {
            Log(L"tray host: Explorer restarted");
            KeepFirst();
            SetTimer(h, kRecollectTimer, kRecollectMs, nullptr);
        }
        return 0;
    }
    // A close, a command, a private message of the taskbar: Explorer's, as if sent to it.
    if (m == WM_CLOSE || m == WM_COMMAND || m == WM_SYSCOMMAND || m >= WM_USER)
        return Forward(m, w, l);
    return DefWindowProcW(h, m, w, l);
}

LRESULT TrayHost::Forward(UINT m, WPARAM w, LPARAM l)
{
    HWND shell = ShellTaskbar();
    if (!shell)
        return 0;
    if (m == WM_COPYDATA || InSendMessage()) {
        DWORD_PTR result = 0;
        if (!SendMessageTimeoutW(shell, m, w, l, SMTO_ABORTIFHUNG, kForwardMs, &result))
            return 0;
        return LRESULT(result);
    }
    PostMessageW(shell, m, w, l);
    return 0;
}

LRESULT TrayHost::OnNotifyIcon(WPARAM w, const COPYDATASTRUCT* cds)
{
    const auto* data = static_cast<const TrayNotifyData*>(cds->lpData);
    if (!data || cds->cbData < offsetof(TrayNotifyData, nid.szInfo) || data->signature != kSignature)
        return Forward(WM_COPYDATA, w, reinterpret_cast<LPARAM>(cds));
    const NotifyIconData32& nid = data->nid;

    // The picture is the app's: copied now, while the app waits for this call.
    std::shared_ptr<const IconImage> image;
    if ((data->message == NIM_ADD || data->message == NIM_MODIFY) && (nid.uFlags & NIF_ICON) && nid.hIcon) {
        try {
            IconImage img;
            if (HIconToImage(reinterpret_cast<HICON>(static_cast<ULONG_PTR>(nid.hIcon)), img))
                image = std::make_shared<const IconImage>(std::move(img));
        } catch (...) {
            image = nullptr;
        }
    }

    // Explorer first: what it answers is what the app is told.
    const bool shell = ShellTaskbar() != nullptr;
    LRESULT result = Forward(WM_COPYDATA, w, reinterpret_cast<LPARAM>(cds));
    if (data->message == NIM_ADD && !result && shell) {
        // The host's TaskbarCreated (not a new Explorer): Explorer has the icon already
        // and refuses a second add. It is the same icon, brought up to date.
        std::vector<BYTE> copy(static_cast<const BYTE*>(cds->lpData), static_cast<const BYTE*>(cds->lpData) + cds->cbData);
        auto* again32 = reinterpret_cast<TrayNotifyData*>(copy.data());
        again32->message = NIM_MODIFY;
        // Apps adding their icon again often leave the tooltip empty (QQ, Feishu): Explorer
        // keeps the one it has rather than losing it to the host's broadcast.
        if ((again32->nid.uFlags & NIF_TIP) && !again32->nid.szTip[0])
            again32->nid.uFlags &= ~NIF_TIP;
        COPYDATASTRUCT again{cds->dwData, cds->cbData, copy.data()};
        result = Forward(WM_COPYDATA, w, reinterpret_cast<LPARAM>(&again));
    }
    if (!shell && data->message != NIM_DELETE)
        result = TRUE; // no Explorer for the moment: the host keeps the icon (it adds it again on TaskbarCreated)
    try {
        Record(cds, std::move(image), result);
    } catch (...) {
        Log(L"tray host: could not record a %s", Name(data->message));
    }
    return result;
}

void TrayHost::Record(const COPYDATASTRUCT* cds, std::shared_ptr<const IconImage> image, LRESULT result)
{
    const auto* data = static_cast<const TrayNotifyData*>(cds->lpData);
    const NotifyIconData32& nid = data->nid;
    const bool hasGuid = (nid.uFlags & NIF_GUID) && cds->cbData >= offsetof(TrayNotifyData, nid.guidItem) + sizeof(GUID);
    const HWND hwnd = Handle(nid.hWnd);
    const std::wstring key = KeyOf(hwnd, nid.uID, hasGuid ? &nid.guidItem : nullptr);
    const bool hasVersion = cds->cbData >= offsetof(TrayNotifyData, nid.szInfoTitle);

    std::lock_guard g(mutex_);
    auto it = icons_.find(key);
    if (data->message == NIM_DELETE) {
        if (it != icons_.end()) {
            icons_.erase(it);
            dirty_ = true;
        }
        return;
    }
    if (data->message == NIM_SETVERSION) {
        if (it != icons_.end() && hasVersion) {
            it->second.version = nid.uVersion;
            dirty_ = true;
        }
        return;
    }
    if (data->message != NIM_ADD && data->message != NIM_MODIFY)
        return;
    if (it == icons_.end()) {
        // A change of an icon the host has not seen (added before it started, the app
        // ignored TaskbarCreated) that Explorer took: the host learns it from here.
        if (!result)
            return;
        // The shell's own icons (volume, network...) stay Explorer's: Atrium has its control centre.
        const DWORD pid = PidOf(hwnd);
        if (!pid || pid == ShellPid())
            return;
        HostIcon icon;
        icon.key = key;
        icon.added = ++nextAdded_;
        it = icons_.emplace(key, std::move(icon)).first;
    }
    HostIcon& icon = it->second;
    if (hwnd && icon.hwnd != hwnd) {
        icon.hwnd = hwnd;
        icon.pid = PidOf(hwnd);
        std::wstring path = ProcessPath(icon.pid);
        icon.exe = path.empty() ? L"" : FoldCase(AppExecutable(path));
    }
    icon.uid = nid.uID;
    if (nid.uFlags & NIF_MESSAGE)
        icon.callback = nid.uCallbackMessage;
    if (nid.uFlags & NIF_ICON) {
        icon.image = std::move(image);
        icon.imageVersion = kImageBase + ++nextImage_;
        // Four picture changes within three seconds: it flashes (an icon blinking for a message).
        const ULONGLONG now = GetTickCount64();
        std::erase_if(icon.pictureChanges, [&](ULONGLONG t) { return now - t > 3000; });
        icon.pictureChanges.push_back(now);
        if (icon.pictureChanges.size() >= 4)
            icon.flashUntil = now + 2500;
    }
    if (nid.uFlags & NIF_TIP)
        icon.tip.assign(nid.szTip, wcsnlen(nid.szTip, std::size(nid.szTip)));
    icon.showTip = (nid.uFlags & NIF_SHOWTIP) != 0 || icon.showTip;
    if ((nid.uFlags & NIF_STATE) && (nid.dwStateMask & NIS_HIDDEN))
        icon.hidden = (nid.dwState & NIS_HIDDEN) != 0;
    if (data->message == NIM_ADD)
        Log(L"tray host: add '%s' (%s) -> %ld", icon.tip.substr(0, icon.tip.find(L'\n')).c_str(), icon.exe.empty() ? L"-" : PathFindFileNameW(icon.exe.c_str()),
            long(result));
    dirty_ = true;
}

LRESULT TrayHost::OnIconRect(WPARAM w, const COPYDATASTRUCT* cds)
{
    const auto* id = static_cast<const NotifyIconIdentifier32*>(cds->lpData);
    if (!id || cds->cbData < sizeof(NotifyIconIdentifier32) || id->signature != kSignature || g_shellQuestions > 0)
        return Forward(WM_COPYDATA, w, reinterpret_cast<LPARAM>(cds));
    const std::wstring key = KeyOf(Handle(id->hWnd), id->uID, &id->guidItem);
    RECT r{};
    {
        std::lock_guard g(mutex_);
        if (icons_.contains(key))
            r = key == clickedKey_ && !IsRectEmpty(&clicked_) ? clicked_ : anchor_;
    }
    if (IsRectEmpty(&r))
        return Forward(WM_COPYDATA, w, reinterpret_cast<LPARAM>(cds));
    return id->message == 1 ? MAKELONG(WORD(SHORT(r.left)), WORD(SHORT(r.top))) : MAKELONG(WORD(SHORT(r.right)), WORD(SHORT(r.bottom)));
}

// Shell_NotifyIcon finds the first Shell_TrayWnd in the z-order. Explorer's comes
// before the host now and then (its taskbar is topmost too): the host moves back.
// Another program's (RetroBar, ExplorerPatcher's, a second shell) is left in front:
// two hosts pushing in turn would take the icons from both.
void TrayHost::KeepFirst()
{
    HWND w = window_.load();
    if (!w || stopping_ || g_hostAside > 0)
        return;
    HWND shell = ShellTaskbar();
    RECT want{}, have{};
    if (shell && GetWindowRect(shell, &want) && GetWindowRect(w, &have) && !EqualRect(&want, &have))
        SetWindowPos(w, nullptr, want.left, want.top, want.right - want.left, want.bottom - want.top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW);
    // ITaskbarList::HrInit takes Explorer's taskband from the first Shell_TrayWnd, else fails with
    // E_NOTIMPL: apps that show progress on their button would not start (TeraCopy #90, NanaZip #86).
    if (HANDLE band = shell ? GetPropW(shell, kTaskband) : nullptr; band != GetPropW(w, kTaskband))
        SetPropW(w, kTaskband, band);
    HWND first = FindWindowW(L"Shell_TrayWnd", nullptr);
    if (first == w) {
        if (yielded_.exchange(false)) {
            Log(L"tray host: in front again");
            dirty_ = true;
            SetTimer(w, kRecollectTimer, 200, nullptr); // updates went past the host meanwhile
        }
        return;
    }
    const DWORD owner = PidOf(first);
    if (first && owner != GetCurrentProcessId() && owner != PidOf(shell)) {
        if (!yielded_.exchange(true)) {
            Log(L"tray host: another program's tray (pid %lu) is in front: left there", owner);
            dirty_ = true;
        }
        return;
    }
    SetWindowPos(w, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    const bool again = FindWindowW(L"Shell_TrayWnd", nullptr) == w;
    const ULONGLONG now = GetTickCount64();
    if (now - lostAt_ > 60000) // not on every round
        Log(L"tray host: Explorer's taskbar came first; moved back (%s)", again ? L"first again" : L"still second");
    lostAt_ = now;
}

// The list in the log whenever its make-up changes (not on a picture or tooltip change).
void TrayHost::LogList()
{
    std::wstring keys;
    std::vector<std::wstring> lines;
    {
        std::lock_guard g(mutex_);
        for (const auto& [key, icon] : icons_) {
            keys += key + (icon.hidden ? L"h;" : L";");
            wchar_t line[400];
            swprintf_s(line, L"tray host:   %s %s v%u cb=0x%X%s%s img=%dx%d '%s'", key.c_str(), icon.exe.empty() ? L"-" : PathFindFileNameW(icon.exe.c_str()),
                       icon.version, icon.callback, icon.hidden ? L" hidden" : L"", icon.showTip ? L" showtip" : L"", icon.image ? icon.image->width : 0,
                       icon.image ? icon.image->height : 0, icon.tip.substr(0, icon.tip.find(L'\n')).substr(0, 60).c_str());
            lines.emplace_back(line);
        }
    }
    if (keys == loggedKeys_)
        return;
    loggedKeys_ = keys;
    Log(L"tray host: %zu icons", lines.size());
    for (const auto& line : lines)
        Log(L"%s", line.c_str());
}

void TrayHost::Prune()
{
    std::lock_guard g(mutex_);
    const size_t before = icons_.size();
    std::erase_if(icons_, [](const auto& item) { return item.second.hwnd && !IsWindow(item.second.hwnd); });
    if (icons_.size() != before)
        dirty_ = true;
}

std::vector<HostIcon> TrayHost::Icons() const
{
    std::lock_guard g(mutex_);
    std::vector<HostIcon> out;
    out.reserve(icons_.size());
    for (const auto& [key, icon] : icons_)
        out.push_back(icon);
    std::ranges::sort(out, [](const HostIcon& a, const HostIcon& b) { return a.added < b.added; });
    return out;
}

std::vector<std::wstring> TrayHost::Flashing() const
{
    std::lock_guard g(mutex_);
    std::vector<std::wstring> out;
    const ULONGLONG now = GetTickCount64();
    for (const auto& [key, icon] : icons_)
        if (icon.flashUntil > now && !icon.exe.empty())
            out.push_back(icon.exe);
    return out;
}

void TrayHost::SetPlaces(RECT anchor)
{
    std::lock_guard g(mutex_);
    anchor_ = anchor;
}

void TrayHost::Hover(const std::wstring& key, POINT at)
{
    HWND w = window_.load();
    if (!w)
        return;
    {
        std::lock_guard g(mutex_);
        if (key == hoverKey_)
            return;
        hoverKey_ = key;
        hoverAt_ = at;
    }
    PostMessageW(w, hover_, 0, 0);
}

// Host thread: a version 4 app that draws its own tooltip is asked to show it after
// the dwell (NIN_POPUPOPEN) and to close it when the pointer leaves (NIN_POPUPCLOSE).
void TrayHost::Popup(bool open)
{
    std::wstring key;
    POINT at{};
    {
        std::lock_guard g(mutex_);
        key = open ? hoverKey_ : popupKey_;
        at = hoverAt_;
        if (open) {
            auto it = icons_.find(key);
            if (key.empty() || it == icons_.end() || it->second.showTip || it->second.version < 4)
                return;
        }
    }
    if (key.empty())
        return;
    Send(key, open ? tray::HostAction::PopupOpen : tray::HostAction::PopupClose, at);
    popupKey_ = open ? key : L"";
}

bool TrayHost::Send(const std::wstring& key, tray::HostAction action, POINT at)
{
    HWND hwnd = nullptr;
    UINT uid = 0, callback = 0, version = 0;
    DWORD pid = 0;
    {
        std::lock_guard g(mutex_);
        auto it = icons_.find(key);
        if (it == icons_.end())
            return false;
        hwnd = it->second.hwnd;
        uid = it->second.uid;
        callback = it->second.callback;
        version = it->second.version;
        pid = it->second.pid;
        if (action != tray::HostAction::HoverIn && action != tray::HostAction::PopupOpen && action != tray::HostAction::PopupClose) {
            clickedKey_ = key;
            clicked_ = {at.x - 16, at.y - 16, at.x + 16, at.y + 16};
        }
    }
    if (!callback || !hwnd || !IsWindow(hwnd))
        return false;
    const bool click = action != tray::HostAction::HoverIn && action != tray::HostAction::PopupOpen && action != tray::HostAction::PopupClose;
    // Atrium has the foreground (its panel): the app may take it for its window or menu.
    if (click)
        AllowSetForegroundWindow(pid);
    bool ok = true;
    for (UINT notification : tray::CallbackSequence(action, version)) {
        WPARAM wp = 0;
        LPARAM lp = 0;
        tray::EncodeCallback(version, uid, notification, at, wp, lp);
        if (!SendNotifyMessageW(hwnd, callback, wp, lp)) {
            ok = false; // an elevated app's window refuses Atrium's messages (UIPI)
            break;
        }
    }
    if (click)
        Log(L"tray host: %s on %s v%u -> %s", Name(action), key.c_str(), version, ok ? L"sent" : L"refused");
    return ok;
}

} // namespace atrium
