// The network widget's data: the link that is carrying this machine's traffic, its rates second by
// second and the last minute of them. GetIfTable2 reads the counters the kernel keeps for every
// interface, so nothing is captured and no service is asked: the samples cost less than a
// millisecond and run on the widgets' own timer, once a second, while a network widget is up.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <unordered_map>
#include <vector>

namespace atrium {

constexpr size_t kNetworkPoints = 60; // a minute of history, one point a second

// Converts MIB_IF_ROW2::ReceiveLinkSpeed (bits/s) to Mbps; NDIS_LINK_SPEED_UNKNOWN (~0) is 0.
inline double LinkSpeedMbps(uint64_t bitsPerSecond) { return bitsPerSecond == ~uint64_t{0} ? 0.0 : double(bitsPerSecond) / 1000000.0; }

// One second of the link, bytes in and out.
struct NetworkRate {
    float down = 0, up = 0;
};

class NetworkMonitor {
public:
    // Read only while a widget shows the link; turning it off makes the next sample a new
    // baseline, so a gap never reads as one huge burst.
    void SetActive(bool on)
    {
        active_ = on;
        if (!on)
            lastMs_ = 0;
    }

    void Sample();

    bool Linked() const { return linked_; }       // a link is connected and carrying traffic
    const std::wstring& Name() const { return name_; } // the adapter's alias: "以太网", "WLAN"
    double LinkMbps() const { return linkMbps_; } // its negotiated speed, 0 when Windows does not say
    double DownBps() const { return down_; }
    double UpBps() const { return up_; }
    double DownBytes() const { return downBytes_; } // since the widget first read the link
    double UpBytes() const { return upBytes_; }
    const std::vector<NetworkRate>& History() const { return history_; }

private:
    struct Traffic {
        uint64_t in = 0, out = 0;
    };

    bool active_ = false;
    bool linked_ = false;
    std::wstring name_;
    double linkMbps_ = 0;
    double down_ = 0, up_ = 0;
    double downBytes_ = 0, upBytes_ = 0;
    std::vector<NetworkRate> history_;
    uint64_t luid_ = 0;   // the interface shown, kept while it is still the one carrying traffic
    uint64_t lastMs_ = 0; // the counters' previous reading (0: none yet)
    std::unordered_map<uint64_t, Traffic> last_; // every usable interface's counters at that reading
};

} // namespace atrium
