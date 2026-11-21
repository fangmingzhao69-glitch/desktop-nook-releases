#include "shell/app_catalog.h"

#include "log.h"
#include "shell/shell_icon.h"
#include "shell/start_layout.h"

#include <shellapi.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <knownfolders.h>
#include <propsys.h>
#include <propkey.h>

#include <winrt/base.h>

#include <algorithm>
#include <cwctype>
#include <string_view>

namespace atrium {

namespace {

std::wstring Fold(const std::wstring& s)
{
    std::wstring out(s);
    if (!out.empty())
        LCMapStringEx(LOCALE_NAME_USER_DEFAULT, LCMAP_LOWERCASE, s.c_str(), int(s.size()), out.data(), int(out.size()), nullptr, nullptr, 0);
    return out;
}

std::wstring PropString(IShellItem2* item, const PROPERTYKEY& key)
{
    PWSTR v = nullptr;
    std::wstring s;
    if (SUCCEEDED(item->GetString(key, &v)) && v)
        s = v;
    CoTaskMemFree(v);
    return s;
}

std::vector<CatalogApp> Enumerate()
{
    std::vector<CatalogApp> apps;
    winrt::com_ptr<IShellItem> folder;
    if (FAILED(SHGetKnownFolderItem(FOLDERID_AppsFolder, KF_FLAG_DEFAULT, nullptr, IID_PPV_ARGS(folder.put()))))
        return apps;
    winrt::com_ptr<IEnumShellItems> items;
    if (FAILED(folder->BindToHandler(nullptr, BHID_EnumItems, IID_PPV_ARGS(items.put()))))
        return apps;
    for (;;) {
        winrt::com_ptr<IShellItem> item;
        if (items->Next(1, item.put(), nullptr) != S_OK)
            break;
        CatalogApp app;
        PWSTR s = nullptr;
        if (SUCCEEDED(item->GetDisplayName(SIGDN_NORMALDISPLAY, &s)) && s)
            app.name = s;
        CoTaskMemFree(s);
        s = nullptr;
        if (SUCCEEDED(item->GetDisplayName(SIGDN_PARENTRELATIVEPARSING, &s)) && s)
            app.id = s;
        CoTaskMemFree(s);
        if (auto item2 = item.try_as<IShellItem2>()) {
            app.aumid = PropString(item2.get(), PKEY_AppUserModel_ID);
            app.target = PropString(item2.get(), PKEY_Link_TargetParsingPath);
            app.args = PropString(item2.get(), PKEY_Link_Arguments);
        }
        if (!app.name.empty() && !app.id.empty())
            apps.push_back(std::move(app));
    }
    std::ranges::sort(apps, [](const CatalogApp& a, const CatalogApp& b) {
        return CompareStringEx(LOCALE_NAME_USER_DEFAULT, NORM_IGNORECASE | SORT_DIGITSASNUMBERS, a.name.c_str(), int(a.name.size()), b.name.c_str(),
                               int(b.name.size()), nullptr, nullptr, 0) == CSTR_LESS_THAN;
    });
    return apps;
}

bool IsPath(const std::wstring& id) { return id.size() > 2 && id[1] == L':' && (id[2] == L'\\' || id[2] == L'/'); }

bool SameText(const std::wstring& a, const std::wstring& b)
{
    return !a.empty() && CompareStringOrdinal(a.c_str(), int(a.size()), b.c_str(), int(b.size()), TRUE) == CSTR_EQUAL;
}

std::wstring ReadUtf8File(const std::wstring& path)
{
    std::wstring text;
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE)
        return text;
    LARGE_INTEGER size{};
    GetFileSizeEx(f, &size);
    std::string bytes(size_t(std::min<LONGLONG>(size.QuadPart, 4 << 20)), '\0');
    DWORD read = 0;
    if (!bytes.empty() && ReadFile(f, bytes.data(), DWORD(bytes.size()), &read, nullptr)) {
        bytes.resize(read);
        size_t skip = bytes.starts_with("\xEF\xBB\xBF") ? 3 : 0;
        int n = MultiByteToWideChar(CP_UTF8, 0, bytes.data() + skip, int(bytes.size() - skip), nullptr, 0);
        text.resize(size_t(std::max(n, 0)));
        MultiByteToWideChar(CP_UTF8, 0, bytes.data() + skip, int(bytes.size() - skip), text.data(), n);
    }
    CloseHandle(f);
    return text;
}

// The Windows start menu's layout file (start2.bin) is encrypted; the
// supported way to read it is Export-StartLayout, which writes JSON:
// {"pinnedList":[{"desktopAppLink":"%APPDATA%\\...lnk"},{"packagedAppId":"...!App"},...]}.
std::wstring ExportStartLayout(HANDLE stopEvent, bool* cancelled = nullptr)
{
    if (cancelled)
        *cancelled = false;
    if (stopEvent && WaitForSingleObject(stopEvent, 0) == WAIT_OBJECT_0) {
        if (cancelled)
            *cancelled = true;
        return {};
    }
    wchar_t temp[MAX_PATH] = {}, sys[MAX_PATH] = {};
    GetTempPathW(MAX_PATH, temp);
    GetSystemDirectoryW(sys, MAX_PATH);
    std::wstring file = std::wstring(temp) + L"atrium-start-layout-" + std::to_wstring(GetCurrentProcessId()) + L".json";
    std::wstring quoted; // a PowerShell single-quoted string
    for (wchar_t ch : file)
        quoted += ch == L'\'' ? L"''" : std::wstring(1, ch);
    std::wstring cmd = L"\"" + std::wstring(sys) +
                       L"\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -NonInteractive -ExecutionPolicy Bypass "
                       L"-Command \"Export-StartLayout -Path '" +
                       quoted + L"'\"";
    DeleteFileW(file.c_str());
    STARTUPINFOW si{sizeof(si)};
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) {
        Log(L"catalog: Export-StartLayout could not start (%lu)", GetLastError());
        return {};
    }
    HANDLE handles[2] = {pi.hProcess, stopEvent};
    DWORD count = stopEvent ? 2 : 1;
    DWORD wait = WaitForMultipleObjects(count, handles, FALSE, 20000);
    if (wait != WAIT_OBJECT_0) {
        TerminateProcess(pi.hProcess, 1);
        WaitForSingleObject(pi.hProcess, 3000);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        DeleteFileW(file.c_str());
        if (cancelled)
            *cancelled = true;
        Log(wait == WAIT_OBJECT_0 + 1 ? L"catalog: Export-StartLayout cancelled" : L"catalog: Export-StartLayout timed out");
        return {};
    }
    DWORD code = 1;
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    std::wstring text;
    if (code == 0)
        text = ReadUtf8File(file);
    else
        Log(L"catalog: Export-StartLayout failed (exit code %lu)", code);
    DeleteFileW(file.c_str());
    return text;
}

