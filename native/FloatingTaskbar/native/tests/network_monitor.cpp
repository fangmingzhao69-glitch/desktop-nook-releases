#include "shell/network_monitor.h"

#include <cstdint>
#include <cstdio>

namespace {

int g_failures = 0;

void Check(bool ok, const char* what)
{
    if (!ok) {
        std::fprintf(stderr, "FAIL: %s\n", what);
        ++g_failures;
    }
}

} // namespace

int main()
{
    using namespace atrium;

    Check(LinkSpeedMbps(0) == 0.0, "zero link speed stays zero");
    Check(LinkSpeedMbps(1000000000ULL) == 1000.0, "1 Gbps reports 1000 Mbps");
    Check(LinkSpeedMbps(2500000000ULL) == 2500.0, "2.5 Gbps reports 2500 Mbps");
    Check(LinkSpeedMbps(~uint64_t{0}) == 0.0, "NDIS_LINK_SPEED_UNKNOWN reports zero instead of ~1.8e13 Mbps");

    if (g_failures) {
        std::fprintf(stderr, "%d network monitor check(s) failed\n", g_failures);
        return 1;
    }
    std::puts("network monitor checks passed");
    return 0;
}
