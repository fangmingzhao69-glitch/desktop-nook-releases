// Task model for the bar: pinned apps plus running app windows, grouped per
// application (D13). Lives on the UI thread (WinEvent hooks need its message
// loop); the bar reads immutable snapshots.
#pragma once

#include "icon_image.h"
#include "pinned_app.h"

#include <windows.h>

#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <string>
#include <vector>

namespace atrium {

struct TaskItem {
    std::wstring key;   // stable grouping key: lowercase AUMID if known, else lowercase exe path
    std::wstring name;  // pinned name, else window/app display name
    bool pinned = false;
    int pinIndex = -1;  // position in the pin list, -1 if not pinned
    std::vector<HWND> windows; // visible app windows of this group, most recently active first
    bool active = false;       // one of `windows` is the foreground window
    bool attention = false;    // a window is flashing (FlashWindowEx / HSHELL_FLASH)
    uint64_t iconVersion = 0;  // changes when the icon should be fetched again
    bool multipleWindows = false;
};

bool AllowsMultipleWindows(const PinnedApp& app);

class TaskModel {
public:
    // Calls `onChanged` on the UI thread (coalesced, at most every ~30 ms)
    // whenever Snapshot() would return something different.
    TaskModel(HWND uiWindow, std::function<void()> onChanged, const std::function<bool(const std::wstring&)>& openExisting = {});
    ~TaskModel();
    TaskModel(const TaskModel&) = delete;
    TaskModel& operator=(const TaskModel&) = delete;

    // Pinned items first in pin order, then running unpinned groups in the
    // order they first appeared. Unpinned groups are omitted if !showRunning,
    // unless `all` (Stage Manager sees every app).
    std::vector<TaskItem> Snapshot(bool all = false) const;

    void SetPins(const std::vector<PinnedApp>& pins);
    void SetShowRunning(bool show);
    // A window flashed for attention (HSHELL_FLASH), also a hidden one (WeChat in the tray flashes
    // its window for a message); called on the UI thread, while that window is not in front.
    void SetOnFlash(std::function<void(HWND)> onFlash);

    // Icon for an item at `sizePx` (square), cached per key and size. Never
    // blocks for long: returns empty until loaded, then onChanged fires.
    IconImage Icon(const std::wstring& key, int sizePx);

    // Left click, like the Windows taskbar: no window -> launch; one window ->
    // minimize if it is the foreground window, otherwise restore and activate;
    // several windows -> activate the next one in turn.
    void Activate(const std::wstring& key, HMONITOR monitor = nullptr); // monitor restricts cycling to this taskbar's windows
    // Middle click / "new window": start another instance.
    void Launch(const std::wstring& key);
    // End the entire application, including tray processes and child helpers.
    bool CanTerminate(const std::wstring& key) const;
    void Terminate(const std::wstring& key);
    // One window of a group (the window previews): restore and bring it to the front, or close it.
    static void ActivateWindow(HWND window);
    // True means the native close command was queued; the application still
    // owns confirmation, cancellation and close-to-tray behavior.
    static bool CloseWindow(HWND window);
    // A pin for a running unpinned item (to append to settings).
    PinnedApp PinFor(const std::wstring& key) const;
    // The path a pinned item was pinned from (a shortcut when that is what was
    // pinned): the shell's menus and AppUserModelID live there, not on the exe.
    std::wstring PinTarget(const std::wstring& key) const;
    // Brings the group's most recent window to the front (never minimizes or
    // launches); used while files are dragged over the item.
    void Show(const std::wstring& key);
    // Moves a running unpinned group to `toIndex` among the unpinned groups.
    void MoveRunning(const std::wstring& key, int toIndex);
    // Files dropped on an item: open them with that app.
    bool CanOpenFiles(const std::wstring& key) const;
    bool OpenFiles(const std::wstring& key, const std::vector<std::wstring>& files);
    // The desktop exe behind an item ("open file location"); empty for packaged apps.
    std::wstring FileLocation(const std::wstring& key) const;

    // The user's current Windows taskbar pins (desktop apps, from
    // %APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar).
    static std::vector<PinnedApp> ImportWindowsPins();

    // Forwarded from the UI window procedure; returns true if handled.
    bool HandleMessage(UINT msg, WPARAM wp, LPARAM lp);

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace atrium
