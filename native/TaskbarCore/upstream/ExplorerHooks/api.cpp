#include "api.hpp"
#include <cstdint>
#include <WinBase.h>
#include <detours/detours.h>
#include <processthreadsapi.h>
#include <wil/resource.h>
#include <wil/win32_helpers.h>
#include <WinUser.h>
#include <CommCtrl.h>

#include "constants.hpp"
#include "animationcoordinates.hpp"

namespace
{
    LRESULT CALLBACK AnimationMessages(int code, WPARAM w, LPARAM l) noexcept
    {
        const auto result = CallNextHookEx(nullptr, code, w, l);
        if (code >= 0 && l)
        {
            const auto& message = *reinterpret_cast<const CWPRETSTRUCT*>(l);
            static const UINT shellMessage = RegisterWindowMessageW(L"SHELLHOOK");
            if (message.message == shellMessage && message.wParam == HSHELL_GETMINRECT && message.lParam)
                CorrectAnimation(*reinterpret_cast<SHELLHOOKINFO*>(message.lParam));
        }
        return result;
    }
}
HHOOK InjectTaskbarAnimationHook(HWND window) noexcept
{
	const auto hook = SetWindowsHookExW(WH_CALLWNDPROCRET, AnimationMessages, wil::GetModuleInstanceHandle(), GetWindowThreadProcessId(window, nullptr));
	if (hook)
	{
		DWORD_PTR result{};
		SendMessageTimeoutW(window, WM_NULL, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 500, &result);
	}
	return hook;
}

LRESULT CALLBACK CallWndProc(int nCode, WPARAM wParam, LPARAM lParam) noexcept
{
	// Placeholder
	return CallNextHookEx(nullptr, nCode, wParam, lParam);
}

HHOOK InjectExplorerHook(HWND window) noexcept
{
	DWORD pid = 0;
	const DWORD tid = GetWindowThreadProcessId(window, &pid);

	wil::unique_process_handle proc(OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid));
	if (!proc) [[unlikely]]
	{
		return nullptr;
	}

	if (!DetourFindRemotePayload(proc.get(), EXPLORER_PAYLOAD, nullptr))
	{
		proc.reset(OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_WRITE, false, pid));
		if (!proc) [[unlikely]]
		{
			return nullptr;
		}

		static constexpr uint32_t content = 0xDEADBEEF;
		if (!DetourCopyPayloadToProcess(proc.get(), EXPLORER_PAYLOAD, &content, sizeof(content))) [[unlikely]]
		{
			return nullptr;
		}
	}

	return SetWindowsHookEx(WH_CALLWNDPROC, CallWndProc, wil::GetModuleInstanceHandle(), tid);
}