// Every "key": "string" pair of a JSON text, in order (all the layout needs).
std::vector<std::pair<std::wstring, std::wstring>> JsonStringPairs(const std::wstring& j)
{
    std::vector<std::pair<std::wstring, std::wstring>> out;
    auto readString = [&](size_t& i) {
        std::wstring s;
        for (++i; i < j.size() && j[i] != L'"'; ++i) {
            if (j[i] != L'\\' || i + 1 >= j.size()) {
                s += j[i];
                continue;
            }
            wchar_t e = j[++i];
            if (e == L'u' && i + 4 < j.size()) {
                s += wchar_t(std::wcstoul(j.substr(i + 1, 4).c_str(), nullptr, 16));
                i += 4;
            } else {
                s += e == L'n' ? L'\n' : e == L't' ? L'\t' : e == L'r' ? L'\r' : e == L'b' ? L'\b' : e == L'f' ? L'\f' : e;
            }
        }
        ++i; // the closing quote
        return s;
    };
    auto skipSpace = [&](size_t& i) {
        while (i < j.size() && iswspace(j[i]))
            ++i;
    };
    for (size_t i = 0; i < j.size();) {
        if (j[i] != L'"') {
            ++i;
            continue;
        }
        std::wstring key = readString(i);
        skipSpace(i);
        if (i >= j.size() || j[i] != L':')
            continue;
        ++i;
        skipSpace(i);
        if (i < j.size() && j[i] == L'"')
            out.emplace_back(key, readString(i));
    }
    return out;
}

