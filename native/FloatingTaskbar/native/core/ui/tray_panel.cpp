#include "ui/tray_panel.h"

#include "clock.h"
#include "localization.h"
#include "log.h"
#include "motion.h"

#include <commctrl.h>
#include <shellscalingapi.h>
#include <shlwapi.h>
#include <windowsx.h>

#include <algorithm>
#include <cmath>
#include <string_view>

namespace atrium {

namespace {

// Small, like the system's own tray. Layout by what is seen: the icons'
// edges, the footer's text and button keep one margin from the panel's
// edges (Margin(), from the corner radius, so nothing crowds a corner), and
// the divider sits halfway between the icons and the footer.
constexpr float kCell = 40, kIcon = 20, kAir = (kCell - kIcon) / 2; // kAir: from a cell's edge to its icon
constexpr float kSplit = 14, kFooterH = 24;                            // icons to divider to footer; footer row
constexpr double kStageMs = 240, kStaggerMs = 14, kRippleMs = 520;
constexpr UINT_PTR kRefreshTimer = 0x5452415A;
constexpr UINT_PTR kTipTimer = 0x5A15;   // the hover delay before an icon's tooltip
constexpr UINT_PTR kHoverTimer = 0x5A17; // while an app's hover popup holds the pointer
constexpr UINT kHoverMs = 50;
// After a left click the panel stays one double-click time for the second click, then
// steps aside; Surface's own timers are 0x5A01 and 0x5A03.
constexpr UINT_PTR kClickTimer = 0x5A11;
// After the focus left right after a click, this looks once more at where it settled:
// Explorer's hidden overflow window holds it for a moment before (or instead of) the app.
constexpr UINT_PTR kSettleTimer = 0x5A13;
constexpr UINT kSettleMs = 150;

D2D1_COLOR_F Alpha(D2D1_COLOR_F c, float a) { return D2D1::ColorF(c.r, c.g, c.b, c.a * a); }
bool In(const D2D1_RECT_F& r, float x, float y) { return x >= r.left && x < r.right && y >= r.top && y < r.bottom; }

std::optional<size_t> IconIndex(std::wstring_view id)
{
    if (!id.starts_with(L"icon:") || id.size() == 5)
        return std::nullopt;
    size_t value = 0;
    for (wchar_t ch : id.substr(5)) {
        if (ch < L'0' || ch > L'9' || value > (SIZE_MAX - static_cast<size_t>(ch - L'0')) / 10)
            return std::nullopt;
        value = value * 10 + static_cast<size_t>(ch - L'0');
    }
    return value;
}

std::optional<size_t> FindTargetIndex(const std::vector<TrayIcon>& icons, const tray::TargetKey& key)
{
    if (!key.shell.Valid())
        return std::nullopt;
    std::optional<size_t> found;
    for (size_t i = 0; i < icons.size(); ++i) {
        const auto cand = icons[i].Target();
        if (cand.shell != key.shell ||
            (!key.identity.empty() && !cand.identity.empty() ? key.identity != cand.identity : key.runtime.empty() || key.runtime != cand.runtime))
            continue;
        if (found)
            return std::nullopt;
        found = i;
    }
    return found;
}

// An app's menu opened through the hidden system tray: Explorer tells the app
// where its (hidden) button is, and some apps open the menu there, at the
// screen's left edge. A newly shown menu-shaped popup from the exact executable
// is moved to the click. Unknown programs and helper processes are excluded. An
// out-of-context event hook: nothing runs in the app or in Explorer.
struct MenuFollow {
    HWINEVENTHOOK hook = nullptr;
    POINT at{};
    std::wstring exe;
    UINT_PTR timer = 0;
    DWORD pid = 0;
    DWORD clickInput = 0;
    ULONGLONG until = 0;
    HWND menu = nullptr; // the menu moved: put back if the app moves it again meanwhile
    POINT placed{};
    int moves = 0;
} g_follow;

void StopFollow()
{
    if (g_follow.hook)
        UnhookWinEvent(g_follow.hook);
    g_follow.hook = nullptr;
    g_follow.menu = nullptr;
    if (g_follow.timer)
        KillTimer(nullptr, g_follow.timer);
    g_follow.timer = 0;
    g_follow.pid = 0;
}

void CALLBACK FollowExpired(HWND, UINT, UINT_PTR timer, DWORD)
{
    if (timer == g_follow.timer && GetTickCount64() >= g_follow.until)
        StopFollow();
}

// Next to the click: above and to the right of it, else below or to the left; in the work area.
POINT MenuPlace(POINT at, int w, int h)
{
    MONITORINFO mi{sizeof(mi)};
    GetMonitorInfoW(MonitorFromPoint(at, MONITOR_DEFAULTTONEAREST), &mi);
    const RECT& wa = mi.rcWork;
    int left = at.x + w <= wa.right ? at.x : at.x - w, top = at.y - h >= wa.top ? at.y - h : at.y;
    left = std::clamp(left, int(wa.left), std::max(int(wa.left), int(wa.right) - w));
    top = std::clamp(top, int(wa.top), std::max(int(wa.top), int(wa.bottom) - h));
    return POINT{left, top};
}

bool FollowCancelKeyDown()
{
    constexpr int kKeys[] = {VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_RETURN, VK_ESCAPE, VK_SPACE};
    return std::ranges::any_of(kKeys, [](int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; });
}

void CALLBACK MenuShown(HWINEVENTHOOK, DWORD event, HWND hwnd, LONG idObject, LONG idChild, DWORD, DWORD)
{
    if (event != EVENT_OBJECT_SHOW && event != EVENT_OBJECT_LOCATIONCHANGE)
        return;
    if (GetTickCount64() > g_follow.until) {
        StopFollow();
        return;
    }
    LASTINPUTINFO input{sizeof(input)};
    POINT cursor{};
    const SIZE dblClick{GetSystemMetrics(SM_CXDOUBLECLK), GetSystemMetrics(SM_CYDOUBLECLK)};
    // The panel's click, or the real click the model sent for it; ignore small pointer jitter.
    if (!GetLastInputInfo(&input) || !GetCursorPos(&cursor) ||
        TrayFollowCancelled(input.dwTime, g_follow.clickInput, TrayModel::LastSyntheticInput(), TrayModel::SyntheticPointerInFlight(), g_follow.at, cursor,
                            dblClick, FollowCancelKeyDown())) {
        StopFollow(); // a later user action must not have its menu moved by this click
        return;
    }
    if (!hwnd || idObject != OBJID_WINDOW || idChild != CHILDID_SELF)
        return;
    if (!g_follow.menu && event != EVENT_OBJECT_SHOW)
        return;
    // Revalidate ownership even on subsequent location events: HWNDs can be reused.
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (!pid || (g_follow.menu && (hwnd != g_follow.menu || pid != g_follow.pid)))
        return;
    std::wstring path;
    bool elevated = true; // an unqueryable token must not authorize a move
    if (HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)) {
        wchar_t buf[32768];
        DWORD size = DWORD(std::size(buf));
        if (QueryFullProcessImageNameW(process, 0, buf, &size))
            path.assign(buf, size);
        HANDLE token = nullptr;
        if (OpenProcessToken(process, TOKEN_QUERY, &token)) {
            TOKEN_ELEVATION e{};
            DWORD got = 0;
            if (GetTokenInformation(token, TokenElevation, &e, sizeof(e), &got))
                elevated = e.TokenIsElevated != 0;
            CloseHandle(token);
        }
        CloseHandle(process);
    }
    if (g_follow.exe.empty() || path.empty() || elevated || CompareStringOrdinal(path.c_str(), -1, g_follow.exe.c_str(), -1, TRUE) != CSTR_EQUAL)
        return;
    wchar_t cls[64]{};
    if (!GetClassNameW(hwnd, cls, 64))
        return;
    const bool nativeMenu = wcscmp(cls, L"#32768") == 0;
    const LONG_PTR style = GetWindowLongPtrW(hwnd, GWL_STYLE), ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
    const bool customMenu = (style & WS_POPUP) && !(style & WS_CAPTION) && (ex & (WS_EX_TOOLWINDOW | WS_EX_TOPMOST)) && !(ex & WS_EX_APPWINDOW) &&
                            _wcsicmp(cls, L"tooltips_class32") != 0;
    if (!nativeMenu && !customMenu)
        return;
    const wchar_t* name = PathFindFileNameW(path.c_str());
    RECT r{};
    if (g_follow.menu) {
        // Moved already: some apps (WPF, a change of monitor's scale) place it once more themselves.
        if (hwnd != g_follow.menu || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, &r))
            return;
        // A pixel or two is the app rounding to its scale.
        if ((std::abs(r.left - g_follow.placed.x) > 2 || std::abs(r.top - g_follow.placed.y) > 2) && g_follow.moves < 3) {
            const POINT p = MenuPlace(g_follow.at, r.right - r.left, r.bottom - r.top);
            g_follow.placed = p;
            ++g_follow.moves;
            SetWindowPos(hwnd, nullptr, p.x, p.y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            Log(L"tray: the menu went to %ld,%ld again: back to %ld,%ld", r.left, r.top, p.x, p.y);
        }
        return;
    }
    // Only a newly shown, menu-shaped popup from the exact executable is eligible.
    if (GetAncestor(hwnd, GA_ROOT) != hwnd || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, &r))
        return;
    const int w = r.right - r.left, h = r.bottom - r.top;
    MONITORINFO mi{sizeof(mi)};
    GetMonitorInfoW(MonitorFromPoint(g_follow.at, MONITOR_DEFAULTTONEAREST), &mi);
    if (w < 40 || h < 20 || w > (mi.rcWork.right - mi.rcWork.left) * 3 / 5 || h > (mi.rcWork.bottom - mi.rcWork.top) * 19 / 20)
        return; // a mark, a balloon, a window: not a menu
    const POINT at = g_follow.at;
    if (at.x >= r.left - 48 && at.x <= r.right + 48 && at.y >= r.top - 48 && at.y <= r.bottom + 48) {
        StopFollow();
        return; // the app put it by the pointer itself
    }
    const POINT p = MenuPlace(at, w, h);
    if (!SetWindowPos(hwnd, nullptr, p.x, p.y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)) {
        Log(L"tray: %s's menu (%s) at %ld,%ld could not be moved (%lu)", name, cls, r.left, r.top, GetLastError());
        StopFollow();
        return;
    }
    Log(L"tray: %s's menu (%s) moved from %ld,%ld to %ld,%ld", name, cls, r.left, r.top, p.x, p.y);
    g_follow.menu = hwnd;
    g_follow.pid = pid;
    g_follow.placed = p;
    g_follow.moves = 0;
    g_follow.until = GetTickCount64() + 800;
    if (g_follow.timer)
        KillTimer(nullptr, g_follow.timer);
    g_follow.timer = SetTimer(nullptr, 0, 800, FollowExpired);
    if (!g_follow.timer)
        StopFollow();
}

