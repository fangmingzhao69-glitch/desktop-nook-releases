#include "shell/shell_taskbar.h"
#include <cstdio>
#include <thread>

int main()
{
    auto run = [](bool pump, UINT timeoutMs, ULONGLONG& elapsedMs) {
        HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, nullptr), stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        HWND hwnd = nullptr;
        std::thread worker([&ready, &stop, &hwnd, pump] {
            hwnd = CreateWindowExW(0, L"STATIC", L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, nullptr, nullptr);
            MSG msg{};
            if (pump)
                PeekMessageW(&msg, nullptr, 0, 0, PM_NOREMOVE);
            SetEvent(ready);
            while (pump && MsgWaitForMultipleObjectsEx(1, &stop, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE) == WAIT_OBJECT_0 + 1)
                while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
                    DispatchMessageW(&msg);
            if (!pump)
                WaitForSingleObject(stop, 10000);
            DestroyWindow(hwnd);
        });
        const bool created = WaitForSingleObject(ready, 5000) == WAIT_OBJECT_0 && hwnd != nullptr;
        const ULONGLONG start = GetTickCount64();
        const bool ok = created && atrium::WindowResponsive(hwnd, timeoutMs);
        elapsedMs = GetTickCount64() - start;
        SetEvent(stop);
        worker.join();
        for (HANDLE h : {ready, stop})
            CloseHandle(h);
        return created ? (ok ? 1 : 0) : -1;
    };
    ULONGLONG elapsed = 0;
    if (atrium::WindowResponsive(nullptr, 50) || run(true, 1000, elapsed) != 1 || run(false, 150, elapsed) != 0 || elapsed < 100 || elapsed >= 1000) {
        std::fprintf(stderr, "FAIL: shell_taskbar WindowResponsive check failed (%llu ms)\n", elapsed);
        return 1;
    }
    std::printf("ok: shell_taskbar WindowResponsive checks passed\n");
    return 0;
}
