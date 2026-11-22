#include "taskbarappearanceservice.hpp"
#include <RpcProxy.h>
#include <CommCtrl.h>
#include <cstdint>
#include <memory>
#include <shellapi.h>
#include <wil/cppwinrt_helpers.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Management.Deployment.h>
#include <winrt/Windows.UI.Xaml.Controls.h>

#include "constants.hpp"
#include "util/color.hpp"

#include "XamlBlurBrush.h"

extern "C"
{
	_Check_return_ HRESULT STDAPICALLTYPE DLLGETCLASSOBJECT_ENTRY(_In_ REFCLSID rclsid, _In_ REFIID riid, _Outptr_ void** ppv);
}

namespace
{
	constexpr wchar_t CapsuleOwner[] = L"DesktopNook.NativeTaskbarShape.Owner";
	constexpr wchar_t CapsulePosition[] = L"DesktopNook.NativeTaskbarShape.Position";
	constexpr wchar_t CapsuleGeometryLock[] = L"DesktopNook.NativeTaskbarShape.GeometryLock";
	constexpr wchar_t CapsuleInset[] = L"DesktopNook.NativeTaskbarShape.Inset";
	constexpr wchar_t CapsuleDivider[] = L"DesktopNook.NativeTaskbarShape.Divider";
	constexpr wchar_t CapsuleTrim[] = L"DesktopNook.NativeTaskbarShape.Trim";
	constexpr wchar_t CapsuleTrimApplied[] = L"DesktopNook.NativeTaskbarShape.TrimApplied";
	constexpr wchar_t CapsuleSmooth[] = L"DesktopNook.NativeTaskbarShape.Smooth";
	constexpr wchar_t CapsuleDividerVisible[] = L"DesktopNook.NativeTaskbarShape.DividerVisible";
	constexpr UINT_PTR CapsuleSubclassId = 1;
	const UINT CapsuleVisualRefresh = RegisterWindowMessageW(L"DesktopNook.NativeTaskbarShape.VisualRefresh");

	struct CapsuleVisual
	{
		wux::UIElement Content{ nullptr };
		wuc::Visual Visual{ nullptr };
		wuc::CompositionClip OriginalClip{ nullptr };
		wuc::CompositionGeometricClip Clip{ nullptr };
		wuc::CompositionRoundedRectangleGeometry Outline{ nullptr };
		wuc::ContainerVisual DividerParent{ nullptr };
		wuc::SpriteVisual Divider{ nullptr };
		wux::UIElement Tray{ nullptr };
		wux::Media::Transform OriginalTrayTransform{ nullptr };
		wux::Media::Transform TrayTransform{ nullptr };
		bool OwnsContainer = false;
		int Width = 0, Height = 0, Inset = -1;
		int Trim = 0;
		UINT Dpi = 0;

		static wux::UIElement FindTray(const wux::DependencyObject& parent, int depth)
		{
			if (!parent || depth < 0) return nullptr;
			const auto type = winrt::get_class_name(parent);
			if (type == L"SystemTray.SystemTrayFrame")
				return parent.try_as<wux::UIElement>();
			// Newer Explorer builds host the tray as the content of a repeater
			// item. Move the item so its complete hit surface moves with the tray.
			if (type == L"Taskbar.TaskbarExtensionElement")
			{
				if (const auto host = parent.try_as<wux::Controls::ContentControl>())
					if (const auto tray = host.Content();
						tray && winrt::get_class_name(tray) == L"SystemTray.SystemTrayFrame")
						return host;
			}
			const int count = wux::Media::VisualTreeHelper::GetChildrenCount(parent);
			for (int i = 0; i < count; ++i)
				if (auto tray = FindTray(wux::Media::VisualTreeHelper::GetChild(parent, i), depth - 1))
					return tray;
			return nullptr;
		}

		void ResetTray()
		{
			if (Tray && TrayTransform && Tray.RenderTransform() == TrayTransform)
				Tray.RenderTransform(OriginalTrayTransform);
			Tray = nullptr;
			OriginalTrayTransform = nullptr;
			TrayTransform = nullptr;
		}

