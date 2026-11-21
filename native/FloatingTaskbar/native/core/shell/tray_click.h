// Tray icon click routing, fallback order and classic toolbar mouse gestures.
#pragma once
#include <cstdint>
#include <cstddef>
#include <vector>

namespace atrium::tray {
enum class Action { Left, Right, Double, DoubleContinuation };
enum class Route { Invoke, ContextMenu, LegacyDefault, Toolbar };
enum class Outcome { Observed, AcceptedUnconfirmed, Unavailable };
inline constexpr int32_t kProviderTimeout = static_cast<int32_t>(0x80131505u); // UIA_E_TIMEOUT
struct Attempt {
    Route route;
    int32_t status;
    bool observed;
};
struct DispatchResult {
    Outcome outcome = Outcome::Unavailable;
    std::vector<Attempt> attempts;
};
enum class MouseEvent { Move, LeftDown, LeftUp, LeftDouble, RightDown, RightUp };
struct Posting {
    bool complete = false, actionQueued = false;
    size_t queued = 0;
};
template<class Post> Posting PostClassicGesture(Action action, Post post)
{
    std::vector<MouseEvent> events{MouseEvent::Move};
    if (action == Action::DoubleContinuation)
        events.insert(events.end(), {MouseEvent::LeftDouble, MouseEvent::LeftUp});
    else if (action == Action::Right)
        events.insert(events.end(), {MouseEvent::RightDown, MouseEvent::RightUp});
    else {
        events.insert(events.end(), {MouseEvent::LeftDown, MouseEvent::LeftUp});
        if (action == Action::Double)
            events.insert(events.end(), {MouseEvent::LeftDouble, MouseEvent::LeftUp});
    }
    Posting result;
    for (MouseEvent event : events) {
        if (!post(event))
            return result;
        ++result.queued;
        if (event == MouseEvent::LeftDown || event == MouseEvent::RightDown || event == MouseEvent::LeftDouble)
            result.actionQueued = true;
    }
    result.complete = true;
    return result;
}
// An accepted asynchronous action must not be repeated through another API: it may toggle
// the UI back, or deliver a destructive second command. Missing evidence is not failure.
template<class Invoke, class Observe> DispatchResult Dispatch(Action action, bool classic, Invoke invoke, Observe observe)
{
    DispatchResult result;
    std::vector<Route> routes;
    if (action == Action::DoubleContinuation && !classic) {
        // The XAML tray has no double-click pattern, but the shell pairs a second Invoke
        // within the double-click time with the first one (#49). One attempt, no fallback.
        routes.push_back(Route::Invoke);
    } else if (action == Action::Double || action == Action::DoubleContinuation) {
        if (classic)
            routes.push_back(Route::Toolbar); // Invoke twice is NOT a double click
    } else {
        routes.push_back(action == Action::Right ? Route::ContextMenu : Route::Invoke);
        if (classic)
            routes.push_back(Route::Toolbar);
        if (action == Action::Left)
            routes.push_back(Route::LegacyDefault);
    }
    for (Route route : routes) {
        const int32_t status = invoke(route);
        const bool uncertain = status >= 0 || status == kProviderTimeout;
        const bool observed = observe(uncertain);
        result.attempts.push_back({route, status, observed});
        if (observed) {
            result.outcome = Outcome::Observed;
            break;
        }
        if (uncertain) {
            result.outcome = Outcome::AcceptedUnconfirmed;
            break;
        }
    }
    return result;
}
} // namespace atrium::tray
