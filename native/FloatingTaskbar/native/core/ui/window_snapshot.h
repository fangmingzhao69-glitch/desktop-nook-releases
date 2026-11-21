// Still pictures of windows for Stage Manager's strip: taken just before a window is put away
// (a minimized window has nothing to show), on worker threads, since PrintWindow waits for the
// window's app. A picture is small (about the size it is shown at), its corners rounded, with a soft
// shadow of its own; both premultiplied BGRA, ready for Canvas::ImagePerspective.
#pragma once

#include "icon_image.h"

#include <windows.h>

#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>
#include <unordered_set>
#include <vector>

namespace atrium {

struct WindowPicture {
    IconImage image;  // the window, with kPicturePad transparent px around it
    IconImage shadow; // its shadow at half the size, kShadowPad of the picture's px around it
    int width = 0, height = 0; // the window's part of `image`, px
    float corner = 0; // its corner radius, px
    bool ok = false;
};

constexpr int kPicturePad = 2;
constexpr int kShadowPad = 24;

// The soft shadow of a w x h rounded rectangle, at half its size, kShadowPad of its px around it.
IconImage SoftShadow(int w, int h);

// The picture of a placeholder window: `icon` in the middle of a plain rounded panel (a window
// put away before Atrium started has no picture).
WindowPicture PlaceholderPicture(const IconImage& icon, int width, int height, bool dark);

// `picture` (a large one, no shadow) at most `largeMax` px either way.
WindowPicture Smaller(const WindowPicture& picture, int largeMax);

class WindowSnapshots {
public:
    // Results are announced with `message` posted to `notify`, then Take()n.
    WindowSnapshots(HWND notify, UINT message);
    ~WindowSnapshots();
    WindowSnapshots(const WindowSnapshots&) = delete;
    WindowSnapshots& operator=(const WindowSnapshots&) = delete;

    // Pictures `window` at most maxWidth x maxHeight px, corners of `radius` px; with largeMax, also
    // a large one (at most largeMax px either way, the window's own corners, no shadow).
    void Request(HWND window, int maxWidth, int maxHeight, float radius, int largeMax = 0);
    struct Result {
        HWND window;
        WindowPicture picture;
        WindowPicture large; // ok only if asked for and taken
    };
    std::vector<Result> Take();

private:
    struct Job {
        HWND window;
        int maxWidth, maxHeight, largeMax;
        float radius;
    };
    void Worker();

    std::mutex lock_;
    std::condition_variable cv_;
    std::deque<Job> queue_;
    std::unordered_set<HWND> pending_; // queued or in-flight: at most one entry per window
    std::vector<Result> done_;
    HWND notify_ = nullptr;
    UINT message_ = 0;
    bool stop_ = false;
    std::thread worker_;
};

} // namespace atrium
