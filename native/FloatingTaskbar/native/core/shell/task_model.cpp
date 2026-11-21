// Task model implementation (D13): EnumWindows + WinEvent hooks on the UI
// thread, AUMID grouping, icons and display names loaded on one worker thread.
//
// Threading: everything except `jobs_`, `icons_` and the ready queues is touched
// by the UI thread only. The worker never touches model state; it publishes
// results under `mutex_` and posts a registered message to the UI window.

#include "shell/task_model.h"
#include "shell/app_processes.h"
#include "shell/messengers.h"
#include "shell/shell_icon.h"

#include "log.h"

#include <windows.h>
#include <appmodel.h>
#include <dwmapi.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <tlhelp32.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <propsys.h>
#include <propkey.h>
#include <propvarutil.h>

#include <winrt/base.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>

namespace atrium {

namespace {

constexpr UINT_PTR kTimerId = 0xA701;     // >= 0xA700: clear of the host's own timers
constexpr UINT kCoalesceMs = 30;
constexpr UINT kWindowIconTimeoutMs = 100; // WM_GETICON on a possibly hung window
constexpr ULONGLONG kRetryFailedIconMs = 10000;

// COM for the calling thread, balanced on scope exit. A thread that is already
// initialised with another model (RPC_E_CHANGED_MODE) just carries on.
struct ComScope {
    bool owned;
    explicit ComScope(DWORD flags = COINIT_APARTMENTTHREADED) : owned(SUCCEEDED(CoInitializeEx(nullptr, flags))) {}
    ComScope(const ComScope&) = delete;
    ComScope& operator=(const ComScope&) = delete;
    ~ComScope()
    {
        if (owned)
            CoUninitialize();
    }
};

std::wstring Lower(std::wstring s)
{
    if (!s.empty())
        CharLowerBuffW(s.data(), static_cast<DWORD>(s.size()));
    return s;
}

std::wstring HwndKey(HWND h)
{
    wchar_t buf[40];
    swprintf_s(buf, L"hwnd:%llx", static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(h)));
    return buf;
}

std::wstring FileStem(const std::wstring& path)
{
    size_t slash = path.find_last_of(L"\\/");
    std::wstring name = slash == std::wstring::npos ? path : path.substr(slash + 1);
    size_t dot = name.find_last_of(L'.');
    if (dot != std::wstring::npos && dot > 0)
        name.resize(dot);
    return name;
}

std::wstring AppsFolderPath(const std::wstring& aumid) { return L"shell:AppsFolder\\" + aumid; }

// Quotes a single command-line argument according to CommandLineToArgvW rules.
std::wstring QuoteArg(const std::wstring& arg)
{
    std::wstring out(1, L'"');
    size_t bs = 0;
    for (wchar_t ch : arg) {
        if (ch == L'\\') {
            ++bs;
        } else {
            out.append(ch == L'"' ? bs * 2 + 1 : bs, L'\\');
            out.push_back(ch);
            bs = 0;
        }
    }
    return out.append(bs * 2, L'\\') + L'"';
}

bool IsAsciiWs(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\v' || c == '\f'; }

std::string XmlAttr(std::string_view tag, std::string_view ltag, std::string_view name)
{
    for (size_t a = ltag.find(name); a != std::string_view::npos; a = ltag.find(name, a + 1)) {
        if (!a || !IsAsciiWs(ltag[a - 1]))
            continue;
        size_t v = a + name.size();
        while (v < ltag.size() && IsAsciiWs(ltag[v]))
            ++v;
        if (v >= ltag.size() || ltag[v] != '=')
            continue;
        ++v;
        while (v < ltag.size() && IsAsciiWs(ltag[v]))
            ++v;
        if (v < ltag.size() && (ltag[v] == '"' || ltag[v] == '\'')) {
            const size_t q = tag.find(ltag[v], v + 1);
            return q == std::string_view::npos ? std::string() : std::string(tag.substr(v + 1, q - v - 1));
        }
    }
    return {};
}

// The app id of a packaged app's executable (under WindowsApps): such a file cannot be run
// itself (#64, Windows Terminal), its app is started by id. The package folder is
// <name>_<version>_<arch>_<resource>_<publisher id>; the family is <name>_<publisher id>; the
// application is the one of the package's manifest whose Executable is this file.
std::wstring PackagedAumid(const std::wstring& exe)
{
    const size_t at = Lower(exe).find(L"\\windowsapps\\");
    const size_t start = at == std::wstring::npos ? std::wstring::npos : at + 13;
    const size_t end = start == std::wstring::npos ? std::wstring::npos : exe.find(L'\\', start);
    if (end == std::wstring::npos)
        return {};
    const std::wstring folder = exe.substr(start, end - start);
    const size_t first = folder.find(L'_'), last = folder.rfind(L'_');
    if (first == std::wstring::npos || last == first)
        return {};
    const std::wstring family = folder.substr(0, first) + L"_" + folder.substr(last + 1);
    HANDLE file = CreateFileW((exe.substr(0, end) + L"\\AppxManifest.xml").c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return {};
    std::string xml(size_t(std::min<DWORD>(GetFileSize(file, nullptr), 1u << 20)), '\0');
    DWORD got = 0;
    ReadFile(file, xml.data(), DWORD(xml.size()), &got, nullptr);
    CloseHandle(file);
    xml.resize(got);
    auto lowerA = [](std::string s) {
        for (char& ch : s)
            ch = char(tolower((unsigned char)ch));
        return s;
    };
    const std::string lx = lowerA(xml), lrel = lowerA(winrt::to_string(exe.substr(end + 1))); // the manifest is UTF-8
    std::string id;
    for (size_t p = lx.find("<application"); p != std::string::npos; p = lx.find("<application", p + 12)) {
        if (p + 12 >= lx.size() || !IsAsciiWs(lx[p + 12]))
            continue;
        const size_t close = lx.find('>', p);
        if (close == std::string::npos)
            break;
        const std::string_view tag(xml.data() + p, close - p), ltag(lx.data() + p, close - p);
        const std::string executable = lowerA(XmlAttr(tag, ltag, "executable")), appId = XmlAttr(tag, ltag, "id");
        if (id.empty())
            id = appId; // the first application, if no executable matches
        if (!executable.empty() && executable == lrel) {
            id = appId;
            break;
        }
    }
    return id.empty() ? std::wstring() : family + L"!" + std::wstring(winrt::to_hstring(id));
}

bool IsPackagedPath(const std::wstring& path) { return Lower(path).find(L"\\windowsapps\\") != std::wstring::npos; }

// ---- process and window identity -----------------------------------------

std::wstring ImagePath(HANDLE process)
{
    std::wstring buf(512, L'\0');
    for (;;) {
        DWORD n = static_cast<DWORD>(buf.size());
        if (QueryFullProcessImageNameW(process, 0, buf.data(), &n)) {
            buf.resize(n);
            return buf;
        }
        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || buf.size() >= 32768)
            return {};
        buf.resize(buf.size() * 2);
    }
}

// AUMID of a packaged process; empty for classic desktop processes.
std::wstring PackagedAumid(HANDLE process)
{
    wchar_t buf[APPLICATION_USER_MODEL_ID_MAX_LENGTH] = {};
    UINT32 len = ARRAYSIZE(buf);
    if (GetApplicationUserModelId(process, &len, buf) != ERROR_SUCCESS)
        return {};
    return buf;
}

std::wstring StoreAumid(IPropertyStore* store)
{
    PROPVARIANT v;
    PropVariantInit(&v);
    std::wstring result;
    if (SUCCEEDED(store->GetValue(PKEY_AppUserModel_ID, &v)) && v.vt == VT_LPWSTR && v.pwszVal)
        result = v.pwszVal;
    PropVariantClear(&v);
    return result;
}

// relaunch: the command the window asks to be started again with (a browser's web app names
// itself there: --app-id).
std::wstring WindowAumid(HWND h, std::wstring* relaunch = nullptr)
{
    winrt::com_ptr<IPropertyStore> store;
    if (FAILED(SHGetPropertyStoreForWindow(h, IID_PPV_ARGS(store.put()))) || !store)
        return {};
    if (relaunch) {
        PROPVARIANT v;
        PropVariantInit(&v);
        if (SUCCEEDED(store->GetValue(PKEY_AppUserModel_RelaunchCommand, &v)) && v.vt == VT_LPWSTR && v.pwszVal)
            *relaunch = v.pwszVal;
        PropVariantClear(&v);
    }
    return StoreAumid(store.get());
}

// UWP frame windows live in ApplicationFrameHost; the app itself owns a child
// "Windows.UI.Core.CoreWindow" in another process.
std::wstring HostedUwpAumid(HWND frame, DWORD framePid)
{
    struct Ctx {
        DWORD framePid;
        std::wstring aumid;
    } ctx{framePid, {}};
    EnumChildWindows(
        frame,
        [](HWND child, LPARAM lp) -> BOOL {
            auto* c = reinterpret_cast<Ctx*>(lp);
            wchar_t cls[64] = {};
            GetClassNameW(child, cls, ARRAYSIZE(cls));
            if (wcscmp(cls, L"Windows.UI.Core.CoreWindow") != 0)
                return TRUE;
            DWORD pid = 0;
            GetWindowThreadProcessId(child, &pid);
            if (!pid || pid == c->framePid)
                return TRUE;
            winrt::handle proc(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
            if (proc)
                c->aumid = PackagedAumid(proc.get());
            return c->aumid.empty() ? TRUE : FALSE;
        },
        reinterpret_cast<LPARAM>(&ctx));
    return ctx.aumid;
}

// A browser's web app (an installed site in its own window, Chrome's and Edge's): its own app,
// not the browser, though the browser's program runs it.
bool IsWebApp(const std::wstring& aumid, const std::wstring& command)
{
    std::wstring id = Lower(aumid), c = Lower(command);
    return id.find(L"_crx_") != std::wstring::npos || c.find(L"--app-id=") != std::wstring::npos || c.find(L"--app=") != std::wstring::npos;
}

// The program that started this process, where it verifiably is the app's launcher (#52, Baidu
// Netdisk: BaiduNetdisk.exe starts module\BrowserEngine\BaiduNetdiskUnite.exe, whose window is the
// app's): the same session, started before it, in a folder above its program, which is named after
// it. A game started by its store (a folder below too, another name) stays apart.
std::wstring LauncherOf(HANDLE process, DWORD pid, ULONGLONG created, const std::wstring& exePath)
{
    using Query = LONG(NTAPI*)(HANDLE, ULONG, PVOID, ULONG, PULONG);
    static const auto kQuery = reinterpret_cast<Query>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationProcess"));
    struct Basic { // PROCESS_BASIC_INFORMATION
        LONG exitStatus;
        PVOID peb;
        ULONG_PTR affinity;
        LONG priority;
        ULONG_PTR pid, parent;
    } info{};
    if (!kQuery || kQuery(process, 0 /* ProcessBasicInformation */, &info, sizeof(info), nullptr) < 0 || !info.parent)
        return {};
    const DWORD parentPid = DWORD(info.parent);
    DWORD session = 0, parentSession = 0;
    if (!ProcessIdToSessionId(pid, &session) || !ProcessIdToSessionId(parentPid, &parentSession) || session != parentSession)
        return {};
    winrt::handle parent(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, parentPid));
    FILETIME c{}, e{}, k{}, u{};
    if (!parent || !GetProcessTimes(parent.get(), &c, &e, &k, &u) || ((ULONGLONG(c.dwHighDateTime) << 32) | c.dwLowDateTime) > created)
        return {}; // that id is another, later process now
    std::wstring launcher = ImagePath(parent.get());
    const size_t slash = launcher.find_last_of(L'\\');
    if (slash == std::wstring::npos)
        return {};
    std::wstring dir = Lower(launcher.substr(0, slash + 1)), own = Lower(exePath);
    if (!own.starts_with(dir) || own.find(L'\\', dir.size()) == std::wstring::npos)
        return {}; // not in a folder below the launcher's
    std::wstring stem = Lower(FileStem(launcher));
    if (stem.size() < 4 || !Lower(FileStem(exePath)).starts_with(stem))
        return {};
    return launcher;
}

struct WinInfo {
    DWORD pid = 0;
    std::wstring exePath;   // empty if the process could not be opened
    std::wstring aumid;     // original case
    std::wstring key;       // lowercase grouping key
    ULONGLONG created = 0;  // process creation time, orders newly seen groups
    bool webApp = false;    // a browser's web app (IsWebApp)
    std::wstring launcher;  // no app identity of its own: the program that started it (LauncherOf)
};

WinInfo ComputeInfo(HWND h, DWORD pid, const wchar_t* cls)
{
    WinInfo wi;
    wi.pid = pid;
    std::wstring relaunch;
    bool frame = wcscmp(cls, L"ApplicationFrameWindow") == 0;
    winrt::handle proc(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    if (frame) {
        wi.aumid = HostedUwpAumid(h, pid);
        if (wi.aumid.empty())
            wi.aumid = WindowAumid(h);
        // exePath stays empty: it would be ApplicationFrameHost.exe for every app.
    } else {
        wi.aumid = WindowAumid(h, &relaunch);
        if (proc) {
            wi.exePath = ImagePath(proc.get());
            if (wi.aumid.empty())
                wi.aumid = PackagedAumid(proc.get());
        }
    }
    if (proc) {
        FILETIME c{}, e{}, k{}, u{};
        if (GetProcessTimes(proc.get(), &c, &e, &k, &u))
            wi.created = (static_cast<ULONGLONG>(c.dwHighDateTime) << 32) | c.dwLowDateTime;
    }
    wi.webApp = IsWebApp(wi.aumid, relaunch);
    if (wi.aumid.empty() && proc && !wi.exePath.empty() && wi.created)
        wi.launcher = LauncherOf(proc.get(), pid, wi.created, wi.exePath);
    if (!wi.aumid.empty())
        wi.key = Lower(wi.aumid);
    else if (!wi.exePath.empty())
        wi.key = Lower(wi.exePath);
    else
        wi.key = HwndKey(h);
    return wi;
}

// The Windows taskbar's rules for what gets a button.
bool IsAppWindow(HWND h, DWORD& pid, wchar_t* cls, int clsCap, std::wstring& title)
{
    if (!IsWindowVisible(h))
        return false;
    LONG_PTR ex = GetWindowLongPtrW(h, GWL_EXSTYLE);
    bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
    if ((ex & WS_EX_TOOLWINDOW) && !appWindow)
        return false;
    if (GetWindow(h, GW_OWNER) && !appWindow)
        return false;
    if ((ex & WS_EX_NOACTIVATE) && !appWindow)
        return false;
    DWORD cloaked = 0;
    if (SUCCEEDED(DwmGetWindowAttribute(h, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked)
        return false;
    pid = 0;
    GetWindowThreadProcessId(h, &pid);
    if (!pid || pid == GetCurrentProcessId())
        return false;
    if (!GetClassNameW(h, cls, clsCap))
        return false;
    // Shell windows, plus top-level CoreWindows (Start, Search: shell UI, not apps;
    // real UWP apps appear through their ApplicationFrameWindow).
    for (const wchar_t* shell : {L"Shell_TrayWnd", L"Shell_SecondaryTrayWnd", L"Progman", L"WorkerW", L"Windows.UI.Core.CoreWindow"})
        if (wcscmp(cls, shell) == 0)
            return false;
    // InternalGetWindowText never sends WM_GETTEXT, so a hung window cannot block us.
    wchar_t text[512] = {};
    if (InternalGetWindowText(h, text, ARRAYSIZE(text)) <= 0)
        return false;
    title = text;
    return true;
}

// ---- shortcuts and names -----------------------------------------------------

// Loads a .lnk without resolving it (no disk search, no UI).
bool ReadLink(const std::wstring& path, std::wstring& target, std::wstring& aumid, std::wstring* args = nullptr)
{
    winrt::com_ptr<IShellLinkW> link;
    if (FAILED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(link.put()))))
        return false;
    auto file = link.try_as<IPersistFile>();
    if (!file || FAILED(file->Load(path.c_str(), STGM_READ)))
        return false;
    wchar_t buf[MAX_PATH * 2] = {};
    if (SUCCEEDED(link->GetPath(buf, ARRAYSIZE(buf), nullptr, 0)))
        target = buf;
    if (args) {
        std::wstring line(1024, L'\0');
        if (SUCCEEDED(link->GetArguments(line.data(), int(line.size()))))
            *args = line.c_str(); // NOLINT(readability-redundant-string-cstr): up to the terminator, not the whole buffer
    }
    if (auto store = link.try_as<IPropertyStore>())
        aumid = StoreAumid(store.get());
    return true;
}

std::wstring FileDescription(const std::wstring& path)
{
    DWORD ignored = 0;
    DWORD size = GetFileVersionInfoSizeW(path.c_str(), &ignored);
    if (!size)
        return {};
    std::vector<BYTE> data(size);
    if (!GetFileVersionInfoW(path.c_str(), 0, size, data.data()))
        return {};
    struct Lang {
        WORD lang, codepage;
    };
    std::vector<Lang> langs;
    Lang* tr = nullptr;
    UINT len = 0;
    if (VerQueryValueW(data.data(), L"\\VarFileInfo\\Translation", reinterpret_cast<void**>(&tr), &len) && tr)
        for (UINT i = 0; i < len / sizeof(Lang); ++i)
            langs.push_back(tr[i]);
    langs.insert(langs.end(), {{0x0409, 0x04B0}, {0x0409, 0x04E4}});
    for (const Lang& l : langs) {
        wchar_t sub[80];
        swprintf_s(sub, L"\\StringFileInfo\\%04x%04x\\FileDescription", l.lang, l.codepage);
        wchar_t* value = nullptr;
        UINT n = 0;
        if (VerQueryValueW(data.data(), sub, reinterpret_cast<void**>(&value), &n) && value && n > 1 && value[0])
            return value;
    }
    return {};
}

std::wstring ShellDisplayName(const std::wstring& parsingName)
{
    winrt::com_ptr<IShellItem> item;
    if (FAILED(SHCreateItemFromParsingName(parsingName.c_str(), nullptr, IID_PPV_ARGS(item.put()))))
        return {};
    PWSTR name = nullptr;
    std::wstring result;
    if (SUCCEEDED(item->GetDisplayName(SIGDN_NORMALDISPLAY, &name)) && name)
        result = name;
    CoTaskMemFree(name);
    return result;
}

// The window's own icon. WM_GETICON goes through SendMessageTimeout so a hung
// window costs at most 100 ms; the HICON belongs to the app, never destroyed.
bool WindowIcon(HWND h, int size, IconImage& out)
{
    if (!IsWindow(h) || IsHungAppWindow(h))
        return false;
    const WPARAM order[] = {size > 24 ? WPARAM(ICON_BIG) : WPARAM(ICON_SMALL), WPARAM(ICON_SMALL2), WPARAM(ICON_BIG), WPARAM(ICON_SMALL)};
    for (WPARAM type : order) {
        DWORD_PTR result = 0;
        if (!SendMessageTimeoutW(h, WM_GETICON, type, 0, SMTO_ABORTIFHUNG, kWindowIconTimeoutMs, &result) || !result)
            continue;
        if (HIconToImage(reinterpret_cast<HICON>(result), out))
            return true;
    }
    for (int index : {GCLP_HICON, GCLP_HICONSM}) {
        HICON icon = reinterpret_cast<HICON>(GetClassLongPtrW(h, index));
        if (icon && HIconToImage(icon, out))
            return true;
    }
    return false;
}

// ---- pins ---------------------------------------------------------------------

struct Pin {
    PinnedApp app;
    std::wstring key, target, exe, aumid;
    bool multipleWindows = false;
    // Starts one browser profile or web app (--profile-directory, --app-id): only windows of that
    // identity are its, never the browser's other windows.
    bool specific = false;
};

Pin ResolvePin(const PinnedApp& app)
{
    Pin p;
    p.app = app;
    std::wstring aumid = app.aumid;
    std::wstring target, args = app.args;
    if (!app.path.empty()) {
        if (IsLink(app.path)) {
            std::wstring linkAumid;
            if (ReadLink(app.path, target, linkAumid, &args) && aumid.empty())
                aumid = linkAumid; // the link's own AUMID wins over the path
        } else {
            target = app.path;
        }
    }
    p.specific = IsWebApp(aumid, args) || Lower(args).find(L"--profile-directory") != std::wstring::npos;
    p.exe = AppExecutable(target);
    p.aumid = aumid;
    p.multipleWindows = AllowsMultipleWindows(PinnedApp{app.name, p.exe, {}, aumid});
    p.target = Lower(target);
    if (!aumid.empty()) {
        p.key = Lower(aumid);
    } else if (!target.empty()) {
        p.key = p.target;
    } else {
        p.key = Lower(app.path);
    }
    if (p.key.empty())
        p.key = Lower(app.name);
    return p;
}

// ---- worker jobs ----------------------------------------------------------------

struct Job {
    bool name = false; // false: icon
    std::wstring key;
    int size = 0;
    std::wstring sig, aumid, pinPath, exePath;
    std::vector<HWND> hwnds;
};

struct IconEntry {
    bool requested = false, loading = false, have = false;
    std::wstring sig;
    ULONGLONG failedAt = 0;
    uint64_t used = 0;
    IconImage image;
};

// What the model knows about one item that icon and name lookups need.
struct Meta {
    std::wstring aumid, pinPath, exePath;
    std::vector<HWND> hwnds;
    std::vector<AppProcessIdentity> processTargets; // retained for pinned apps closed to the tray
    std::wstring Signature() const { return aumid + L"|" + pinPath + L"|" + exePath; }
};

struct IconKey {
    std::wstring key;
    int size;
    bool operator==(const IconKey&) const = default;
};
struct IconKeyHash {
    size_t operator()(const IconKey& k) const { return std::hash<std::wstring>()(k.key) * 31 + size_t(k.size); }
};

bool Same(const TaskItem& a, const TaskItem& b)
{
    return a.key == b.key && a.name == b.name && a.pinned == b.pinned && a.pinIndex == b.pinIndex && a.windows == b.windows && a.active == b.active &&
           a.attention == b.attention && a.iconVersion == b.iconVersion;
}

struct ScanRec {
    HWND hwnd = nullptr;
    int z = 0; // EnumWindows order, top first
    std::wstring title;
    WinInfo info;
};

} // namespace

struct TaskModel::Impl {
    HWND ui;
    std::function<void()> onChanged;
    std::function<bool(const std::wstring&)> openExisting;
    std::function<void(HWND)> onFlash;
    ComScope com;
    UINT shellHookMsg = 0, readyMsg = 0;
    std::vector<HWINEVENTHOOK> hooks;
    bool shellHooked = false, timerArmed = false, dirty = true, notify = false, showRunning = true;

    // UI-thread state.
    std::vector<Pin> pins;
    std::unordered_map<std::wstring, size_t> pinByKey, pinByTarget;
    std::unordered_map<HWND, WinInfo> infos;      // identity cache per window
    std::unordered_map<HWND, uint64_t> lastActive; // activation sequence number
    std::unordered_set<HWND> attention;
    uint64_t seq = 0;
    HWND lastFg = nullptr;
    std::vector<ScanRec> scan;
    std::vector<std::wstring> order;               // unpinned groups, first seen first
    std::vector<TaskItem> items;                   // pins + running groups (not filtered by showRunning)
    std::unordered_map<std::wstring, Meta> meta;
    std::unordered_map<std::wstring, std::wstring> names;
    std::unordered_set<std::wstring> namesRequested;
    std::unordered_map<std::wstring, uint64_t> iconVersion;

    // Shared with the worker.
    std::mutex mutex, fallbackMutex;
    std::condition_variable cv, fallbackCv;
    std::deque<Job> jobs;
    std::unordered_map<IconKey, IconEntry, IconKeyHash> icons;
    std::unordered_set<std::wstring> liveIconKeys;
    std::unordered_map<std::wstring, int> latestIconSize;
    uint64_t iconUse = 0;
    std::vector<std::wstring> readyIcons;
    std::vector<std::pair<std::wstring, std::wstring>> readyNames;
    bool stop = false, fallbackStop = false;
    std::thread worker, fallbackThread;
    struct LaunchPlan {
        std::wstring file, args, dir;
    };
    std::optional<std::pair<LaunchPlan, std::wstring>> fallbackPending;
    uint64_t fallbackGen = 0;

    static Impl* g_self;

    Impl(const Impl&) = delete;
    Impl& operator=(const Impl&) = delete;
    Impl(HWND window, std::function<void()> changed, std::function<bool(const std::wstring&)> opener)
        : ui(window),
          onChanged(std::move(changed)),
          openExisting(std::move(opener)),
          shellHookMsg(RegisterWindowMessageW(L"SHELLHOOK")),
          readyMsg(RegisterWindowMessageW(L"Atrium.TaskModel.Ready")),
          shellHooked(RegisterShellHookWindow(window) != FALSE)
    {
        g_self = this;
        if (!shellHooked)
            Log(L"tasks: RegisterShellHookWindow failed (%lu); no flash notifications", GetLastError());
        worker = std::thread([this] { WorkerMain(); });
        fallbackThread = std::thread([this] { FallbackMain(); });
        struct Range {
            DWORD lo, hi;
        };
        const Range ranges[] = {{EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE},
                                {EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE},
                                {EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED},
                                {EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND},
                                {EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND}};
        for (const Range& r : ranges) {
            HWINEVENTHOOK h = SetWinEventHook(r.lo, r.hi, nullptr, &Impl::WinEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (h)
                hooks.push_back(h);
            else
                Log(L"tasks: SetWinEventHook(0x%X-0x%X) failed (%lu)", r.lo, r.hi, GetLastError());
        }
        Rescan();
        Rebuild();
        ArmTimer(); // catches events that raced the initial scan
    }

    ~Impl()
    {
        for (HWINEVENTHOOK h : hooks)
            UnhookWinEvent(h);
        if (shellHooked)
            DeregisterShellHookWindow(ui);
        if (timerArmed)
            KillTimer(ui, kTimerId);
        {
            std::scoped_lock g(mutex, fallbackMutex);
            stop = fallbackStop = true;
        }
        cv.notify_all();
        fallbackCv.notify_all();
        if (worker.joinable())
            worker.join();
        if (fallbackThread.joinable())
            fallbackThread.join();
        g_self = nullptr;
    }

    // ---- events -------------------------------------------------------------

    static void CALLBACK WinEventProc(HWINEVENTHOOK, DWORD event, HWND hwnd, LONG idObject, LONG idChild, DWORD, DWORD)
    {
        if (!g_self || idObject != OBJID_WINDOW || idChild != CHILDID_SELF || !hwnd)
            return;
        try {
            g_self->OnEvent(event, hwnd);
        } catch (...) {
            Log(L"tasks: exception in WinEvent handler");
        }
    }

    void OnEvent(DWORD event, HWND hwnd)
    {
        if (event == EVENT_SYSTEM_FOREGROUND) {
            NoteForeground(hwnd);
        } else if (event == EVENT_OBJECT_DESTROY) {
            if (!infos.contains(hwnd))
                return; // not something we show
        } else if (GetAncestor(hwnd, GA_PARENT) != GetDesktopWindow()) {
            return; // child window
        }
        if (event == EVENT_OBJECT_SHOW || event == EVENT_OBJECT_UNCLOAKED || event == EVENT_OBJECT_CREATE)
            infos.erase(hwnd); // identity (AUMID) may have been set meanwhile
        MarkDirty();
    }

    void NoteForeground(HWND fg)
    {
        if (!fg)
            return;
        HWND root = GetAncestor(fg, GA_ROOTOWNER);
        lastActive[fg] = ++seq;
        attention.erase(fg);
        // The app in front: also a window of it that flashed hidden (in the tray) has been answered.
        DWORD fgPid = 0;
        GetWindowThreadProcessId(fg, &fgPid);
        if (fgPid)
            std::erase_if(attention, [&](HWND h) {
                DWORD pid = 0;
                GetWindowThreadProcessId(h, &pid);
                return pid == fgPid;
            });
        if (root && root != fg) {
            lastActive[root] = ++seq;
            attention.erase(root);
        }
        lastFg = root ? root : fg;
    }

    void MarkDirty()
    {
        dirty = true;
        ArmTimer();
    }

    void ArmTimer()
    {
        if (!timerArmed && SetTimer(ui, kTimerId, kCoalesceMs, nullptr))
            timerArmed = true;
    }

    bool HandleMessage(UINT msg, WPARAM wp, LPARAM lp)
    {
        try {
            if (msg == WM_TIMER && wp == kTimerId) {
                KillTimer(ui, kTimerId);
                timerArmed = false;
                Tick();
                return true;
            }
            if (shellHookMsg && msg == shellHookMsg) {
                OnShellHook(wp, reinterpret_cast<HWND>(lp));
                return true;
            }
            if (readyMsg && msg == readyMsg) {
                OnReady();
                return true;
            }
        } catch (const std::exception& e) {
            Log(L"tasks: exception in HandleMessage: %S", e.what());
        } catch (...) {
            Log(L"tasks: unknown exception in HandleMessage");
        }
        return false;
    }

    void OnShellHook(WPARAM code, HWND hwnd)
    {
        if (code == HSHELL_FLASH && hwnd) {
            HWND root = GetAncestor(hwnd, GA_ROOTOWNER);
            root = root ? root : hwnd;
            HWND fg = GetForegroundWindow();
            if (root != fg && root != GetAncestor(fg, GA_ROOTOWNER)) {
                attention.insert(root);
                if (onFlash)
                    onFlash(root);
            }
        }
        MarkDirty(); // window created/destroyed/activated/redrawn
    }

    void OnReady()
    {
        std::vector<std::wstring> doneIcons;
        std::vector<std::pair<std::wstring, std::wstring>> doneNames;
        {
            std::lock_guard g(mutex);
            doneIcons.swap(readyIcons);
            doneNames.swap(readyNames);
        }
        for (auto& key : doneIcons)
            if (meta.contains(key))
                ++iconVersion[key];
        for (auto& [key, name] : doneNames)
            if (meta.contains(key))
                names[key] = name;
        if (Rebuild()) {
            notify = true;
            ArmTimer();
        }
    }

    void Tick()
    {
        bool changed = false;
        if (dirty) {
            dirty = false;
            Rescan();
            changed = Rebuild();
        }
        if (changed || notify) {
            notify = false;
            if (onChanged)
                onChanged();
        }
    }

    // ---- model --------------------------------------------------------------

    HWND ForegroundRoot() const
    {
        HWND fg = GetForegroundWindow();
        if (!fg)
            return nullptr;
        HWND root = GetAncestor(fg, GA_ROOTOWNER);
        return root ? root : fg;
    }

    static BOOL CALLBACK CollectWindow(HWND h, LPARAM lp)
    {
        reinterpret_cast<std::vector<HWND>*>(lp)->push_back(h);
        return TRUE;
    }

    void Rescan()
    {
        std::vector<HWND> all;
        EnumWindows(&Impl::CollectWindow, reinterpret_cast<LPARAM>(&all));
        scan.clear();
        std::unordered_set<HWND> seen;
        int z = 0;
        for (HWND h : all) {
            ++z;
            DWORD pid = 0;
            wchar_t cls[256] = {};
            std::wstring title;
            if (!IsAppWindow(h, pid, cls, ARRAYSIZE(cls), title))
                continue;
            auto it = infos.find(h);
            if (it == infos.end() || it->second.pid != pid)
                it = infos.insert_or_assign(h, ComputeInfo(h, pid, cls)).first;
            seen.insert(h);
            scan.push_back(ScanRec{h, z, std::move(title), it->second});
        }
        std::erase_if(infos, [&](const auto& kv) { return !seen.contains(kv.first); });
        std::erase_if(lastActive, [](const auto& kv) { return !IsWindow(kv.first); });
        std::erase_if(attention, [](HWND h) { return !IsWindow(h); });
        // Foreground changes normally arrive as events; this covers a missed one.
        HWND fg = ForegroundRoot();
        if (fg && fg != lastFg)
            NoteForeground(fg);
    }

    // Pin that a window with this identity belongs to, or -1.
    int MatchPin(const WinInfo& info) const
    {
        auto it = pinByKey.find(info.key);
        if (it != pinByKey.end())
            return int(it->second);
        // Desktop launchers can give the actual window a different identity or executable.
        // Exact AUMIDs still win; an executable shared by several pins is ambiguous. A web app
        // is not its browser.
        if (!info.exePath.empty() && !info.webApp) {
            auto jt = pinByTarget.find(Lower(info.exePath));
            if (jt != pinByTarget.end() && jt->second < pins.size())
                return int(jt->second);
        }
        // A window without an identity of its own, of a program its pinned launcher started.
        if (!info.launcher.empty()) {
            auto jt = pinByTarget.find(Lower(info.launcher));
            if (jt != pinByTarget.end() && jt->second < pins.size())
                return int(jt->second);
        }
        return -1;
    }

    // Recomputes `items`/`meta` from `scan` and the pins; true if `items` changed.
    bool Rebuild()
    {
        std::vector<TaskItem> out;
        std::unordered_map<std::wstring, Meta> newMeta;
        std::unordered_map<std::wstring, size_t> at;
        std::unordered_map<std::wstring, ULONGLONG> firstSeen; // for ordering new groups
        std::unordered_map<std::wstring, std::wstring> firstTitle;

        for (size_t i = 0; i < pins.size(); ++i) {
            const Pin& p = pins[i];
            TaskItem t;
            t.key = p.key;
            t.name = p.app.name;
            t.pinned = true;
            t.pinIndex = int(i);
            t.multipleWindows = p.multipleWindows;
            at[p.key] = out.size();
            out.push_back(std::move(t));
            Meta& m = newMeta[p.key];
            m.aumid = p.aumid;
            m.pinPath = p.app.path;
            m.exePath = p.exe;
        }
        size_t pinCount = out.size();

        // Most recently active first; never-activated windows keep z-order.
        std::vector<const ScanRec*> sorted;
        sorted.reserve(scan.size());
        for (const ScanRec& r : scan)
            sorted.push_back(&r);
        auto activity = [&](HWND h) {
            auto it = lastActive.find(h);
            return it == lastActive.end() ? uint64_t(0) : it->second;
        };
        std::ranges::stable_sort(sorted, [&](const ScanRec* a, const ScanRec* b) {
            uint64_t x = activity(a->hwnd), y = activity(b->hwnd);
            return x != y ? x > y : a->z < b->z;
        });

        HWND fg = ForegroundRoot();
        std::unordered_set<std::wstring> running;
        for (const ScanRec* r : sorted) {
            int pin = MatchPin(r->info);
            const std::wstring& key = pin >= 0 ? pins[size_t(pin)].key : r->info.key;
            auto it = at.find(key);
            if (it == at.end()) {
                TaskItem t;
                t.key = key;
                it = at.emplace(key, out.size()).first;
                out.push_back(std::move(t));
                firstTitle[key] = r->title;
            }
            TaskItem& t = out[it->second];
            t.windows.push_back(r->hwnd);
            if (fg && (r->hwnd == fg || GetAncestor(r->hwnd, GA_ROOTOWNER) == fg))
                t.active = true;
            if (attention.contains(r->hwnd))
                t.attention = true;
            Meta& m = newMeta[key];
            if (m.exePath.empty())
                m.exePath = r->info.exePath;
            if (m.aumid.empty())
                m.aumid = r->info.aumid;
            m.hwnds.push_back(r->hwnd);
            if (std::ranges::none_of(m.processTargets,
                                     [&](const AppProcessIdentity& target) { return target.exePath == r->info.exePath && target.aumid == r->info.aumid; }))
                m.processTargets.push_back({r->info.exePath, r->info.aumid});
            if (it->second >= pinCount) {
                running.insert(key);
                auto f = firstSeen.find(key);
                if (f == firstSeen.end() || (r->info.created && r->info.created < f->second))
                    firstSeen[key] = r->info.created;
            }
        }

        // A window that flashed while hidden (WeChat, WeCom in the tray flash theirs for a message)
        // lights its app's item, found by its program.
        for (HWND h : attention) {
            if (std::ranges::any_of(scan, [&](const ScanRec& r) { return r.hwnd == h; }))
                continue;
            DWORD pid = 0;
            GetWindowThreadProcessId(h, &pid);
            winrt::handle proc(pid ? OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid) : nullptr);
            const std::wstring exe = proc ? ImagePath(proc.get()) : std::wstring();
            if (exe.empty())
                continue;
            for (TaskItem& t : out)
                if (const Meta& m = newMeta[t.key]; !m.exePath.empty() && _wcsicmp(m.exePath.c_str(), exe.c_str()) == 0) {
                    t.attention = true;
                    break;
                }
        }

        // A shortcut may point to a launcher; retain the actual app identity after it hides.
        for (auto& [key, value] : newMeta) {
            const auto previous = meta.find(key);
            if (value.processTargets.empty() && previous != meta.end())
                value.processTargets = previous->second.processTargets;
        }

        // Unpinned groups: keep first-seen order, append new ones by process start.
        std::erase_if(order, [&](const std::wstring& k) { return !running.contains(k); });
        std::vector<std::wstring> fresh;
        for (const auto& k : running)
            if (std::ranges::find(order, k) == order.end())
                fresh.push_back(k);
        std::ranges::sort(fresh,
                          [&](const std::wstring& a, const std::wstring& b) { return firstSeen[a] != firstSeen[b] ? firstSeen[a] < firstSeen[b] : a < b; });
        order.insert(order.end(), fresh.begin(), fresh.end());

        std::vector<TaskItem> result(out.begin(), out.begin() + std::ptrdiff_t(pinCount));
        for (const auto& k : order)
            result.push_back(std::move(out[at[k]]));

        for (TaskItem& t : result) {
            const Meta& m = newMeta[t.key];
            if (!t.pinned)
                t.multipleWindows = AllowsMultipleWindows(PinnedApp{t.name, m.exePath, {}, m.aumid});
            if (t.name.empty()) {
                auto n = names.find(t.key);
                if (n != names.end()) {
                    t.name = n->second;
                } else {
                    // Provisional until the worker has the shell display name.
                    t.name = !m.exePath.empty() ? FileStem(m.exePath) : !m.pinPath.empty() ? FileStem(m.pinPath) : firstTitle[t.key];
                    if (t.name.empty())
                        t.name = m.aumid;
                }
            }
            auto v = iconVersion.find(t.key);
            t.iconVersion = v == iconVersion.end() ? 0 : v->second;
            bool pinNamed = t.pinned && !pins[size_t(t.pinIndex)].app.name.empty();
            if (!pinNamed && !names.contains(t.key) && (!m.aumid.empty() || !m.exePath.empty()) && namesRequested.insert(t.key).second) {
                Job j;
                j.name = true;
                j.key = t.key;
                j.aumid = m.aumid;
                j.exePath = m.exePath;
                j.pinPath = m.pinPath;
                Enqueue(j);
            }
        }
        meta = std::move(newMeta);

        bool changed = result.size() != items.size();
        for (size_t i = 0; !changed && i < result.size(); ++i)
            changed = !Same(result[i], items[i]);
        items = std::move(result);
        std::erase_if(names, [&](const auto& entry) { return !meta.contains(entry.first); });
        std::erase_if(namesRequested, [&](const auto& key) { return !meta.contains(key); });
        std::erase_if(iconVersion, [&](const auto& entry) { return !meta.contains(entry.first); });
        {
            std::lock_guard g(mutex);
            liveIconKeys.clear();
            for (const auto& [key, value] : meta)
                liveIconKeys.insert(key);
            std::erase_if(latestIconSize, [&](const auto& entry) { return !liveIconKeys.contains(entry.first); });
            PruneIcons();
        }
        return changed;
    }

    const TaskItem* Find(const std::wstring& key) const
    {
        for (const TaskItem& t : items)
            if (t.key == key)
                return &t;
        return nullptr;
    }

    std::vector<TaskItem> Snapshot(bool all) const
    {
        if (showRunning || all)
            return items;
        std::vector<TaskItem> out;
        for (const TaskItem& t : items)
            if (t.pinned)
                out.push_back(t);
        return out;
    }

    void SetPins(const std::vector<PinnedApp>& list)
    {
        bool same = list.size() == pins.size();
        for (size_t i = 0; same && i < list.size(); ++i)
            same = list[i] == pins[i].app;
        if (same)
            return;
        pins.clear();
        pinByKey.clear();
        pinByTarget.clear();
        for (const PinnedApp& app : list) {
            Pin p = ResolvePin(app);
            if (pinByKey.contains(p.key))
                p.key += L"#" + std::to_wstring(pins.size()); // duplicate pin: shown, never matched
            else
                pinByKey[p.key] = pins.size();
            auto addTarget = [&](const std::wstring& target) {
                if (target.empty())
                    return;
                auto [it, added] = pinByTarget.emplace(target, pins.size());
                if (!added && it->second != pins.size())
                    it->second = size_t(-1);
            };
            // A browser profile's or web app's shortcut keeps its explicit identity; the browser's
            // own pin takes its other windows, whichever profile (#52).
            if (!p.specific) {
                addTarget(p.target);
                addTarget(Lower(p.exe));
            }
            pins.push_back(std::move(p));
        }
        if (Rebuild()) {
            notify = true;
            ArmTimer();
        }
    }

    void SetShowRunning(bool show)
    {
        if (show == showRunning)
            return;
        showRunning = show;
        notify = true;
        ArmTimer();
    }

    // ---- icons --------------------------------------------------------------

    // Under mutex. Keep the latest size of every live/pinned item and every
    // in-flight lookup. Historical sizes share a byte/count budget.
    void PruneIcons()
    {
        std::erase_if(icons, [&](const auto& entry) { return !entry.second.loading && !liveIconKeys.contains(entry.first.key); });
        size_t bytes = 0;
        for (const auto& [key, entry] : icons)
            bytes += entry.image.bgra.size();
        while (bytes > 16 * 1024 * 1024 || icons.size() > 512) {
            auto victim = icons.end();
            for (auto it = icons.begin(); it != icons.end(); ++it) {
                const auto current = latestIconSize.find(it->first.key);
                if (it->second.loading || (current != latestIconSize.end() && current->second == it->first.size))
                    continue;
                if (victim == icons.end() || it->second.used < victim->second.used)
                    victim = it;
            }
            if (victim == icons.end())
                break; // protected live working set only
            bytes -= victim->second.image.bgra.size();
            icons.erase(victim);
        }
    }

    void Enqueue(const Job& job)
    {
        {
            std::lock_guard g(mutex);
            jobs.push_back(job);
        }
        cv.notify_one();
    }

    IconImage Icon(const std::wstring& key, int px)
    {
        px = std::clamp(px, 8, 512);
        auto mit = meta.find(key);
        if (mit == meta.end())
            return {};
        const Meta& m = mit->second;
        std::wstring sig = m.Signature();

        std::unique_lock g(mutex);
        latestIconSize[key] = px;
        PruneIcons();
        IconEntry& e = icons[IconKey{key, px}];
        e.used = ++iconUse;
        bool want = !e.requested;
        if (e.requested && !e.loading && e.sig != sig)
            want = true; // identity changed (pinned, relaunched from elsewhere): refresh, keep showing the old one
        if (e.requested && !e.loading && !e.have && e.sig == sig && GetTickCount64() - e.failedAt > kRetryFailedIconMs)
            want = true;
        if (want) {
            e.requested = e.loading = true;
            e.sig = sig;
            jobs.push_back(Job{false, key, px, sig, m.aumid, m.pinPath, m.exePath, m.hwnds});
            cv.notify_one();
        }
        return e.have ? e.image : IconImage{};
    }

    void WorkerMain()
    {
        ComScope workerCom; // STA is fine for IShellItemImageFactory
        for (;;) {
            Job job;
            {
                std::unique_lock g(mutex);
                cv.wait(g, [&] { return stop || !jobs.empty(); });
                if (stop)
                    return;
                job = std::move(jobs.front());
                jobs.pop_front();
                if (!liveIconKeys.contains(job.key)) {
                    if (!job.name)
                        icons.erase(IconKey{job.key, job.size});
                    continue;
                }
            }
            try {
                if (job.name)
                    RunNameJob(job);
                else
                    RunIconJob(job);
            } catch (...) {
                Log(L"tasks: icon worker exception for %s", job.key.c_str());
            }
        }
    }

    void RunNameJob(const Job& job)
    {
        std::wstring name;
        if (!job.aumid.empty())
            name = ShellDisplayName(AppsFolderPath(job.aumid));
        if (name.empty() && !job.exePath.empty())
            name = FileDescription(job.exePath);
        if (name.empty() && !job.pinPath.empty() && IsLink(job.pinPath))
            name = FileStem(job.pinPath);
        if (name.empty() && !job.exePath.empty())
            name = FileStem(job.exePath);
        if (name.empty())
            return;
        {
            std::lock_guard g(mutex);
            readyNames.emplace_back(job.key, std::move(name));
        }
        PostMessageW(ui, readyMsg, 0, 0);
    }

    void RunIconJob(const Job& job)
    {
        std::vector<std::wstring> sources;
        auto add = [&](std::wstring s) {
            if (!s.empty() && std::ranges::find(sources, s) == sources.end())
                sources.push_back(std::move(s));
        };
        add(job.pinPath); // a pin's shortcut carries the icon the user sees in Windows
        if (!job.aumid.empty())
            add(AppsFolderPath(job.aumid));
        // An exe without an icon of its own (a single-file app that sets its window's at run time)
        // shows Windows' generic one: its windows' icon comes first then (#83).
        const bool exeIcon = ExtractIconExW(job.exePath.c_str(), -1, nullptr, nullptr, 0) > 0;
        if (exeIcon)
            add(job.exePath);

        IconImage img;
        LARGE_INTEGER t0, t1, f;
        QueryPerformanceCounter(&t0);
        bool ok = LoadAtriumIcon(job.exePath, job.size, img);
        for (size_t i = 0; !ok && i < sources.size(); ++i)
            ok = LoadShellImage(sources[i], job.size, img);
        for (size_t i = 0; !ok && i < job.hwnds.size(); ++i) {
            IconImage raw;
            if (WindowIcon(job.hwnds[i], job.size, raw)) {
                img = FitToSquare(raw, job.size);
                ok = true;
            }
        }
        if (!ok && !exeIcon && !job.exePath.empty())
            ok = LoadShellImage(job.exePath, job.size, img);
        QueryPerformanceCounter(&t1);
        QueryPerformanceFrequency(&f);
        if (!ok)
            Log(L"tasks: no icon for %s (%.1f ms)", job.key.c_str(), double(t1.QuadPart - t0.QuadPart) * 1000.0 / double(f.QuadPart));

        bool changed = false;
        {
            std::lock_guard g(mutex);
            auto found = icons.find(IconKey{job.key, job.size});
            if (found == icons.end())
                return;
            if (!liveIconKeys.contains(job.key)) {
                icons.erase(found);
                return;
            }
            IconEntry& e = found->second;
            if (e.sig != job.sig)
                return;
            e.loading = false;
            if (ok) {
                changed = !e.have || e.image.bgra != img.bgra;
                e.image = std::move(img);
                e.have = true;
            } else {
                e.failedAt = GetTickCount64();
            }
            if (changed)
                readyIcons.push_back(job.key);
            PruneIcons();
        }
        if (changed)
            PostMessageW(ui, readyMsg, 0, 0);
    }

    // ---- actions --------------------------------------------------------------

    static bool Exec(const std::wstring& file, const std::wstring& args, const std::wstring& dir)
    {
        SHELLEXECUTEINFOW sei{sizeof(sei)};
        sei.fMask = SEE_MASK_FLAG_NO_UI | SEE_MASK_NOASYNC;
        sei.lpFile = file.c_str();
        sei.lpParameters = args.empty() ? nullptr : args.c_str();
        sei.lpDirectory = dir.empty() ? nullptr : dir.c_str();
        sei.nShow = SW_SHOWNORMAL;
        if (!ShellExecuteExW(&sei)) {
            Log(L"tasks: launch of %s failed (%lu)", file.c_str(), GetLastError());
            return false;
        }
        return true;
    }

    // Whether a program shows a window of its own (any visible, unowned, ordinary one).
    static bool ShowsWindow(const std::wstring& exe)
    {
        struct Look {
            const std::wstring* exe;
            bool found = false;
        } look{&exe};
        EnumWindows(
            [](HWND h, LPARAM l) -> BOOL {
                auto& k = *reinterpret_cast<Look*>(l);
                if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) || (GetWindowLongW(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW))
                    return TRUE;
                DWORD pid = 0;
                GetWindowThreadProcessId(h, &pid);
                if (HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)) {
                    wchar_t path[MAX_PATH * 2];
                    DWORD size = DWORD(std::size(path));
                    k.found = QueryFullProcessImageNameW(p, 0, path, &size) && _wcsicmp(path, k.exe->c_str()) == 0;
                    CloseHandle(p);
                }
                return !k.found;
            },
            reinterpret_cast<LPARAM>(&look));
        return look.found;
    }

