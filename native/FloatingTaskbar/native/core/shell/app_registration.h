// Atrium's Installed apps registry entry and Start menu shortcut.
#pragma once

#include <string>

namespace atrium {

inline constexpr wchar_t kAppId[] = L"DesktopNook.FloatingTaskbar";

bool IsRegisteredApplication(const std::wstring& directory);
// Development builds never register, including complete development packages.
void RegisterApplication(const std::wstring& directory);

} // namespace atrium
