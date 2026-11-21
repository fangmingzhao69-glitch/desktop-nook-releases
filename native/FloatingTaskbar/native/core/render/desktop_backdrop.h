// The backdrop of the desktop widgets (D35, D38): the desktop itself, never
// the windows above it. Two ways:
//  - "desktop": Windows.Graphics.Capture of Explorer's desktop window
//    (Progman). A window capture holds the window's own content even where
//    other windows cover it; frames come only when the desktop changes (a new
//    wallpaper, a slideshow), so the widgets' glass is drawn again only then;
//  - "wallpaper" (the setting widgetBackdrop, and the fallback): the
//    wallpaper files laid out as Windows lays them out (IDesktopWallpaper:
//    fill, fit, stretch, tile, centre, span; the background colour), composed
//    on a worker thread and checked again every few seconds. No capture runs.
// Render thread only, except the worker.
#pragma once

#include "capture.h"
#include "gpu.h"

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace atrium {

class DesktopBackdrop {
public:
    // `frameEvent` is set when the desktop capture has a new frame.
    DesktopBackdrop(const Gpu& gpu, HANDLE frameEvent);
    ~DesktopBackdrop();
    DesktopBackdrop(const DesktopBackdrop&) = delete;
    DesktopBackdrop& operator=(const DesktopBackdrop&) = delete;

    // "desktop", "wallpaper" or "off" (no widgets: nothing runs); takes the
    // newest frame or picture. Cheap when nothing changed.
    void Update(const std::wstring& mode);
    // The desktop, screen px from Origin(); null until the first frame.
    ID3D11Texture2D* Texture() const { return texture_.get(); }
    POINT Origin() const { return origin_; }
    // Bumped with every new picture.
    unsigned Version() const { return version_; }
    // Whether a picture after `version` changed anything in `area` (screen px). The capture
    // reports where each frame changed (Windows 11 24H2 and later; else the whole desktop), so a
    // widget's glass is not drawn again for a change elsewhere on the desktop.
    bool ChangedSince(unsigned version, const RECT& area) const;

private:
    bool StartCapture(); // adopts a started capture (see Starting); false while none
    void StopCapture();
    // Starting the capture can block (it never returned on one system: #66, the bar stayed blank),
    // so it starts on a thread of its own; the render thread adopts it when done, or gives up.
    struct Starting {
        Starting() = default; // the starting thread's own, before it hands over
        explicit Starting(HANDLE frameEvent);
        Starting(const Starting&) = delete;
        Starting& operator=(const Starting&) = delete;
        ~Starting();
        HANDLE wake = nullptr; // the frame event, its own copy: the bar may close its handle before the thread ends
        std::mutex lock;
        bool done = false, ok = false, abandoned = false;
        HRESULT error = S_OK;
        HWND desktop = nullptr;
        winrt::Windows::Graphics::Capture::GraphicsCaptureItem item{nullptr};
        winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool pool{nullptr};
        winrt::Windows::Graphics::Capture::GraphicsCaptureSession session{nullptr};
        winrt::Windows::Graphics::SizeInt32 size{};
    };
    std::shared_ptr<Starting> starting_;
    double startingSince_ = 0;
    void StartWallpaper();
    void StopWallpaper();
    void Store(ID3D11Texture2D* frame);

    const Gpu& gpu_;
    HANDLE frameEvent_;
    std::wstring mode_;
    winrt::com_ptr<ID3D11Texture2D> texture_;
    POINT origin_{};
    unsigned version_ = 0;
    struct Change {
        unsigned version;
        RECT rect; // screen px
    };
    std::vector<Change> changes_; // the last few pictures' changed areas, oldest first
    void Changed(const RECT& rect); // records the picture just stored as version_

    // The desktop window's capture.
    HWND desktop_ = nullptr;
    double retryMs_ = 0; // when to try the capture again (Explorer restarting)
    winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice device_{nullptr};
    winrt::Windows::Graphics::Capture::GraphicsCaptureItem item_{nullptr};
    winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool pool_{nullptr};
    winrt::Windows::Graphics::Capture::GraphicsCaptureSession session_{nullptr};
    winrt::Windows::Graphics::SizeInt32 size_{};
    std::atomic<bool> closed_{false};
    winrt::Windows::Graphics::Capture::GraphicsCaptureItem::Closed_revoker closedRevoker_;

    // The wallpaper, composed by the worker (BGRA, premultiplied, opaque).
    struct Picture {
        int width = 0, height = 0;
        POINT origin{};
        std::vector<uint8_t> pixels;
    };
    // Shared with the worker: it asks Explorer, which may not answer, so the render thread
    // may leave without it; the worker ends when its call returns.
    struct Wallpaper {
        explicit Wallpaper(HANDLE frameEvent);
        Wallpaper(const Wallpaper&) = delete;
        Wallpaper& operator=(const Wallpaper&) = delete;
        ~Wallpaper();
        HANDLE stop = nullptr, done = nullptr, wake = nullptr; // wake: the frame event, its own copy
        std::mutex lock;
        std::unique_ptr<Picture> picture; // new, not yet taken
    };
    std::unique_ptr<Picture> TakePicture(); // the worker's newest, if there is one
    static void WallpaperLoop(Wallpaper& work); // worker
    std::shared_ptr<Wallpaper> wallpaper_;
    std::thread worker_;
};

// The glass's backdrop in remote-control mode (setting remoteVisible): the wallpaper under one
// monitor, never the screen. Nothing reads the screen, so Atrium's windows may show in every
// capture, a remote-control app's Desktop Duplication included.
class WallpaperCapture : public BackdropSource {
public:
    WallpaperCapture(const Gpu& gpu, HMONITOR monitor, HANDLE frameEvent) : gpu_(gpu), monitor_(monitor), wallpaper_(gpu, frameEvent) {}
    ID3D11Texture2D* Acquire() override;
    void Release() override {}
    ID3D11Texture2D* Again() override { return version_ ? texture_.get() : nullptr; }
    bool KeepsOwnWindowsOut() const override { return true; }

private:
    const Gpu& gpu_;
    HMONITOR monitor_;
    DesktopBackdrop wallpaper_;
    unsigned version_ = 0;
    winrt::com_ptr<ID3D11Texture2D> texture_; // the monitor's part of the picture
};

} // namespace atrium
