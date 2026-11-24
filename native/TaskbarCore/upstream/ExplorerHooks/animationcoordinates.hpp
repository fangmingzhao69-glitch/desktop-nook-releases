#pragma once
#include <appmodel.h>
#include <propsys.h>
#include <shellapi.h>
#include <shobjidl.h>
#include <cstdint>
#include <cwchar>
#include <initializer_list>
namespace
{
	std::uint64_t AnimationHash(const wchar_t* text) noexcept
	{
		std::uint64_t hash = 14695981039346656037ULL;
		for (; *text; ++text)
		{
			const wchar_t normalized = *text >= L'a' && *text <= L'z' ? static_cast<wchar_t>(*text - 32) : *text;
			hash = (hash ^ static_cast<std::uint16_t>(normalized)) * 1099511628211ULL;
		}
		return hash;
	}

	bool AnimationIdentity(HWND window, wchar_t (&text)[512]) noexcept
	{
		IPropertyStore* store{};
		if (SUCCEEDED(SHGetPropertyStoreForWindow(window, IID_PPV_ARGS(&store))))
		{
			const PROPERTYKEY key{{0x9f4c2855,0x9f79,0x4b39,{0xa8,0xd0,0xe1,0xd4,0x2d,0xe1,0xd5,0xf3}},5};
			PROPVARIANT value{};
			if (SUCCEEDED(store->GetValue(key, &value)) && value.vt == VT_LPWSTR && value.pwszVal)
				wcsncpy_s(text, value.pwszVal, _TRUNCATE);
			PropVariantClear(&value);
			store->Release();
			if (*text) return true;
		}
		DWORD pid{};
		GetWindowThreadProcessId(window, &pid);
		wil::unique_process_handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid));
		if (!process) return false;
		UINT32 length = ARRAYSIZE(text);
		if (GetApplicationUserModelId(process.get(), &length, text) == ERROR_SUCCESS) return true;
		DWORD pathLength = ARRAYSIZE(text);
		return QueryFullProcessImageNameW(process.get(), 0, text, &pathLength) != FALSE;
	}

	void CorrectAnimation(SHELLHOOKINFO& animation) noexcept
	{
		// Windows 11 can return two packed signed POINTS in the first two
		// RECT fields. Preserve that wire format when returning the correction.
		const bool packed = animation.rc.right == 0 && animation.rc.bottom == 0;
		RECT rectangle = animation.rc;
		if (packed)
		{
			rectangle = {static_cast<short>(LOWORD(animation.rc.left)), static_cast<short>(HIWORD(animation.rc.left)),
				static_cast<short>(LOWORD(animation.rc.top)), static_cast<short>(HIWORD(animation.rc.top))};
		}
		bool active = false;
		for (auto name : {L"Shell_TrayWnd", L"Shell_SecondaryTrayWnd"})
		{
			HWND taskbar{};
			while ((taskbar = FindWindowExW(nullptr, taskbar, name, nullptr)))
			{
				const auto bridge = FindWindowExW(taskbar, nullptr, L"Windows.UI.Composition.DesktopWindowContentBridge", nullptr);
				if (bridge && GetPropW(bridge, L"DesktopNook.NativeTaskbarShape.Owner")) { active = true; break; }
			}
		}
		if (!active) return;
		wchar_t identity[512]{};
		const bool identified = AnimationIdentity(animation.hwnd, identity);
		wchar_t positionKey[96]{}, sizeKey[96]{};
		if (identified)
		{
			const auto hash = AnimationHash(identity);
			swprintf_s(positionKey, L"DesktopNook.Animation.%016llx.Position", static_cast<unsigned long long>(hash));
			swprintf_s(sizeKey, L"DesktopNook.Animation.%016llx.Size", static_cast<unsigned long long>(hash));
		}
		for (auto name : {L"Shell_TrayWnd", L"Shell_SecondaryTrayWnd"})
		{
			HWND taskbar{};
			while ((taskbar = FindWindowExW(nullptr, taskbar, name, nullptr)))
			{
				const auto bridge = FindWindowExW(taskbar, nullptr, L"Windows.UI.Composition.DesktopWindowContentBridge", nullptr);
				if (!bridge || !GetPropW(bridge, L"DesktopNook.NativeTaskbarShape.Owner")) continue;
				auto& rect = rectangle;
				const auto width = rect.right - rect.left;
				const auto height = rect.bottom - rect.top;
				if (width < 0 || height < 0 || width > 200 || height > 200) continue;
				const auto size = identified ? reinterpret_cast<std::uintptr_t>(GetPropW(bridge, sizeKey)) : 0;
				const auto point = identified ? reinterpret_cast<std::uintptr_t>(GetPropW(bridge, positionKey)) : 0;
				const LONG originalX = (rect.left + rect.right) / 2, originalY = (rect.top + rect.bottom) / 2;
				if (size)
				{
					const LONG centerX = static_cast<LONG>(point) + static_cast<LONG>(size) / 2;
					const LONG centerY = static_cast<LONG>(point >> 32) + static_cast<LONG>(size >> 32) / 2;
					rect = {centerX - width / 2, centerY - height / 2, centerX - width / 2 + width, centerY - height / 2 + height};
				}
				else
				{
					const auto center = static_cast<std::uint64_t>(static_cast<std::uint32_t>(originalX)) |
						(static_cast<std::uint64_t>(static_cast<std::uint32_t>(originalY)) << 32);
					if (GetPropW(bridge, L"DesktopNook.Animation.Window") == animation.hwnd &&
						reinterpret_cast<std::uintptr_t>(GetPropW(bridge, L"DesktopNook.Animation.Target")) == center &&
						GetTickCount64() - reinterpret_cast<std::uintptr_t>(GetPropW(bridge, L"DesktopNook.Animation.Tick")) < 8)
						return;
					RECT outer{}, inner{};
					if (!GetWindowRect(taskbar, &outer) || !GetWindowRect(bridge, &inner) ||
						rect.top < outer.top - 80 || rect.bottom > outer.bottom + 80 ||
						rect.left < outer.left || rect.right > outer.left + inner.right - inner.left) continue;
					OffsetRect(&rect, inner.left - outer.left, inner.top - outer.top);
				}
				const auto before = static_cast<std::uint64_t>(static_cast<std::uint32_t>(originalX)) |
					(static_cast<std::uint64_t>(static_cast<std::uint32_t>(originalY)) << 32);
				const auto after = static_cast<std::uint64_t>(static_cast<std::uint32_t>((rect.left + rect.right) / 2)) |
					(static_cast<std::uint64_t>(static_cast<std::uint32_t>((rect.top + rect.bottom) / 2)) << 32);
				SetPropW(bridge, L"DesktopNook.Animation.Source", reinterpret_cast<HANDLE>(before));
				SetPropW(bridge, L"DesktopNook.Animation.Target", reinterpret_cast<HANDLE>(after));
				SetPropW(bridge, L"DesktopNook.Animation.Window", animation.hwnd);
				SetPropW(bridge, L"DesktopNook.Animation.Tick", reinterpret_cast<HANDLE>(static_cast<UINT_PTR>(GetTickCount64())));
				animation.rc = packed ? RECT{static_cast<LONG>(MAKELONG(rect.left, rect.top)),
					static_cast<LONG>(MAKELONG(rect.right, rect.bottom)), 0, 0} : rect;
				return;
			}
		}
	}
}
