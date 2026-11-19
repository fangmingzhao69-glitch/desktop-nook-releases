#include <Windows.h>
#include <filesystem>
#include <mutex>
#include <stdexcept>
#include <wil/resource.h>
#include <unknwn.h>
#include <winrt/base.h>
#include "taskbar/taskbarattributeworker.hpp"
#include "../ProgramLog/log.hpp"

std::filesystem::path QingxiaComponentDirectory()
{
    HMODULE module{};
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&QingxiaComponentDirectory), &module))
        winrt::throw_last_error();

    std::wstring path(32768, L'\0');
    const auto length = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) winrt::throw_last_error();
    path.resize(length);
    return std::filesystem::path(path).parent_path();
}

extern "C" __declspec(dllexport) unsigned int WINAPI QingxiaTaskbarAbiVersion() noexcept
{
    return 1;
}

extern "C" __declspec(dllexport) HRESULT WINAPI QingxiaRunTaskbar(
    const wchar_t* settingsRoot, HANDLE stop, HANDLE ready, BOOL dry) noexcept
{
    if (!settingsRoot || !stop || !ready) return E_INVALIDARG;
    try
    {
        winrt::init_apartment(winrt::apartment_type::single_threaded);
        const auto apartment = wil::scope_exit([] { winrt::uninit_apartment(); });
        auto storage = std::make_optional(std::filesystem::path(settingsRoot));
        static std::once_flag logInitialization;
        std::call_once(logInitialization, [&] { Log::Initialize(storage); });

        bool configExists = false;
        TaskbarAttributeWorker* running = nullptr;
        ConfigManager config(storage, configExists, [](void* value) {
            auto worker = *static_cast<TaskbarAttributeWorker**>(value);
            if (worker) worker->ConfigurationChanged();
        }, &running);
        if (!configExists) return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);

        const auto pump = [&]() -> HRESULT
        {
            if (!SetEvent(ready)) return HRESULT_FROM_WIN32(GetLastError());
            for (;;)
            {
                const DWORD result = MsgWaitForMultipleObjectsEx(1, &stop, INFINITE, QS_ALLINPUT, MWMO_ALERTABLE | MWMO_INPUTAVAILABLE);
                if (result == WAIT_OBJECT_0) return S_OK;
                if (result == WAIT_IO_COMPLETION) continue;
                if (result != WAIT_OBJECT_0 + 1) return HRESULT_FROM_WIN32(GetLastError());
                for (MSG message; PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE); )
                {
                    if (message.message == WM_QUIT) return message.wParam ? E_FAIL : S_OK;
                    TranslateMessage(&message);
                    DispatchMessageW(&message);
                }
            }
        };
        if (dry) return pump();

        HMODULE module{};
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(&QingxiaRunTaskbar), &module))
            return HRESULT_FROM_WIN32(GetLastError());
        DynamicLoader loader;
        TaskbarAttributeWorker worker(config, module, loader, storage);
        running = &worker;
        const auto disarm = wil::scope_exit([&] { running = nullptr; });
        return pump();
    }
    catch (...) { return winrt::to_hresult(); }
}