    // Whether a process of this program runs (with a window or without, in the tray).
    static bool Runs(const std::wstring& exe)
    {
        const wchar_t* name = PathFindFileNameW(exe.c_str());
        bool found = false;
        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        PROCESSENTRY32W pe{sizeof(pe)};
        for (BOOL ok = snap != INVALID_HANDLE_VALUE && Process32FirstW(snap, &pe); ok && !found; ok = Process32NextW(snap, &pe)) {
            if (_wcsicmp(pe.szExeFile, name) != 0)
                continue;
            winrt::handle p(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pe.th32ProcessID));
            found = p && _wcsicmp(ImagePath(p.get()).c_str(), exe.c_str()) == 0;
        }
        if (snap != INVALID_HANDLE_VALUE)
            CloseHandle(snap);
        return found;
    }

    void FallbackMain()
    {
        for (std::unique_lock lk(fallbackMutex);;) {
            fallbackCv.wait(lk, [&] { return fallbackStop || fallbackPending.has_value(); });
            if (fallbackStop)
                return;
            const auto [plan, exe] = std::move(*fallbackPending);
            const uint64_t gen = fallbackGen;
            fallbackPending.reset();
            for (int i = 0;; ++i) {
                if (i == 30) {
                    lk.unlock();
                    Log(L"tasks: %s showed no window from its tray icon: started", PathFindFileNameW(exe.c_str()));
                    const ComScope fallbackCom(COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
                    Exec(plan.file, plan.args, plan.dir);
                    lk.lock();
                    break;
                }
                if (fallbackCv.wait_for(lk, std::chrono::milliseconds(100), [&] { return fallbackStop || fallbackGen != gen; }))
                    break;
                lk.unlock();
                const bool shown = ShowsWindow(exe);
                lk.lock();
                if (shown || fallbackStop || fallbackGen != gen)
                    break;
            }
        }
    }

    void Launch(const std::wstring& key)
    {
        const TaskItem* task = Find(key);
        if (task && !task->multipleWindows) {
            for (HWND window : task->windows)
                if (IsWindowVisible(window)) {
                    BringToFront(window);
                    return;
                }
            auto m = meta.find(key);
            if (m != meta.end() && openExisting && openExisting(m->second.exePath)) {
                // Its tray icon was clicked (an app running in the background). Most open their
                // window then; one that does not within a moment is started as its pin starts it
                // (a single-instance app then shows its window) instead of the click doing nothing (#15).
                // Never a chat app: WeChat, QQ and the like run several accounts side by side, and
                // starting one again opens another one's login window.
                if (auto plan = PlanFor(key); plan && MessengerOfProgram(m->second.exePath) == Messenger::None) {
                    {
                        std::lock_guard g(fallbackMutex);
                        ++fallbackGen;
                        fallbackPending.emplace(*plan, m->second.exePath);
                    }
                    fallbackCv.notify_all();
                }
                return;
            }
            // A chat app running in the tray whose icon could not be clicked (not listed yet, the
            // tray not hosted): started again, it would open another account's login window.
            if (m != meta.end() && MessengerOfProgram(m->second.exePath) != Messenger::None && Runs(m->second.exePath)) {
                Log(L"tasks: %s runs, its tray icon not reached: not started again", PathFindFileNameW(m->second.exePath.c_str()));
                return;
            }
        }
        if (auto plan = PlanFor(key))
            Exec(plan->file, plan->args, plan->dir);
    }

    // How `key` is started: its pin's shortcut or program (a packaged program by its app id),
    // else the running app's program or app id.
    std::optional<LaunchPlan> PlanFor(const std::wstring& key)
    {
        auto pit = pinByKey.find(key);
        size_t pinIndex = pit != pinByKey.end() ? pit->second : pins.size();
        if (pinIndex == pins.size()) // duplicate pins carry a suffixed key
            for (size_t i = 0; i < pins.size(); ++i)
                if (pins[i].key == key)
                    pinIndex = i;
        if (pinIndex < pins.size()) {
            const PinnedApp& app = pins[pinIndex].app;
            if (!app.path.empty() && IsPackagedPath(app.path) && !IsLink(app.path)) {
                const std::wstring aumid = !app.aumid.empty() ? app.aumid : PackagedAumid(app.path);
                if (!aumid.empty())
                    return LaunchPlan{AppsFolderPath(aumid), {}, {}};
                Log(L"tasks: pin %s is a packaged program without an app id", app.name.c_str());
            }
            if (!app.path.empty()) {
                const bool link = IsLink(app.path);
                const size_t slash = link ? std::wstring::npos : app.path.find_last_of(L"\\/");
                return LaunchPlan{app.path, link ? std::wstring() : app.args, slash == std::wstring::npos ? std::wstring() : app.path.substr(0, slash)};
            }
            if (!app.aumid.empty())
                return LaunchPlan{AppsFolderPath(app.aumid), {}, {}};
            Log(L"tasks: pin %s has nothing to launch", app.name.c_str());
            return std::nullopt;
        }
        auto mit = meta.find(key);
        if (mit == meta.end()) {
            Log(L"tasks: launch of unknown item %s", key.c_str());
            return std::nullopt;
        }
        const Meta& m = mit->second;
        const bool packaged = IsPackagedPath(m.exePath);
        if (!m.exePath.empty() && !packaged)
            return LaunchPlan{m.exePath, {}, {}};
        const std::wstring aumid = !m.aumid.empty() ? m.aumid : packaged ? PackagedAumid(m.exePath) : std::wstring();
        if (!aumid.empty())
            return LaunchPlan{AppsFolderPath(aumid), {}, {}};
        Log(L"tasks: no way to start %s", key.c_str());
        return std::nullopt;
    }

    // A hung window never reads the command: Windows carries it out. A slow one still reads it after
    // the timeout, and a second restore would take a maximized window back to its normal size (#84).
    static void SysCommand(HWND h, WPARAM command)
    {
        DWORD_PTR ignored = 0;
        if (IsHungAppWindow(h))
            ShowWindowAsync(h, command == SC_RESTORE ? SW_RESTORE : SW_MINIMIZE);
        else
            SendMessageTimeoutW(h, WM_SYSCOMMAND, command, 0, SMTO_ABORTIFHUNG, 500, &ignored);
    }

    static void BringToFront(HWND h)
    {
        // A closed-to-tray window must be restored by its app, not by the task switcher.
        if (!IsWindowVisible(h))
            return;
        if (IsIconic(h))
            SysCommand(h, SC_RESTORE);
        HWND target = GetLastActivePopup(h);
        if (!target || target == h || !IsWindowVisible(target))
            target = h;
        auto isFront = [&] {
            HWND fg = GetForegroundWindow();
            return fg == target || fg == h || (fg && GetAncestor(fg, GA_ROOTOWNER) == h);
        };
        if (SetForegroundWindow(target) && isFront())
            return;
        // Foreground lock refused: the bar never takes activation itself, so on
        // Windows 11 this is the normal case. SwitchToThisWindow
        // is what task switchers use; unlike a synthetic Alt press it sends no
        // input to the user's app.
        static bool logged = false;
        if (!logged) {
            Log(L"tasks: SetForegroundWindow refused, switching with SwitchToThisWindow (logged once)");
            logged = true;
        }
        if (IsWindowVisible(target))
            SwitchToThisWindow(target, TRUE);
    }

    void Activate(const std::wstring& key, HMONITOR monitor)
    {
        const TaskItem* t = Find(key);
        auto windows = t ? t->windows : std::vector<HWND>{};
        std::erase_if(windows, [](HWND h) { return !IsWindowVisible(h); });
        if (monitor)
            std::erase_if(windows, [&](HWND h) { return MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != monitor; });
        if (windows.empty()) {
            Launch(key);
            return;
        }
        HWND fg = ForegroundRoot();
        bool inGroup = fg && std::ranges::find(windows, fg) != windows.end();
        if (windows.size() == 1) {
            HWND w = windows[0];
            if (inGroup && !IsIconic(w))
                SysCommand(w, SC_MINIMIZE);
            else
                BringToFront(w);
            return;
        }
        // Windows are most-recently-active first: the last one is the next in turn.
        BringToFront(inGroup ? windows.back() : windows.front());
    }

    std::vector<AppProcessIdentity> TerminationTargets(const std::wstring& key) const
    {
        const auto found = meta.find(key);
        if (found == meta.end())
            return {};
        if (!found->second.processTargets.empty())
            return found->second.processTargets;
        // Pinned apps can keep running in the tray after their last window closes.
        return {{found->second.exePath, found->second.aumid}};
    }

    std::wstring PinTarget(const std::wstring& key) const
    {
        auto m = meta.find(key);
        return m == meta.end() ? std::wstring{} : m->second.pinPath;
    }

    PinnedApp PinFor(const std::wstring& key) const
    {
        const TaskItem* t = Find(key);
        auto m = meta.find(key);
        return (!t || m == meta.end()) ? PinnedApp{} : PinnedApp{t->name, m->second.exePath, {}, m->second.aumid};
    }

    // Exe that starts the item, unless it is a packaged app (those are only
    // started through their AUMID).
    std::wstring DesktopExe(const std::wstring& key) const
    {
        auto m = meta.find(key);
        return (m == meta.end() || m->second.exePath.empty() || IsPackagedPath(m->second.exePath)) ? std::wstring() : m->second.exePath;
    }

    std::wstring Aumid(const std::wstring& key) const
    {
        auto m = meta.find(key);
        return m == meta.end() ? std::wstring() : m->second.aumid;
    }

    void Show(const std::wstring& key)
    {
        const TaskItem* t = Find(key);
        if (t && !t->windows.empty() && !t->active)
            BringToFront(t->windows.front());
    }

    void MoveRunning(const std::wstring& key, int to)
    {
        auto it = std::ranges::find(order, key);
        if (it == order.end())
            return;
        order.erase(it);
        order.insert(order.begin() + std::clamp<std::ptrdiff_t>(to, 0, std::ptrdiff_t(order.size())), key);
        if (Rebuild()) {
            notify = true;
            ArmTimer();
        }
    }

    bool OpenFiles(const std::wstring& key, const std::vector<std::wstring>& files)
    {
        if (files.empty())
            return false;
        if (std::wstring exe = DesktopExe(key); !exe.empty()) {
            std::wstring args;
            for (const auto& f : files)
                args += (args.empty() ? L"" : L" ") + QuoteArg(f);
            Log(L"tasks: opening %zu file(s) with %s", files.size(), exe.c_str());
            return Exec(exe, args, {});
        }
        std::wstring aumid = Aumid(key);
        if (aumid.empty())
            return false;
        winrt::com_ptr<IApplicationActivationManager> aam;
        if (FAILED(CoCreateInstance(CLSID_ApplicationActivationManager, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(aam.put()))))
            return false;
        std::vector<PIDLIST_ABSOLUTE> ids;
        for (const auto& f : files) {
            PIDLIST_ABSOLUTE id = nullptr;
            if (SUCCEEDED(SHParseDisplayName(f.c_str(), nullptr, &id, 0, nullptr)))
                ids.push_back(id);
        }
        winrt::com_ptr<IShellItemArray> array;
        bool ok = !ids.empty() && SUCCEEDED(SHCreateShellItemArrayFromIDLists(UINT(ids.size()), const_cast<PCIDLIST_ABSOLUTE_ARRAY>(ids.data()), array.put()));
        DWORD pid = 0;
        ok = ok && SUCCEEDED(aam->ActivateForFile(aumid.c_str(), array.get(), L"open", &pid));
        if (ok)
            Log(L"tasks: opening %zu file(s) with %s", ids.size(), aumid.c_str());
        for (auto* id : ids)
            CoTaskMemFree(id);
        if (!ok)
            Log(L"tasks: opening files with %s failed", aumid.c_str());
        return ok;
    }
};