// %ALLUSERSPROFILE%, %APPDATA% and the like, from the shell's known folders:
// the process environment may lack them (Atrium started from a service or a
// sandbox), and then those pins would seem to point nowhere.
std::wstring ExpandFolders(const std::wstring& value)
{
    static const std::pair<const wchar_t*, const KNOWNFOLDERID*> kFolders[] = {
        {L"%ALLUSERSPROFILE%", &FOLDERID_ProgramData}, {L"%PROGRAMDATA%", &FOLDERID_ProgramData},
        {L"%APPDATA%", &FOLDERID_RoamingAppData},      {L"%LOCALAPPDATA%", &FOLDERID_LocalAppData},
        {L"%USERPROFILE%", &FOLDERID_Profile},         {L"%PUBLIC%", &FOLDERID_Public},
        {L"%PROGRAMFILES%", &FOLDERID_ProgramFiles},   {L"%PROGRAMFILES(X86)%", &FOLDERID_ProgramFilesX86},
        {L"%SYSTEMROOT%", &FOLDERID_Windows},          {L"%WINDIR%", &FOLDERID_Windows}};
    std::wstring out = value;
    for (const auto& [token, id] : kFolders) {
        size_t len = wcslen(token);
        if (out.size() < len || CompareStringOrdinal(out.c_str(), int(len), token, int(len), TRUE) != CSTR_EQUAL)
            continue;
        PWSTR path = nullptr;
        if (SUCCEEDED(SHGetKnownFolderPath(*id, KF_FLAG_DEFAULT, nullptr, &path)))
            out = std::wstring(path) + out.substr(len);
        CoTaskMemFree(path);
        break;
    }
    wchar_t expanded[MAX_PATH * 2] = {};
    if (out.find(L'%') != std::wstring::npos && ExpandEnvironmentStringsW(out.c_str(), expanded, DWORD(std::size(expanded))))
        out = expanded;
    return out;
}