void FollowMenu(POINT at, const std::wstring& exe)
{
    StopFollow();
    if (exe.empty() || _wcsicmp(PathFindFileNameW(exe.c_str()), L"explorer.exe") == 0)
        return;
    LASTINPUTINFO input{sizeof(input)};
    if (!GetLastInputInfo(&input))
        return;
    g_follow.clickInput = input.dwTime;
    g_follow.at = at;
    g_follow.exe = exe;
    g_follow.until = GetTickCount64() + 3000; // slow apps take a while to show it
    g_follow.hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_LOCATIONCHANGE, nullptr, MenuShown, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    if (g_follow.hook)
        g_follow.timer = SetTimer(nullptr, 0, 3000, FollowExpired);
    if (!g_follow.timer)
        StopFollow();
}
} // namespace

TrayPanel::TrayPanel(HINSTANCE instance, TrayModel& model) : surface_(instance, L"Atrium Tray", true), model_(model)
{
    surface_.paint = [this](Canvas& c) { Paint(c); };
    surface_.message = [this](UINT m, WPARAM w, LPARAM l, LRESULT& r) { return Message(m, w, l, r); };
    surface_.onDismiss = [this] { Close(); };
    wchar_t privacy[8] = {};
    private_ = GetEnvironmentVariableW(L"ATRIUM_PRIVACY", privacy, 8) && privacy[0] == L'1';
}