		int ApplyTrayTrim(int trim, float pixel, UINT dpi)
		{
			if (trim <= 0)
			{
				ResetTray();
				return 0;
			}
			if (!Tray || !Tray.XamlRoot())
			{
				ResetTray();
				if (const auto root = Content.XamlRoot())
					Tray = FindTray(root.Content(), 6);
				if (!Tray) return 0;
			}
			if (TrayTransform && Tray.RenderTransform() == TrayTransform && Trim == trim && Dpi == dpi)
				return trim;
			if (!TrayTransform || Tray.RenderTransform() != TrayTransform)
				OriginalTrayTransform = Tray.RenderTransform();
			wux::Media::TranslateTransform offset;
			offset.X(-trim * pixel);
			if (OriginalTrayTransform)
			{
				wux::Media::TransformGroup transforms;
				transforms.Children().Append(OriginalTrayTransform);
				transforms.Children().Append(offset);
				TrayTransform = transforms;
			}
			else
				TrayTransform = offset;
			Tray.RenderTransform(TrayTransform);
			return trim;
		}

		void Reset(HWND window)
		{
			RemovePropW(window, CapsuleSmooth);
			RemovePropW(window, CapsuleDividerVisible);
			RemovePropW(window, CapsuleTrimApplied);
			ResetTray();
			if (Visual && Clip && Visual.Clip() == Clip)
				Visual.Clip(OriginalClip);
			if (DividerParent && Divider)
				DividerParent.Children().Remove(Divider);
			if (OwnsContainer && Content && wuxh::ElementCompositionPreview::GetElementChildVisual(Content) == DividerParent)
				wuxh::ElementCompositionPreview::SetElementChildVisual(Content, nullptr);
			Visual = nullptr;
			OriginalClip = nullptr;
			Clip = nullptr;
			Outline = nullptr;
			DividerParent = nullptr;
			Divider = nullptr;
			OwnsContainer = false;
			Inset = -1;
			Trim = 0;
		}

		void Refresh(HWND window)
		{
			const auto packed = reinterpret_cast<std::uintptr_t>(GetPropW(window, CapsulePosition));
			const auto insetValue = reinterpret_cast<std::uintptr_t>(GetPropW(window, CapsuleInset));
			if (!Content || !packed || !insetValue || !GetPropW(window, CapsuleOwner))
			{
				Reset(window);
				return;
			}
			const int inset = static_cast<int>(insetValue) - 1;
			const int width = static_cast<int>((packed >> 32) & 0xffff);
			const int height = static_cast<int>((packed >> 48) & 0xffff);
			const int visible = height - inset * 2;
			const UINT dpi = GetDpiForWindow(window);
			if (width < 100 || visible < 20 || inset < 0 || dpi == 0) return;

			if (!Outline)
			{
				Visual = wuxh::ElementCompositionPreview::GetElementVisual(Content);
				OriginalClip = Visual.Clip();
				const auto compositor = Visual.Compositor();
				Outline = compositor.CreateRoundedRectangleGeometry();
				Clip = compositor.CreateGeometricClip(Outline);
				if (const auto current = wuxh::ElementCompositionPreview::GetElementChildVisual(Content))
					DividerParent = current.try_as<wuc::ContainerVisual>();
				else
				{
					DividerParent = compositor.CreateContainerVisual();
					wuxh::ElementCompositionPreview::SetElementChildVisual(Content, DividerParent);
					OwnsContainer = true;
				}
				if (DividerParent)
				{
					Divider = compositor.CreateSpriteVisual();
					Divider.Brush(compositor.CreateColorBrush({ 120, 136, 145, 170 }));
					DividerParent.Children().InsertAtTop(Divider);
				}
			}

			const float pixel = 96.f / static_cast<float>(dpi);
			const int requested = static_cast<int>(reinterpret_cast<std::uintptr_t>(GetPropW(window, CapsuleTrim)));
			const int trim = ApplyTrayTrim(requested > 0 && requested <= width / 3 ? requested : 0, pixel, dpi);
			const int faceWidth = width - trim;
			const float radius = static_cast<float>(visible) / 2 * pixel;
			if (Width != width || Height != height || Inset != inset || Trim != trim || Dpi != dpi || Visual.Clip() != Clip)
			{
				Outline.Offset({ 0, inset * pixel });
				Outline.Size({ faceWidth * pixel, visible * pixel });
				Outline.CornerRadius({ radius, radius });
				Visual.Clip(Clip);
				// A GDI rounded region clips off the antialiased pixels. Keep only a
				// rectangular Win32 region for the vertical insets; Composition owns the curve.
				if (const auto region = CreateRectRgn(0, inset, faceWidth, height - inset))
				{
					if (!SetWindowRgn(window, region, TRUE))
						DeleteObject(region);
					else
						SetPropW(window, CapsuleSmooth, reinterpret_cast<HANDLE>(static_cast<UINT_PTR>(1)));
				}
				Width = width;
				Height = height;
				Inset = inset;
				Trim = trim;
				Dpi = dpi;
			}
			if (trim)
				SetPropW(window, CapsuleTrimApplied, reinterpret_cast<HANDLE>(static_cast<UINT_PTR>(trim)));
			else
				RemovePropW(window, CapsuleTrimApplied);
			if (Divider)
			{
				const auto x = reinterpret_cast<std::uintptr_t>(GetPropW(window, CapsuleDivider));
				const bool draw = x > 2 && x < static_cast<std::uintptr_t>(faceWidth - 2);
				Divider.Opacity(draw ? 1.f : 0.f);
				Divider.Size({ pixel, visible * .5f * pixel });
				Divider.Offset({ static_cast<float>(x) * pixel, (inset + visible * .25f) * pixel, 0 });
				if (draw)
					SetPropW(window, CapsuleDividerVisible, reinterpret_cast<HANDLE>(x));
				else
					RemovePropW(window, CapsuleDividerVisible);
			}
		}
	};

