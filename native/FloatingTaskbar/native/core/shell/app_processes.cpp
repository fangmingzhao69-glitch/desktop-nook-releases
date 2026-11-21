#include "shell/app_processes.h"

#include "log.h"
#include "win32_path.h"

#include <windows.h>
#include <appmodel.h>
#include <shlobj.h>
#include <tlhelp32.h>

#include <algorithm>
#include <memory>
#include <string_view>
#include <vector>

namespace atrium {
namespace {
using Handle = std::unique_ptr<void, decltype(&CloseHandle)>;

bool SharedHost(const std::wstring& path)
{
    const auto slash = path.find_last_of(L"\\/");
    const auto name = path.substr(slash == std::wstring::npos ? 0 : slash + 1);
    constexpr const wchar_t* kHosts[] = {L"explorer.exe", L"ApplicationFrameHost.exe", L"RuntimeBroker.exe", L"dllhost.exe", L"svchost.exe"};
    return std::ranges::any_of(kHosts, [&](const wchar_t* host) { return _wcsicmp(name.c_str(), host) == 0; });
}

struct Process {
    Handle handle{nullptr, CloseHandle};
    std::wstring path;
    DWORD pid, parent;
    ULONGLONG created;
    bool selected;
};

bool Matches(HANDLE process, const std::wstring& path, std::span<const AppProcessIdentity> apps)
{
    wchar_t aumid[APPLICATION_USER_MODEL_ID_MAX_LENGTH]{};
    UINT32 length = ARRAYSIZE(aumid);
    const bool packaged = GetApplicationUserModelId(process, &length, aumid) == ERROR_SUCCESS;
    return std::ranges::any_of(apps, [&](const AppProcessIdentity& app) {
        if (!CanTerminateApp(app))
            return false;
        if (packaged && !app.aumid.empty())
            return _wcsicmp(aumid, app.aumid.c_str()) == 0;
        return !app.exePath.empty() && _wcsicmp(path.c_str(), app.exePath.c_str()) == 0;
    });
}

std::wstring LongFolder(const std::wstring& path)
{
    wchar_t stack[MAX_PATH]{};
    DWORD len = GetLongPathNameW(path.c_str(), stack, ARRAYSIZE(stack));
    if (len > 0 && len < ARRAYSIZE(stack))
        return std::wstring(stack, len);
    if (len >= ARRAYSIZE(stack)) {
        std::wstring expanded(len, L'\0');
        len = GetLongPathNameW(path.c_str(), expanded.data(), len);
        if (len > 0 && len < expanded.size())
            return expanded.substr(0, len);
    }
    return path;
}

std::wstring NormalizeFolder(std::wstring_view folder)
{
    std::wstring normalized;
    normalized.reserve(folder.size() + 1);
    for (wchar_t ch : folder) {
        if (ch == L'/')
            ch = L'\\';
        if (ch == L'\\' && !normalized.empty() && normalized.back() == L'\\')
            continue;
        normalized.push_back(ch);
    }
    if (normalized.empty())
        return normalized;
    if (normalized.back() != L'\\')
        normalized.push_back(L'\\');
    normalized = LongFolder(normalized);
    if (normalized.back() != L'\\')
        normalized.push_back(L'\\');
    return normalized;
}

bool EqualPath(std::wstring_view a, std::wstring_view b)
{
    return !a.empty() && a.size() == b.size() &&
           CompareStringOrdinal(a.data(), static_cast<int>(a.size()), b.data(), static_cast<int>(b.size()), TRUE) == CSTR_EQUAL;
}

bool StartsWithPath(std::wstring_view path, std::wstring_view prefix)
{
    return !prefix.empty() && path.size() >= prefix.size() && EqualPath(path.substr(0, prefix.size()), prefix);
}

template<typename Fn> std::wstring QueryWin32Folder(Fn&& fn)
{
    wchar_t stack[MAX_PATH]{};
    UINT len = fn(stack, ARRAYSIZE(stack));
    if (len > 0 && len < ARRAYSIZE(stack))
        return NormalizeFolder(std::wstring_view(stack, len));
    if (len < ARRAYSIZE(stack))
        return {};
    std::wstring path(len, L'\0');
    len = fn(path.data(), len);
    return (len > 0 && len < path.size()) ? NormalizeFolder(std::wstring_view(path).substr(0, len)) : std::wstring{};
}

std::wstring KnownFolder(REFKNOWNFOLDERID id)
{
    PWSTR raw = nullptr;
    const HRESULT hr = SHGetKnownFolderPath(id, 0, nullptr, &raw);
    const std::unique_ptr<wchar_t, decltype(&CoTaskMemFree)> guard(raw, CoTaskMemFree);
    if (FAILED(hr) || !raw)
        return {};
    return NormalizeFolder(raw);
}

void AddAppFolder(std::vector<std::wstring>& folders, const std::wstring& path)
{
    auto folder = path.substr(0, path.find_last_of(L"\\/") + 1);
    if (folder.empty() || IsSharedAppFolder(folder))
        return;
    if (std::ranges::none_of(folders, [&](const std::wstring& existing) { return EqualPath(existing, folder); }))
        folders.push_back(std::move(folder));
}
} // namespace

bool CanTerminateApp(const AppProcessIdentity& app) { return (!app.exePath.empty() || !app.aumid.empty()) && !SharedHost(app.exePath); }

bool IsSharedAppFolder(std::wstring_view folder)
{
    const std::wstring normalized = NormalizeFolder(folder);
    if (normalized.empty() || (normalized.size() <= 3 && normalized.find(L":\\") != std::wstring::npos))
        return true;
    if (StartsWithPath(normalized, QueryWin32Folder(GetSystemDirectoryW)) || StartsWithPath(normalized, QueryWin32Folder(GetSystemWow64DirectoryW)))
        return true;
    if (EqualPath(normalized, QueryWin32Folder(GetWindowsDirectoryW)) ||
        EqualPath(normalized, QueryWin32Folder([](wchar_t* buf, UINT size) -> UINT { return GetTempPathW(size, buf); })))
        return true;
    const KNOWNFOLDERID sharedFolders[] = {
        FOLDERID_ProgramFiles,  FOLDERID_ProgramFilesX86, FOLDERID_ProgramFilesCommon, FOLDERID_ProgramFilesCommonX86,
        FOLDERID_Profile,       FOLDERID_Desktop,         FOLDERID_Downloads,          FOLDERID_Documents,
        FOLDERID_PublicDesktop, FOLDERID_PublicDocuments, FOLDERID_LocalAppData,       FOLDERID_RoamingAppData,
    };
    return std::ranges::any_of(sharedFolders, [&](const KNOWNFOLDERID& id) { return EqualPath(normalized, KnownFolder(id)); });
}

bool TerminateAppProcesses(std::span<const AppProcessIdentity> apps)
{
    DWORD session = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session))
        return false;
    const HANDLE rawSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (rawSnapshot == INVALID_HANDLE_VALUE) {
        Log(L"tasks: process snapshot failed (%lu)", GetLastError());
        return false;
    }
    Handle snapshot(rawSnapshot, CloseHandle);
    std::vector<Process> processes;
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (!Process32FirstW(snapshot.get(), &entry)) {
        Log(L"tasks: reading process snapshot failed (%lu)", GetLastError());
        return false;
    }
    do {
        DWORD processSession = 0;
        if (entry.th32ProcessID == GetCurrentProcessId() || !ProcessIdToSessionId(entry.th32ProcessID, &processSession) || processSession != session)
            continue;
        Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, entry.th32ProcessID), CloseHandle);
        if (!process)
            continue;
        std::wstring path = ProcessImagePath(process.get());
        if (path.empty() || SharedHost(path))
            continue;
        FILETIME created{}, exited{}, kernel{}, user{};
        if (!GetProcessTimes(process.get(), &created, &exited, &kernel, &user))
            continue;
        const bool selected = Matches(process.get(), path, apps);
        processes.push_back({std::move(process), path, entry.th32ProcessID, entry.th32ParentProcessID,
                             (ULONGLONG(created.dwHighDateTime) << 32) | created.dwLowDateTime, selected});
    } while (Process32NextW(snapshot.get(), &entry));
    // The app's folders: its helpers are the processes it started from them.
    std::vector<std::wstring> folders;
    for (const auto& process : processes)
        if (process.selected)
            AddAppFolder(folders, process.path);
    auto inside = [&](const std::wstring& path) {
        return std::ranges::any_of(folders, [&](const std::wstring& folder) {
            return !folder.empty() && path.size() > folder.size() && _wcsnicmp(path.c_str(), folder.c_str(), folder.size()) == 0;
        });
    };
    // Select helpers before ending parents; creation times reject reused parent PIDs.
    bool changed;
    do {
        changed = false;
        for (auto& child : processes) {
            if (child.selected)
                continue;
            for (const auto& parent : processes)
                if (parent.selected && child.parent == parent.pid && child.created >= parent.created && inside(child.path)) {
                    child.selected = changed = true;
                    break;
                }
        }
    } while (changed);

    bool success = true;
    for (const auto& process : processes) {
        if (!process.selected || WaitForSingleObject(process.handle.get(), 0) == WAIT_OBJECT_0)
            continue;
        // Query handles stay alive until the end, keeping captured PIDs from being reused.
        Handle target(OpenProcess(PROCESS_TERMINATE, FALSE, process.pid), CloseHandle);
        if (!target || !TerminateProcess(target.get(), ERROR_PROCESS_ABORTED)) {
            Log(L"tasks: ending process %lu failed (%lu)", process.pid, GetLastError());
            success = false;
        } else {
            Log(L"tasks: ended app process %lu", process.pid);
        }
    }
    return success;
}

} // namespace atrium
