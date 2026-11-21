// Long-path Win32 module/process image queries and Unicode path case folding.
#pragma once

#include <windows.h>

#include <string>

namespace atrium {

inline std::wstring ModulePath(HMODULE module = nullptr)
{
    std::wstring path(MAX_PATH, L'\0');
    for (;;) {
        const DWORD len = GetModuleFileNameW(module, path.data(), DWORD(path.size()));
        if (len == 0)
            return {};
        if (len < path.size()) {
            path.resize(len);
            return path;
        }
        if (path.size() >= 32768)
            return {};
        const size_t next = path.size() * 2;
        path.resize(next > 32768 ? 32768 : next);
    }
}

inline std::wstring ProcessImagePath(HANDLE process)
{
    if (!process)
        return {};
    wchar_t stack[MAX_PATH * 2];
    DWORD len = DWORD(sizeof(stack) / sizeof(stack[0]));
    if (QueryFullProcessImageNameW(process, 0, stack, &len))
        return std::wstring(stack, len);
    if (GetLastError() != ERROR_INSUFFICIENT_BUFFER)
        return {};
    std::wstring path(32768, L'\0');
    len = DWORD(path.size());
    if (!QueryFullProcessImageNameW(process, 0, path.data(), &len))
        return {};
    path.resize(len);
    return path;
}

inline std::wstring ProcessImagePath(DWORD pid)
{
    struct ScopedHandle {
        HANDLE handle;
        explicit ScopedHandle(HANDLE h) : handle(h) {}
        ScopedHandle(const ScopedHandle&) = delete;
        ScopedHandle& operator=(const ScopedHandle&) = delete;
        ~ScopedHandle()
        {
            if (handle)
                CloseHandle(handle);
        }
    };
    const ScopedHandle process{OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)};
    return ProcessImagePath(process.handle);
}

inline std::wstring FoldCase(const std::wstring& text)
{
    if (text.empty())
        return text;
    const int count = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, text.data(), int(text.size()), nullptr, 0, nullptr, nullptr, 0);
    if (count <= 0)
        return text;
    std::wstring folded(size_t(count), L'\0');
    if (LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, text.data(), int(text.size()), folded.data(), count, nullptr, nullptr, 0) <= 0)
        return text;
    return folded;
}

} // namespace atrium
