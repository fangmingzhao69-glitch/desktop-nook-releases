#include "shell/wechat_source.h"

#include "localization.h"

#include "log.h"
#include "shell/uia_client.h"
#include "../posted.h"
#include "win32_path.h"

#include <tlhelp32.h>
#include <uiautomation.h>

#include <winrt/base.h>

#include <algorithm>
#include <map>
#include <vector>

namespace atrium {

namespace {

struct Row {
    std::wstring chat;    // the chat's name
    std::wstring summary; // the newest message as the list shows it
    int unread = 0;
    bool muted = false;
};

bool IsTime(const std::wstring& s)
{
    // "17:06", "10/27", "2025/10/27", "昨天", "星期三"
    if (s.empty() || s.size() > 12)
        return false;
    if (s == L"昨天" || s.starts_with(L"星期"))
        return true;
    return std::ranges::all_of(s, [](wchar_t c) { return iswdigit(c) || c == L':' || c == L'/' || c == L'-'; });
}

std::wstring Trim(std::wstring s)
{
    while (!s.empty() && iswspace(s.back()))
        s.pop_back();
    size_t a = s.find_first_not_of(L" \t");
    return a == std::wstring::npos ? std::wstring() : s.substr(a);
}

// A row's name, line by line: the chat; then "[N条] summary" or "[N条]" and
// the summary on its own line, or only the summary when read; the time; and
// "消息免打扰" for a muted chat.
Row Parse(const std::wstring& name)
{
    std::vector<std::wstring> lines;
    size_t from = 0;
    for (;;) {
        size_t nl = name.find(L'\n', from);
        lines.push_back(Trim(name.substr(from, nl == std::wstring::npos ? std::wstring::npos : nl - from)));
        if (nl == std::wstring::npos)
            break;
        from = nl + 1;
    }
    Row r;
    if (lines.empty())
        return r;
    r.chat = lines[0];
    for (size_t i = 1; i < lines.size(); ++i) {
        std::wstring l = lines[i];
        if (l == L"消息免打扰") {
            r.muted = true;
            continue;
        }
        if (l.size() > 3 && l[0] == L'[' && r.unread == 0 && r.summary.empty()) {
            size_t close = l.find(L"条]");
            if (close != std::wstring::npos && close > 1) {
                r.unread = _wtoi(l.substr(1, close - 1).c_str());
                l = Trim(l.substr(close + 2));
            }
        }
        if (!l.empty() && r.summary.empty() && !IsTime(l))
            r.summary = l;
    }
    return r;
}

// WeChat's top-level Qt windows (the main window is one of them, shown,
// minimised or hidden in the tray).
std::vector<HWND> WeChatWindows(DWORD pid)
{
    struct Ctx {
        DWORD pid;
        std::vector<HWND> out;
    } ctx{pid, {}};
    EnumWindows(
        [](HWND h, LPARAM l) -> BOOL {
            auto* c = reinterpret_cast<Ctx*>(l);
            DWORD p = 0;
            GetWindowThreadProcessId(h, &p);
            wchar_t cls[96] = {};
            GetClassNameW(h, cls, 96);
            if (p == c->pid && wcsstr(cls, L"QWindowIcon"))
                c->out.push_back(h);
            return TRUE;
        },
        reinterpret_cast<LPARAM>(&ctx));
    return ctx.out;
}

void UpdateUnread(const std::vector<std::pair<std::wstring, Row>>& rows, bool baseline, const std::wstring& exe, HWND notify, UINT msg,
                  std::map<std::wstring, int>& unread)
{
    std::map<std::wstring, int> rebuilt;
    for (size_t i = 0; i < rows.size(); ++i) {
        const auto& [key, r] = rows[i];
        auto old = unread.find(key);
        // A chat not seen before counts only at the top of the list,
        // where a new message puts it (scrolling brings old ones in).
        bool rose = baseline && !r.muted && (old != unread.end() ? r.unread > old->second : r.unread > 0 && i < 3);
        // Not gated on baseline: WeChat in front clears it, and reading happens there (#79).
        bool read = old != unread.end() && old->second > 0 && r.unread == 0;
        if (rose || read) {
            auto n = std::make_unique<Notice>();
            n->source = L"wechat";
            n->title = r.chat;
            n->unread = rose;
            if (rose) {
                n->app = Tr(L"WeChat");
                n->appId = exe;
                n->body = r.summary;
                if (!exe.empty())
                    n->action = L"app:" + exe;
            }
            if (PostOwned(notify, msg, 0, std::move(n)) && rose)
                Log(L"wechat: new message (unread %d)", r.unread);
        }
        if (baseline)
            unread[key] = r.unread;
        else
            rebuilt[key] = r.unread;
    }
    if (!baseline)
        unread = std::move(rebuilt);
    if (unread.size() > 512) {
        std::erase_if(unread, [&](const auto& kv) { return std::ranges::none_of(rows, [&](const auto& row) { return row.first == kv.first; }); });
        while (unread.size() > 512)
            unread.erase(unread.begin());
    }
}

void ReadRow(IUIAutomationElement* e, std::vector<std::pair<std::wstring, Row>>& rows)
{
    BSTR id = nullptr, name = nullptr;
    bool ok = e && SUCCEEDED(e->get_CachedAutomationId(&id)) && SUCCEEDED(e->get_CachedName(&name)) && name;
    if (ok) {
        Row r = Parse(name);
        std::wstring key = id && *id ? id : r.chat;
        if (!r.chat.empty())
            rows.emplace_back(key, r);
    }
    SysFreeString(id);
    SysFreeString(name);
}

} // namespace

WeChatSource::WeChatSource(HWND notify, UINT msg) : notify_(notify), msg_(msg), wake_(CreateEventW(nullptr, FALSE, FALSE, nullptr))
{
    thread_ = std::thread([this] { Run(); });
}

WeChatSource::~WeChatSource()
{
    stop_ = true;
    SetEvent(wake_);
    thread_.join();
    CloseHandle(wake_);
}

void WeChatSource::Run()
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    winrt::com_ptr<IUIAutomationCondition> cells;
    winrt::com_ptr<IUIAutomationCacheRequest> cache;
    winrt::com_ptr<IUIAutomation> uia = CreateUia(stop_, wake_, L"wechat", [&](IUIAutomation* u) {
        cells = nullptr;
        VARIANT v{};
        v.vt = VT_BSTR;
        v.bstrVal = SysAllocString(L"mmui::ChatSessionCell");
        HRESULT hr = u->CreatePropertyCondition(UIA_ClassNamePropertyId, v, cells.put());
        VariantClear(&v);
        if (FAILED(hr))
            return hr;
        return CreateRawCache(u, {UIA_AutomationIdPropertyId, UIA_NamePropertyId}, cache);
    });
    std::map<std::wstring, int> unread; // chat row id -> unread count last seen
    bool baseline = false;
    int state = -1; // 0 no WeChat, 1 no list, 2 reading, 3 reading the held rows (logged on change)
    auto report = [&](int now, const wchar_t* text) {
        if (now != state)
            Log(L"wechat: %s", text);
        state = now;
    };
    std::vector<std::pair<DWORD, std::wstring>> procs;
    ULONGLONG listed = 0;
    // The chat rows last found in a shown window. Closed to the tray, WeChat's window leaves its
    // rows out of the tree (Qt skips hidden controls), but rows already held still read: their
    // names keep the chat and the unread count, so messages are seen there too.
    std::vector<winrt::com_ptr<IUIAutomationElement>> held;
    std::wstring heldProgram;
    {
        UiaWatch watch(uia.get(), wake_);
        UiaWindowWake windows(wake_, L"QWindowIcon");
        while (!stop_ && uia && cells) {
            try {
                std::wstring exe;
                // Process discovery is infrequent; message events only re-read the client tree.
                ULONGLONG tick = GetTickCount64();
                if (!listed || tick - listed >= 10000 || (state < 2 && tick - listed >= 2000)) {
                    procs.clear();
                    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                    PROCESSENTRY32W pe{sizeof(pe)};
                    for (BOOL ok = snap != INVALID_HANDLE_VALUE && Process32FirstW(snap, &pe); ok; ok = Process32NextW(snap, &pe))
                        if (_wcsicmp(pe.szExeFile, L"Weixin.exe") == 0 || _wcsicmp(pe.szExeFile, L"WeChat.exe") == 0)
                            procs.emplace_back(pe.th32ProcessID, ProcessImagePath(pe.th32ProcessID));
                    if (snap != INVALID_HANDLE_VALUE)
                        CloseHandle(snap);
                    listed = tick;
                    windows.SetProcesses(procs);
                }
                // WeChat in front is read by its user: nothing is read meanwhile (each read runs on its
                // UI thread, and typing made one per key, #79); its list then is the next baseline.
                DWORD front = 0;
                GetWindowThreadProcessId(GetForegroundWindow(), &front);
                if (std::ranges::any_of(procs, [&](const auto& p) { return p.first == front; })) {
                    watch.Clear();
                    baseline = false;
                    WaitForUiaWake(wake_, 1000);
                    continue;
                }
                watch.Prune();
                std::vector<std::pair<std::wstring, Row>> rows;
                bool found = false;
                bool anyHung = false;
                for (const auto& [p, program] : procs) {
                    for (HWND h : WeChatWindows(p)) {
                        if (IsHungAppWindow(h)) {
                            anyHung = true;
                            continue;
                        }
                        winrt::com_ptr<IUIAutomationElement> root;
                        winrt::com_ptr<IUIAutomationElementArray> all;
                        if (FAILED(uia->ElementFromHandle(h, root.put())) || !root ||
                            FAILED(root->FindAllBuildCache(TreeScope_Descendants, cells.get(), cache.get(), all.put())) || !all)
                            continue;
                        int n = 0;
                        all->get_Length(&n);
                        std::vector<winrt::com_ptr<IUIAutomationElement>> elements;
                        for (int i = 0; i < n; ++i) {
                            winrt::com_ptr<IUIAutomationElement> e;
                            all->GetElement(i, e.put());
                            ReadRow(e.get(), rows);
                            if (e)
                                elements.push_back(std::move(e));
                        }
                        if (n > 0) {
                            held = std::move(elements);
                            heldProgram = program;
                            exe = program;
                            if (!watch.Contains(h)) {
                                watch.Clear();
                                watch.Add(h, root.get());
                            }
                            found = true;
                            break;
                        }
                    }
                    if (found)
                        break;
                }
                bool fromHeld = false;
                if (!found && !anyHung && !held.empty() && !procs.empty()) {
                    // In the tray: the rows held from when the window was shown.
                    for (size_t i = 0; i < held.size();) {
                        winrt::com_ptr<IUIAutomationElement> fresh;
                        if (FAILED(held[i]->BuildUpdatedCache(cache.get(), fresh.put())) || !fresh) {
                            held.erase(held.begin() + ptrdiff_t(i)); // gone (WeChat restarted, the list rebuilt)
                            continue;
                        }
                        ReadRow(fresh.get(), rows);
                        ++i;
                    }
                    found = fromHeld = !rows.empty();
                    if (found) {
                        exe = heldProgram;
                        if (state != 3)
                            Log(L"wechat: window hidden, reading the %zu chats held from it", held.size());
                    }
                }
                if (procs.empty()) {
                    report(0, L"WeChat is not running");
                    held.clear();
                    unread.clear();
                    baseline = false; // after a restart its list is a new baseline
                } else if (!found && !anyHung) {
                    report(1, L"WeChat's chat list is not readable (open its window once)");
                    baseline = false;
                } else if (found) {
                    if (state < 2 || (state == 3 && !fromHeld))
                        Log(L"wechat: reading WeChat's chat list (%zu chats in view)", rows.size());
                    state = fromHeld ? 3 : 2;
                    UpdateUnread(rows, baseline, exe, notify_, msg_, unread);
                    baseline = true;
                }
            } catch (...) {
                Log(L"wechat: reading failed");
            }
            // Providers can omit UIA events. A slow reconciliation pass covers that case.
            if (!stop_)
                WaitForUiaWake(wake_, state >= 2 ? 2000 : 6000);
        }
    }
    cells = nullptr;
    cache = nullptr;
    uia = nullptr;
    winrt::uninit_apartment();
}

} // namespace atrium