// The layout's entries as apps. A pinned shortcut is the catalog's entry only
// when that is the same app (its AUMID, or the same target under the same
// name); otherwise the shortcut itself, which keeps its arguments and keeps
// two shortcuts to one program apart.
std::vector<CatalogApp> ResolvePins(const std::vector<std::pair<std::wstring, std::wstring>>& layout, const std::vector<CatalogApp>& apps)
{
    std::vector<CatalogApp> out;
    auto add = [&](const CatalogApp& a) {
        if (std::ranges::none_of(out, [&](const CatalogApp& o) { return SameText(o.id, a.id); }))
            out.push_back(a);
    };
    for (const auto& [key, value] : layout) {
        if (key == L"packagedAppId") {
            auto it = std::ranges::find_if(apps, [&](const CatalogApp& a) { return SameText(a.aumid, value) || SameText(a.id, value); });
            if (it != apps.end())
                add(*it);
            else
                Log(L"catalog: start pin %s is not installed", value.c_str());
            continue;
        }
        if (key != L"desktopAppLink")
            continue;
        std::wstring expanded = ExpandFolders(value);
        // Start layouts exported before the rename still refer to this shortcut.
        constexpr std::wstring_view kLegacy = L"\\Atrium.Core.lnk";
        if (GetFileAttributesW(expanded.c_str()) == INVALID_FILE_ATTRIBUTES && expanded.size() >= kLegacy.size() &&
            SameText(expanded.substr(expanded.size() - kLegacy.size()), std::wstring(kLegacy)))
            expanded.replace(expanded.size() - kLegacy.size(), kLegacy.size(), L"\\Atrium.lnk");
        if (GetFileAttributesW(expanded.c_str()) == INVALID_FILE_ATTRIBUTES) {
            Log(L"catalog: start pin %s not found", value.c_str());
            continue;
        }
        CatalogApp link;
        link.id = expanded;
        winrt::com_ptr<IShellItem2> item;
        if (SUCCEEDED(SHCreateItemFromParsingName(expanded.c_str(), nullptr, IID_PPV_ARGS(item.put())))) {
            PWSTR s = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_NORMALDISPLAY, &s)) && s)
                link.name = s; // localised ("File Explorer.lnk" shows as 文件资源管理器)
            CoTaskMemFree(s);
            link.aumid = PropString(item.get(), PKEY_AppUserModel_ID);
            link.target = PropString(item.get(), PKEY_Link_TargetParsingPath);
            link.args = PropString(item.get(), PKEY_Link_Arguments);
        }
        if (link.name.empty()) {
            std::wstring file = link.id.substr(link.id.find_last_of(L"\\/") + 1);
            link.name = file.size() > 4 ? file.substr(0, file.size() - 4) : file;
        }
        auto it = std::ranges::find_if(apps, [&](const CatalogApp& a) { return SameText(a.aumid, link.aumid) || SameText(a.id, link.aumid); });
        if (it == apps.end())
            it = std::ranges::find_if(apps, [&](const CatalogApp& a) { return SameText(a.target, link.target) && SameText(a.name, link.name); });
        add(it != apps.end() ? *it : link);
    }
    return out;
}

// Not an app of a folder: an uninstaller ("卸载微信", "Uninstall Git", unins000.exe), or a shortcut
// to a document ("7-Zip Help", release notes, a web page, a folder). A shortcut with no target path
// (a packaged app, an installer's advertised shortcut) is an app.
bool NotAnApp(const CatalogApp& app)
{
    const std::wstring name = Fold(app.name), target = Fold(app.target.substr(app.target.find_last_of(L"\\/") + 1));
    if (name.find(L"uninstall") != std::wstring::npos || name.find(L"卸载") != std::wstring::npos || name.find(L"解除安装") != std::wstring::npos ||
        target.starts_with(L"unins"))
        return true;
    return !target.empty() && !target.ends_with(L".exe");
}

