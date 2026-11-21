// Tray snapshots and actions; evidence contracts: docs/TRAY.md.
#pragma once

#include "icon_image.h"
#include "pinned_app.h"
#include "shell/tray_click.h"
#include "shell/tray_host.h"
#include "shell/tray_pointer.h"
#include "shell/tray_target.h"

#include <windows.h>

#include <condition_variable>
#include <deque>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace atrium {

struct TrayIcon {
    std::wstring tip;      // the tooltip; may span lines
    std::wstring exe;      // the program it belongs to, when recognised
    std::shared_ptr<const IconImage> image; // null when unknown
    uint64_t imageVersion = 0; // renderer cache identity; allocator addresses can be reused
    // A one-shade glyph (white or black, as many tray icons are drawn for the taskbar's theme)
    // redrawn in the other ink: for bright glass (`inkedOnLight`) or dark. Null for coloured icons.
    std::shared_ptr<const IconImage> inked;
    bool inkedOnLight = false;
    bool promoted = false; // on the system taskbar rather than in its overflow
    int index = 0;         // position among the icons of the same place
    std::vector<int> runtimeId; // UIA identity, never an ordinal click target
    tray::ShellSession shell; // PID + process creation time + taskbar HWND
    std::wstring matchMethod;
    bool fromRecord = false;
    std::wstring recordId; // stable Windows identity, also before the overflow UI exists
    std::wstring hostKey;  // an icon the tray host received (TrayHost); empty: read from Explorer
    DWORD pid = 0;         // its window's process (hosted)
    bool appTooltip = false; // a version 4 app drawing its own tooltip (NIN_POPUPOPEN): no standard one
    bool flashing = false;   // its icon flashes (hosted): a message waiting
    size_t order = SIZE_MAX; // place in Windows' own order of the tray icons (UIOrderList), if known
    int exeOrdinal = -1;     // position among icons of the same program
    tray::TargetKey Target() const { return {shell, runtimeId, recordId, exe, tip}; }
};

class TrayModel {
public:
    // Posts msg: 0=list changed, 1=observed app response, 2=verified native popup,
    // 3=needs user intervention, 4=accepted/unconfirmed (quiet), 5=explicit popup opened.
    // No HRESULT is treated as an application acknowledgement.
    TrayModel(HWND notify, UINT msg, std::function<std::vector<PinnedApp>()> appNames = {});
    ~TrayModel();
    TrayModel(const TrayModel&) = delete;
    TrayModel& operator=(const TrayModel&) = delete;

    void Refresh(); // asynchronous
    std::vector<TrayIcon> Icons() const;
    bool Loaded() const;
    bool Available() const; // the shell's tray was found in the last read
    // A left click (Invoke) or a right click (the context menu) on `icon`.
    void Click(const TrayIcon& icon, bool right);
    void DoubleClick(const TrayIcon& icon, bool continuation = false);
    void OpenSystemTray(); // explicit asynchronous fallback
    // GetLastInputInfo time of the last real pointer click sent to a tray button (0: none).
    static DWORD LastSyntheticInput();
    // True while the physical pointer right-click fallback is moving the cursor.
    static bool SyntheticPointerInFlight() { return tray::g_pointerInFlight; }
    // Opens an existing background session; false when the app is not in the tray/running.
    bool OpenExisting(const std::wstring& exe);
    // The tray host (setting `trayHost`): the apps' icons as they send them, clicked with
    // the shell's own messages. Icons only Explorer's tray has stay in the list. UI thread.
    void SetHosting(bool on);
    bool Hosting() const { return host_ != nullptr; }
    // A hosted icon only (the shell protocol has these; Explorer's buttons do not):
    // the middle button, and the pointer resting on an icon (null: on none).
    void MiddleClick(const TrayIcon& icon);
    void Hover(const TrayIcon* icon);
    void SetHostPlaces(RECT anchor); // the panel's place on screen, for apps that ask where their icon is
    // The programs whose tray icon flashes now (hosted icons only): a message waiting.
    std::vector<std::wstring> Flashing() const;

    // First line of a tooltip, for display.
    static std::wstring Title(const std::wstring& tip);

private:
    struct Job {
        bool refresh = false;
        TrayIcon icon;
        tray::Action action = tray::Action::Left;
        bool systemTray = false;
    };
    void Worker();
    bool ClickHosted(const TrayIcon& icon, tray::HostAction action);
    HWND notify_;
    UINT msg_;
    std::function<std::vector<PinnedApp>()> appNames_;
    mutable std::mutex mutex_;
    std::condition_variable cv_;
    std::deque<Job> jobs_;
    std::vector<TrayIcon> icons_;
    bool loaded_ = false, available_ = false, stop_ = false;
    std::thread thread_;
    std::unique_ptr<TrayHost> host_;
    // Ink for a hosted one-shade glyph, by picture version.
    mutable std::map<uint64_t, std::pair<std::shared_ptr<const IconImage>, bool>> hostInk_;
    mutable std::map<std::wstring, std::wstring> hostNames_; // program descriptions, for icons without a tooltip
    // Windows' order of the icons by "guid:{...}", "uid:<exe>|<uid>" and "exe:<exe>" (the first of the program).
    std::map<std::wstring, size_t> windowsOrder_;
};

} // namespace atrium
