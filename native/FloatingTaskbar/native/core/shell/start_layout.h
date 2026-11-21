// The start menu's own arrangement, %LOCALAPPDATA%\Atrium\start.json: its
// size (small, medium, large or the largest), the apps the user hid, and its tiles in the user's order, apps and folders of apps.
// Until the user arranges something the menu shows the apps pinned to the
// Windows start menu (`custom` false); the first change copies them in, and
// from then on the two are separate. Also the initials the menu searches and
// indexes names by (the pinyin of Chinese names).
#pragma once

#include <string>
#include <vector>

namespace atrium {

struct StartTile {
    std::wstring id;   // an app: its catalog id (CatalogApp::id); a folder: "folder:<n>"
    std::wstring name; // an app: its name when saved (shown when it is not found); a folder: its name
    std::vector<StartTile> apps; // a folder's apps, in order
    bool Folder() const { return id.starts_with(L"folder:"); }
};

struct StartLayout {
    // Two independent choices, each small, medium (the default), large and the
    // largest. `size` is the menu's own: how many tiles a page holds. `icon` is
    // how large a tile's icon and a folder's cover are drawn; the cells grow
    // with it. The tables are in ui/start_menu.cpp.
    static constexpr int kSizes = 4, kMedium = 1;
    int size = kMedium;
    int icon = kMedium;
    bool custom = false;           // tiles arranged by the user (else the Windows pins)
    std::vector<StartTile> tiles;
    std::vector<std::wstring> hidden; // apps kept out of the menu (but for the end of the A-Z list)
    // The Windows start menu's pins already taken into the user's arrangement: one pinned there
    // later is added at the end (#63); one the user removed here is not brought back.
    std::vector<std::wstring> windowsPins;
    bool windowsPinsKnown = false;
    void Clamp();
    std::wstring NewFolderId() const;
};

StartLayout LoadStartLayout();
bool SaveStartLayout(const StartLayout& layout);

// The first letter of each word, and for each Chinese character the first
// letter of its pinyin (the 3755 common characters of GB2312), lower case:
// "Visual Studio Code" -> "vsc", "微信" -> "wx".
std::wstring NameInitials(const std::wstring& name);
// The A-Z index's letter of a name: 'A'..'Z', or '#' (digits, symbols, rare characters).
wchar_t IndexLetter(const std::wstring& name);

} // namespace atrium
