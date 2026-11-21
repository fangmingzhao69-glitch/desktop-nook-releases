// A value handed to a window's thread with a message: once posted the window owns
// it and takes it back with TakePosted; a message that could not be posted drops it.
#pragma once

#include <windows.h>

#include <memory>

namespace atrium {

template<class T> bool PostOwned(HWND hwnd, UINT message, WPARAM wParam, std::unique_ptr<T> value)
{
    if (!PostMessageW(hwnd, message, wParam, reinterpret_cast<LPARAM>(value.get())))
        return false;
    static_cast<void>(value.release());
    return true; // NOLINT(clang-analyzer-cplusplus.NewDeleteLeaks): the window owns it now
}

template<class T> std::unique_ptr<T> TakePosted(LPARAM lParam) { return std::unique_ptr<T>(reinterpret_cast<T*>(lParam)); }

} // namespace atrium
