#include "shell/tray_host.h"

#include <chrono>
#include <cstdio>

using namespace atrium;

int main()
{
    const auto start = std::chrono::steady_clock::now();
    for (int i = 0; i < 20; ++i) {
        TrayHost host(nullptr, WM_APP, 0, 60000);
        if (host.Running()) {
            std::fprintf(stderr, "FAIL: host reported Running() before delayed start finished\n");
            return 1;
        }
    }
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start).count();
    if (elapsed > 5000) {
        std::fprintf(stderr, "FAIL: start/stop loop took too long (%lld ms)\n", static_cast<long long>(elapsed));
        return 1;
    }
    std::printf("ok: 20 delayed-start/timeout cycles exited in %lld ms\n", static_cast<long long>(elapsed));
    return 0;
}