float TrayPanel::Margin() const { return std::clamp(radius_ * 0.65f, 14.f, 22.f); }

int TrayPanel::Cols() const
{
    if (columns_)
        return columns_;
    // At most six across, the rows as even as they can be (13 icons: 5, 5, 3), and wide enough for the footer.
    const int n = int(icons_.size()), rows = std::max(1, (n + 5) / 6);
    return std::clamp((n + rows - 1) / rows, 4, 6);
}

D2D1_SIZE_F TrayPanel::Size() const
{
    int cols = Cols(), rows = std::max(1, int((icons_.size() + size_t(cols) - 1) / size_t(cols)));
    const float m = Margin();
    return D2D1::SizeF(2 * (m - kAir) + float(cols) * kCell, (m - kAir) + float(rows) * kCell - kAir + 2 * kSplit + kFooterH + m);
}

D2D1_RECT_F TrayPanel::CellRect(size_t i) const
{
    // Rows from the top, left to right; a last row that is not full keeps to the left, as
    // in a grid (centred, its icons looked like gaps between the ones above).
    const size_t cols = size_t(Cols()), row = i / cols;
    const float pad = Margin() - kAir;
    float x = pad + float(i % cols) * kCell, y = pad + float(row) * kCell;
    return D2D1::RectF(x, y, x + kCell, y + kCell);
}