	LRESULT CALLBACK KeepCapsulePosition(HWND window, UINT message, WPARAM wParam, LPARAM lParam,
		UINT_PTR subclassId, DWORD_PTR context)
	{
		auto* capsule = reinterpret_cast<CapsuleVisual*>(context);
		if (message == CapsuleVisualRefresh && CapsuleVisualRefresh)
		{
			try { capsule->Refresh(window); }
			catch (...) {} // Keep the rounded Win32 fallback if the compositor cannot create the clip.
			return 0;
		}
		if (message == WM_WINDOWPOSCHANGING && lParam)
		{
			// Let Explorer perform its normal validation first, then retain the managed
			// capsule coordinates even if its default handler proposed a full-width child.
			const auto result = DefSubclassProc(window, message, wParam, lParam);
			const auto packed = GetPropW(window, CapsuleOwner)
				? reinterpret_cast<std::uintptr_t>(GetPropW(window, CapsulePosition)) : 0;
			auto& position = *reinterpret_cast<WINDOWPOS*>(lParam);
			if (packed && (position.flags & (SWP_NOMOVE | SWP_NOSIZE)) != (SWP_NOMOVE | SWP_NOSIZE))
			{
				const auto x = static_cast<int>(packed & 0xffff);
				const auto y = static_cast<std::int16_t>((packed >> 16) & 0xffff);
				const auto width = static_cast<int>((packed >> 32) & 0xffff);
				const auto height = static_cast<int>((packed >> 48) & 0xffff);
				RECT parent{};
				if (GetClientRect(GetParent(window), &parent) && width > 0 && x + width <= parent.right &&
					height == parent.bottom && y >= -16 && y <= 0)
				{
					position.x = x;
					position.y = y;
					position.cx = width;
					position.cy = height;
					position.flags &= ~(SWP_NOMOVE | SWP_NOSIZE);
				}
			}
			return result;
		}
		else if (message == WM_NCDESTROY)
		{
			RemovePropW(window, CapsuleGeometryLock);
			RemoveWindowSubclass(window, KeepCapsulePosition, subclassId);
			delete capsule;
		}
		return DefSubclassProc(window, message, wParam, lParam);
	}

	void DetachCapsulePositionLock(HWND window)
	{
		DWORD_PTR context = 0;
		if (GetWindowSubclass(window, KeepCapsulePosition, CapsuleSubclassId, &context) &&
			RemoveWindowSubclass(window, KeepCapsulePosition, CapsuleSubclassId))
		{
			RemovePropW(window, CapsuleGeometryLock);
			std::unique_ptr<CapsuleVisual> capsule(reinterpret_cast<CapsuleVisual*>(context));
			try { capsule->Reset(window); } catch (...) {}
		}
	}
}

DWORD TaskbarAppearanceService::s_ProxyStubRegistrationCookie = 0;

TaskbarAppearanceService::TaskbarAppearanceService() :
	m_RegisterCookie(0),
	m_XamlThreadQueue(winrt::Windows::System::DispatcherQueue::GetForCurrentThread())
{
	InstallProxyStub();
	winrt::check_hresult(RegisterActiveObject(static_cast<ITaskbarAppearanceService*>(this), CLSID_TaskbarAppearanceService, ACTIVEOBJECT_STRONG, &m_RegisterCookie));
}

