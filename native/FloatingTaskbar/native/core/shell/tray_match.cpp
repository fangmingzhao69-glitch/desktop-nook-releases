#include "tray_match.h"
#include <algorithm>
#include <cwctype>
#include <map>
#include <numeric>
#include <string_view>

namespace atrium::tray {
std::wstring Title(const std::wstring& tip)
{
    const auto a = tip.find_first_not_of(L" \t\r\n");
    if (a == std::wstring::npos)
        return {};
    auto line = tip.substr(a, tip.find_first_of(L"\r\n", a) - a);
    while (!line.empty() && iswspace(line.back()))
        line.pop_back();
    return line;
}
std::wstring Key(const std::wstring& text)
{
    std::wstring out;
    for (wchar_t c : text)
        if (!iswspace(c) && std::wstring_view(L"-_:：·|()（）[]【】.,，").find(c) == std::wstring_view::npos)
            out += c >= L'A' && c <= L'Z' ? wchar_t(c + L'a' - L'A') : c;
    return out;
}
namespace {
bool DelimitedPrefix(const std::wstring& text, const std::wstring& name)
{
    // Keep the boundary that Key removes: Steam - syncing is a status suffix,
    // SteamVR / SteamHelper is a different name, even if Steam is the only candidate.
    std::wstring prefix;
    for (size_t i = 0; i < text.size(); ++i) {
        prefix += Key(text.substr(i, 1));
        if (prefix == name)
            return i + 1 < text.size() && Key(text.substr(i + 1, 1)).empty();
        if (!name.starts_with(prefix))
            return false;
    }
    return false;
}
int Score(const std::wstring& tip, const std::vector<std::wstring>& names, size_t firstTitle)
{
    const auto title = Title(tip), key = Key(title);
    if (key.empty())
        return 0;
    int best = 0;
    for (size_t n = 0; n < names.size(); ++n) {
        const auto name = Key(Title(names[n]));
        if (name.size() < 2)
            continue;
        int score = key == name ? 100 : DelimitedPrefix(title, name) ? 80 : 0;
        if (n >= firstTitle && score != 100)
            score = 0;
        best = std::max(best, score);
    }
    return best;
}
bool ContainsCenter(const Rect& cell, const Rect& icon)
{
    if (!cell.Valid() || !icon.Valid())
        return false;
    const long x = icon.left + (icon.right - icon.left) / 2;
    const long y = icon.top + (icon.bottom - icon.top) / 2;
    return x >= cell.left && x < cell.right && y >= cell.top && y < cell.bottom;
}
} // namespace
const wchar_t* MethodName(Method m)
{
    switch (m) {
    case Method::Geometry: return L"geometry";
    case Method::Name: return L"name";
    case Method::Order: return L"order";
    case Method::Running: return L"running";
    case Method::Record: return L"record";
    default: return L"unknown";
    }
}
Result Match(const std::vector<Button>& buttons, const std::vector<Record>& records, const std::vector<Program>& programs)
{
    Result out;
    out.aliases.resize(records.size(), kNone);
    out.sharedPlace.resize(records.size(), false);
    std::vector<size_t> sorted(records.size());
    std::iota(sorted.begin(), sorted.end(), 0);
    std::ranges::stable_sort(sorted, [&](size_t a, size_t b) {
        if (records[a].order != records[b].order)
            return records[a].order < records[b].order;
        return records[a].id < records[b].id;
    });
    std::map<std::wstring, size_t> identities;
    std::vector<size_t> live;
    for (size_t r : sorted) {
        if (!records[r].live || records[r].identity.empty())
            continue;
        const auto [it, fresh] = identities.try_emplace(records[r].identity, r);
        out.aliases[r] = it->second;
        if (fresh)
            live.push_back(r);
    }
    // Includes multiple identities from the SAME executable. Never consume a shared rectangle.
    for (size_t r : live)
        for (size_t s : live)
            if (r != s && records[r].bounds.Valid() && records[r].bounds == records[s].bounds)
                out.sharedPlace[r] = true;
    std::vector<bool> used(records.size(), false);
    for (size_t b = 0; b < buttons.size(); ++b)
        out.items.push_back({b});
    auto assign = [&](size_t b, size_t r, Method method) {
        out.items[b].record = r;
        out.items[b].method = method;
        used[r] = true;
    };
    // Geometry is usable only when unique in BOTH directions, before any records are consumed.
    for (size_t b = 0; b < buttons.size(); ++b) {
        std::vector<size_t> hits;
        for (size_t r : live)
            if (!out.sharedPlace[r] && ContainsCenter(buttons[b].bounds, records[r].bounds))
                hits.push_back(r);
        if (hits.size() != 1)
            continue;
        size_t r = hits.front(), count = 0;
        for (const auto& button : buttons)
            count += ContainsCenter(button.bounds, records[r].bounds);
        if (count == 1)
            assign(b, r, Method::Geometry);
    }
    // Mutual unique best name. Equal scores never silently select the first executable.
    for (int threshold : {100, 80}) {
        for (size_t b = 0; b < buttons.size(); ++b) {
            if (out.items[b].record != kNone)
                continue;
            int best = threshold - 1;
            size_t chosen = kNone;
            bool tied = false;
            for (size_t r : live) {
                if (used[r])
                    continue;
                int score = Score(buttons[b].tip, records[r].names, records[r].firstTitle);
                if (score > best)
                    best = score, chosen = r, tied = false;
                else if (score == best)
                    tied = true;
            }
            if (chosen == kNone || tied)
                continue;
            bool contested = false;
            for (size_t other = 0; other < buttons.size(); ++other)
                if (other != b && out.items[other].record == kNone && Score(buttons[other].tip, records[chosen].names, records[chosen].firstTitle) >= best)
                    contested = true;
            if (!contested)
                assign(b, chosen, Method::Name);
        }
    }
    // Order is a weak hint: require complete equal-size groups and agreement with every anchor.
    // Never zip a partial UIA tree to a longer list of records, or override a name anchor.
    for (bool promoted : {true, false}) {
        std::vector<size_t> bs, rs;
        for (size_t b = 0; b < buttons.size(); ++b)
            if (buttons[b].promoted == promoted)
                bs.push_back(b);
        for (size_t r : live)
            if (records[r].promoted == promoted)
                rs.push_back(r);
        if (bs.empty() || bs.size() != rs.size())
            continue;
        bool agrees = true;
        for (size_t i = 0; i < bs.size(); ++i) {
            const size_t b = bs[i], r = rs[i];
            if (records[r].order == kNone || (i && records[r].order == records[rs[i - 1]].order) ||
                (out.items[b].record != kNone && out.items[b].record != r) || (used[r] && out.items[b].record != r))
                agrees = false;
        }
        if (agrees)
            for (size_t i = 0; i < bs.size(); ++i)
                if (out.items[bs[i]].record == kNone)
                    assign(bs[i], rs[i], Method::Order);
    }
    for (auto& item : out.items) {
        if (item.record != kNone)
            continue;
        int best = 79;
        bool tied = false;
        for (size_t p = 0; p < programs.size(); ++p) {
            int score = Score(buttons[item.button].tip, programs[p].names, programs[p].firstTitle);
            if (score > best)
                best = score, item.program = p, tied = false;
            else if (score == best)
                tied = true;
        }
        if (tied)
            item.program = kNone;
        if (item.program != kNone)
            item.method = Method::Running;
    }
    // Identity-only deduplication. Neither exe, name, nor geometry proves two icons are one.
    for (size_t r : live)
        if (!used[r])
            out.items.push_back({kNone, r, kNone, Method::Record});
    return out;
}
} // namespace atrium::tray