void TrayPanel::Place()
{
    RECT r = PopupRect(bar_, Size(), PopupAlign::Right);
    if (surface_.Visible())
        surface_.Move(r); // pops out of its entry on the bar
    else
        surface_.Show(r, true, PopupOrigin(bar_, r, anchor_));
}

void TrayPanel::Open(RECT anchor, RECT bar, HWND previous)
{
    previous_ = previous;
    anchor_ = anchor;
    bar_ = bar;
    icons_ = model_.Icons();
    columns_ = 0;
    columns_ = Cols();
    hover_.clear();
    pressed_.clear();
    hoverAnim_.clear();
    openedMs_ = ClockMs();
    model_.Refresh(); // the tooltips and the set change; the panel updates when read
    model_.SetHostPlaces(anchor);
    SetTimer(surface_.Hwnd(), kRefreshTimer, 1500, nullptr);
    Place();
    Log(L"tray: panel opened (%zu icons)", icons_.size());
}

void TrayPanel::Close()
{
    model_.Hover(nullptr);
    HideTip();
    KillTimer(surface_.Hwnd(), kRefreshTimer);
    KillTimer(surface_.Hwnd(), kClickTimer);
    KillTimer(surface_.Hwnd(), kSettleTimer);
    KillTimer(surface_.Hwnd(), kHoverTimer);
    lastClick_.reset();
    if (!surface_.Visible())
        return;
    surface_.Hide();
    lastClosed_ = GetTickCount64();
}

void TrayPanel::Refresh()
{
    if (!surface_.Visible())
        return;
    if (!pressed_.empty())
        return; // freeze hit cells until mouse-up
    const bool hadIconHover = hover_.starts_with(L"icon:"), hadTip = !tipFor_.empty();
    const auto hoverIdx = IconIndex(hover_), tipIdx = IconIndex(tipFor_);
    const auto hoverTarget = hoverIdx && *hoverIdx < icons_.size() ? std::optional(icons_[*hoverIdx].Target()) : std::nullopt;
    const auto tipTarget = tipIdx && *tipIdx < icons_.size() ? std::optional(icons_[*tipIdx].Target()) : std::nullopt;
    auto fresh = model_.Icons();
    std::vector<tray::TargetKey> oldKeys, freshKeys;
    for (const auto& icon : icons_)
        oldKeys.push_back(icon.Target());
    for (const auto& icon : fresh)
        freshKeys.push_back(icon.Target());
    const auto order = tray::StableOrder(oldKeys, freshKeys);
    icons_.clear();
    for (size_t i : order)
        icons_.push_back(std::move(fresh[i]));
    if (const auto newHoverIdx = hoverTarget ? FindTargetIndex(icons_, *hoverTarget) : std::nullopt)
        hover_ = L"icon:" + std::to_wstring(*newHoverIdx);
    else if (hadIconHover) {
        hover_.clear();
        KillTimer(surface_.Hwnd(), kHoverTimer);
        model_.Hover(nullptr);
    }
    Place();
    surface_.Invalidate();
    if (hadTip) {
        if (const auto newTipIdx = tipTarget ? FindTargetIndex(icons_, *tipTarget) : std::nullopt) {
            tipFor_ = L"icon:" + std::to_wstring(*newTipIdx);
            if (tipFor_ == hover_ && tip_ && IsWindowVisible(tip_))
                ShowTip(); // a tooltip that changes while shown changes in place, as in the system tray
        } else {
            HideTip();
        }
    }
}

// The icon's tooltip, all of its lines, above its cell after the system's hover delay,
// as the system tray shows it. Not for a version 4 app that draws its own (the tray
// host asks it to, NIN_POPUPOPEN), nor in private mode.
void TrayPanel::ArmTip()
{
    HideTip();
    const std::optional<size_t> idx = IconIndex(hover_);
    if (!idx || *idx >= icons_.size() || private_ || icons_[*idx].appTooltip || icons_[*idx].tip.empty())
        return;
    tipFor_ = hover_;
    UINT delay = 500;
    if (tip_)
        delay = UINT(SendMessageW(tip_, TTM_GETDELAYTIME, TTDT_INITIAL, 0));
    SetTimer(surface_.Hwnd(), kTipTimer, delay ? delay : 500, nullptr);
}