TaskModel::Impl* TaskModel::Impl::g_self = nullptr;

bool AllowsMultipleWindows(const PinnedApp& app)
{
    if (app.multipleWindows)
        return *app.multipleWindows;
    std::wstring exe = Lower(FileStem(app.path));
    return exe == L"explorer" || Lower(app.aumid) == L"microsoft.windows.explorer" || exe == L"chrome" || exe == L"msedge" || exe == L"firefox" ||
           exe == L"brave" || exe == L"opera" || exe == L"vivaldi" || exe == L"chromium";
}

// ---- TaskModel: every entry point is exception-safe -----------------------------

TaskModel::TaskModel(HWND uiWindow, std::function<void()> onChanged, const std::function<bool(const std::wstring&)>& openExisting)
    : impl_(std::make_unique<Impl>(uiWindow, std::move(onChanged), openExisting))
{
}

TaskModel::~TaskModel() = default;

std::vector<TaskItem> TaskModel::Snapshot(bool all) const
{
    try {
        return impl_->Snapshot(all);
    } catch (...) {
        Log(L"tasks: Snapshot failed");
        return {};
    }
}

void TaskModel::SetPins(const std::vector<PinnedApp>& pins)
{
    try {
        impl_->SetPins(pins);
    } catch (...) {
        Log(L"tasks: SetPins failed");
    }
}

