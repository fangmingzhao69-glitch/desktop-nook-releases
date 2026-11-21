#include "ime_status.h"

#include "keyboard_hook.h"
#include "../posted.h"

#include <imm.h>

#include <chrono>
#include <cwctype>
#include <memory>

namespace atrium {

namespace {

ImeWatcher* g_watcher = nullptr; // one watcher, installed on the UI thread

// CapsLock's latch as the system has it now. Skips hung windows and keeps `fallback` if AttachThreadInput fails (UIPI).
bool ForegroundCapsLock(HWND foreground, bool fallback)
{
    if (!foreground || IsHungAppWindow(foreground))
        return fallback;
    const DWORD foregroundThread = GetWindowThreadProcessId(foreground, nullptr);
    const DWORD current = GetCurrentThreadId();
    if (!foregroundThread || foregroundThread == current)
        return (GetKeyState(VK_CAPITAL) & 0x0001) != 0;
    if (!AttachThreadInput(current, foregroundThread, TRUE))
        return fallback;
    const bool caps = (GetKeyState(VK_CAPITAL) & 0x0001) != 0;
    AttachThreadInput(current, foregroundThread, FALSE);
    return caps;
}

// The focus window of the foreground thread (it can differ from the top-level
// window in an Electron/WebView2/WinUI3 host): the IME state belongs to it.
HWND FocusWindow(HWND foreground)
{
    if (!foreground)
        return nullptr;
    GUITHREADINFO info{sizeof(info)};
    if (GetGUIThreadInfo(0, &info)) { // 0: the foreground thread's queue
        if (HWND focus = info.hwndFocus ? info.hwndFocus : info.hwndActive)
            return focus;
    }
    return foreground;
}

// The entry's click: an IME on its native script goes to English, otherwise (off, or English) to its
// script; a keyboard without an IME goes to the next input language. The IME window takes a sent
// control message only (a posted one is dropped), hence the worker. Returns true when switched
// synchronously; false when a layout-change message was posted asynchronously.
bool Switch(HWND window, const ImeStatus& now)
{
    HWND ime = now.ime ? ImmGetDefaultIMEWnd(window) : nullptr;
    if (!ime) {
        PostMessageW(window, WM_INPUTLANGCHANGEREQUEST, 0, LPARAM(HKL_NEXT));
        return false;
    }
    const bool native = now.Latin();
    DWORD_PTR ignored = 0;
    if (native && !now.open)
        SendMessageTimeoutW(ime, WM_IME_CONTROL, IMC_SETOPENSTATUS, TRUE, SMTO_ABORTIFHUNG | SMTO_BLOCK, 120, &ignored);
    SendMessageTimeoutW(ime, WM_IME_CONTROL, IMC_SETCONVERSIONMODE, (native ? IME_CMODE_NATIVE : 0) | (now.fullShape ? IME_CMODE_FULLSHAPE : 0),
                        SMTO_ABORTIFHUNG | SMTO_BLOCK, 120, &ignored);
    return true;
}

} // namespace

std::wstring LanguageGlyph(LANGID langid)
{
    wchar_t locale[LOCALE_NAME_MAX_LENGTH]{};
    if (!LCIDToLocaleName(MAKELCID(langid, SORT_DEFAULT), locale, LOCALE_NAME_MAX_LENGTH, 0))
        return L"A";
    wchar_t name[160]{};
    const DWORD kinds[] = {LOCALE_SNATIVEDISPLAYNAME, LOCALE_SLOCALIZEDDISPLAYNAME};
    for (DWORD kind : kinds)
        if (GetLocaleInfoEx(locale, kind, name, DWORD(sizeof(name) / sizeof(name[0]))) && name[0])
            return std::wstring(1, name[0]);
    // Even the names are missing: the ISO code's first letter, upper-cased.
    if (GetLocaleInfoEx(locale, LOCALE_SISO639LANGNAME, name, DWORD(sizeof(name) / sizeof(name[0]))) && name[0])
        return std::wstring(1, wchar_t(towupper(name[0])));
    return L"A";
}

std::wstring ImeGlyph(const ImeStatus& status)
{
    if (status.Latin())
        return status.upper ? L"A" : L"a";
    if (status.caps && status.upper)
        return L"A";
    switch (PRIMARYLANGID(LOWORD(status.layout))) {
    case LANG_CHINESE: return L"中";
    case LANG_JAPANESE: return L"あ";
    case LANG_KOREAN: return L"가";
    default: return LanguageGlyph(LOWORD(status.layout));
    }
}

ImeStatus QueryImeStatus(HWND foreground, bool askIme)
{
    ImeStatus status;
    if (!foreground)
        return status;
    const DWORD thread = GetWindowThreadProcessId(foreground, nullptr);
    if (!thread)
        return status;
    const HKL hkl = GetKeyboardLayout(thread);
    status.layout = DWORD(reinterpret_cast<uintptr_t>(hkl) & 0xffffffff);
    if (!status.layout)
        return status;
    status.ime = ImmIsIME(hkl) != FALSE;
    if (!askIme) {
        status.reliable = false; // the identity alone: the control messages were not asked
        return status;
    }
    if (status.ime) {
        if (HWND ime = ImmGetDefaultIMEWnd(foreground)) {
            DWORD_PTR answer = 0;
            if (SendMessageTimeoutW(ime, WM_IME_CONTROL, IMC_GETOPENSTATUS, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 120, &answer))
                status.open = answer != 0;
            else
                status.reliable = false; // a stalled IME window answers nothing: not a state
            if (SendMessageTimeoutW(ime, WM_IME_CONTROL, IMC_GETCONVERSIONMODE, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 120, &answer)) {
                status.native = (answer & IME_CMODE_NATIVE) != 0;
                status.fullShape = (answer & IME_CMODE_FULLSHAPE) != 0;
            } else {
                status.reliable = false;
            }
        } else {
            status.reliable = false; // no window to ask: not a state, and not a false English either
        }
    }
    return status;
}

ImeWatcher::ImeWatcher(HWND window, UINT message)
    : window_(window),
      message_(message),
      wake_(CreateEventW(nullptr, FALSE, FALSE, nullptr)),
      caps_(ForegroundCapsLock(GetForegroundWindow(), false)), // initial latch; resynced on foreground change
      foreground_(SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nullptr, OnWinEvent, 0, 0, WINEVENT_OUTOFCONTEXT)),
      // CHANGE is not watched: it fires for every composition and candidate
      // change, which would read the IME on every key of a sentence. Shift and
      // Ctrl+Space cover the middle/English switch.
      ime_(SetWinEventHook(EVENT_OBJECT_IME_SHOW, EVENT_OBJECT_IME_HIDE, nullptr, OnWinEvent, 0, 0, WINEVENT_OUTOFCONTEXT)),
      keyToken_(KeyboardHook::Instance().Add(&ImeWatcher::OnKey, this))
{
    g_watcher = this;
    thread_ = std::thread([this] { Run(); });
    Wake(); // the state already in front, before the first switch
}