void TrayPanel::ShowTip()
{
    const std::optional<size_t> idx = IconIndex(tipFor_);
    if (tipFor_ != hover_ || !idx || *idx >= icons_.size() || !surface_.Visible())
        return;
    const size_t i = *idx;
    const RECT win = surface_.Rect();
    const float s = surface_.Scale();
    const D2D1_RECT_F cell = CellRect(i);
    const RECT cellPx{win.left + LONG(cell.left * s), win.top + LONG(cell.top * s), win.left + LONG(cell.right * s), win.top + LONG(cell.bottom * s)};
    const std::wstring& text = icons_[i].tip;
    if (tip_ && IsWindowVisible(tip_) && text == tipText_ && EqualRect(&cellPx, &tipCell_))
        return;
    if (!tip_) {
        INITCOMMONCONTROLSEX icc{sizeof(icc), ICC_WIN95_CLASSES};
        InitCommonControlsEx(&icc);
        tip_ = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, TOOLTIPS_CLASSW, nullptr, WS_POPUP | TTS_NOPREFIX | TTS_ALWAYSTIP, CW_USEDEFAULT,
                               CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, surface_.Hwnd(), nullptr, nullptr, nullptr);
        if (!tip_)
            return;
        TTTOOLINFOW ti{sizeof(ti)};
        ti.uFlags = TTF_TRACK | TTF_ABSOLUTE;
        ti.hwnd = surface_.Hwnd();
        ti.uId = 1;
        ti.lpszText = const_cast<wchar_t*>(L"");
        SendMessageW(tip_, TTM_ADDTOOLW, 0, reinterpret_cast<LPARAM>(&ti));
    }
    TTTOOLINFOW ti{sizeof(ti)};
    ti.hwnd = surface_.Hwnd();
    ti.uId = 1;
    ti.lpszText = const_cast<wchar_t*>(text.c_str());
    SendMessageW(tip_, TTM_SETMAXTIPWIDTH, 0, LPARAM(400 * s));
    SendMessageW(tip_, TTM_UPDATETIPTEXTW, 0, reinterpret_cast<LPARAM>(&ti));
    // Activated before TTM_GETBUBBLESIZE: an inactive tooltip has not yet sized its theme and font (#89).
    if (!IsWindowVisible(tip_)) {
        SendMessageW(tip_, TTM_TRACKPOSITION, 0, MAKELPARAM(win.left, win.top));
        SendMessageW(tip_, TTM_TRACKACTIVATE, TRUE, reinterpret_cast<LPARAM>(&ti));
    }
    // Centred above the icon's cell, kept on its monitor.
    const LRESULT size = SendMessageW(tip_, TTM_GETBUBBLESIZE, 0, reinterpret_cast<LPARAM>(&ti));
    const int w = LOWORD(size), h = HIWORD(size);
    POINT at{win.left + LONG((cell.left + cell.right) / 2 * s) - w / 2, win.top + LONG(cell.top * s) - h - LONG(4 * s)};
    MONITORINFO mi{sizeof(mi)};
    if (GetMonitorInfoW(MonitorFromWindow(surface_.Hwnd(), MONITOR_DEFAULTTONEAREST), &mi)) {
        at.x = std::clamp(at.x, mi.rcWork.left, std::max(mi.rcWork.left, mi.rcWork.right - w));
        if (at.y < mi.rcWork.top)
            at.y = win.top + LONG(cell.bottom * s) + LONG(4 * s);
    }
    SendMessageW(tip_, TTM_TRACKPOSITION, 0, MAKELPARAM(at.x, at.y));
    tipText_ = text;
    tipCell_ = cellPx;
}

void TrayPanel::HideTip()
{
    KillTimer(surface_.Hwnd(), kTipTimer);
    tipFor_.clear();
    tipText_.clear();
    tipCell_ = {};
    if (!tip_)
        return;
    TTTOOLINFOW ti{sizeof(ti)};
    ti.hwnd = surface_.Hwnd();
    ti.uId = 1;
    SendMessageW(tip_, TTM_TRACKACTIVATE, FALSE, reinterpret_cast<LPARAM>(&ti));
}

void TrayPanel::LeaveHover()
{
    const std::optional<size_t> idx = IconIndex(hover_);
    const size_t i = idx.value_or(icons_.size());
    if (hover_.starts_with(L"icon:") && i >= icons_.size()) {
        KillTimer(surface_.Hwnd(), kHoverTimer);
        model_.Hover(nullptr);
        hover_.clear();
        surface_.Invalidate();
        return;
    }
    POINT cur{};
    HWND under = i < icons_.size() && surface_.Visible() && GetCursorPos(&cur) ? WindowFromPoint(cur) : nullptr;
    if (under == surface_.Hwnd() && HitAt(surface_.ScreenToContent(cur)) == hover_) {
        KillTimer(surface_.Hwnd(), kHoverTimer);
        if (!tracking_) {
            TRACKMOUSEEVENT tme{sizeof(tme), TME_LEAVE, surface_.Hwnd(), 0};
            tracking_ = TrackMouseEvent(&tme) != FALSE;
        }
        return;
    }
    DWORD pid = 0;
    if (under)
        GetWindowThreadProcessId(under, &pid);
    // An app's own hover popup (NIN_POPUPOPEN) appears under the pointer and takes it from the panel (#89).
    if (i < icons_.size() && pid && pid == icons_[i].pid && pid != GetCurrentProcessId()) {
        SetTimer(surface_.Hwnd(), kHoverTimer, kHoverMs, nullptr);
        return;
    }
    KillTimer(surface_.Hwnd(), kHoverTimer);
    model_.Hover(nullptr);
    hover_.clear();
    surface_.Invalidate();
}

