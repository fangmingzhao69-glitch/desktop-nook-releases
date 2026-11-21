// Manual read-only COM capability probe. Never sets preferences or opens overflow.
// ABI reference: Chromium status_tray_state_changer_win.cc (see docs/TRAY.md).
#include <windows.h>
#include <wrl.h>
#include <iostream>
#include <atomic>
struct NotifyItem {
    PWSTR exe, tip;
    HICON icon;
    HWND window;
    DWORD preference;
    UINT id;
    GUID guid;
};
struct __declspec(uuid("D782CCBA-AFB0-43F1-94DB-FDA3779EACCB")) NotifyCallback : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Notify(ULONG, NotifyItem*) = 0;
};
struct __declspec(uuid("D133CE13-3537-48BA-93A7-AFCD5D2053B4")) TrayNotify : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE RegisterCallback(NotifyCallback*, ULONG*) = 0;
    virtual HRESULT STDMETHODCALLTYPE UnregisterCallback(ULONG*) = 0;
};
class Callback final : public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, NotifyCallback> {
public:
    std::atomic<unsigned> count{0};
    HRESULT STDMETHODCALLTYPE Notify(ULONG event, NotifyItem* item) override
    {
        if (!item)
            return E_POINTER;
        ++count;
        std::wcout << L"event=" << event << L" uid=" << item->id << L" icon=" << item->icon << L" exe=" << (item->exe ? item->exe : L"") << L'\n';
        return S_OK;
    }
};
int wmain()
{
    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr))
        return 1;
    {
        CLSID cls{};
        CLSIDFromString(L"{25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD}", &cls);
        Microsoft::WRL::ComPtr<TrayNotify> tray;
        hr = CoCreateInstance(cls, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&tray));
        std::wcout << L"create=0x" << std::hex << unsigned(hr) << std::dec << L'\n';
        if (SUCCEEDED(hr)) {
            auto callback = Microsoft::WRL::Make<Callback>();
            ULONG cookie = 0;
            hr = tray->RegisterCallback(callback.Get(), &cookie);
            if (SUCCEEDED(hr)) {
                const auto until = GetTickCount64() + 500;
                while (GetTickCount64() < until) {
                    MSG msg;
                    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                        TranslateMessage(&msg);
                        DispatchMessageW(&msg);
                    }
                    Sleep(10);
                }
            }
            std::wcout << L"register=0x" << std::hex << unsigned(hr) << std::dec << L" count=" << callback->count << L'\n';
            if (SUCCEEDED(hr))
                std::wcout << L"unregister=0x" << std::hex << unsigned(tray->UnregisterCallback(&cookie)) << L'\n';
        }
    }
    CoUninitialize();
    return FAILED(hr) ? 1 : 0;
}
