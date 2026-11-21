// The shell's taskbar window. With the tray host on (shell/tray_host.h) Atrium has
// a window of the same class too, above Explorer's so that apps' tray calls reach
// it: every lookup of Explorer's taskbar goes through here, never FindWindow.
#pragma once

#include <windows.h>
#include <shellapi.h>

#include <atomic>

namespace atrium {

// The Shell_TrayWnd of the shell's process (the desktop's), else the first one of
// another process than `skip` (default: this one). No allocation: the crash
// handler and the guard use it.
inline HWND ShellTaskbar(DWORD skip = GetCurrentProcessId())
{
    DWORD shell = 0;
    if (HWND desktop = GetShellWindow())
        GetWindowThreadProcessId(desktop, &shell);
    HWND other = nullptr;
    for (HWND h = nullptr; (h = FindWindowExW(nullptr, h, L"Shell_TrayWnd", nullptr)) != nullptr;) {
        DWORD pid = 0;
        GetWindowThreadProcessId(h, &pid);
        if (shell && pid == shell)
            return h;
        if (pid != skip && !other)
            other = h;
    }
    return other;
}

// Set while Atrium itself asks the shell where a tray icon is (the tray reader):
// the tray host passes such questions to Explorer instead of answering them.
inline std::atomic_int g_shellQuestions{0};
struct ShellQuestion {
    ShellQuestion() { ++g_shellQuestions; }
    ShellQuestion(const ShellQuestion&) = delete;
    ShellQuestion& operator=(const ShellQuestion&) = delete;
    ~ShellQuestion() { --g_shellQuestions; }
};

// Above 0 while Atrium's tray host steps behind Explorer's taskbar for one of Atrium's own
// app-bar calls (AppBarMessage): it does not move itself back in front meanwhile.
inline std::atomic_int g_hostAside{0};

inline std::atomic<HWND> g_shellFailHwnd{nullptr};
inline std::atomic<ULONGLONG> g_shellFailAt{0};

inline bool IsShellCooldown(HWND w)
{
    if (!w || w != g_shellFailHwnd.load(std::memory_order_acquire))
        return false;
    const ULONGLONG at = g_shellFailAt.load(std::memory_order_relaxed), now = GetTickCount64();
    return now >= at && now - at < 2000;
}
inline void NoteShellResponsiveness(HWND w, bool ok)
{
    if (!ok && w) {
        g_shellFailAt.store(GetTickCount64(), std::memory_order_relaxed);
        g_shellFailHwnd.store(w, std::memory_order_release);
    } else if (ok && w && w == g_shellFailHwnd.load(std::memory_order_acquire))
        g_shellFailHwnd.store(nullptr, std::memory_order_release);
}
inline bool WindowResponsive(HWND w, UINT ms = 500)
{
    DWORD_PTR p;
    return w && !IsHungAppWindow(w) && SendMessageTimeoutW(w, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, ms, &p);
}
inline bool ShellResponsive(HWND shell, UINT timeoutMs = 500, bool honorCooldown = true)
{
    if (!shell || IsHungAppWindow(shell) || (honorCooldown && IsShellCooldown(shell)))
        return false;
    const bool ok = WindowResponsive(shell, timeoutMs);
    NoteShellResponsiveness(shell, ok);
    return ok;
}

// ABM_GETSTATE/ABM_SETSTATE always go via SendMessageTimeoutW(WM_COPYDATA) in AppBarMessage32 (bounded timeout, works
// whether TrayHost is first or not). Other AppBar calls step TrayHost behind Explorer's taskbar when first.
inline bool TryAppBarMessage(DWORD message, APPBARDATA& abd, UINT_PTR& out, UINT timeoutMs = 1500, UINT sendFlags = SMTO_ABORTIFHUNG, bool honorCooldown = true)
{
    HWND shell = ShellTaskbar();
    if (message != ABM_GETSTATE && message != ABM_SETSTATE) {
        if (!ShellResponsive(shell, timeoutMs < 500u ? timeoutMs : 500u, honorCooldown && message != ABM_REMOVE))
            return false;
        HWND firstWnd = FindWindowW(L"Shell_TrayWnd", nullptr);
        DWORD first = 0;
        GetWindowThreadProcessId(firstWnd, &first);
        struct HostAside {
            HWND host;
            explicit HostAside(HWND w) : host(w)
            {
                ++g_hostAside;
                // Synchronous SetWindowPos ensures SHAppBarMessage sees the updated z-order immediately.
                SetWindowPos(host, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
            ~HostAside()
            {
                SetWindowPos(host, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                --g_hostAside;
            }
            HostAside(const HostAside&) = delete;
            HostAside& operator=(const HostAside&) = delete;
        };
        if (first == GetCurrentProcessId()) {
            const HostAside aside{firstWnd};
            out = SHAppBarMessage(message, &abd);
        } else {
            out = SHAppBarMessage(message, &abd);
        }
        return true;
    }
    if (!shell || IsHungAppWindow(shell) || (honorCooldown && IsShellCooldown(shell)))
        return false;
    struct AppBarData32 {
        DWORD cbSize;
        DWORD hWnd;
        UINT uCallbackMessage;
        UINT uEdge;
        RECT rc;
        INT64 lParam;
    };
    struct AppBarMessage32 {
        AppBarData32 abd;
        DWORD dwMessage;
        DWORD padding1;
        UINT64 hSharedMemory;
        DWORD dwSourceProcessId;
        DWORD padding2;
    };
    static_assert(sizeof(AppBarData32) == 40 && sizeof(AppBarMessage32) == 64);
    AppBarMessage32 data{};
    data.abd.cbSize = sizeof(AppBarData32);
    data.abd.hWnd = DWORD(reinterpret_cast<ULONG_PTR>(abd.hWnd));
    data.abd.uCallbackMessage = abd.uCallbackMessage;
    data.abd.uEdge = abd.uEdge;
    data.abd.rc = abd.rc;
    data.abd.lParam = INT64(abd.lParam);
    data.dwMessage = message;
    data.dwSourceProcessId = GetCurrentProcessId();
    COPYDATASTRUCT cds{0, sizeof(data), &data};
    DWORD_PTR result = 0;
    if (!SendMessageTimeoutW(shell, WM_COPYDATA, reinterpret_cast<WPARAM>(abd.hWnd), reinterpret_cast<LPARAM>(&cds), sendFlags, timeoutMs, &result)) {
        if (GetLastError() == ERROR_TIMEOUT)
            NoteShellResponsiveness(shell, false);
        return false;
    }
    NoteShellResponsiveness(shell, true);
    out = result;
    return true;
}

inline UINT_PTR AppBarMessage(DWORD m, APPBARDATA& abd, UINT ms = 1500)
{
    UINT_PTR out = 0;
    return TryAppBarMessage(m, abd, out, ms) ? out : 0;
}
struct ShellProbe {
    HWND shell = ShellTaskbar();
    bool responsive = ShellResponsive(shell, 500);
    ULONGLONG lastProbeMs = GetTickCount64();
};
inline HRESULT ShellNotifyIconRect(const NOTIFYICONIDENTIFIER& id, RECT& rect, ShellProbe& probe)
{
    const ULONGLONG now = GetTickCount64();
    const bool reprobe = now - probe.lastProbeMs >= 500;
    if (!probe.responsive || !probe.shell || IsHungAppWindow(probe.shell) || (reprobe && !ShellResponsive(probe.shell, 500))) {
        probe.responsive = false;
        return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    }
    const ULONGLONG callStart = reprobe ? (probe.lastProbeMs = GetTickCount64()) : now;
    const ShellQuestion explorers;
    const HRESULT hr = Shell_NotifyIconGetRect(&id, &rect);
    if (SUCCEEDED(hr) || (GetTickCount64() - callStart < 500 && !IsHungAppWindow(probe.shell)))
        return hr;
    probe.responsive = false;
    return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
}

struct ForegroundAttach {
    DWORD me = GetCurrentThreadId(), other = 0;
    bool attached = false;
    explicit ForegroundAttach(HWND fg)
    {
        if (!fg || IsHungAppWindow(fg))
            return;
        other = GetWindowThreadProcessId(fg, nullptr);
        attached = other && other != me && AttachThreadInput(me, other, TRUE) != FALSE;
    }
    ~ForegroundAttach()
    {
        if (attached)
            AttachThreadInput(me, other, FALSE);
    }
    ForegroundAttach(const ForegroundAttach&) = delete;
    ForegroundAttach& operator=(const ForegroundAttach&) = delete;
};

} // namespace atrium