void TaskModel::SetShowRunning(bool show) { impl_->SetShowRunning(show); }

void TaskModel::SetOnFlash(std::function<void(HWND)> onFlash) { impl_->onFlash = std::move(onFlash); }

IconImage TaskModel::Icon(const std::wstring& key, int sizePx)
{
    try {
        return impl_->Icon(key, sizePx);
    } catch (...) {
        Log(L"tasks: Icon failed");
        return {};
    }
}

void TaskModel::Activate(const std::wstring& key, HMONITOR monitor)
{
    try {
        impl_->Activate(key, monitor);
    } catch (...) {
        Log(L"tasks: Activate failed");
    }
}

void TaskModel::ActivateWindow(HWND window)
{
    if (IsWindow(window))
        Impl::BringToFront(window);
}

bool TaskModel::CloseWindow(HWND window)
{
    if (!IsWindow(window)) {
        SetLastError(ERROR_INVALID_WINDOW_HANDLE);
        return false;
    }
    if (!PostMessageW(window, WM_SYSCOMMAND, SC_CLOSE, 0)) {
        const DWORD error = GetLastError();
        Log(L"tasks: window close request failed for %p (%lu)", window, error);
        return false;
    }
    return true;
}

void TaskModel::Launch(const std::wstring& key)
{
    try {
        impl_->Launch(key);
    } catch (...) {
        Log(L"tasks: Launch failed");
    }
}