HRESULT TaskbarAppearanceService::GetVersion(DWORD* apiVersion) noexcept
{
	if (apiVersion)
	{
		*apiVersion = TAP_API_VERSION;
	}

	return S_OK;
}

HRESULT TaskbarAppearanceService::SetTaskbarAppearance(HWND taskbar, TaskbarBrush brush, UINT color) try
{
	if (const auto info = GetTaskbarInfo(taskbar))
	{
		if (info->background.control && info->background.originalFill)
		{
			const winrt::Windows::UI::Color tint = Util::Color::FromABGR(color);
			wux::Media::Brush newBrush = nullptr;
			if (brush == Acrylic)
			{
				wux::Media::AcrylicBrush acrylicBrush;
				// on the taskbar, using Backdrop instead of HostBackdrop
				// makes the effect still show what's behind, but also not disable itself
				// when the window isn't active
				// this is because it sources what's behind the XAML, and the taskbar window
				// is transparent so what's behind is actually the content behind the window
				// (it doesn't need to poke a hole like HostBackdrop)
				acrylicBrush.BackgroundSource(wux::Media::AcrylicBackgroundSource::Backdrop);
				acrylicBrush.TintColor(tint);

				newBrush = std::move(acrylicBrush);
			}
			else if (brush == SolidColor)
			{
				wux::Media::SolidColorBrush solidBrush;
				solidBrush.Color(tint);

				newBrush = std::move(solidBrush);
			}

			info->background.control.Fill(newBrush);
		}
	}

	return S_OK;
}
catch (...)
{
	return winrt::to_hresult();
}

HRESULT TaskbarAppearanceService::SetTaskbarBlur(HWND taskbar, UINT color, FLOAT blurAmount) try
{
	if (const auto info = GetTaskbarInfo(taskbar))
	{
		if (info->background.control && info->background.originalFill)
		{
			const winrt::Windows::UI::Color tint = Util::Color::FromABGR(color);
			const wfn::float4 tintHdr = {
				tint.R / 255.0f,
				tint.G / 255.0f,
				tint.B / 255.0f,
				tint.A / 255.0f
			};

			auto compositor = wuxh::ElementCompositionPreview::GetElementVisual(info->background.control).Compositor();
			info->background.control.Fill(winrt::make<XamlBlurBrush>(std::move(compositor), blurAmount, tintHdr));
		}
	}

	return S_OK;
}
catch (...)
{
	return winrt::to_hresult();
}

HRESULT TaskbarAppearanceService::ReturnTaskbarToDefaultAppearance(HWND taskbar) try
{
	for (const auto& [handle, info] : m_Taskbars)
	{
		if (GetAncestor(info.window, GA_PARENT) == taskbar)
		{
			RestoreDefaultControlFill(info.background);
			break;
		}
	}

	return S_OK;
}
catch (...)
{
	return winrt::to_hresult();
}

HRESULT TaskbarAppearanceService::SetTaskbarBorderVisibility(HWND taskbar, BOOL visible) try
{
	for (const auto& [handle, info] : m_Taskbars)
	{
		if (GetAncestor(info.window, GA_PARENT) == taskbar)
		{
			if (visible)
			{
				RestoreDefaultControlFill(info.border);
			}
			else
			{
				if (info.border.control && info.border.originalFill)
				{
					wux::Media::SolidColorBrush brush;
					brush.Opacity(0);
					info.border.control.Fill(brush);
				}
			}

			break;
		}
	}

	return S_OK;
}
catch (...)
{
	return winrt::to_hresult();
}

HRESULT TaskbarAppearanceService::RestoreAllTaskbarsToDefault() try
{
	for (const auto& [handle, info] : m_Taskbars)
	{
		RestoreDefaultControlFill(info.background);
		RestoreDefaultControlFill(info.border);
	}

	return S_OK;
}
catch (...)
{
	return winrt::to_hresult();
}

HRESULT TaskbarAppearanceService::RestoreAllTaskbarsToDefaultWhenProcessDies(DWORD pid)
{
	if (!m_Process)
	{
		m_Process.reset(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, pid));
		if (!m_Process) [[unlikely]]
		{
			return HRESULT_FROM_WIN32(GetLastError());
		}

		if (!RegisterWaitForSingleObject(m_WaitHandle.put(), m_Process.get(), ProcessWaitCallback, this, INFINITE, WT_EXECUTEONLYONCE))
		{
			m_Process.reset();
			return HRESULT_FROM_WIN32(GetLastError());
		}

		return S_OK;
	}
	else
	{
		if (GetProcessId(m_Process.get()) == pid)
		{
			return S_OK; // already watching for this process to die.
		}
		else
		{
			return E_ILLEGAL_METHOD_CALL;
		}
	}
}