ImeWatcher::~ImeWatcher()
{
    stop_ = true;
    Wake();
    if (thread_.joinable())
        thread_.join();
    KeyboardHook::Instance().Remove(keyToken_);
    if (ime_)
        UnhookWinEvent(ime_);
    if (foreground_)
        UnhookWinEvent(foreground_);
    if (wake_)
        CloseHandle(wake_);
    g_watcher = nullptr;
}

void ImeWatcher::Toggle()
{
    toggle_ = true;
    Wake();
}

void ImeWatcher::Wake()
{
    if (wake_)
        SetEvent(wake_);
}

void ImeWatcher::Run()
{
    SetThreadDescription(GetCurrentThread(), L"Atrium IME");
    ImeStatus last; // the last complete read: carried through a stalled one
    HWND lastForeground = nullptr;
    while (!stop_) {
        WaitForSingleObject(wake_, INFINITE);
        if (stop_)
            break;
        // One read for a burst: a switch fires the IME window's event, may
        // move the foreground and comes with a key. Events during the wait and
        // the read set the event again for the next round.
        std::this_thread::sleep_for(std::chrono::milliseconds(60));
        HWND foreground = GetForegroundWindow();
        if (!foreground)
            continue;
        HWND focus = FocusWindow(foreground), asked = focus;
        ImeStatus status = QueryImeStatus(focus);
        if (status.Empty() && focus != foreground)
            status = QueryImeStatus(asked = foreground);
        if (toggle_.exchange(false) && status.reliable) {
            if (!Switch(asked, status))
                std::this_thread::sleep_for(std::chrono::milliseconds(50)); // WM_INPUTLANGCHANGEREQUEST is posted; let the foreground thread process it
            status = QueryImeStatus(asked);
        }
        if (foreground != GetForegroundWindow() || FocusWindow(foreground) != focus)
            continue; // the foreground moved while the IME was asked: not a state of anything
        if (foreground != lastForeground) {
            lastForeground = foreground;
            caps_ = ForegroundCapsLock(foreground, caps_.load());
        }
        if (!status.reliable && !status.Empty() && !last.Empty() && status.layout == last.layout)
            ImeCarryForward(status, last);
        if (status.reliable && !status.Empty())
            last = status;
        if (status.Empty() || !status.reliable)
            continue; // never announce a read that did not finish
        PostOwned(window_, message_, 0, std::make_unique<Sample>(Sample{foreground, status}));
    }
}

void CALLBACK ImeWatcher::OnWinEvent(HWINEVENTHOOK, DWORD event, HWND, LONG, LONG, DWORD, DWORD)
{
    if (!g_watcher)
        return;
    if (event == EVENT_SYSTEM_FOREGROUND || (event >= EVENT_OBJECT_IME_SHOW && event <= EVENT_OBJECT_IME_HIDE))
        g_watcher->Wake();
}

bool ImeShouldWakeOnKey(DWORD vk, bool down, bool switchModifierDown, bool syntheticReplay)
{
    if (vk == VK_SHIFT || vk == VK_LSHIFT || vk == VK_RSHIFT)
        return true;
    if (!down)
        return vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL;
    return vk == VK_CAPITAL || vk == VK_KANA || vk == VK_KANJI || (vk == VK_SPACE && (switchModifierDown || syntheticReplay));
}

bool ImeWatcher::OnKey(void* context, DWORD vk, DWORD /*scan*/, bool /*extended*/, bool down, bool /*injected*/, ULONG_PTR extraInfo)
{
    auto* self = static_cast<ImeWatcher*>(context);
    if (!self)
        return false;
    if (vk == VK_CAPITAL) {
        if (!down) {
            self->capsDown_ = false;
        } else if (!self->capsDown_) {
            self->capsDown_ = true;
            self->caps_ = !self->caps_.load();
        }
    }
    // Atrium's Win key hook replays the Space of Win+Space itself: the Win key
    // is already up when the replay arrives, so the combination check no longer sees it.
    const bool mod = vk == VK_SPACE && down && ((GetAsyncKeyState(VK_LWIN) | GetAsyncKeyState(VK_RWIN) | GetAsyncKeyState(VK_CONTROL)) & 0x8000) != 0;
    if (ImeShouldWakeOnKey(vk, down, mod, extraInfo == kAtriumSyntheticKey))
        self->Wake();
    return false; // never swallow a key
}

} // namespace atrium