void TrayPanel::SetAttention(const std::vector<TaskItem>& tasks)
{
    std::vector<std::wstring> next;
    for (const auto& task : tasks)
        if (task.attention)
            next.push_back(task.key);
    if (next != attention_) {
        attention_ = std::move(next);
        surface_.Invalidate();
    }
}

float TrayPanel::Hover(Canvas& c, const std::wstring& id, bool on)
{
    auto& f = hoverAnim_[id];
    float v = f.Step(on, c.now);
    if (f.moving)
        c.Continue();
    return v;
}

void TrayPanel::Paint(Canvas& c)
{
    const Theme& t = c.theme;
    const D2D1_SIZE_F size = Size();
    const float w = size.width, h = size.height;
    hits_.clear();
    if (c.panel)
        c.Fill(D2D1::RectF(0, 0, w, h), radius_, t.panel);

    const float m = Margin(), gridTop = m - kAir, footTop = h - m - kFooterH, divider = footTop - kSplit;
    const float plateR = std::clamp(radius_ - m - 2, 6.f, 12.f); // concentric with the panel's corners
    if (icons_.empty()) {
        std::wstring msg = !model_.Loaded() ? Tr(L"Reading…") : model_.Available() ? Tr(L"No tray icons") : Tr(L"The system tray is unavailable");
        c.Text(msg, D2D1::RectF(m, gridTop, w - m, gridTop + kCell), 12.5f, t.subtext, DWRITE_FONT_WEIGHT_NORMAL, 1);
    }
    const int iconPx = int(std::lround(kIcon * c.scale));
    for (size_t i = 0; i < icons_.size(); ++i) {
        const TrayIcon& icon = icons_[i];
        D2D1_RECT_F cell = CellRect(i);
        std::wstring id = L"icon:" + std::to_wstring(i);
        float e = motion::EaseOut(float((c.now - openedMs_ - kStaggerMs * double(std::min<size_t>(i, 18))) / kStageMs));
        if (e < 1)
            c.Continue();
        c.BeginGroup(e, 1, (cell.left + cell.right) / 2, (cell.top + cell.bottom) / 2, 0, 6 * (1 - e));
        float hv = Hover(c, id, hover_ == id);
        D2D1_RECT_F plate = D2D1::RectF(cell.left + 2, cell.top + 2, cell.right - 2, cell.bottom - 2);
        if (hv > 0.001f)
            c.Fill(plate, plateR, Alpha(t.hover, hv));
        if (pressed_ == id)
            c.Fill(plate, plateR, t.press);
        if (rippleId_ == id && c.now - rippleMs_ < kRippleMs) {
            c.Ripple(plate, plateR, rippleX_, rippleY_, float((c.now - rippleMs_) / kRippleMs),
                     t.light ? D2D1::ColorF(1, 1, 1, 0.6f) : D2D1::ColorF(1, 1, 1, 0.22f));
            c.Continue();
        }
        float cx = (cell.left + cell.right) / 2, cy = (cell.top + cell.bottom) / 2;
        D2D1_RECT_F ir = D2D1::RectF(cx - kIcon / 2, cy - kIcon / 2, cx + kIcon / 2, cy + kIcon / 2);
        c.BeginGroup(1, pressed_ == id ? 0.9f : 1 + 0.1f * hv, cx, cy);
        if (icon.image) {
            // A white or black glyph follows the ink as the panel's text does (0.50 / 0.42, faded):
            // its inked copy over bright glass, or over dark glass, cross-fading with the ink.
            const float dark = icon.inked ? c.DarkAt(ir) : 0.f;
            const float inked = !icon.inked ? 0.f : icon.inkedOnLight ? dark : 1 - dark;
            wchar_t key[64];
            if (inked < 0.999f) {
                swprintf_s(key, L"tray|%llu|normal|%d", icon.imageVersion, iconPx);
                c.Image(key, *icon.image, ir, 1 - inked);
            }
            if (inked > 0.001f) {
                swprintf_s(key, L"tray|%llu|inked|%d", icon.imageVersion, iconPx);
                c.Image(key, *icon.inked, ir, inked);
            }
        } else {
            // Not recognised: its initial on a quiet disc.
            std::wstring title = TrayModel::Title(icon.tip);
            c.Fill(ir, kIcon / 2, Alpha(t.hover, 1.4f));
            c.Text(title.empty() ? L"?" : title.substr(0, 1), ir, 10, t.text, DWRITE_FONT_WEIGHT_SEMI_BOLD, 1);
        }
        c.EndGroup();
        c.EndGroup();
        // Only an existing task attention signal for the exact executable. No guessed unread count.
        if (icon.flashing || (!icon.exe.empty() && std::ranges::find(attention_, icon.exe) != attention_.end())) {
            const float pulse = 0.72f + 0.28f * float(std::sin((c.now - openedMs_) * 0.006));
            c.Fill(D2D1::RectF(ir.right - 3, ir.top - 3, ir.right + 4, ir.top + 4), 3.5f, D2D1::ColorF(0.96f, 0.22f, 0.25f, pulse));
            c.Continue();
        }
        hits_.push_back({cell, id});
    }

    // Below the grid: the name under the pointer, and the system's own tray.
    c.Line(m, divider, w - m, divider, t.divider);
    std::wstring label;
    if (hover_.starts_with(L"icon:")) {
        size_t i = size_t(std::stoul(hover_.substr(5)));
        if (i < icons_.size())
            label = private_ ? Tr(L"Tray icon") : TrayModel::Title(icons_[i].tip);
    }
    if (label.empty())
        label = icons_.empty() ? Tr(L"Tray") : TrFormat(L"%d icons", int(icons_.size()));
    std::wstring sys = Tr(L"System tray");
    float bw = c.TextWidth(sys, 11.5f) + 20;
    D2D1_RECT_F b = D2D1::RectF(w - m - bw, footTop, w - m, footTop + kFooterH);
    c.Text(label, D2D1::RectF(m, footTop, b.left - 10, footTop + kFooterH), 12, hover_.starts_with(L"icon:") ? t.text : t.subtext);
    float hv = Hover(c, L"system", hover_ == L"system");
    c.Fill(b, (b.bottom - b.top) / 2, Alpha(pressed_ == L"system" ? t.press : t.hover, 0.45f + 0.55f * hv));
    c.Text(sys, b, 11.5f, t.text, DWRITE_FONT_WEIGHT_NORMAL, 1);
    hits_.push_back({b, L"system"});
}