HRESULT TaskbarAppearanceService::KillExplorerWhenPackageUninstalls(LPCWSTR packageFullName) try
{
	const std::wstring_view packageFullNameSv { packageFullName };

	if (!m_PackageCatalog)
	{
		winrt::Windows::Management::Deployment::PackageManager pm;
		auto catalog = wam::PackageCatalog::OpenForPackage(pm.FindPackageForUser(L"", packageFullNameSv));

		m_PackageUninstallingToken = catalog.PackageUninstalling({ get_weak(), &TaskbarAppearanceService::OnPackageUninstalling });
		m_PackageUpdatingToken = catalog.PackageUpdating({ get_weak(), &TaskbarAppearanceService::OnPackageUpdating });
		m_PackageBeingWatched = packageFullNameSv;
		m_PackageCatalog = std::move(catalog);

		return S_OK;
	}
	else
	{
		if (m_PackageBeingWatched == packageFullNameSv)
		{
			return S_OK; // already watching for this package to get uninstalled.
		}
		else
		{
			return E_ILLEGAL_METHOD_CALL;
		}
	}
}
catch (...)
{
	return winrt::to_hresult();
}

void TaskbarAppearanceService::RegisterTaskbar(InstanceHandle frameHandle, HWND window, const wux::UIElement& content)
{
	m_Taskbars.insert_or_assign(frameHandle, TaskbarInfo { { }, { }, window });
	DWORD_PTR existing = 0;
	if (GetWindowSubclass(window, KeepCapsulePosition, CapsuleSubclassId, &existing))
		return;
	auto capsule = std::make_unique<CapsuleVisual>();
	capsule->Content = content;
	if (!SetWindowSubclass(window, KeepCapsulePosition, CapsuleSubclassId, reinterpret_cast<DWORD_PTR>(capsule.get())))
		return;
	if (!SetPropW(window, CapsuleGeometryLock, reinterpret_cast<HANDLE>(static_cast<UINT_PTR>(1))))
		RemoveWindowSubclass(window, KeepCapsulePosition, CapsuleSubclassId);
	else
		static_cast<void>(capsule.release());
}

void TaskbarAppearanceService::RegisterTaskbarBackground(InstanceHandle frameHandle, wux::Shapes::Shape element)
{
	if (const auto it = m_Taskbars.find(frameHandle); it != m_Taskbars.end())
	{
		it->second.background.control = element;

		// sometimes we may see objects come with their fill set to null, wait until the system initialized it before
		// we start messing with it
		// in the future if the taskbar doesn't change the original brush, but tries to change the entire brush
		// this could be useful to suppress it and update the saved brush
		element.RegisterPropertyChangedCallback(wux::Shapes::Shape::FillProperty(), { get_weak(), &TaskbarAppearanceService::OnTaskbarBackgroundUpdated });
		it->second.background.originalFill = element.Fill();
	}
}

void TaskbarAppearanceService::RegisterTaskbarBorder(InstanceHandle frameHandle, wux::Shapes::Shape element)
{
	if (const auto it = m_Taskbars.find(frameHandle); it != m_Taskbars.end())
	{
		it->second.border.control = element;

		element.RegisterPropertyChangedCallback(wux::Shapes::Shape::FillProperty(), { get_weak(), &TaskbarAppearanceService::OnTaskbarBorderUpdated });
		it->second.border.originalFill = element.Fill();
	}
}

void TaskbarAppearanceService::UnregisterTaskbar(InstanceHandle frameHandle)
{
	if (auto it = m_Taskbars.find(frameHandle); it != m_Taskbars.end())
	{
		DetachCapsulePositionLock(it->second.window);
		const auto fillProperty = wux::Shapes::Shape::FillProperty();
		if (it->second.background.fillChangedToken != 0)
		{
			it->second.background.control.UnregisterPropertyChangedCallback(fillProperty, it->second.background.fillChangedToken);
		}

		if (it->second.border.fillChangedToken != 0)
		{
			it->second.border.control.UnregisterPropertyChangedCallback(fillProperty, it->second.border.fillChangedToken);
		}

		m_Taskbars.erase(it);
	}
}

