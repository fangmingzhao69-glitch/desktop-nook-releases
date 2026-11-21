#include "keyboard_hook.h"

#include "log.h"

#include <algorithm>

namespace atrium {

namespace {

constexpr UINT kRehookMs = 10000;

std::atomic<KeyboardHook*> g_hook{nullptr}; // the hook procedure has no context of its own

} // namespace

KeyboardHook& KeyboardHook::Instance()
{
    static KeyboardHook hook;
    return hook;
}

KeyboardHook::~KeyboardHook()
{
    stop_ = true;
    if (const DWORD tid = threadId_.load())
        PostThreadMessageW(tid, WM_QUIT, 0, 0);
    if (thread_.joinable())
        thread_.join();
    g_hook = nullptr;
}

int KeyboardHook::Add(Handler handler, void* context)
{
    const std::lock_guard lifecycle(lifecycleMutex_);
    bool started = false;
    int token = 0;
    {
        const std::lock_guard lock(handlersMutex_);
        token = nextToken_++;
        handlers_.push_back({token, handler, context});
        if (handlers_.size() == 1) {
            stop_ = false;
            ready_ = false;
            started = true;
            thread_ = std::thread([this] { Run(); });
        }
    }
    if (started) {
        std::unique_lock lock(readyMutex_);
        readyCv_.wait(lock, [this] { return ready_.load(); });
    }
    return token;
}

void KeyboardHook::Remove(int token)
{
    const std::lock_guard lifecycle(lifecycleMutex_);
    std::thread finished;
    {
        const std::lock_guard lock(handlersMutex_);
        std::erase_if(handlers_, [&](const Entry& entry) { return entry.token == token; });
        if (!handlers_.empty())
            return;
        stop_ = true;
        if (const DWORD tid = threadId_.load())
            PostThreadMessageW(tid, WM_QUIT, 0, 0);
        finished = std::move(thread_);
    }
    if (finished.joinable())
        finished.join();
    threadId_ = 0;
}

void KeyboardHook::Run()
{
    g_hook = this;
    threadId_ = GetCurrentThreadId();
    MSG msg;
    PeekMessageW(&msg, nullptr, 0, 0, PM_NOREMOVE); // the thread's queue, for WM_QUIT
    HHOOK hook = SetWindowsHookExW(WH_KEYBOARD_LL, HookProc, GetModuleHandleW(nullptr), 0);
    if (hook)
        Log(L"keyboard: one low-level hook on its own thread");
    else
        Log(L"keyboard: could not watch the keyboard (%lu)", GetLastError());
    {
        const std::lock_guard lock(readyMutex_);
        ready_ = true;
    }
    readyCv_.notify_all();
    // Low-level hooks are called on this thread while it waits for messages:
    // it does nothing else, so a busy UI thread never delays a key. Windows
    // drops a hook that once answered too late without a word, so it is set
    // again now and then, between presses (#71).
    SetTimer(nullptr, 0, kRehookMs, nullptr);
    while (!stop_ && GetMessageW(&msg, nullptr, 0, 0) > 0) {
        if (msg.message != WM_TIMER) {
            DispatchMessageW(&msg);
        } else if (HHOOK again = SetWindowsHookExW(WH_KEYBOARD_LL, HookProc, GetModuleHandleW(nullptr), 0)) {
            if (hook)
                UnhookWindowsHookEx(hook);
            hook = again;
        }
    }
    if (hook)
        UnhookWindowsHookEx(hook);
    g_hook = nullptr;
}

LRESULT CALLBACK KeyboardHook::HookProc(int code, WPARAM wparam, LPARAM lparam)
{
    KeyboardHook* self = g_hook.load();
    const auto* key = reinterpret_cast<const KBDLLHOOKSTRUCT*>(lparam);
    if (code != HC_ACTION || !self)
        return CallNextHookEx(nullptr, code, wparam, lparam);
    const bool down = wparam == WM_KEYDOWN || wparam == WM_SYSKEYDOWN;
    const bool up = wparam == WM_KEYUP || wparam == WM_SYSKEYUP;
    if (!down && !up)
        return CallNextHookEx(nullptr, code, wparam, lparam);
    const bool injected = (key->flags & LLKHF_INJECTED) != 0;
    // A handler may have the key kept from Windows. Handlers run under the
    // recursive lock: Remove waits for the one in flight, so a destroyed handler
    // is never called, and a handler that sends a key (WinKey's replay) may
    // re-enter this hook on this same thread without deadlocking.
    const bool extended = (key->flags & LLKHF_EXTENDED) != 0;
    const std::lock_guard lock(self->handlersMutex_);
    for (const Entry& entry : self->handlers_)
        if (entry.handler(entry.context, key->vkCode, key->scanCode, extended, down, injected, key->dwExtraInfo))
            return 1;
    return CallNextHookEx(nullptr, code, wparam, lparam);
}

} // namespace atrium