std::wstring TrayPanel::HitAt(POINT pt) const
{
    float s = surface_.Scale(), x = float(pt.x) / s, y = float(pt.y) / s;
    for (const Hit& h : hits_)
        if (In(h.rect, x, y))
            return h.id;
    return {};
}

void TrayPanel::Activate(const std::wstring& id, bool right)
{
    if (id == L"system") {
        if (!right) {
            Close();
            if (onSystemTray)
                onSystemTray();
        }
        return;
    }
    if (!id.starts_with(L"icon:"))
        return;
    size_t i = size_t(std::stoul(id.substr(5)));
    if (i >= icons_.size())
        return;
    const TrayIcon selected = pressedIcon_.value_or(icons_[i]);
    if (right && selected.hostKey.empty()) { // a hosted icon's app is told the pointer itself
        POINT at;
        GetCursorPos(&at);
        FollowMenu(at, selected.exe);
    }
    if (!right) {
        POINT at{};
        GetCursorPos(&at);
        const bool second = lastClick_ && tray::SameGestureTarget(lastClick_->Target(), selected.Target()) &&
                            GetTickCount64() - lastWhen_ <= GetDoubleClickTime() && std::abs(at.x - lastAt_.x) <= GetSystemMetrics(SM_CXDOUBLECLK) / 2 &&
                            std::abs(at.y - lastAt_.y) <= GetSystemMetrics(SM_CYDOUBLECLK) / 2;
        if (second) {
            model_.DoubleClick(selected, true); // first click already delivered
            lastClick_.reset();
            Close(); // the panel has done its part
        } else {
            model_.Click(selected, false); // immediate; no double-click timer
            lastClick_ = selected;
            lastAt_ = at;
            lastWhen_ = GetTickCount64();
            // Some apps open only on a double click: the panel waits for the second one.
            SetTimer(surface_.Hwnd(), kClickTimer, GetDoubleClickTime(), nullptr);
        }
        return;
    }
    lastClick_.reset();
    model_.Click(selected, right);
}