TaskbarAppearanceService::~TaskbarAppearanceService()
{
	if (m_PackageCatalog)
	{
		m_PackageCatalog.PackageUpdating(m_PackageUpdatingToken);
		m_PackageCatalog.PackageUninstalling(m_PackageUninstallingToken);

		m_PackageBeingWatched.clear();
		m_PackageUpdatingToken = { };
		m_PackageUninstallingToken = { };
		m_PackageCatalog = nullptr;
	}

	// wait for all callbacks to be done, as they have a raw pointer to the instance.
	winrt::check_bool(UnregisterWaitEx(m_WaitHandle.get(), INVALID_HANDLE_VALUE));
	m_WaitHandle.release();

	m_Process.reset();

	winrt::check_hresult(RestoreAllTaskbarsToDefault());
	winrt::check_hresult(RevokeActiveObject(m_RegisterCookie, nullptr));
}

void TaskbarAppearanceService::InstallProxyStub()
{
	if (!s_ProxyStubRegistrationCookie)
	{
		winrt::com_ptr<IUnknown> proxyStub;
		winrt::check_hresult(DLLGETCLASSOBJECT_ENTRY(PROXY_CLSID_IS, winrt::guid_of<decltype(proxyStub)::type>(), proxyStub.put_void()));
		winrt::check_hresult(CoRegisterClassObject(PROXY_CLSID_IS, proxyStub.get(), CLSCTX_INPROC_SERVER, REGCLS_MULTIPLEUSE, &s_ProxyStubRegistrationCookie));

		winrt::check_hresult(CoRegisterPSClsid(IID_ITaskbarAppearanceService, PROXY_CLSID_IS));
		winrt::check_hresult(CoRegisterPSClsid(IID_IVersionedApi, PROXY_CLSID_IS));
	}
}

void TaskbarAppearanceService::UninstallProxyStub()
{
	if (s_ProxyStubRegistrationCookie)
	{
		winrt::check_hresult(CoRevokeClassObject(s_ProxyStubRegistrationCookie));
	}
}

winrt::fire_and_forget TaskbarAppearanceService::OnProcessDied()
{
	m_WaitHandle.reset();
	m_Process.reset();

	const auto self_weak = get_weak();
	co_await wil::resume_foreground(m_XamlThreadQueue);

	if (const auto self = self_weak.get())
	{
		self->RestoreAllTaskbarsToDefault();
	}
}

void TaskbarAppearanceService::OnPackageUninstalling(const wam::PackageCatalog&, const wam::PackageUninstallingEventArgs &args)
{
	if (args.Package().Id().FullName() == m_PackageBeingWatched)
	{
		// kill explorer to force the system to restart it, thereby unloading our DLL.
		TerminateProcess(GetCurrentProcess(), 0);
	}
}

void TaskbarAppearanceService::OnPackageUpdating(const wam::PackageCatalog&, const wam::PackageUpdatingEventArgs &args)
{
	if (args.SourcePackage().Id().FullName() == m_PackageBeingWatched)
	{
		// kill explorer to force the system to restart it, thereby unloading our DLL.
		TerminateProcess(GetCurrentProcess(), 0);
	}
}

std::optional<TaskbarAppearanceService::TaskbarInfo> TaskbarAppearanceService::GetTaskbarInfo(HWND taskbar)
{
	for (const auto& [handle, info] : m_Taskbars)
	{
		if (GetAncestor(info.window, GA_PARENT) == taskbar)
		{
			return info;
		}
	}

	return std::nullopt;
}

void TaskbarAppearanceService::OnTaskbarBackgroundUpdated(const wux::DependencyObject &sender, const wux::DependencyProperty&)
{
	for (auto& [handle, info] : m_Taskbars)
	{
		if (sender == info.background.control && !info.background.originalFill)
		{
			info.background.originalFill = info.background.control.Fill();
		}
	}
}

void TaskbarAppearanceService::OnTaskbarBorderUpdated(const wux::DependencyObject &sender, const wux::DependencyProperty&)
{
	for (auto& [handle, info] : m_Taskbars)
	{
		if (sender == info.border.control && !info.border.originalFill)
		{
			info.border.originalFill = info.border.control.Fill();
		}
	}
}

void TaskbarAppearanceService::RestoreDefaultControlFill(const ControlInfo<wux::Shapes::Shape> &info)
{
	if (info.control && info.originalFill)
	{
		info.control.Fill(info.originalFill);
	}
}

void TaskbarAppearanceService::ProcessWaitCallback(void* parameter, BOOLEAN)
{
	reinterpret_cast<TaskbarAppearanceService *>(parameter)->OnProcessDied();
}
