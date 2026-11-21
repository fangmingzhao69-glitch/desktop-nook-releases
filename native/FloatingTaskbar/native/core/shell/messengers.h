// The chat apps Atrium's notification sources name one by one (WeChat, QQ, Feishu, WeCom,
// DingTalk): which of them a notice or a program is, and the plain card for a message that
// came with no text to show.
//
// Each flashes for a new message: its window (FlashWindowEx, HSHELL_FLASH, also a window hidden
// in the tray) or its tray icon. Where its text is read (WeChat's chat list, Feishu's popup, QQ's
// Windows notification) a card follows within moments. A flash no card followed (WeCom and
// DingTalk publish nothing readable; WeChat in the tray before its window was ever opened) still
// gets one: "WeChat · New message", as the reference notifiers do.
#pragma once

#include <windows.h>

#include <array>
#include <cwctype>
#include <string>
#include <vector>

namespace atrium {

enum class Messenger { None = -1, WeChat, QQ, Feishu, WeCom, DingTalk, Count };

struct MessengerInfo {
    const wchar_t* key;  // the notice source and the settings' name ("wechat")
    const wchar_t* name; // English display name (Tr)
    std::array<const wchar_t*, 4> exes; // program file names, lower case
    std::array<const wchar_t*, 4> names; // how a Windows notification names it (lower case)
};

inline const MessengerInfo& InfoOf(Messenger m)
{
    static const MessengerInfo kInfo[] = {
        {L"wechat", L"WeChat", {L"weixin.exe", L"wechat.exe", nullptr, nullptr}, {L"weixin", L"wechat", L"微信", nullptr}},
        {L"qq", L"QQ", {L"qq.exe", nullptr, nullptr, nullptr}, {L"qq", nullptr, nullptr, nullptr}},
        {L"feishu", L"Feishu", {L"feishu.exe", L"lark.exe", nullptr, nullptr}, {L"feishu", L"lark", L"飞书", nullptr}},
        {L"wecom", L"WeCom", {L"wxwork.exe", L"wecom.exe", L"wxworklocal.exe", L"wxworklocalpro.exe"}, {L"wxwork", L"wecom", L"企业微信", nullptr}},
        {L"dingtalk", L"DingTalk", {L"dingtalk.exe", nullptr, nullptr, nullptr}, {L"dingtalk", L"钉钉", nullptr, nullptr}},
    };
    return kInfo[int(m)];
}

inline std::wstring MessengerLower(std::wstring s)
{
    if (!s.empty())
        CharLowerBuffW(s.data(), DWORD(s.size()));
    return s;
}

// A program (a full path or a file name).
inline Messenger MessengerOfProgram(const std::wstring& program)
{
    std::wstring file = MessengerLower(program.substr(program.find_last_of(L"\\/") + 1));
    for (int i = 0; i < int(Messenger::Count); ++i)
        for (const wchar_t* exe : InfoOf(Messenger(i)).exes)
            if (exe && file == exe)
                return Messenger(i);
    return Messenger::None;
}

// A notice: by its source ("wechat", "feishu", a plain card's), else a Windows notification by its
// sender: the AUMID ("QQ", a program path) or the shown name ("钉钉").
inline Messenger MessengerOfNotice(const std::wstring& source, const std::wstring& app, const std::wstring& appId)
{
    for (int i = 0; i < int(Messenger::Count); ++i)
        if (source == InfoOf(Messenger(i)).key)
            return Messenger(i);
    if (source != L"windows")
        return Messenger::None;
    if (Messenger m = MessengerOfProgram(appId); m != Messenger::None)
        return m;
    std::wstring id = MessengerLower(appId), shown = MessengerLower(app);
    if (size_t bang = id.find(L'!'); bang != std::wstring::npos) // packaged: "publisher.app_hash!App"
        id.resize(bang);
    for (int i = 0; i < int(Messenger::Count); ++i)
        for (const wchar_t* n : InfoOf(Messenger(i)).names)
            if (n && (id == n || shown == n || (wcslen(n) > 3 && id.find(n) != std::wstring::npos)))
                return Messenger(i);
    return Messenger::None;
}

// Flashes and cards per app. A flash after a quiet spell begins a burst; once the wait is over,
// a burst no card came for (from a little before it) is due its plain card. Within a burst, more
// flashes (each message flashes again) add nothing: the card says there is something new.
class MessengerAlerts {
public:
    static constexpr ULONGLONG kQuietMs = 8000, kWaitMs = 3000, kBeforeMs = 5000;

    // True when this flash begins a burst (check Due after kWaitMs).
    bool Flash(Messenger m, ULONGLONG now)
    {
        if (m == Messenger::None)
            return false;
        State& s = state_[size_t(m)];
        const bool begins = !s.pending && (s.last == 0 || now - s.last >= kQuietMs);
        s.last = now;
        if (begins) {
            s.pending = true;
            s.burst = now;
        }
        return begins;
    }
    void Noticed(Messenger m, ULONGLONG now)
    {
        if (m != Messenger::None)
            state_[size_t(m)].noticed = now;
    }
    // The apps whose burst is over without a card; `waiting` tells whether one is still pending.
    std::vector<Messenger> Due(ULONGLONG now, bool* waiting = nullptr)
    {
        std::vector<Messenger> out;
        bool more = false;
        for (size_t i = 0; i < state_.size(); ++i) {
            State& s = state_[i];
            if (!s.pending)
                continue;
            if (now - s.burst < kWaitMs) {
                more = true;
                continue;
            }
            s.pending = false;
            if (!s.noticed || s.noticed + kBeforeMs < s.burst)
                out.push_back(Messenger(i));
        }
        if (waiting)
            *waiting = more;
        return out;
    }

private:
    struct State {
        ULONGLONG burst = 0, last = 0, noticed = 0;
        bool pending = false;
    };
    std::array<State, size_t(Messenger::Count)> state_{};
};

} // namespace atrium