bool TrayPanel::Message(UINT m, WPARAM w, LPARAM l, LRESULT& result)
{
    POINT pt{GET_X_LPARAM(l), GET_Y_LPARAM(l)};
    switch (m) {
    case WM_TIMER:
        if (w == kTipTimer) {
            KillTimer(surface_.Hwnd(), kTipTimer);
            ShowTip();
            return true;
        }
        if (w == kSettleTimer) {
            KillTimer(surface_.Hwnd(), kSettleTimer);
            HWND fg = GetForegroundWindow();
            wchar_t cls[64] = {};
            if (fg)
                GetClassNameW(fg, cls, 64);
            const bool shell =
                !wcscmp(cls, L"TopLevelWindowForOverflowXamlIsland") || !wcscmp(cls, L"Shell_TrayWnd") || !wcscmp(cls, L"Shell_SecondaryTrayWnd");
            if (fg != surface_.Hwnd() && !shell)
                Close(); // a window of the app took it: the click opened it
            return true;
        }
        if (w == kClickTimer) {
            // No second click came: the panel steps aside, as after a click that reached the app.
            KillTimer(surface_.Hwnd(), kClickTimer);
            lastClick_.reset();
            Close();
            return true;
        }
        if (w == kRefreshTimer) {
            if (surface_.Visible())
                model_.Refresh();
            return true;
        }
        if (w == kHoverTimer) {
            LeaveHover();
            return true;
        }
        return false;
    case WM_ACTIVATE:
        if (LOWORD(w) == WA_INACTIVE && surface_.Visible()) {
            // Losing the focus right after a forwarded click is usually Explorer's hidden
            // overflow window taking it for a moment, not the app coming forward: look
            // again shortly, so that a second click can still complete a double click.
            if (lastClick_ && GetTickCount64() - lastWhen_ <= GetDoubleClickTime())
                SetTimer(surface_.Hwnd(), kSettleTimer, kSettleMs, nullptr);
            else
                Close();
        }
        return false;
    case WM_MOUSEMOVE: {
        KillTimer(surface_.Hwnd(), kHoverTimer);
        if (!tracking_) {
            TRACKMOUSEEVENT tme{sizeof(tme), TME_LEAVE, surface_.Hwnd(), 0};
            tracking_ = TrackMouseEvent(&tme) != FALSE;
        }
        std::wstring h = HitAt(pt);
        if (h != hover_) {
            hover_ = h;
            // A hosted icon's app is told the pointer is on it, as the system tray does.
            const size_t i = h.starts_with(L"icon:") ? size_t(std::stoul(h.substr(5))) : icons_.size();
            model_.Hover(i < icons_.size() && !icons_[i].hostKey.empty() ? &icons_[i] : nullptr);
            ArmTip();
            surface_.Invalidate();
        }
        return true;
    }
    case WM_MBUTTONUP: {
        const std::wstring h = HitAt(pt);
        const size_t i = h.starts_with(L"icon:") ? size_t(std::stoul(h.substr(5))) : icons_.size();
        if (i < icons_.size() && !icons_[i].hostKey.empty())
            model_.MiddleClick(icons_[i]);
        return true;
    }
    case WM_MOUSELEAVE:
        tracking_ = false;
        HideTip();
        if (!pressed_.empty()) {
            pressed_.clear();
            surface_.Invalidate();
        }
        LeaveHover();
        return true;
    case WM_LBUTTONDOWN:
    case WM_RBUTTONDOWN:
        HideTip(); // a click ends the tooltip, as in the system tray
        pressed_ = HitAt(pt);
        pressedIcon_.reset();
        if (pressed_.starts_with(L"icon:")) {
            const size_t i = size_t(std::stoul(pressed_.substr(5)));
            if (i < icons_.size())
                pressedIcon_ = icons_[i];
        }
        if (!pressed_.empty()) {
            float s = surface_.Scale();
            rippleId_ = pressed_;
            rippleX_ = float(pt.x) / s;
            rippleY_ = float(pt.y) / s;
            rippleMs_ = ClockMs();
            surface_.Ripple(rippleX_, rippleY_);
        }
        surface_.Invalidate();
        return true;
    case WM_LBUTTONUP:
    case WM_RBUTTONUP: {
        std::wstring h = HitAt(pt);
        bool same = !h.empty() && h == pressed_;
        pressed_.clear();
        surface_.Invalidate();
        if (same)
            Activate(h, m == WM_RBUTTONUP);
        pressedIcon_.reset();
        return true;
    }
    case WM_KEYDOWN:
        if (w == VK_ESCAPE)
            Close();
        return w == VK_ESCAPE;
    default: break;
    }
    (void)result;
    return false;
}

} // namespace atrium
