// Folded into ZHEYE from Atrium Desktop (MIT). Host-specific identity and lifetime.
#pragma once
#include <windows.h>
#include <string>

namespace zheye {
inline std::wstring Env(const wchar_t* name)
{
    DWORD n = GetEnvironmentVariableW(name, nullptr, 0);
    if (!n || n > 32768) return {};
    std::wstring value(n, L'\0');
    DWORD written = GetEnvironmentVariableW(name, value.data(), n);
    if (!written || written >= n) return {};
    value.resize(written);
    return value;
}
inline const std::wstring& Identity()
{
    static const std::wstring id = Env(L"ZHEYE_DOCK_ID");
    return id;
}
inline const std::wstring& DataDirectory()
{
    static const std::wstring dir = Env(L"ZHEYE_DOCK_DATA");
    return dir;
}
inline const std::wstring& HostClass()
{
    static const std::wstring name = L"ZheyeDockHost." + Identity();
    return name;
}
inline HANDLE ParentProcess()
{
    static HANDLE process = [] {
        auto value = Env(L"ZHEYE_DOCK_PARENT");
        DWORD pid = wcstoul(value.c_str(), nullptr, 10);
        return pid ? OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid) : nullptr;
    }();
    return process;
}
inline bool ParentAlive() { auto parent = ParentProcess(); return parent && WaitForSingleObject(parent, 0) == WAIT_TIMEOUT; }
inline void OpenSettings()
{
    const auto value = Env(L"ZHEYE_DOCK_OWNER");
    HWND owner = reinterpret_cast<HWND>(static_cast<UINT_PTR>(_wcstoui64(value.c_str(), nullptr, 10)));
    DWORD pid = 0;
    GetWindowThreadProcessId(owner, &pid);
    if (ParentAlive() && GetProcessId(ParentProcess()) == pid)
        PostMessageW(owner, RegisterWindowMessageW(L"DesktopNook.Dock.Settings"), 0, 0);
}
inline void OpenPanel()
{
    const auto value = Env(L"ZHEYE_DOCK_OWNER");
    HWND owner = reinterpret_cast<HWND>(static_cast<UINT_PTR>(_wcstoui64(value.c_str(), nullptr, 10)));
    DWORD pid = 0;
    GetWindowThreadProcessId(owner, &pid);
    if (ParentAlive() && GetProcessId(ParentProcess()) == pid)
        PostMessageW(owner, RegisterWindowMessageW(L"DesktopNook.Dock.Panel"), 0, 0);
}
inline void UseSystemTaskbar()
{
    const auto value = Env(L"ZHEYE_DOCK_OWNER");
    HWND owner = reinterpret_cast<HWND>(static_cast<UINT_PTR>(_wcstoui64(value.c_str(), nullptr, 10)));
    DWORD pid = 0;
    GetWindowThreadProcessId(owner, &pid);
    if (ParentAlive() && GetProcessId(ParentProcess()) == pid)
        PostMessageW(owner, RegisterWindowMessageW(L"DesktopNook.Dock.System"), 0, 0);
}
}