// Every shortcut in `dir` and its subfolders.
void CollectLinks(const std::wstring& dir, std::vector<std::pair<std::wstring, std::wstring>>& links)
{
    WIN32_FIND_DATAW fd{};
    HANDLE h = FindFirstFileExW((dir + L"\\*").c_str(), FindExInfoBasic, &fd, FindExSearchNameMatch, nullptr, 0);
    if (h == INVALID_HANDLE_VALUE)
        return;
    std::vector<std::wstring> subdirs;
    do {
        const std::wstring name = fd.cFileName;
        if (name == L"." || name == L"..")
            continue;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
            subdirs.push_back(dir + L"\\" + name);
        else if (name.size() > 4 && Fold(name.substr(name.size() - 4)) == L".lnk")
            links.emplace_back(L"desktopAppLink", dir + L"\\" + name);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    for (const std::wstring& sub : subdirs)
        CollectLinks(sub, links);
}

// The All apps list's folders: each folder right under the two Start Menu\Programs folders (a
// folder of both is one), with the shortcuts in it and in its subfolders, as All apps shows them;
// by the name the shell shows (desktop.ini's localised name). Windows' own folders
// (Accessibility, Windows Tools' parts, Startup) are left out.
std::vector<CatalogFolder> ReadStartFolders(const std::vector<CatalogApp>& apps)
{
    static const wchar_t* const kSystem[] = {L"accessibility", L"accessories",  L"administrative tools", L"maintenance",
                                             L"startup",       L"system tools", L"windows powershell",   L"windows tools"};
    std::vector<std::pair<std::wstring, std::vector<std::pair<std::wstring, std::wstring>>>> found; // shown name, its links
    for (const KNOWNFOLDERID* id : {&FOLDERID_CommonPrograms, &FOLDERID_Programs}) {
        PWSTR root = nullptr;
        if (FAILED(SHGetKnownFolderPath(*id, KF_FLAG_DEFAULT, nullptr, &root)) || !root) {
            CoTaskMemFree(root);
            continue;
        }
        const std::wstring programs = root;
        CoTaskMemFree(root);
        WIN32_FIND_DATAW fd{};
        HANDLE h = FindFirstFileExW((programs + L"\\*").c_str(), FindExInfoBasic, &fd, FindExSearchNameMatch, nullptr, 0);
        if (h == INVALID_HANDLE_VALUE)
            continue;
        do {
            const std::wstring name = fd.cFileName, folded = Fold(name);
            if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || name == L"." || name == L".." ||
                std::ranges::any_of(kSystem, [&](const wchar_t* s) { return folded == s; }))
                continue;
            const std::wstring dir = programs + L"\\" + name;
            std::vector<std::pair<std::wstring, std::wstring>> links;
            CollectLinks(dir, links);
            if (links.empty())
                continue;
            std::wstring shown = name;
            winrt::com_ptr<IShellItem> item;
            if (SUCCEEDED(SHCreateItemFromParsingName(dir.c_str(), nullptr, IID_PPV_ARGS(item.put())))) {
                PWSTR s = nullptr;
                if (SUCCEEDED(item->GetDisplayName(SIGDN_NORMALDISPLAY, &s)) && s && *s)
                    shown = s;
                CoTaskMemFree(s);
            }
            auto it = std::ranges::find_if(found, [&](const auto& f) { return Fold(f.first) == Fold(shown); });
            if (it == found.end())
                found.emplace_back(shown, std::move(links));
            else
                it->second.insert(it->second.end(), links.begin(), links.end());
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    std::vector<CatalogFolder> out;
    for (auto& [name, links] : found) {
        CatalogFolder folder{name, {}};
        for (CatalogApp& app : ResolvePins(links, apps))
            if (!NotAnApp(app))
                folder.apps.push_back(std::move(app));
        if (folder.apps.size() >= 2)
            out.push_back(std::move(folder));
    }
    return out;
}

} // namespace

AppCatalog::AppCatalog(HWND notify, UINT readyMsg) : notify_(notify), msg_(readyMsg), stopEvent_(CreateEventW(nullptr, TRUE, FALSE, nullptr)), refresh_(true)
{
    if (!stopEvent_)
        winrt::throw_last_error();
    thread_ = std::thread([this] { Worker(); });
}

AppCatalog::~AppCatalog()
{
    {
        std::lock_guard g(mutex_);
        stop_ = true;
    }
    if (stopEvent_)
        SetEvent(stopEvent_);
    cv_.notify_all();
    thread_.join();
    if (stopEvent_)
        CloseHandle(stopEvent_);
}

void AppCatalog::Refresh()
{
    {
        std::lock_guard g(mutex_);
        refresh_ = true;
    }
    cv_.notify_all();
}

bool AppCatalog::Loaded() const
{
    std::lock_guard g(mutex_);
    return loaded_;
}

std::vector<CatalogApp> AppCatalog::Apps() const
{
    std::lock_guard g(mutex_);
    return apps_;
}

std::vector<CatalogApp> AppCatalog::StartPins() const
{
    std::lock_guard g(mutex_);
    return startPins_;
}

std::vector<CatalogFolder> AppCatalog::StartFolders() const
{
    std::lock_guard g(mutex_);
    return startFolders_;
}

void AppCatalog::PrefetchStartIcons(int px)
{
    std::lock_guard g(mutex_);
    prefetchPx_ = std::clamp(px, 8, 512);
    QueuePrefetch();
}

void AppCatalog::SetStartIds(std::vector<std::wstring> ids)
{
    std::lock_guard g(mutex_);
    startIds_ = std::set<std::wstring>(ids.begin(), ids.end());
    QueuePrefetch();
}

void AppCatalog::QueuePrefetch()
{
    if (prefetchPx_ <= 0)
        return;
    auto queue = [&](const std::wstring& id) {
        auto key = std::make_pair(id, prefetchPx_);
        if (!icons_.contains(key))
            QueueIcon(key, true);
    };
    for (const CatalogApp& app : startPins_)
        queue(app.id);
    for (const std::wstring& id : startIds_)
        queue(id);
    cv_.notify_all();
}

IconImage AppCatalog::Icon(const std::wstring& id, int px)
{
    px = std::clamp(px, 8, 512);
    std::lock_guard g(mutex_);
    auto key = std::make_pair(id, px);
    if (auto it = icons_.find(key); it != icons_.end()) {
        it->second.used = ++iconUse_;
        return it->second.image;
    }
    if (QueueIcon(key, false))
        cv_.notify_one();
    return {};
}

bool AppCatalog::QueueIcon(const std::pair<std::wstring, int>& key, bool prefetch)
{
    if (requested_.contains(key))
        return false;
    if (auto it = failed_.find(key); it != failed_.end() && (it->second.attempts >= 3 || GetTickCount64() - it->second.time < 2000))
        return false;
    requested_.insert(key);
    if (prefetch)
        iconJobs_.push_front(key);
    else
        iconJobs_.push_back(key);
    return true;
}

void AppCatalog::SetVisibleIcons(std::vector<std::pair<std::wstring, int>> icons)
{
    std::lock_guard g(mutex_);
    visibleIcons_.clear();
    visibleIcons_.insert(icons.begin(), icons.end());
    PruneIcons();
}

void AppCatalog::PruneIcons()
{
    auto protectedIcon = [&](const auto& key) {
        return visibleIcons_.contains(key) || requested_.contains(key) ||
               (key.second == prefetchPx_ &&
                (startIds_.contains(key.first) || std::any_of(startPins_.begin(), startPins_.end(), [&](const auto& app) { return app.id == key.first; })));
    };
    auto staleIcon = [&](const auto& entry) {
        return loaded_ && !liveIds_.contains(entry.first.first) && !startIds_.contains(entry.first.first) && !protectedIcon(entry.first);
    };
    std::erase_if(icons_, staleIcon);
    std::erase_if(failed_, staleIcon);
    size_t bytes = 0;
    for (const auto& [key, icon] : icons_)
        bytes += icon.image.bgra.size();
    while (bytes > 16 * 1024 * 1024 || icons_.size() > 512) {
        auto victim = icons_.end();
        for (auto it = icons_.begin(); it != icons_.end(); ++it)
            if (!protectedIcon(it->first) && (victim == icons_.end() || it->second.used < victim->second.used))
                victim = it;
        if (victim == icons_.end())
            break;
        bytes -= victim->second.image.bgra.size();
        icons_.erase(victim);
    }
    while (failed_.size() > 512) {
        auto victim = std::ranges::min_element(failed_, [](const auto& a, const auto& b) { return a.second.time < b.second.time; });
        failed_.erase(victim);
    }
}

void AppCatalog::RefreshStartLayout()
{
    wchar_t local[MAX_PATH] = {};
    GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
    std::wstring bin = std::wstring(local) + L"\\Packages\\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\\LocalState\\start2.bin";
    WIN32_FILE_ATTRIBUTE_DATA fa{};
    bool hasBin = GetFileAttributesExW(bin.c_str(), GetFileExInfoStandard, &fa) != 0;
    if (layoutLoaded_ && (!hasBin || CompareFileTime(&fa.ftLastWriteTime, &layoutTime_) == 0))
        return;
    ULONGLONG t1 = GetTickCount64();
    bool cancelled = false;
    std::wstring json = ExportStartLayout(stopEvent_, &cancelled);
    if (cancelled)
        return;
    // Only a complete export replaces the pins; otherwise the old ones stay and the next refresh retries
    // when start2.bin exists. On Windows 10 (no start2.bin), try once so opening Start does not respawn PowerShell.
    bool found = json.find(L"\"pinnedList\"") != std::wstring::npos;
    if (found)
        layout_ = JsonStringPairs(json);
    if (found || !hasBin) {
        layoutTime_ = fa.ftLastWriteTime;
        layoutLoaded_ = true;
    }
    Log(L"catalog: start layout exported (%zu entries) in %llu ms", layout_.size(), GetTickCount64() - t1);
}

void AppCatalog::Worker()
{
    // STA: shell item image factories of some handlers need it.
    winrt::init_apartment(winrt::apartment_type::single_threaded);
    for (;;) {
        bool refresh = false;
        std::pair<std::wstring, int> job;
        {
            std::unique_lock g(mutex_);
            cv_.wait(g, [&] { return stop_ || refresh_ || !iconJobs_.empty(); });
            if (stop_)
                break;
            if (!iconJobs_.empty()) {
                // Icons before a refresh (it takes about a second); newest
                // requests first: they are what is on screen now.
                job = iconJobs_.back();
                iconJobs_.pop_back();
            } else {
                refresh = true;
                refresh_ = false;
            }
        }
        if (refresh) {
            ULONGLONG t0 = GetTickCount64();
            std::vector<CatalogApp> apps;
            try {
                apps = Enumerate();
            } catch (...) {
                Log(L"catalog: enumeration failed");
            }
            Log(L"catalog: %zu apps in %llu ms", apps.size(), GetTickCount64() - t0);
            // The start menu's pins: exported again only when its layout file changed.
            std::vector<CatalogApp> pins;
            try {
                RefreshStartLayout();
                pins = ResolvePins(layout_, apps);
            } catch (...) {
                Log(L"catalog: start pins failed");
            }
            std::vector<CatalogFolder> folders;
            try {
                folders = ReadStartFolders(apps);
            } catch (...) {
                Log(L"catalog: start folders failed");
            }
            {
                std::lock_guard g(mutex_);
                apps_ = std::move(apps);
                startPins_ = std::move(pins);
                startFolders_ = std::move(folders);
                liveIds_.clear();
                for (const auto& app : apps_)
                    liveIds_.insert(app.id);
                for (const auto& app : startPins_)
                    liveIds_.insert(app.id);
                for (const auto& folder : startFolders_)
                    for (const auto& app : folder.apps)
                        liveIds_.insert(app.id);
                liveIds_.insert(startIds_.begin(), startIds_.end());
                failed_.clear(); // catalogue changes allow another bounded attempt
                loaded_ = true;
                PruneIcons();
                QueuePrefetch();
            }
            PostMessageW(notify_, msg_, 0, 0);
            continue;
        }
        IconImage img;
        LoadShellImage(IsPath(job.first) ? job.first : L"shell:AppsFolder\\" + job.first, job.second, img);
        {
            std::lock_guard g(mutex_);
            requested_.erase(job);
            if (!img.Empty()) {
                icons_[job] = {std::move(img), ++iconUse_};
                failed_.erase(job);
            } else {
                auto& failure = failed_[job];
                ++failure.attempts;
                failure.time = GetTickCount64();
            }
            PruneIcons();
        }
        PostMessageW(notify_, msg_, 1, 0);
    }
    winrt::uninit_apartment();
}

bool AppCatalog::Launch(const CatalogApp& app)
{
    std::wstring path = IsPath(app.id) ? app.id : L"shell:AppsFolder\\" + app.id;
    SHELLEXECUTEINFOW sei{sizeof(sei)};
    sei.fMask = SEE_MASK_FLAG_NO_UI;
    sei.lpFile = path.c_str();
    sei.nShow = SW_SHOWNORMAL;
    if (!ShellExecuteExW(&sei)) {
        Log(L"catalog: launch of %s failed (%lu)", app.id.c_str(), GetLastError());
        return false;
    }
    Log(L"catalog: launched %s", app.id.c_str());
    return true;
}

std::wstring AppCatalog::FileLocation(const CatalogApp& app)
{
    for (const std::wstring& file : {app.target, IsPath(app.id) ? app.id : std::wstring()})
        if (!file.empty() && GetFileAttributesW(file.c_str()) != INVALID_FILE_ATTRIBUTES)
            return file;
    return {};
}

bool AppCatalog::LaunchAsAdmin(const CatalogApp& app)
{
    // The shortcut keeps its arguments and working folder; without one, the exe with the link's arguments.
    const bool link = IsPath(app.id) && GetFileAttributesW(app.id.c_str()) != INVALID_FILE_ATTRIBUTES;
    const std::wstring file = link ? app.id : app.target;
    if (file.empty() || GetFileAttributesW(file.c_str()) == INVALID_FILE_ATTRIBUTES)
        return false;
    const std::wstring dir = link ? std::wstring() : file.substr(0, file.find_last_of(L"\\/"));
    SHELLEXECUTEINFOW sei{sizeof(sei)};
    sei.fMask = SEE_MASK_FLAG_NO_UI;
    sei.lpVerb = L"runas";
    sei.lpFile = file.c_str();
    sei.lpParameters = link || app.args.empty() ? nullptr : app.args.c_str();
    sei.lpDirectory = dir.empty() ? nullptr : dir.c_str();
    sei.nShow = SW_SHOWNORMAL;
    if (!ShellExecuteExW(&sei)) {
        const DWORD error = GetLastError();
        if (error != ERROR_CANCELLED) // the user said no at the prompt
            Log(L"catalog: elevated launch of %s failed (%lu)", file.c_str(), error);
        return false;
    }
    Log(L"catalog: launched %s as administrator", file.c_str());
    return true;
}

PinnedApp AppCatalog::PinFor(const CatalogApp& app)
{
    PinnedApp p;
    p.name = app.name;
    // Desktop apps pin by their exe so the pin matches their windows; the rest
    // by the AppsFolder id, which also starts them (shell:AppsFolder\<id>).
    std::wstring lower = app.target;
    for (auto& c : lower)
        c = wchar_t(towlower(c));
    if (!app.target.empty() && lower.size() > 4 && lower.substr(lower.size() - 4) == L".exe") {
        p.path = app.target;
        p.args = app.args;
    } else if (IsPath(app.id))
        p.path = app.id; // a shortcut outside the catalog
    else
        p.aumid = !app.aumid.empty() ? app.aumid : app.id;
    return p;
}

bool AppCatalog::Matches(const CatalogApp& app, const std::wstring& query)
{
    if (query.empty())
        return true;
    std::wstring q = Fold(query);
    if (Fold(app.name).find(q) != std::wstring::npos)
        return true;
    // Localised names hide the English one ("计算器"); the id and the exe
    // usually carry it (Microsoft.WindowsCalculator_..., notepad.exe).
    if (std::ranges::all_of(q, [](wchar_t c) { return c < 128 && iswalnum(c); }) && NameInitials(app.name).starts_with(q))
        return true;
    auto tail = [](const std::wstring& s) {
        size_t p = s.find_last_of(L"\\/");
        return p == std::wstring::npos ? s : s.substr(p + 1);
    };
    return q.size() >= 3 && (Fold(tail(app.id)).find(q) != std::wstring::npos || Fold(tail(app.target)).find(q) != std::wstring::npos);
}

} // namespace atrium