bool TaskModel::CanTerminate(const std::wstring& key) const
{
    const auto targets = impl_->TerminationTargets(key);
    return std::ranges::any_of(targets, CanTerminateApp);
}

void TaskModel::Terminate(const std::wstring& key)
{
    try {
        const auto targets = impl_->TerminationTargets(key);
        TerminateAppProcesses(targets);
        impl_->MarkDirty();
    } catch (...) {
        Log(L"tasks: Terminate failed");
    }
}

PinnedApp TaskModel::PinFor(const std::wstring& key) const
{
    try {
        return impl_->PinFor(key);
    } catch (...) {
        return {};
    }
}

void TaskModel::Show(const std::wstring& key)
{
    try {
        impl_->Show(key);
    } catch (...) {
        Log(L"tasks: Show failed");
    }
}

void TaskModel::MoveRunning(const std::wstring& key, int toIndex)
{
    try {
        impl_->MoveRunning(key, toIndex);
    } catch (...) {
        Log(L"tasks: MoveRunning failed");
    }
}

bool TaskModel::CanOpenFiles(const std::wstring& key) const
{
    try {
        return !impl_->DesktopExe(key).empty() || !impl_->Aumid(key).empty();
    } catch (...) {
        return false;
    }
}

bool TaskModel::OpenFiles(const std::wstring& key, const std::vector<std::wstring>& files)
{
    try {
        return impl_->OpenFiles(key, files);
    } catch (...) {
        Log(L"tasks: OpenFiles failed");
        return false;
    }
}

