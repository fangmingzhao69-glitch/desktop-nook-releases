// Value-only notification-area reconciliation. No Windows headers or calls.
#pragma once
#include <cstddef>
#include <string>
#include <vector>

namespace atrium::tray {
inline constexpr size_t kNone = static_cast<size_t>(-1);
struct Rect {
    long left = 0, top = 0, right = 0, bottom = 0;
    bool operator==(const Rect&) const = default;
    bool Valid() const { return right > left && bottom > top; }
};
struct Button {
    std::wstring tip;
    bool promoted = false;
    Rect bounds;
};
struct Record {
    std::wstring id, exe, tip;
    // GUID, or the HWND/PID/uID that actually passed the shell probe. Never exe/rect.
    std::wstring identity;
    bool live = false, promoted = false;
    size_t order = kNone;
    Rect bounds;
    std::vector<std::wstring> names;
    size_t firstTitle = kNone;
};
struct Program {
    std::wstring exe;
    std::vector<std::wstring> names;
    size_t firstTitle = kNone;
};
enum class Method { Unknown, Geometry, Name, Order, Running, Record };
const wchar_t* MethodName(Method method);
struct Pair {
    size_t button = kNone, record = kNone, program = kNone;
    Method method = Method::Unknown;
};
struct Result {
    std::vector<Pair> items; // UIA order, then unmatched live records in UIOrderList order
    std::vector<size_t> aliases; // canonical record index; none for dead/unprobeable records
    std::vector<bool> sharedPlace;
};
std::wstring Title(const std::wstring& tip);
std::wstring Key(const std::wstring& text);
Result Match(const std::vector<Button>& buttons, const std::vector<Record>& records, const std::vector<Program>& programs = {});
} // namespace atrium::tray
