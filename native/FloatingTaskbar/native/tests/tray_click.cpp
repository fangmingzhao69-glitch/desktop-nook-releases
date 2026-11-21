#include "shell/tray_click.h"
#include "shell/tray_target.h"
#include "shell/tray_image.h"
#include <iostream>
#include <stdexcept>
using namespace atrium::tray;
void Check(bool value, const char* message)
{
    if (!value)
        throw std::runtime_error(message);
}
int main()
{
    try {
        int fallbackCalls = 0;
        auto art = LoadArtwork([] { return true; },
                               [&] {
                                   ++fallbackCalls;
                                   return true;
                               });
        Check(art == Artwork::Snapshot && fallbackCalls == 0, "valid tray state image cannot be replaced by branding");
        art = LoadArtwork([] { return false; },
                          [&] {
                              ++fallbackCalls;
                              return true;
                          });
        Check(art == Artwork::Application && fallbackCalls == 1, "missing or corrupt snapshot uses application fallback");
        auto fail = [](Route) { return int32_t(-1); };
        auto unseen = [](bool) { return false; };
        auto r = Dispatch(Action::Left, true, fail, unseen);
        Check(r.attempts.size() == 3 && r.attempts[1].route == Route::Toolbar && r.attempts[2].route == Route::LegacyDefault, "classic left fallback order");
        r = Dispatch(Action::Right, true, fail, unseen);
        Check(r.attempts.size() == 2 && r.attempts.back().route == Route::Toolbar, "classic right messages after failed context menu");
        r = Dispatch(Action::Left, false, [](Route) { return int32_t(0); }, unseen);
        Check(r.outcome == Outcome::AcceptedUnconfirmed && r.attempts.size() == 1, "S_OK without evidence is not success or safe to repeat");
        r = Dispatch(Action::Left, true, [](Route) { return kProviderTimeout; }, unseen);
        Check(r.outcome == Outcome::AcceptedUnconfirmed && r.attempts.size() == 1, "timeout does not prove the asynchronous action was cancelled");
        r = Dispatch(Action::Right, false, fail, [](bool) { return true; });
        Check(r.outcome == Outcome::Observed && r.attempts.size() == 1, "observed response wins even with provider error");
        r = Dispatch(Action::Double, false, fail, unseen);
        Check(r.attempts.empty() && r.outcome == Outcome::Unavailable, "XAML double click cannot be emulated by Invoke twice");
        r = Dispatch(
            Action::Double, true,
            [](Route route) {
                Check(route == Route::Toolbar, "classic double route");
                return int32_t(0);
            },
            [](bool) { return true; });
        Check(r.outcome == Outcome::Observed, "classic double response");
        const ShellSession shell{42, 100, 200};
        TargetKey a{shell, {1, 2}, L"guid:a", L"a.exe", L"A"};
        TargetKey b{shell, {1, 3}, L"guid:b", L"b.exe", L"B"};
        auto target = ResolveTarget(a, shell, {b, a});
        Check(target.index == 1 && target.reason == Resolution::Runtime, "reordering preserves exact runtime target");
        auto changed = a;
        changed.tip = L"A - syncing";
        Check(ResolveTarget(a, shell, {changed}).index == 0, "live record and runtime allow changing status");
        changed.identity = b.identity;
        auto reassigned = b;
        reassigned.identity = a.identity;
        reassigned.exe = a.exe;
        Check(ResolveTarget(a, shell, {changed, reassigned}).reason == Resolution::Conflict, "record reassignment cannot redirect runtime click");
        Check(ResolveTarget(a, shell, {reassigned}).reason == Resolution::Missing, "missing runtime cannot jump to matching record");
        Check(ResolveTarget(a, shell, {a, a}).reason == Resolution::Ambiguous, "duplicate runtime fails closed");
        changed = a;
        changed.exe = b.exe;
        Check(ResolveTarget(a, shell, {changed}).reason == Resolution::Conflict, "executable metadata conflict fails closed");
        auto recordOnly = a;
        recordOnly.runtime.clear();
        Check(ResolveTarget(recordOnly, shell, {b, a}).index == 1, "initially unmaterialized icon resolves by live identity");
        auto restarted = shell;
        ++restarted.started;
        Check(ResolveTarget(a, restarted, {a}).reason == Resolution::ShellChanged, "PID reuse does not preserve Explorer instance");
        Check(ResolveTarget(recordOnly, restarted, {a}).reason == Resolution::ShellChanged, "record clicks also reject old shell snapshots");
        restarted = shell;
        ++restarted.window;
        Check(ResolveTarget(a, restarted, {a}).reason == Resolution::ShellChanged, "taskbar window replacement invalidates runtime");
        auto unknown = a;
        unknown.identity.clear();
        unknown.exe.clear();
        changed = unknown;
        changed.tip = L"Different app";
        Check(ResolveTarget(unknown, shell, {changed}).reason == Resolution::Conflict, "unidentified recycled runtime cannot change name silently");
        TargetKey program;
        program.exe = a.exe;
        Check(ResolveTarget(program, shell, {a}).reason == Resolution::Program, "explicit program activation needs unique candidate");
        Check(ResolveTarget(program, shell, {a, a}).reason == Resolution::Ambiguous, "same exe multiple icons is ambiguous");
        Check(SameGestureTarget(a, a), "same icon forms a double gesture");
        changed = a;
        changed.shell = restarted;
        Check(!SameGestureTarget(a, changed), "double gesture cannot span shell replacement");
        changed = a;
        changed.runtime = b.runtime;
        Check(!SameGestureTarget(a, changed), "record remapping cannot combine separate gestures");
        Check(!SameGestureTarget({}, {}), "unknown gestures are not identical");
        Check(StableOrder({a, b}, {b, a}) == std::vector<size_t>{1, 0}, "refresh preserves surviving icon order");
        auto c = a;
        c.identity = L"guid:c";
        c.runtime = {9};
        Check(StableOrder({a, b}, {c, b, a}) == std::vector<size_t>{2, 1, 0}, "new icons append after survivors");
        Check(StableOrder({a, b}, {c, b}) == std::vector<size_t>{1, 0}, "removed icons disappear without reordering survivors");
        Check(StableOrder({recordOnly, b}, {b, a}) == std::vector<size_t>{1, 0}, "materializing a record preserves its place");
        std::vector<MouseEvent> continuation;
        PostClassicGesture(Action::DoubleContinuation, [&](MouseEvent e) {
            continuation.push_back(e);
            return true;
        });
        Check(continuation == std::vector{MouseEvent::Move, MouseEvent::LeftDouble, MouseEvent::LeftUp}, "double continuation never replays first click");
        int duplicateCalls = 0;
        r = Dispatch(
            Action::DoubleContinuation, false,
            [&](Route) {
                ++duplicateCalls;
                return 0;
            },
            unseen);
        Check(r.outcome == Outcome::AcceptedUnconfirmed && r.attempts.size() == 1 && r.attempts[0].route == Route::Invoke && duplicateCalls == 1,
              "XAML double continuation is one second Invoke, never a fallback");
        duplicateCalls = 0;
        r = Dispatch(
            Action::DoubleContinuation, false,
            [&](Route) {
                ++duplicateCalls;
                return -1;
            },
            unseen);
        Check(r.attempts.size() == 1 && duplicateCalls == 1, "a refused second Invoke is not retried through another route");
        for (Action action : {Action::Left, Action::Right, Action::Double}) {
            const std::vector<MouseEvent> expected =
                action == Action::Right  ? std::vector{MouseEvent::Move, MouseEvent::RightDown, MouseEvent::RightUp} :
                action == Action::Double ? std::vector{MouseEvent::Move, MouseEvent::LeftDown, MouseEvent::LeftUp, MouseEvent::LeftDouble, MouseEvent::LeftUp} :
                                           std::vector{MouseEvent::Move, MouseEvent::LeftDown, MouseEvent::LeftUp};
            std::vector<MouseEvent> actual;
            auto posting = PostClassicGesture(action, [&](MouseEvent event) {
                actual.push_back(event);
                return true;
            });
            Check(actual == expected && posting.complete, "classic message order implements native gesture");
            for (size_t failure = 0; failure < expected.size(); ++failure) {
                size_t calls = 0;
                posting = PostClassicGesture(action, [&](MouseEvent) { return calls++ != failure; });
                Check(!posting.complete && posting.queued == failure && calls == failure + 1, "posting stops at first failed message");
                Check(posting.actionQueued == (failure > 1), "failure after button down has uncertain delivery");
            }
        }
        r = Dispatch(Action::Double, true, [](Route) { return int32_t(1); }, unseen);
        Check(r.outcome == Outcome::AcceptedUnconfirmed && r.attempts.size() == 1, "partial toolbar delivery cannot replay a gesture");
        std::cout << "tray dispatch: all scenarios passed\n";
    } catch (const std::exception& e) {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