std::wstring TaskModel::PinTarget(const std::wstring& key) const
{
    try {
        return impl_->PinTarget(key);
    } catch (...) {
        return {};
    }
}

std::wstring TaskModel::FileLocation(const std::wstring& key) const
{
    try {
        return impl_->DesktopExe(key);
    } catch (...) {
        return {};
    }
}

bool TaskModel::HandleMessage(UINT msg, WPARAM wp, LPARAM lp) { return impl_->HandleMessage(msg, wp, lp); }

std::vector<PinnedApp> TaskModel::ImportWindowsPins()
{
    std::vector<PinnedApp> pins;
    try {
        ComScope com;
        PWSTR base = nullptr;
        std::wstring dir;
        if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_RoamingAppData, 0, nullptr, &base)))
            dir = std::wstring(base) + L"\\Microsoft\\Internet Explorer\\Quick Launch\\User Pinned\\TaskBar";
        CoTaskMemFree(base);
        if (dir.empty())
            return pins;
        WIN32_FIND_DATAW fd{};
        HANDLE find = FindFirstFileW((dir + L"\\*.lnk").c_str(), &fd);
        if (find == INVALID_HANDLE_VALUE)
            return pins;
        do {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
                continue;
            PinnedApp p{fd.cFileName, dir + L"\\" + fd.cFileName};
            p.name.resize(p.name.size() - 4); // ".lnk"
            std::wstring target;
            ReadLink(p.path, target, p.aumid);
            pins.push_back(std::move(p));
        } while (FindNextFileW(find, &fd));
        FindClose(find);
    } catch (...) {
        Log(L"tasks: ImportWindowsPins failed");
    }
    return pins;
}

} // namespace atrium
